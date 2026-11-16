using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MoviePropBibleTests
{
    [Fact]
    public void Deterministic_detector_marks_a_prop_recurring_from_repeated_scene_usage()
    {
        var propId = Guid.NewGuid();
        var sceneOne = Guid.NewGuid();
        var sceneTwo = Guid.NewGuid();
        var result = new DeterministicMovieRecurringPropDetector().Detect(
            [new MoviePropDetectionInput(propId, "Red notebook")],
            [
                new MoviePropDetectionUsageInput(propId, sceneOne, Guid.NewGuid()),
                new MoviePropDetectionUsageInput(propId, sceneTwo, Guid.NewGuid()),
            ],
            [],
            DateTime.UnixEpoch);

        var candidate = Assert.Single(result.Candidates);
        Assert.True(result.ProviderFree);
        Assert.Equal(1, result.DetectorVersion);
        Assert.True(candidate.IsRecurring);
        Assert.Equal(2, candidate.SceneCount);
        Assert.Equal("The prop is present across multiple scenes.", candidate.Reason);
    }

    [Fact]
    public void Deterministic_detector_uses_bounded_shot_text_without_inventing_records()
    {
        var propId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var result = new DeterministicMovieRecurringPropDetector().Detect(
            [new MoviePropDetectionInput(propId, "Brass compass")],
            [],
            [
                new MoviePropDetectionShotInput(sceneId, shotId, "The brass compass rests on the crate."),
                new MoviePropDetectionShotInput(Guid.NewGuid(), Guid.NewGuid(), "The brass compass remains in frame."),
            ],
            DateTime.UnixEpoch);

        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.IsRecurring);
        Assert.Equal(2, candidate.UsageCount);
        Assert.Equal(2, candidate.SceneCount);
        Assert.True(result.ProviderFree);
    }

    [Fact]
    public void Detector_does_not_report_single_occurrence_as_recurring()
    {
        var result = new DeterministicMovieRecurringPropDetector().Detect(
            [new MoviePropDetectionInput(Guid.NewGuid(), "One-off cup")],
            [new MoviePropDetectionUsageInput(Guid.NewGuid(), Guid.NewGuid(), null)],
            [],
            DateTime.UnixEpoch);

        Assert.Empty(result.Candidates);
    }
}
