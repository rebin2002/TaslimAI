using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

// Deterministic unit coverage for the #708 criterion-3 reference-video audio-extraction
// primitive. No ffmpeg binary, network, provider, billing or inference dependency is used:
// all media-process work flows through the injected IMusicReferenceVideoAudioProcessor seam
// with a fake, so the tests also prove the primitive cannot reach anything beyond that seam.
public sealed class MusicReferenceVideoAudioExtractionTests
{
    private const int MaxInputBytes = 1024 * 1024;      // 1 MiB
    private const int MaxOutputBytes = 64 * 1024;       // 64 KiB
    private const double MaxDurationSeconds = 600;

    private static MusicReferenceVideoAudioExtractionOptions TestOptions() => new()
    {
        MaxInputBytes = MaxInputBytes,
        MaxOutputBytes = MaxOutputBytes,
        MinDurationSeconds = 0.5,
        MaxDurationSeconds = MaxDurationSeconds,
        ProcessTimeoutSeconds = 5,
    };

    private static byte[] Mp4LikeVideoBytes(int length = 4096)
    {
        var bytes = new byte[length];
        Span<byte> header = stackalloc byte[] { 0x00, 0x00, 0x00, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' };
        header.CopyTo(bytes.AsSpan());
        return bytes;
    }

    private static MusicReferenceVideoAudioSource AuthorizedMp4Source(int length = 4096)
        => MusicReferenceVideoAudioSource.FromAuthorizedPrivateStream(new MemoryStream(Mp4LikeVideoBytes(length)), length, MusicReferenceVideoAudioSource.Mp4MediaType);

    private static void WriteFile(string path, int size)
    {
        var payload = new byte[size];
        new Random(7).NextBytes(payload);
        File.WriteAllBytes(path, payload);
    }

    private static string? WorkspaceOf(FakeProcessor fake)
        => fake.ProbeInputPaths.Count > 0 ? Path.GetDirectoryName(fake.ProbeInputPaths[0]) : fake.Plans.Count > 0 ? Path.GetDirectoryName(fake.Plans[0].InputFilePath) : null;

    [Fact]
    public async Task ValidReferenceVideo_ExtractsAudioThroughTypedPlanAndRemovesPrivateWorkspace()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mov,mp4,m4a,3gp,3g2,mj2", 30, 1),
            OnExtract = plan =>
            {
                WriteFile(plan.OutputFilePath, 2048);
                return Task.FromResult(0);
            },
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        var result = await extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source());

        Assert.Equal(2048, result.AudioBytes.Length);
        Assert.Equal("audio/mpeg", result.AudioContentType);
        Assert.Equal(TimeSpan.FromSeconds(30), result.SourceDuration);
        Assert.Equal(1, fake.ProbeCallCount);
        Assert.Equal(1, fake.ExtractCallCount);

        // The plan is a typed argument vector that keeps only the audio track:
        var plan = fake.Plans[0];
        Assert.Contains("-vn", plan.ArgumentList);
        Assert.Contains("-map", plan.ArgumentList);
        Assert.Contains("0:a:0", plan.ArgumentList);
        Assert.Contains("libmp3lame", plan.ArgumentList);
        Assert.Equal(1, plan.ArgumentList.Count(a => a == "-i"));
        Assert.True(plan.ProcessTimeout > TimeSpan.Zero);
        Assert.Equal(MaxOutputBytes, plan.MaxOutputBytes);

