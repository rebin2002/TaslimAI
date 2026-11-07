using Taslim.Api.Movies;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The approved-production preflight and the eventual queue must use the same
/// provider-owned single-clip decision. These tests never submit provider work,
/// persist jobs, or reserve customer usage.
/// </summary>
public sealed class MovieProductionAdmissionDecisionTests
{
    private static FakeMovieVideoProvider CreateProvider(
        FakeProviderScenarioCatalog scenarios,
        FakeProviderCallLog calls,
        MovieClipDurationRange? durationRange = null) =>
        new(scenarios, calls)
        {
            SceneClipDurationRange = durationRange ?? new MovieClipDurationRange(2, 10),
        };

    private static MovieProductionValidationException? Decide(
        FakeMovieVideoProvider provider,
        int durationSeconds = 5) =>
        MovieProductionGenerationOrchestrator.EvaluateProductionClipAdmission(
            provider,
            durationSeconds,
            "720p",
            "720p",
            "Standard",
            "native",
            "16:9");

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public void Supported_clip_boundaries_pass_without_provider_submission(int durationSeconds)
    {
        var scenarios = new FakeProviderScenarioCatalog();
        var calls = new FakeProviderCallLog();
        var provider = CreateProvider(scenarios, calls);

        Assert.Null(Decide(provider, durationSeconds));
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(60)]
    [InlineData(7_200)]
    public void Out_of_range_single_clips_fail_even_inside_a_two_hour_movie(int durationSeconds)
    {
        var scenarios = new FakeProviderScenarioCatalog();
        var calls = new FakeProviderCallLog();
        var provider = CreateProvider(scenarios, calls);

        var rejection = Decide(provider, durationSeconds);

        Assert.NotNull(rejection);
        Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", rejection.Code);
        Assert.Contains("shorter shots", rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Unknown_provider_clip_range_fails_closed()
    {
        var scenarios = new FakeProviderScenarioCatalog();
        var calls = new FakeProviderCallLog();
        var provider = CreateProvider(scenarios, calls);
        provider.SceneClipDurationRange = null;

        var rejection = Decide(provider);

        Assert.NotNull(rejection);
        Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", rejection.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Unavailable_provider_is_rejected_before_other_capability_checks()
    {
        var scenarios = new FakeProviderScenarioCatalog
        {
            [FakeProviderKind.Movie] = FakeProviderScenario.AllProvidersUnavailable,
        };
        var calls = new FakeProviderCallLog();
        var provider = CreateProvider(scenarios, calls);
        provider.SceneClipCostIntentSupported = false;
        provider.SceneClipDurationRange = null;

        var rejection = Decide(provider, 7_200);

        Assert.NotNull(rejection);
        Assert.Equal("MOVIE_PROVIDER_UNAVAILABLE", rejection.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Unsupported_priced_output_profile_fails_closed_on_supported_duration()
    {
        var scenarios = new FakeProviderScenarioCatalog();
        var calls = new FakeProviderCallLog();
        var provider = CreateProvider(scenarios, calls);
        provider.SceneClipCostIntentSupported = false;

        var rejection = Decide(provider, 5);

        Assert.NotNull(rejection);
        Assert.Equal("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED", rejection.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Provider_without_scene_clip_operation_is_rejected_even_when_available(bool quickMovieOnly)
    {
        var calls = new FakeProviderCallLog();
        var provider = CreateProvider(new FakeProviderScenarioCatalog(), calls);
        provider.SupportedOperations = quickMovieOnly
            ? new[] { MovieStudioOperations.QuickMovie }
            : Array.Empty<string>();

        var rejection = Decide(provider);

        Assert.NotNull(rejection);
        Assert.Equal("MOVIE_PROVIDER_UNAVAILABLE", rejection.Code);
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Scene_clip_operation_admission_is_case_insensitive()
    {
        var calls = new FakeProviderCallLog();
        var provider = CreateProvider(new FakeProviderScenarioCatalog(), calls);
        provider.SupportedOperations = [MovieStudioOperations.SceneClip.ToUpperInvariant()];

        Assert.Null(Decide(provider));
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

}
