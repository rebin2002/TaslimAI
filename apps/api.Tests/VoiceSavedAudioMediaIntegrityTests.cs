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
/// Saved-audio integrity for Voice Studio. The media type and content length that the durable
/// job records must be exactly the ones the private storage boundary accepts and persists, for
/// every output container the Voice contract advertises. Every audio payload here is a synthetic
/// fixture carrying the real container magic bytes, so no speech provider is contacted or enabled.
/// </summary>
public sealed class VoiceSavedAudioMediaIntegrityTests
{
    [Theory]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("wav", "audio/wav")]
    [InlineData("flac", "audio/flac")]
    [InlineData("ogg", "audio/ogg")]
    [InlineData("opus", "audio/ogg")]
    [InlineData("aac", "audio/aac")]
    [InlineData("m4a", "audio/mp4")]
    [InlineData("webm", "audio/webm")]
    public async Task EveryAdvertisedVoiceContainerSavesWithTheMediaTypeTheJobRecords(string format, string contentType)
    {
        var audio = SyntheticAudio(format);
        var result = await PublishAsync(format, contentType, audio);
        var artifact = result.Outputs[0].FileArtifact!;

        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(artifact.FileName, artifact.ContentType);
        GeneratedMediaSecurity.ValidateContent(descriptor, artifact.Content.Span, new FileOptions());

        Assert.StartsWith("voice-", artifact.FileName, StringComparison.Ordinal);
        Assert.EndsWith($".{format}", artifact.FileName, StringComparison.Ordinal);
        Assert.Equal($".{format}", descriptor.Extension);
        Assert.Equal(contentType, descriptor.ContentType);

        var metadata = JsonSerializer.Deserialize<VoiceOutputMetadata>(artifact.MetadataJson!)!;
        Assert.Equal(AssetTypes.Audio, metadata.AssetType);
        Assert.Equal(format, metadata.Format);
        Assert.Equal(descriptor.ContentType, metadata.ContentType);
        Assert.Equal((long)audio.Length, metadata.SizeBytes);
        Assert.Equal(artifact.Content.Length, metadata.SizeBytes);
        // The user-facing job result and the saved file must describe the same media.
        Assert.Equal(metadata.ContentType, JsonSerializer.Deserialize<VoiceOutputMetadata>(result.ResultJson)!.ContentType);
    }

    [Fact]
    public async Task WebmOpusSpeechIsSavableAsAudioRatherThanRefusedAsVideo()
    {
        var audio = SyntheticAudio("webm");
        var result = await PublishAsync("webm", "audio/webm", audio);
        var artifact = result.Outputs[0].FileArtifact!;

        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(artifact.FileName, artifact.ContentType);

        Assert.Equal(".webm", descriptor.Extension);
        Assert.Equal("audio/webm", descriptor.ContentType);
        Assert.Equal("audio/webm", artifact.ContentType);
        Assert.Equal(audio.Length, artifact.Content.Length);
    }

    [Fact]
    public void GeneratedVideoStillDeclaresWebmAsVideo()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("assembly-1.webm", "video/webm");

        Assert.Equal(".webm", descriptor.Extension);
        Assert.Equal("video/webm", descriptor.ContentType);
    }

    [Theory]
    [InlineData("assembly-1.webm", "audio/mpeg")]
    [InlineData("assembly-1.webm", "video/mp4")]
    [InlineData("voice-1.webm", "audio/opus")]
    [InlineData("voice-1.mp3", "audio/webm")]
    [InlineData("voice-1.opus", "audio/opus")]
    [InlineData("clip-1.mp4", "audio/webm")]
    public void ContainerStillRefusesEveryMediaTypeItDoesNotDeclare(string fileName, string contentType)
    {
        var exception = Assert.Throws<FileUploadValidationException>(
            () => GeneratedMediaSecurity.ValidateDescriptor(fileName, contentType));

        Assert.Equal("Generated file content type does not match its extension.", exception.Message);
    }

    [Theory]
    [InlineData("voice-1.webm", "audio/webm;q=0.9")]
    [InlineData("voice-1.webm", "audio")]
    [InlineData("voice-1.webm", " audio/webm extra")]
    [InlineData("voice-1.mp3", "")]
    [InlineData("voice-1.mp3", "   ")]
    public void ContainerRegistryOnlyAcceptsAWholeProfileName(string fileName, string contentType)
    {
        Assert.Throws<FileUploadValidationException>(() => GeneratedMediaSecurity.ValidateDescriptor(fileName, contentType));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(4_096)]
    [InlineData(320_000)]
    public async Task SavedAudioLengthIsTheExactByteCountThatWasValidated(int sizeBytes)
    {
        var audio = SyntheticAudio("mp3", sizeBytes);
        var result = await PublishAsync("mp3", "audio/mpeg", audio);
        var artifact = result.Outputs[0].FileArtifact!;

        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(artifact.FileName, artifact.ContentType);
        GeneratedMediaSecurity.ValidateContent(descriptor, artifact.Content.Span, new FileOptions());

        Assert.Equal(sizeBytes, artifact.Content.Length);
        Assert.Equal((long)sizeBytes, JsonSerializer.Deserialize<VoiceOutputMetadata>(artifact.MetadataJson!)!.SizeBytes);
    }

    [Theory]
    [InlineData("", "audio/mpeg")]
    [InlineData("mkv", "audio/mpeg")]
    [InlineData("mp3", "application/octet-stream")]
    [InlineData("mp3", "video/webm")]
    [InlineData("mp3", "")]
    public async Task OutputWhoseProfileTheStorageBoundaryCannotDescribeFailsBeforeAnyAssetIsRecorded(string format, string contentType)
    {
        var handler = Handler(format, contentType, SyntheticAudio("mp3"));

        await Assert.ThrowsAsync<VoiceOutputInvalidException>(
            () => handler.ExecuteAsync(Job(), new Progress<int>(), CancellationToken.None));
    }

    private static async Task<GenerationHandlerResult> PublishAsync(string format, string contentType, byte[] audio)
    {
        var handler = Handler(format, contentType, audio);
        return await handler.ExecuteAsync(Job(), new Progress<int>(), CancellationToken.None);
    }

    private static VoiceGenerationJobHandler Handler(string format, string contentType, byte[] audio) => new(
        [new FixtureVoiceProvider(format, contentType, audio)],
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
    /// Builds bytes carrying the real magic header of the requested container so the storage
    /// boundary sniffs an honest container, rather than an all-zero buffer that never gets that far.
    /// </summary>
    private static byte[] SyntheticAudio(string format, int sizeBytes = 512)
    {
        var header = format switch
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
