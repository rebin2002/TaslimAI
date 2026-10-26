using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class VoiceGenerationFormatConsistencyTests
{
    [Fact]
    public async Task Mismatched_audio_content_type_and_format_is_rejected_before_publication()
    {
        var handler = new VoiceGenerationJobHandler(
            [new MismatchedVoiceProvider()],
            Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "mismatch" }),
            NullLogger<VoiceGenerationJobHandler>.Instance);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.VoiceGenerate,
            InputJson = VoiceGenerationContractMapper.SerializeInput(new VoiceGenerationInput(
                Guid.NewGuid(), null, "A short test narration", "en", "neutral", "clear", null, null)),
        };

        await Assert.ThrowsAsync<VoiceOutputInvalidException>(() =>
            handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));
    }

    private sealed class MismatchedVoiceProvider : IVoiceGenerationProvider
    {
        public string Key => "mismatch";

        public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VoiceProviderResult(
                "ID3"u8.ToArray(),
                "audio/mpeg",
                "wav",
                1000,
                24000,
                new VoiceProviderUsage("test", request.Text.Length, 3, 0m, 1)));
    }
}
