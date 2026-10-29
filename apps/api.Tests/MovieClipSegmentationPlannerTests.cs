using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieClipSegmentationPlannerTests
{
    [Fact]
    public void Two_hour_feature_plan_uses_720_verified_ten_second_clips_without_losing_runtime()
    {
        Assert.True(MovieClipSegmentationPlanner.TryPlan(
            7_200, new MovieClipDurationRange(2, 10), out var segments));

        Assert.Equal(720, segments.Count);
        Assert.Equal(7_200, segments.Sum(segment => segment.DurationSeconds));
        Assert.Equal(new MoviePlannedClipSegment(1, 0, 10), segments[0]);
        Assert.Equal(new MoviePlannedClipSegment(720, 7_190, 10), segments[^1]);
        Assert.All(segments, segment => Assert.InRange(segment.DurationSeconds, 2, 10));
        for (var index = 1; index < segments.Count; index++)
        {
            Assert.Equal(index + 1, segments[index].Sequence);
            Assert.Equal(segments[index - 1].StartSeconds
                + segments[index - 1].DurationSeconds, segments[index].StartSeconds);
        }
    }

    [Theory]
    [InlineData(11, 2, 10, 2)]
    [InlineData(19, 2, 10, 2)]
    [InlineData(21, 2, 10, 3)]
    [InlineData(12, 6, 10, 2)]
    [InlineData(30, 1, 30, 1)]
    public void Uneven_runtime_is_redistributed_without_an_unsupported_short_tail(
        int totalSeconds, int minimumSeconds, int maximumSeconds, int expectedCount)
    {
        var range = new MovieClipDurationRange(minimumSeconds, maximumSeconds);
        Assert.True(MovieClipSegmentationPlanner.TryPlan(totalSeconds, range, out var segments));

        Assert.Equal(expectedCount, segments.Count);
        Assert.Equal(totalSeconds, segments.Sum(segment => segment.DurationSeconds));
        Assert.All(segments, segment => Assert.True(range.Supports(segment.DurationSeconds)));
        Assert.Equal(totalSeconds, segments[^1].StartSeconds + segments[^1].DurationSeconds);
    }

    [Theory]
    [InlineData(0, 2, 10)]
    [InlineData(-1, 2, 10)]
    [InlineData(7_201, 2, 10)]
    [InlineData(11, 6, 10)]
    [InlineData(1, 2, 10)]
    [InlineData(30, 0, 10)]
    [InlineData(30, 10, 2)]
    public void Invalid_or_unrepresentable_duration_fails_closed_with_no_partial_plan(
        int totalSeconds, int minimumSeconds, int maximumSeconds)
    {
        Assert.False(MovieClipSegmentationPlanner.TryPlan(
            totalSeconds, new MovieClipDurationRange(minimumSeconds, maximumSeconds), out var segments));
        Assert.Empty(segments);
    }

    [Fact]
    public void Unknown_provider_duration_capability_fails_closed()
    {
        Assert.False(MovieClipSegmentationPlanner.TryPlan(7_200, null, out var segments));
        Assert.Empty(segments);
    }
}
