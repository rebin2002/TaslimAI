using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class VoiceGenerationFormatConsistencyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Null_or_blank_provider_format_is_rejected_with_the_typed_exception(string? format)
    {
        var provider = new InvalidVoiceFormatProvider(format);
        var handler = new VoiceGenerationJobHandler(
            [provider],
            Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "invalid-format" }),
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
        Assert.Equal(1, provider.Calls);
    }

    private sealed class InvalidVoiceFormatProvider(string? format) : IVoiceGenerationProvider
    {
        public string Key => "invalid-format";

        public int Calls { get; private set; }

        public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new VoiceProviderResult(
                "ID3"u8.ToArray(),
                "audio/mpeg",
                format!,
                1000,
                24000,
                new VoiceProviderUsage("test", request.Text.Length, 3, 0m, 1)));
        }
    }
}
