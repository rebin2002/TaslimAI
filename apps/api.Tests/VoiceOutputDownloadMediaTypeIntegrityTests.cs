using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Voice;
using Xunit;
using FileOptions = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

/// <summary>
/// Download identity for saved speech. Voice Studio names the download file after the container the
/// provider synthesised and serves the media type recorded on that file, so the two must be resolved
/// from one speech contract instead of from the provider's own <c>Content-Type</c> spelling. Providers
/// do agree on the container but not always on its name: RFC 7845 registers <c>audio/opus</c> for an
/// Opus stream that lives in the Ogg container Voice Studio already calls <c>audio/ogg</c>, and the
/// OpenAI voice adapter explicitly tolerates both <c>audio/mp3</c> and <c>audio/opus</c> while passing
/// the raw header straight through. Every payload here is a synthetic fixture carrying the real
/// container magic bytes, so no speech provider is contacted or enabled.
/// </summary>
public sealed class VoiceOutputDownloadMediaTypeIntegrityTests
{
    /// <summary>
    /// A different spelling of the same container is the same speech take. Refusing it loses a
    /// synthesis whose provider spend is already booked and whose bytes are about to be written.
    /// </summary>
    [Theory]
    [InlineData("mp3", "audio/mpeg", "audio/mpeg")]
    [InlineData("mp3", "audio/mp3", "audio/mpeg")]
    [InlineData("mp3", "audio/x-mpeg", "audio/mpeg")]
    [InlineData("wav", "audio/x-wav", "audio/wav")]
    [InlineData("wav", "audio/wave", "audio/wav")]
    [InlineData("flac", "audio/x-flac", "audio/flac")]
    [InlineData("aac", "audio/x-aac", "audio/aac")]
    [InlineData("m4a", "audio/x-m4a", "audio/mp4")]
    [InlineData("ogg", "audio/opus", "audio/ogg")]
    [InlineData("ogg", "audio/vorbis", "audio/ogg")]
    [InlineData("opus", "audio/opus", "audio/ogg")]
    public async Task AliasOfTheAdvertisedContainerSavesUnderTheRegisteredProfile(
        string container, string declaredContentType, string expectedContentType)
    {
        var audio = SyntheticAudio(container);

        var result = await PublishAsync(container, declaredContentType, audio);
        var artifact = result.Outputs[0].FileArtifact!;

        // The saved file must clear the private storage boundary it is about to be written through.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(artifact.FileName, artifact.ContentType);
        GeneratedMediaSecurity.ValidateContent(descriptor, artifact.Content.Span, new FileOptions());

        Assert.Equal($".{container}", descriptor.Extension);
        Assert.Equal(expectedContentType, descriptor.ContentType);
        Assert.Equal(expectedContentType, artifact.ContentType);
        Assert.StartsWith("voice-", artifact.FileName, StringComparison.Ordinal);
        Assert.EndsWith($".{container}", artifact.FileName, StringComparison.Ordinal);

        // One media type for every record the download is assembled from.
        var metadata = JsonSerializer.Deserialize<VoiceOutputMetadata>(artifact.MetadataJson!)!;
        Assert.Equal(expectedContentType, metadata.ContentType);
        Assert.Equal(container, metadata.Format);
        Assert.Equal(metadata.ContentType, descriptor.ContentType);
        Assert.Equal(expectedContentType, JsonSerializer.Deserialize<VoiceOutputMetadata>(result.ResultJson)!.ContentType);
    }