        // No shell composition, no network destinations, no caller-controlled names.
        Assert.All(plan.ArgumentList, argument =>
        {
            Assert.DoesNotContain('\0', argument);
            Assert.DoesNotContain("&", argument);
            Assert.DoesNotContain(";", argument);
            Assert.DoesNotContain("|", argument);
            Assert.DoesNotContain("http", argument, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal("reference.mp4", Path.GetFileName(plan.InputFilePath));
        Assert.Equal("audio.mp3", Path.GetFileName(plan.OutputFilePath));
        Assert.Matches("^[0-9a-f]{32}$", Path.GetFileName(Path.GetDirectoryName(plan.InputFilePath)!));

        // The private workspace (staged source + output) is fully removed on success too.
        Assert.False(File.Exists(plan.InputFilePath));
        Assert.False(File.Exists(plan.OutputFilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(plan.InputFilePath)!));
    }

    [Fact]
    public async Task SilentReferenceVideo_IsRejectedAndNeverReachesExtraction()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 12, 0),
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.MissingAudioTrack, error.Code);
        Assert.Equal(0, fake.ExtractCallCount);
        AssertWorkspaceRemoved(fake);
    }

    [Fact]
    public async Task NonIsoBmffBytes_AreRejectedBeforeAnyProcessorWork()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);
        var ebml = new byte[4096];
        byte[] magic = { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        magic.CopyTo(ebml, 0);
        var source = MusicReferenceVideoAudioSource.FromAuthorizedPrivateStream(
            new MemoryStream(ebml), ebml.Length, MusicReferenceVideoAudioSource.Mp4MediaType);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(source));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.MalformedContainer, error.Code);
        Assert.Equal(0, fake.ProbeCallCount);
        Assert.Equal(0, fake.ExtractCallCount);
    }

    [Fact]
    public async Task ProbeFailure_IsSanitizedAndLeaksNoContentOrPaths()
    {
        string? capturedInput = null;
        var fake = new FakeProcessor
        {
            OnProbe = input =>
            {
                capturedInput = input;
                throw new InvalidOperationException("ffprobe exited with SECRET-BUCKET-KEY sk-live-abc for " + input);
            },
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.MalformedContainer, error.Code);
        Assert.Null(error.InnerException);
        var fullText = error.Message + error.ToString();
        Assert.DoesNotContain("SECRET-BUCKET-KEY", fullText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-abc", fullText, StringComparison.Ordinal);
        Assert.NotNull(capturedInput);
        Assert.DoesNotContain(capturedInput!, fullText, StringComparison.Ordinal);
        AssertWorkspaceRemoved(fake);
    }

    [Fact]
    public async Task OversizedDeclaredInput_IsRejectedWithoutAnyWork()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);
        var source = MusicReferenceVideoAudioSource.FromAuthorizedPrivateStream(
            new MemoryStream(Mp4LikeVideoBytes(16)), MaxInputBytes + 1, MusicReferenceVideoAudioSource.Mp4MediaType);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(source));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.InputTooLarge, error.Code);
        Assert.Equal(0, fake.ProbeCallCount);
        Assert.Equal(0, fake.ExtractCallCount);
    }

    [Fact]
    public async Task ActualBytesExceedingHardLimit_AreRejectedDuringBoundedCopy()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);
        var oversized = Mp4LikeVideoBytes(MaxInputBytes + 512 * 1024);
        // A lying declared size below the cap cannot bypass it: the copy enforces the hard limit.
        var source = MusicReferenceVideoAudioSource.FromAuthorizedPrivateStream(
            new MemoryStream(oversized), 4096, MusicReferenceVideoAudioSource.Mp4MediaType);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(source));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.InputTooLarge, error.Code);
        Assert.Equal(0, fake.ProbeCallCount);
    }

    [Fact]
    public async Task OverlongReferenceVideo_IsRejectedBeforeExtraction()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mov", MaxDurationSeconds + 300, 1),
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.InputTooLong, error.Code);
        Assert.Equal(0, fake.ExtractCallCount);
        AssertWorkspaceRemoved(fake);
    }

    [Fact]
    public async Task NonPositiveDuration_IsRejectedBeforeExtraction()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 0.1, 1),
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.InvalidDuration, error.Code);
        Assert.Equal(0, fake.ExtractCallCount);
    }

    [Fact]
    public async Task CancellationDuringProbe_PropagatesAndCleansPrivateWorkspace()
    {
        var fake = new FakeProcessor
        {
            OnProbe = _ => throw new TaskCanceledException("canceled while probing"),
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        Assert.Equal(0, fake.ExtractCallCount);
        AssertWorkspaceRemoved(fake);
    }

    [Fact]
    public async Task CancellationDuringExtraction_CleansStagedInputAndPartialOutput()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
            OnExtract = plan =>
            {
                WriteFile(plan.OutputFilePath, 512); // partial audio written before the kill
                throw new OperationCanceledException("canceled mid-encode");
            },
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        var plan = fake.Plans[0];
        Assert.False(File.Exists(plan.InputFilePath));
        Assert.False(File.Exists(plan.OutputFilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(plan.InputFilePath)!));
    }

    [Fact]
    public async Task NonZeroProcessorExit_IsRejectedAsFailureAndCleansUp()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
            OnExtract = plan =>
            {
                WriteFile(plan.OutputFilePath, 128); // partial garbage output
                return Task.FromResult(1);
            },
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.ProcessorFailed, error.Code);
        AssertWorkspaceRemoved(fake);
    }

    [Fact]
    public async Task MissingOrEmptyOutput_IsRejected()
    {
        var noOutput = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
            OnExtract = _ => Task.FromResult(0),
        };
        var emptyOutput = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
            OnExtract = plan =>
            {
                File.WriteAllBytes(plan.OutputFilePath, Array.Empty<byte>());
                return Task.FromResult(0);
            },
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), noOutput);

        var missing = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));
        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.OutputEmpty, missing.Code);
        AssertWorkspaceRemoved(noOutput);

        var extractor2 = new MusicReferenceVideoAudioExtractor(TestOptions(), emptyOutput);
        var empty = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor2.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));
        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.OutputEmpty, empty.Code);
        AssertWorkspaceRemoved(emptyOutput);
    }

    [Fact]
    public async Task OversizedExtractionOutput_IsRejectedWithCleanup()
    {
        var fake = new FakeProcessor
        {
            ProbeResult = new MusicReferenceVideoProbeResult("mp4", 30, 1),
            OnExtract = plan =>
            {
                WriteFile(plan.OutputFilePath, MaxOutputBytes + 1);
                return Task.FromResult(0);
            },
        };
        var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

        var error = await Assert.ThrowsAsync<MusicReferenceVideoAudioExtractionException>(
            () => extractor.ExtractAudioFromVideoAsync(AuthorizedMp4Source()));

        Assert.Equal(MusicReferenceVideoAudioExtractionCodes.OutputTooLarge, error.Code);
        AssertWorkspaceRemoved(fake);
    }

    [Fact]
    public void SourceAcceptsOnlyAuthorizedPrivateVideoHandles_NeverUrlsWorkspacesOrTraversals()
    {
        // Arbitrary URL strings are not rooted paths and are rejected outright.
        Assert.Equal(
            MusicReferenceVideoAudioExtractionCodes.InvalidSource,
            ThrowsCode(() => MusicReferenceVideoAudioSource.FromAuthorizedPrivateFile(
                "https://evil.example/private.mp4", Path.GetTempPath(), MusicReferenceVideoAudioSource.Mp4MediaType)));

        // Traversal outside the authorized private root is rejected before any open.
        var root = Path.Combine(Path.GetTempPath(), "taslim-refroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(
                MusicReferenceVideoAudioExtractionCodes.InvalidSource,
                ThrowsCode(() => MusicReferenceVideoAudioSource.FromAuthorizedPrivateFile(
                    Path.Combine(root, "..", "..", "escape.mp4"), root, MusicReferenceVideoAudioSource.Mp4MediaType)));

            // Only the whitelisted video extensions and media types are accepted.
            Assert.Equal(
                MusicReferenceVideoAudioExtractionCodes.UnsupportedMediaType,
                ThrowsCode(() => MusicReferenceVideoAudioSource.FromAuthorizedPrivateFile(
                    Path.Combine(root, "clip.mkv"), root, MusicReferenceVideoAudioSource.Mp4MediaType)));
            Assert.Equal(
                MusicReferenceVideoAudioExtractionCodes.UnsupportedMediaType,
                ThrowsCode(() => MusicReferenceVideoAudioSource.FromAuthorizedPrivateStream(
                    new MemoryStream(Mp4LikeVideoBytes()), 4096, "video/webm")));
            Assert.Equal(
                MusicReferenceVideoAudioExtractionCodes.InvalidSource,
                ThrowsCode(() => MusicReferenceVideoAudioSource.FromAuthorizedPrivateStream(
                    new MemoryStream(Array.Empty<byte>()), 0, MusicReferenceVideoAudioSource.Mp4MediaType)));

            // A real file inside the root is accepted.
            var inside = Path.Combine(root, "clip.mp4");
            File.WriteAllBytes(inside, Mp4LikeVideoBytes());
            var handle = MusicReferenceVideoAudioSource.FromAuthorizedPrivateFile(
                inside, root, MusicReferenceVideoAudioSource.Mp4MediaType);
            Assert.Equal(MusicReferenceVideoAudioSource.Mp4MediaType, handle.MediaContentType);
            Assert.Equal(new FileInfo(inside).Length, handle.DeclaredSizeBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WholeAuthorizedHandleFlowsThroughExtraction_AndVideoNeverReachesThePlanAsOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "taslim-refroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inside = Path.Combine(root, "authorized.mp4");
            File.WriteAllBytes(inside, Mp4LikeVideoBytes(8192));
            var handle = MusicReferenceVideoAudioSource.FromAuthorizedPrivateFile(
                inside, root, MusicReferenceVideoAudioSource.Mp4MediaType);

            var fake = new FakeProcessor
            {
                ProbeResult = new MusicReferenceVideoProbeResult("mp4", 12, 2),
                OnExtract = plan =>
                {
                    WriteFile(plan.OutputFilePath, 1024);
                    return Task.FromResult(0);
                },
            };
            var extractor = new MusicReferenceVideoAudioExtractor(TestOptions(), fake);

            var result = await extractor.ExtractAudioFromVideoAsync(handle);

            Assert.Equal(1024, result.AudioBytes.Length);
            // The authorized source file itself is untouched (private storage not mutated).
            Assert.Equal(new FileInfo(inside).Length, new FileInfo(inside).Length);
            // Two audio tracks is still a valid reference: extraction proceeds.
            Assert.Equal(1, fake.ExtractCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrimitiveIsFreeOfInferenceProviderAndBillingDependencies()
    {
        var extractorType = typeof(MusicReferenceVideoAudioExtractor);
        var constructors = extractorType.GetConstructors();
        var constructor = Assert.Single(constructors);
        Assert.Equal(
            new[] { typeof(MusicReferenceVideoAudioExtractionOptions), typeof(IMusicReferenceVideoAudioProcessor) },
            constructor.GetParameters().Select(p => p.ParameterType).ToArray());

        var forbidden = new[] { "Provider", "Billing", "Ledger", "Credit", "Inference", "HttpClient", "Mubert", "StableAudio", "Payment", "Usage" };
        foreach (var field in extractorType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
        {
            var typeName = field.FieldType.Name;
            Assert.All(forbidden, marker => Assert.DoesNotContain(marker, typeName, StringComparison.OrdinalIgnoreCase));
            Assert.True(
                field.FieldType == typeof(MusicReferenceVideoAudioExtractionOptions) || field.FieldType == typeof(IMusicReferenceVideoAudioProcessor),
                $"Unexpected dependency {field.FieldType.Name} on the extraction primitive.");
        }
    }

    private static string ThrowsCode(Action action)
    {
        var error = Assert.Throws<MusicReferenceVideoAudioExtractionException>(action);
        return error.Code;
    }

    private static void AssertWorkspaceRemoved(FakeProcessor fake)
    {
        var workspace = WorkspaceOf(fake);
        Assert.NotNull(workspace);
        Assert.False(Directory.Exists(workspace));
    }

    private sealed class FakeProcessor : IMusicReferenceVideoAudioProcessor
    {
        public MusicReferenceVideoProbeResult? ProbeResult { get; init; }
        public Action<string>? OnProbe { get; init; }
        public Func<string, Task<MusicReferenceVideoProbeResult>>? ProbeFactory { get; init; }
        public Func<MusicReferenceVideoExtractionPlan, Task<int>>? OnExtract { get; init; }
        public List<string> ProbeInputPaths { get; } = new();
        public List<MusicReferenceVideoExtractionPlan> Plans { get; } = new();
        public int ProbeCallCount => ProbeInputPaths.Count;
        public int ExtractCallCount => Plans.Count;

        public Task<MusicReferenceVideoProbeResult> ProbeAsync(string inputFilePath, CancellationToken cancellationToken)
        {
            ProbeInputPaths.Add(inputFilePath);
            cancellationToken.ThrowIfCancellationRequested();
            OnProbe?.Invoke(inputFilePath);
            if (ProbeFactory is not null)
                return ProbeFactory(inputFilePath);
            return Task.FromResult(ProbeResult ?? throw new InvalidOperationException("No probe result configured."));
        }

        public Task<int> ExtractAudioAsync(MusicReferenceVideoExtractionPlan plan, CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            cancellationToken.ThrowIfCancellationRequested();
            if (OnExtract is null)
                throw new InvalidOperationException("No extract behavior configured.");
            return OnExtract(plan);
        }
    }
}
