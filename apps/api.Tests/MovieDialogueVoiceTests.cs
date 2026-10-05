using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueVoiceTests
{
    [Fact]
    public async Task Default_adapter_is_disabled_and_makes_no_external_call()
    {
        var provider = new UnavailableMovieDialogueVoiceProvider();
        var request = new MovieDialogueVoiceProviderRequest(Guid.NewGuid(), Guid.NewGuid(), null, "Narrator", "Hello", MovieDialogueLanguages.English, 0, 1_000, null);

        await Assert.ThrowsAsync<MovieDialogueVoiceProviderUnavailableException>(() => provider.GenerateAsync(request));
    }

    [Fact]
    public async Task Deterministic_adapter_returns_zero_cost_audio_for_execution_tests()
    {
        var provider = new DeterministicMovieDialogueVoiceProvider();
        var request = new MovieDialogueVoiceProviderRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Mara", "Hello", MovieDialogueLanguages.Arabic, 250, 1_250, "Calm");

        var result = await provider.GenerateAsync(request);

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("mp3", result.Format);
        Assert.Equal(1_000, result.DurationMilliseconds);
        Assert.Equal(0m, result.Usage.ActualCostUsd);
        Assert.Equal(request.Text.Length, result.Usage.InputCharacters);
    }

    [Fact]
    public async Task Handler_keeps_voice_execution_disabled_by_default()
    {
        var handler = new MovieDialogueVoiceGenerationJobHandler(
            [new UnavailableMovieDialogueVoiceProvider()],
            Options.Create(new MovieDialogueVoiceOptions()),
            NullLogger<MovieDialogueVoiceGenerationJobHandler>.Instance);
        var input = new MovieDialogueVoiceInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Mara", "Hello", MovieDialogueLanguages.English, 0, 1_000, null, Guid.NewGuid());
        var job = new GenerationJob { Id = Guid.NewGuid(), JobType = GenerationJobTypes.MovieDialogueVoiceGenerate, InputJson = System.Text.Json.JsonSerializer.Serialize(input) };

        await Assert.ThrowsAsync<MovieDialogueVoiceProviderUnavailableException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public void Take_selection_requires_approval_and_published_assets_are_explicit()
    {
        Assert.False(MovieDialogueTakeLifecycle.CanSelect(MovieDialogueTakeStatuses.Succeeded));
        Assert.True(MovieDialogueTakeLifecycle.CanSelect(MovieDialogueTakeStatuses.Approved));
        Assert.False(MovieDialogueTakeLifecycle.HasPublishedAsset(Guid.NewGuid(), null));
        Assert.True(MovieDialogueTakeLifecycle.HasPublishedAsset(Guid.NewGuid(), Guid.NewGuid()));
    }
}
