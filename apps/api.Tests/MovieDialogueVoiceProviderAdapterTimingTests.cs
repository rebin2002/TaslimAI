using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueVoiceProviderAdapterTimingTests
{
    [Theory]
    [InlineData(2_001)]
    [InlineData(3_000)]
    public async Task Rejects_provider_audio_that_exceeds_the_authored_dialogue_window(int reportedDuration)
    {
        var adapter = CreateAdapter(reportedDuration);
        await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(() => adapter.GenerateAsync(CreateRequest()));
    }

    [Theory]
    [InlineData(1_999, 1_999)]
    [InlineData(2_000, 2_000)]
    [InlineData(null, 2_000)]
    [InlineData(0, 2_000)]
    [InlineData(-1, 2_000)]
    public async Task Preserves_in_window_duration_or_falls_back_to_the_authored_window(int? reportedDuration, int expectedDuration)
    {
        var result = await CreateAdapter(reportedDuration).GenerateAsync(CreateRequest());
        Assert.Equal(expectedDuration, result.DurationMilliseconds);
    }

    private static MovieDialogueVoiceProviderAdapter CreateAdapter(int? reportedDuration) => new(
        new FixedDurationVoiceProvider(reportedDuration),
        Options.Create(new MovieDialogueVoiceOptions { Enabled = true, ProviderKey = "fake", MaxDurationMilliseconds = 10_000 }),
        Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fake", Model = "voice-test-model" }));

    private static MovieDialogueVoiceProviderRequest CreateRequest() => new(
        Guid.NewGuid(), Guid.NewGuid(), null, "Narrator", "Hello from Movie Studio.",
        MovieDialogueLanguages.English, 1_000, 3_000, null, Guid.NewGuid());

    private sealed class FixedDurationVoiceProvider(int? durationMilliseconds) : IVoiceGenerationProvider
    {
        public string Key => "fake";

        public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VoiceProviderResult(
                new byte[] { 0x49, 0x44, 0x33, 0x04 },
                "audio/mpeg",
                "mp3",
                durationMilliseconds,
                null,
                new VoiceProviderUsage("voice-test-model", request.Text.Length, 4, null, 0)));
        }
    }
}
