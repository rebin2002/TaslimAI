using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests.ProviderTesting;

public sealed class FakeProviderConformanceTests
{
    [Fact]
    public async Task Representative_image_voice_music_and_movie_outputs_have_contract_shapes()
    {
        var catalog = new FakeProviderScenarioCatalog();
        var calls = new FakeProviderCallLog();
        var image = new FakeImageGenerationProvider(catalog, calls);
        var voice = new FakeVoiceGenerationProvider(catalog, calls);
        var music = new FakeMusicGenerationProvider(catalog, calls);
        var movie = new FakeMovieVideoProvider(catalog, calls);

        var imageResult = await image.GenerateAsync(
            new ImageGenerationInput("A test image", ImageGenerationValues.Auto, ImageGenerationValues.Square, ImageGenerationValues.Standard, null, null, null, null, null, null),
            new ImagePromptBuildResult("A test image", ImageGenerationValues.Auto, ImageGenerationValues.Square, ImageGenerationValues.Standard, null, null, null));
        var voiceResult = await voice.GenerateAsync(new VoiceGenerationInput(Guid.NewGuid(), null, "A test voice", "en", VoiceGenerationValues.Neutral, VoiceGenerationValues.Clear, null, null));
        var musicResult = await music.GenerateAsync(new MusicGenerationInput("A test track", "A contract test", "ambient", "calm", 30, MusicGenerationValues.Instrumental, "auto", null, null, null));
        var movieSubmission = await movie.SubmitAsync(new MovieVideoGenerationRequest(MovieStudioOperations.QuickMovie, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, "A test movie", 1, "16:9", "cinematic", "en", null, null, null, null), CancellationToken.None);
        var movieStatus = await movie.GetStatusAsync(movieSubmission.ProviderJobId, CancellationToken.None);
        var movieResult = await movie.RetrieveAsync(movieSubmission.ProviderJobId, movieStatus, CancellationToken.None);

        Assert.Equal("image/png", imageResult.ContentType);
        Assert.Equal("audio/mpeg", voiceResult.ContentType);
        Assert.Equal("mp3", musicResult.Format);
        Assert.Equal("video/mp4", movieResult.ContentType);
        Assert.Equal(4, calls.Calls(FakeProviderKind.Image) + calls.Calls(FakeProviderKind.Voice) + calls.Calls(FakeProviderKind.Music) + calls.Calls(FakeProviderKind.Movie));
    }

    [Theory]
    [InlineData(FakeProviderScenario.TransientFailure)]
    [InlineData(FakeProviderScenario.PermanentFailure)]
    [InlineData(FakeProviderScenario.RateLimit)]
    [InlineData(FakeProviderScenario.Timeout)]
    [InlineData(FakeProviderScenario.StaleWorker)]
    [InlineData(FakeProviderScenario.AllProvidersUnavailable)]
    public async Task Failure_scenarios_are_deterministic_and_normalized(FakeProviderScenario scenario)
    {
        var catalog = new FakeProviderScenarioCatalog { [FakeProviderKind.Voice] = scenario };
        var calls = new FakeProviderCallLog();
        var provider = new FakeVoiceGenerationProvider(catalog, calls);
        var request = new VoiceGenerationInput(Guid.NewGuid(), null, "Failure scenario", "en", VoiceGenerationValues.Neutral, VoiceGenerationValues.Clear, null, null);

        var exception = await Assert.ThrowsAsync<FakeProviderException>(() => provider.GenerateAsync(request));
        Assert.Equal(scenario, exception.Scenario);
        Assert.Equal(1, calls.Calls(FakeProviderKind.Voice));
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_delayed_completion_and_duplicate_result_are_observable_without_wall_clock_flakiness()
    {
        var catalog = new FakeProviderScenarioCatalog { [FakeProviderKind.Voice] = FakeProviderScenario.Cancellation };
        var calls = new FakeProviderCallLog();
        var provider = new FakeVoiceGenerationProvider(catalog, calls);
        var request = new VoiceGenerationInput(Guid.NewGuid(), null, "Cancel me", "en", VoiceGenerationValues.Neutral, VoiceGenerationValues.Clear, null, null);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(request, cancellation.Token));
        Assert.Equal(1, calls.Cancellations(FakeProviderKind.Voice));

        catalog[FakeProviderKind.Voice] = FakeProviderScenario.DuplicateCallbackOrResult;
        await provider.GenerateAsync(request);
        Assert.Equal(1, calls.DuplicateResults(FakeProviderKind.Voice));

        catalog[FakeProviderKind.Voice] = FakeProviderScenario.DelayedCompletion;
        var delayed = await provider.GenerateAsync(request);
        Assert.Equal("audio/mpeg", delayed.ContentType);
    }

    [Fact]
    public async Task Transient_retry_and_fallback_success_are_reusable_adapter_contracts()
    {
        var transientCatalog = new FakeProviderScenarioCatalog { [FakeProviderKind.Voice] = FakeProviderScenario.TransientFailure };
        var transientCalls = new FakeProviderCallLog();
        var transient = new FakeVoiceGenerationProvider(transientCatalog, transientCalls);
        var request = new VoiceGenerationInput(Guid.NewGuid(), null, "Retry me", "en", VoiceGenerationValues.Neutral, VoiceGenerationValues.Clear, null, null);

        await Assert.ThrowsAsync<FakeProviderException>(() => FakeProviderConformance.WithTransientRetriesAsync(
            token => transient.GenerateAsync(request, token), maxRetries: 2));
        Assert.Equal(3, transientCalls.Calls(FakeProviderKind.Voice));

        var unavailableCatalog = new FakeProviderScenarioCatalog { [FakeProviderKind.Voice] = FakeProviderScenario.AllProvidersUnavailable };
        var unavailable = new FakeVoiceGenerationProvider(unavailableCatalog, new FakeProviderCallLog());
        var fallback = await FakeProviderConformance.WithFallbackAsync(
            async () => (object)await unavailable.GenerateAsync(request),
            () => Task.FromResult<object>("fallback-output"));
        Assert.Equal("fallback-output", fallback);
    }

    [Fact]
    public void Configuration_validation_and_secret_sanitization_are_explicit()
    {
        Assert.Throws<FakeProviderConfigurationException>(() => new FakeProviderConfiguration("", "secret").Validate());
        Assert.Throws<FakeProviderConfigurationException>(() => new FakeProviderConfiguration("unconfigured", "secret").Validate());
        var configuration = new FakeProviderConfiguration("future-adapter", "test-secret");
        configuration.Validate();
        var safe = configuration.SanitizedDescription();
        Assert.Contains("secretConfigured=True", safe);
        FakeProviderConformance.AssertSecretSanitized(safe, "test-secret");
    }
}
