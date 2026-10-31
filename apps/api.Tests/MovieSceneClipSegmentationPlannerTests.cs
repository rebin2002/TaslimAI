using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSceneClipSegmentationPlannerTests
{
    [Fact]
    public void Two_hour_film_is_split_without_crossing_explicit_scene_boundaries()
    {
        var opening = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var ending = Guid.NewGuid();
        // Deliberately unordered source data: persisted scene sequence is authoritative.
        var scenes = new[]
        {
            new MoviePlannedSceneDuration(ending, 3, 2_400),
            new MoviePlannedSceneDuration(opening, 1, 1_200),
            new MoviePlannedSceneDuration(middle, 2, 3_600),
        };

        Assert.True(MovieSceneClipSegmentationPlanner.TryPlan(
            7_200, scenes, new MovieClipDurationRange(2, 10), out var slots));

        Assert.Equal(720, slots.Count);
        Assert.Equal(7_200, slots.Sum(slot => slot.DurationSeconds));
        Assert.Equal(new MoviePlannedSceneClipSegment(opening, 1, 1, 0, 0, 10), slots[0]);
        Assert.Equal(new MoviePlannedSceneClipSegment(middle, 2, 121, 1_200, 0, 10), slots[120]);
        Assert.Equal(new MoviePlannedSceneClipSegment(ending, 3, 481, 4_800, 0, 10), slots[480]);
        Assert.Equal(new MoviePlannedSceneClipSegment(ending, 3, 720, 7_190, 2_390, 10), slots[^1]);
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.Equal(i + 1, slots[i].ClipSequence);
            Assert.Equal(slots[i - 1].TimelineStartSeconds + slots[i - 1].DurationSeconds,
                slots[i].TimelineStartSeconds);
        }
        Assert.All(slots, slot => Assert.InRange(slot.DurationSeconds, 2, 10));
    }

    [Fact]
    public void Short_tail_is_rebalanced_only_within_its_own_scene()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True(MovieSceneClipSegmentationPlanner.TryPlan(
            22,
            [new(first, 1, 11), new(second, 2, 11)],
            new MovieClipDurationRange(2, 10),
            out var slots));

        Assert.Equal(4, slots.Count);
        Assert.Equal(new[] { 6, 5, 6, 5 }, slots.Select(slot => slot.DurationSeconds).ToArray());
        Assert.Equal(new[] { 0, 6, 11, 17 }, slots.Select(slot => slot.TimelineStartSeconds).ToArray());
        Assert.Equal(new[] { 0, 6, 0, 6 }, slots.Select(slot => slot.SceneOffsetSeconds).ToArray());
    }

    [Fact]
    public void Unrepresentable_scene_fails_closed_even_when_film_duration_is_splittable()
    {
        // 22 seconds can be covered by 6..10-second clips, but the first
        // 11-second scene cannot. Borrowing seconds across a scene is forbidden.
        Assert.True(MovieClipSegmentationPlanner.TryPlan(
            22, new MovieClipDurationRange(6, 10), out _));
        Assert.False(MovieSceneClipSegmentationPlanner.TryPlan(
            22,
            [new(Guid.NewGuid(), 1, 11), new(Guid.NewGuid(), 2, 11)],
            new MovieClipDurationRange(6, 10),
            out var slots));
        Assert.Empty(slots);
    }

    [Fact]
    public void A_later_failed_scene_discards_the_entire_prepared_preview()
    {
        Assert.False(MovieSceneClipSegmentationPlanner.TryPlan(
            16,
            [new(Guid.NewGuid(), 1, 10), new(Guid.NewGuid(), 2, 6)],
            new MovieClipDurationRange(7, 10),
            out var slots));
        Assert.Empty(slots);
    }

    [Theory]
    [InlineData(7_201)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_film_duration_fails_without_partial_output(int duration)
    {
        Assert.False(MovieSceneClipSegmentationPlanner.TryPlan(
            duration, [new(Guid.NewGuid(), 1, 10)], new MovieClipDurationRange(2, 10),
            out var slots));
        Assert.Empty(slots);
    }

    [Fact]
    public void Missing_provider_capability_or_scene_plan_fails_closed()
    {
        var scene = new MoviePlannedSceneDuration(Guid.NewGuid(), 1, 10);
        Assert.False(MovieSceneClipSegmentationPlanner.TryPlan(
            10, [scene], null, out var unknownCapability));
        Assert.False(MovieSceneClipSegmentationPlanner.TryPlan(
            10, [], new MovieClipDurationRange(2, 10), out var missingScenes));
        Assert.Empty(unknownCapability);
        Assert.Empty(missingScenes);
    }

    [Fact]
    public void Duplicate_identity_or_order_and_runtime_mismatch_are_rejected()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var range = new MovieClipDurationRange(2, 10);
        var invalidPlans = new IReadOnlyList<MoviePlannedSceneDuration>[]
        {
            [new(firstId, 1, 10), new(firstId, 2, 10)],
            [new(firstId, 1, 10), new(secondId, 1, 10)],
            [new(Guid.Empty, 1, 10), new(secondId, 2, 10)],
            [new(firstId, 0, 10), new(secondId, 2, 10)],
            [new(firstId, 1, 0), new(secondId, 2, 20)],
            [new(firstId, 1, 10), new(secondId, 2, 9)],
            [new(firstId, 1, 11), new(secondId, 2, 10)],
        };
        foreach (var scenes in invalidPlans)
        {
            Assert.False(MovieSceneClipSegmentationPlanner.TryPlan(
                20, scenes, range, out var slots));
            Assert.Empty(slots);
        }
    }
}
