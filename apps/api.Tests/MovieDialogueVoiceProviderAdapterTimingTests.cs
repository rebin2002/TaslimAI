using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueVoiceProviderAdapterTimingTests
{
    [Fact]
    public async Task Rejects_provider_audio_that_exceeds_the_authored_dialogue_window()
    {
        var adapter = new MovieDialogueVoiceProviderAdapter(
            new FixedDurationVoiceProvider(3_000),
            Options.Create(new MovieDialogueVoiceOptions { Enabled = true, ProviderKey = "fake", MaxDurationMilliseconds = 10_000 }),
            Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fake", Model = "voice-test-model" }));

        var request = new MovieDialogueVoiceProviderRequest(
            Guid.NewGuid(), Guid.NewGuid(), null, "Narrator", "Hello from Movie Studio.",
            MovieDialogueLanguages.English, 0, 2_000, null, Guid.NewGuid());

        await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(() => adapter.GenerateAsync(request));
    }

    private sealed class FixedDurationVoiceProvider(int durationMilliseconds) : IVoiceGenerationProvider
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