    /// <summary>
    /// The served header is the registered profile, never the provider's decoration of it: casing,
    /// surrounding whitespace and media-type parameters must not reach the stored file record.
    /// </summary>
    [Theory]
    [InlineData("mp3", "AUDIO/MPEG")]
    [InlineData("mp3", "  audio/mpeg  ")]
    [InlineData("mp3", "audio/mpeg;q=0.9")]
    [InlineData("mp3", " audio/mpeg ; codecs=mp4a.40.2 ")]
    [InlineData("opus", "Audio/Ogg;codecs=opus")]
    [InlineData("ogg", "audio/ogg;codecs=vorbis")]
    public async Task ProviderHeaderFormattingNeverReachesTheServedMediaType(
        string container, string declaredContentType)
    {
        var expectedContentType = VoiceGenerationValues.SpeechContainers[container].PersistedContentType;

        var result = await PublishAsync(container, declaredContentType, SyntheticAudio(container));
        var artifact = result.Outputs[0].FileArtifact!;

        Assert.Equal(expectedContentType, artifact.ContentType);
        Assert.DoesNotContain(";", artifact.ContentType, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", artifact.ContentType, StringComparison.Ordinal);
        Assert.Equal(artifact.ContentType, artifact.ContentType.Trim().ToLowerInvariant());
        Assert.Equal(
            artifact.ContentType,
            GeneratedMediaSecurity.ValidateDescriptor(artifact.FileName, artifact.ContentType).ContentType);
    }

    /// <summary>
    /// A header that names a container other than the one being saved is refused by the quality gate,
    /// before the artifact is built, and not by the storage boundary afterwards.
    /// </summary>
    [Theory]
    [InlineData("mp3", "audio/wav")]
    [InlineData("mp3", "audio/webm")]
    [InlineData("mp3", "audio/opus")]
    [InlineData("wav", "audio/mpeg")]
    [InlineData("wav", "audio/flac")]
    [InlineData("opus", "audio/mpeg")]
    [InlineData("ogg", "audio/aac")]
    [InlineData("m4a", "audio/mpeg")]
    [InlineData("webm", "video/webm")]
    [InlineData("webm", "application/octet-stream")]
    [InlineData("mp3", "application/json")]
    [InlineData("mp3", "")]
    [InlineData("mp3", "   ")]
    public async Task HeaderNamingAnotherContainerFailsTheGateBeforeAnyOutputIsBuilt(
        string container, string declaredContentType)
    {
        var handler = Handler(container, declaredContentType, SyntheticAudio(container));

        await Assert.ThrowsAsync<VoiceOutputInvalidException>(
            () => handler.ExecuteAsync(Job(), new Progress<int>(), CancellationToken.None));
    }

    /// <summary>
    /// Every advertised speech container saves as itself: the download name's extension and the
    /// persisted media type stay a pair the storage registry accepts, for the whole advertised set.
    /// </summary>
    [Theory]
    [InlineData("aac")]
    [InlineData("flac")]
    [InlineData("m4a")]
    [InlineData("mp3")]
    [InlineData("ogg")]
    [InlineData("opus")]
    [InlineData("wav")]
    [InlineData("webm")]
    public async Task EveryAdvertisedContainerSavesUnderItsOwnSpeechProfile(string container)
    {
        var profile = VoiceGenerationValues.SpeechContainers[container];

        var result = await PublishAsync(container, profile.PersistedContentType, SyntheticAudio(container));
        var artifact = result.Outputs[0].FileArtifact!;
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(artifact.FileName, artifact.ContentType);

        Assert.Equal($".{container}", descriptor.Extension);
        Assert.Equal(profile.PersistedContentType, descriptor.ContentType);
        GeneratedMediaSecurity.ValidateContent(descriptor, artifact.Content.Span, new FileOptions());
    }

    /// <summary>
    /// The speech contract must not drift from the container registry the storage boundary enforces:
    /// every persisted profile is one that registry accepts for that very extension.
    /// </summary>
    [Fact]
    public void AdvertisedContainersPersistAProfileTheStorageBoundaryRegisters()
    {
        foreach (var (container, profile) in VoiceGenerationValues.SpeechContainers)
        {
            Assert.True(
                VoiceGenerationValues.Formats.Contains(container),
                $"'{container}' is a speech container profile but not an advertised voice format");
            var descriptor = GeneratedMediaSecurity.ValidateDescriptor($"voice-1.{container}", profile.PersistedContentType);
            Assert.Equal($".{container}", descriptor.Extension);
            Assert.Equal(profile.PersistedContentType, descriptor.ContentType);
        }

        Assert.Equal(VoiceGenerationValues.Formats.Count, VoiceGenerationValues.SpeechContainers.Count);
        foreach (var format in VoiceGenerationValues.Formats)
            Assert.True(VoiceGenerationValues.SpeechContainers.ContainsKey(format));
    }

    private static Task<GenerationHandlerResult> PublishAsync(string container, string contentType, byte[] audio) =>
        Handler(container, contentType, audio).ExecuteAsync(Job(), new Progress<int>(), CancellationToken.None);

    private static VoiceGenerationJobHandler Handler(string container, string contentType, byte[] audio) => new(
        [new FixtureVoiceProvider(container, contentType, audio)],
        Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fixture" }),
        NullLogger<VoiceGenerationJobHandler>.Instance);

    private static GenerationJob Job() => new()
    {
        Id = Guid.NewGuid(),
        JobType = GenerationJobTypes.VoiceGenerate,
        InputJson = VoiceGenerationContractMapper.SerializeInput(new VoiceGenerationInput(
            Guid.NewGuid(),
            null,
            "مرحبا بكم في تسليم",
            "ar",
            VoiceGenerationValues.Warm,
            VoiceGenerationValues.Clear,
            null,
            null)),
    };

    /// <summary>
    /// Bytes carrying the real magic header of the requested container, so the storage boundary sniffs
    /// an honest container rather than a buffer that never gets that far.
    /// </summary>
    private static byte[] SyntheticAudio(string container, int sizeBytes = 512)
    {
        var header = container switch
        {
            "mp3" => new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08 },
            "wav" => Concat("RIFF"u8.ToArray(), [0x00, 0x10, 0x00, 0x00], "WAVE"u8.ToArray()),
            "flac" => "fLaC"u8.ToArray(),
            "ogg" or "opus" => "OggS"u8.ToArray(),
            "aac" => [0xFF, 0xF1],
            "m4a" => Concat([0x00, 0x00, 0x00, 0x20], "ftyp"u8.ToArray(), "M4A "u8.ToArray()),
            "webm" => [0x1A, 0x45, 0xDF, 0xA3],
            _ => [0x00],
        };
        var bytes = new byte[Math.Max(sizeBytes, header.Length)];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var buffer = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }
        return buffer;
    }

    private sealed class FixtureVoiceProvider(string format, string contentType, byte[] audio) : IVoiceGenerationProvider
    {
        public string Key => "fixture";

        public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VoiceProviderResult(
                audio,
                contentType,
                format,
                1_800,
                24_000,
                new VoiceProviderUsage("fixture-voice-model", request.Text.Length, audio.Length, 0m, 12)));
    }
}
