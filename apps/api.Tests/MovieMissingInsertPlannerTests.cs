using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieMissingInsertPlannerTests
{
    [Fact]
    public void Builds_one_minimal_pending_insert_from_an_uncovered_edit_range()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sceneId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var storySceneId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var firstShotId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var secondShotId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var trackId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var firstItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var secondItemId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var guideId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var storyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var input = new MovieMissingInsertPlannerInput(
            projectId,
            new MovieMissingInsertTimelineSnapshot(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                4,
                MovieTimelineRevisionStatuses.Draft,
                5_000,
                [new MovieMissingInsertTimelineTrackSnapshot(
                    trackId,
                    1,
                    MovieTimelineTrackKinds.Video,
                    false,
                    [
                        new MovieMissingInsertTimelineItemSnapshot(firstItemId, MovieTimelineItemKinds.VisualTake, firstShotId, Guid.NewGuid(), 0, 2_000),
                        new MovieMissingInsertTimelineItemSnapshot(secondItemId, MovieTimelineItemKinds.VisualTake, secondShotId, Guid.NewGuid(), 3_000, 5_000),
                    ])]),
            new MovieMissingInsertStorySnapshot(storyId, 2, new Dictionary<Guid, Guid> { [sceneId] = storySceneId }),
            new MovieMissingInsertGuideSnapshot(guideId, 3),
            [
                Shot(firstShotId, sceneId, 1, "Wide harbor", subjects: null),
                Shot(secondShotId, sceneId, 2, "Mara looks toward the ferry", subjects: "Mara"),
            ],
            [new MovieMissingInsertProductionKitSnapshot(secondShotId, "kit-hash-2", 1, ["Mara"], [])]);

        var result = MovieMissingInsertPlanner.Build(input, DateTime.UnixEpoch);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(2_000, gap.TimelineInMilliseconds);
        Assert.Equal(3_000, gap.TimelineOutMilliseconds);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal(MovieMissingInsertProposalTypes.Reaction, proposal.InsertType);
        Assert.Equal(MovieMissingInsertApprovalStatuses.PendingApproval, proposal.ApprovalStatus);
        Assert.True(proposal.RequiresApproval);
        Assert.False(proposal.ChangesStoryCanon);
        Assert.True(result.RequiresApproval);
        Assert.False(result.CanonicalTimelineChanged);
        Assert.False(result.GenerationQueued);
        Assert.True(result.Grounding.IsComplete);
        Assert.Equal(storySceneId, proposal.Grounding.StorySceneId);
        Assert.Equal("kit-hash-2", proposal.Grounding.ProductionKitHash);
        Assert.Contains("Shot 2", proposal.Description);
    }

    [Fact]
    public void Withholds_proposals_when_story_guide_or_production_kit_grounding_is_missing()
    {
        var sceneId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var shotId = Guid.Parse("13131313-1313-1313-1313-131313131313");
        var input = new MovieMissingInsertPlannerInput(
            Guid.NewGuid(),
            new MovieMissingInsertTimelineSnapshot(
                Guid.NewGuid(),
                1,
                MovieTimelineRevisionStatuses.Draft,
                2_000,
                [new MovieMissingInsertTimelineTrackSnapshot(
                    Guid.NewGuid(),
                    1,
                    MovieTimelineTrackKinds.Video,
                    false,
                    [new MovieMissingInsertTimelineItemSnapshot(Guid.NewGuid(), MovieTimelineItemKinds.VisualTake, shotId, Guid.NewGuid(), 0, 1_000)])]),
            null,
            null,
            [Shot(shotId, sceneId, 1, "A quiet room", subjects: null)],
            []);

        var result = MovieMissingInsertPlanner.Build(input, DateTime.UnixEpoch);

        Assert.Single(result.Gaps);
        Assert.Empty(result.Proposals);
        Assert.False(result.RequiresApproval);
        Assert.False(result.Grounding.IsComplete);
        Assert.Contains(result.Warnings, item => item.Code == "approved_story_missing");
        Assert.Contains(result.Warnings, item => item.Code == "locked_guide_missing");
        Assert.Contains(result.Warnings, item => item.Code == "production_kit_missing");
    }

    [Fact]
    public void Explicit_gap_is_not_double_counted_and_location_change_is_review_only()
    {
        var sceneId = Guid.NewGuid();
        var beforeId = Guid.NewGuid();
        var afterId = Guid.NewGuid();
        var input = new MovieMissingInsertPlannerInput(
            Guid.NewGuid(),
            new MovieMissingInsertTimelineSnapshot(
                Guid.NewGuid(),
                1,
                MovieTimelineRevisionStatuses.Draft,
                4_000,
                [new MovieMissingInsertTimelineTrackSnapshot(
                    Guid.NewGuid(),
                    1,
                    MovieTimelineTrackKinds.Video,
                    false,
                    [
                        new MovieMissingInsertTimelineItemSnapshot(Guid.NewGuid(), MovieTimelineItemKinds.VisualTake, beforeId, Guid.NewGuid(), 0, 1_000),
                        new MovieMissingInsertTimelineItemSnapshot(Guid.NewGuid(), MovieTimelineItemKinds.Gap, null, null, 1_000, 2_000),
                        new MovieMissingInsertTimelineItemSnapshot(Guid.NewGuid(), MovieTimelineItemKinds.VisualTake, afterId, Guid.NewGuid(), 2_000, 4_000),
                    ])]),
            new MovieMissingInsertStorySnapshot(Guid.NewGuid(), 1, new Dictionary<Guid, Guid> { [sceneId] = Guid.NewGuid() }),
            new MovieMissingInsertGuideSnapshot(Guid.NewGuid(), 1),
            [
                Shot(beforeId, sceneId, 1, "Scene", subjects: null, locationSet: "Dock"),
                Shot(afterId, sceneId, 2, "Scene", subjects: null, locationSet: "Warehouse"),
            ],
            [new MovieMissingInsertProductionKitSnapshot(afterId, "hash", 1, [], [])]);

        var result = MovieMissingInsertPlanner.Build(input, DateTime.UnixEpoch);

        Assert.Single(result.Gaps);
        Assert.Equal(1_000, result.Gaps[0].TimelineInMilliseconds);
        Assert.Equal(2_000, result.Gaps[0].TimelineOutMilliseconds);
        var finding = Assert.Single(result.ContinuityFindings);
        Assert.Equal("continuity_location_boundary", finding.Code);
        Assert.Equal("review", finding.Severity);
    }

    [Fact]
    public void Proposal_id_is_stable_for_the_same_canonical_inputs()
    {
        var shotId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var input = new MovieMissingInsertPlannerInput(
            Guid.NewGuid(),
            new MovieMissingInsertTimelineSnapshot(
                Guid.NewGuid(),
                1,
                MovieTimelineRevisionStatuses.Draft,
                2_000,
                [new MovieMissingInsertTimelineTrackSnapshot(
                    Guid.NewGuid(),
                    1,
                    MovieTimelineTrackKinds.Video,
                    false,
                    [new MovieMissingInsertTimelineItemSnapshot(Guid.NewGuid(), MovieTimelineItemKinds.VisualTake, shotId, Guid.NewGuid(), 500, 1_500)])]),
            new MovieMissingInsertStorySnapshot(Guid.NewGuid(), 1, new Dictionary<Guid, Guid> { [sceneId] = Guid.NewGuid() }),
            new MovieMissingInsertGuideSnapshot(Guid.NewGuid(), 1),
            [Shot(shotId, sceneId, 1, "Room", subjects: null)],
            [new MovieMissingInsertProductionKitSnapshot(shotId, "stable", 1, [], [])]);

        var first = MovieMissingInsertPlanner.Build(input, DateTime.UnixEpoch);
        var second = MovieMissingInsertPlanner.Build(input, DateTime.UnixEpoch);

        Assert.Equal(first.Gaps.Select(item => item.Id), second.Gaps.Select(item => item.Id));
        Assert.Equal(first.Proposals.Select(item => item.Id), second.Proposals.Select(item => item.Id));
    }

    private static MovieMissingInsertShotSnapshot Shot(
        Guid id,
        Guid sceneId,
        int sequence,
        string description,
        string? subjects,
        string? locationSet = null) =>
        new(
            id,
            sceneId,
            1,
            "Scene",
            sequence,
            description,
            null,
            subjects,
            subjects is null ? [] : [Guid.NewGuid()],
            locationSet,
            "Use the existing production plan.",
            "Preserve existing continuity.",
            "Medium shot",
            "Static",
            null,
            "Keep the established look.",
            Guid.NewGuid());
}
