using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// A feature-length project's timeline must never be silently substituted
/// for (or truncated into) the duration of one potentially billable clip.
/// These are pure, provider-free admission tests.
/// </summary>
public sealed class MovieProductionClipDurationResolutionTests
{
    [Theory]
    [InlineData(5, 120, 7200, 5)]
    [InlineData(null, 10, 7200, 10)]
    [InlineData(null, null, 7200, 60)]
    [InlineData(3600, 7200, 7200, 3600)]
    public void Production_render_preserves_selected_shot_or_scene_duration(
        int? shotSeconds, int? sceneSeconds, int movieSeconds, int expected)
    {
        Assert.Equal(expected,
            MovieProductionGenerationOrchestrator.ResolveSingleClipDuration(
                shotSeconds, sceneSeconds, movieSeconds));
    }

    [Theory]
    [InlineData(0, null, 7200)]
    [InlineData(7200, 120, 7200)]
    [InlineData(3601, 60, 7200)]
    [InlineData(null, 7200, 7200)]
    [InlineData(null, 0, 7200)]
    public void Production_render_rejects_invalid_single_clip_duration_instead_of_clamping(
        int? shotSeconds, int? sceneSeconds, int movieSeconds)
    {
        var error = Assert.Throws<MovieProductionValidationException>(() =>
            MovieProductionGenerationOrchestrator.ResolveSingleClipDuration(
                shotSeconds, sceneSeconds, movieSeconds));
        Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", error.Code);
        Assert.Contains("shorter shots", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
