using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class VoiceGenerationUnitTests
{
    [Fact]
    public void Validator_accepts_supported_languages_and_rejects_unsupported_values()
    {
        var valid = Input(language: "ku");
        VoiceGenerationRequestValidator.Validate(valid, new VoiceGenerationOptions());

        var invalid = Input(language: "fr");
        var exception = Assert.Throws<VoiceRequestValidationException>(() => VoiceGenerationRequestValidator.Validate(invalid, new VoiceGenerationOptions()));
        Assert.Equal(GenerationJobErrorCodes.VoiceRequestInvalid, exception.Code);
    }

    [Fact]
    public async Task Disabled_voice_generation_fails_with_safe_unavailable_exception_without_fabricating_audio()
    {
        var handler = new VoiceGenerationJobHandler(
            [new FakeVoiceProvider()],
            Options.Create(new VoiceGenerationOptions { Enabled = false }),
            NullLogger<VoiceGenerationJobHandler>.Instance);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.VoiceGenerate,
            InputJson = VoiceGenerationContractMapper.SerializeInput(Input()),
        };

        await Assert.ThrowsAsync<VoiceProviderUnavailableException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task Handler_returns_private_audio_artifact_and_user_safe_result_metadata()
    {
        var handler = new VoiceGenerationJobHandler(
            [new FakeVoiceProvider()],
            Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fake" }),
            NullLogger<VoiceGenerationJobHandler>.Instance);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.VoiceGenerate,
            InputJson = VoiceGenerationContractMapper.SerializeInput(Input()),
        };

        var result = await handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);

        Assert.Single(result.Outputs);
        Assert.Equal(GenerationJobOutputTypes.StoredFile, result.Outputs[0].OutputType);
        Assert.NotNull(result.Outputs[0].FileArtifact);
        Assert.Equal("audio/mpeg", result.Outputs[0].FileArtifact!.ContentType);
        Assert.EndsWith(".mp3", result.Outputs[0].FileArtifact!.FileName);
        Assert.NotNull(result.Outputs[0].Asset);
        Assert.Equal(AssetTypes.Audio, result.Outputs[0].Asset!.AssetType);
        Assert.DoesNotContain("provider", result.ResultJson, StringComparison.OrdinalIgnoreCase);
        var metadata = JsonSerializer.Deserialize<VoiceOutputMetadata>(result.ResultJson);
        Assert.NotNull(metadata);
        Assert.Equal("ar", metadata!.Language);
        Assert.Equal(320L, metadata.SizeBytes);
    }

    private static VoiceGenerationInput Input(string language = "ar") => new(
        Guid.NewGuid(),
        null,
        "مرحبا بكم في تسليم",
        language,
        VoiceGenerationValues.Warm,
        VoiceGenerationValues.Clear,
        "Speak clearly.",
        null);

    private sealed class FakeVoiceProvider : IVoiceGenerationProvider
    {
        public string Key => "fake";

        public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VoiceProviderResult(
                new byte[320],
                "audio/mpeg",
                "mp3",
                1800,
                44100,
                new VoiceProviderUsage("fake-voice-model", request.Text.Length, 320, 0m, 12)));
    }
}
