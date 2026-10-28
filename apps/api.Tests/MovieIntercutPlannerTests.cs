using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieIntercutPlannerTests
{
    private static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SceneOne = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SceneTwo = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid ShotOne = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid ShotTwo = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid ShotThree = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid PrimaryOne = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid PrimaryTwo = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid Reaction = Guid.Parse("40000000-0000-0000-0000-000000000003");
    private static readonly Guid Insert = Guid.Parse("40000000-0000-0000-0000-000000000004");

    [Fact]
    public void Planner_intercuts_reaction_and_insert_coverage_without_reordering_primary_story_beats()
    {
        var request = new MovieIntercutPlanRequest
        {
            TimelineId = Guid.Parse("50000000-0000-0000-0000-000000000001"),
            BaseTimelineVersion = 4,
            Clips =
            [
                Coverage(PrimaryTwo, ShotTwo, SceneTwo, 2, 1, 3, MovieIntercutCoverageKinds.Primary),
                Coverage(Insert, ShotThree, SceneOne, 1, 2, 1, MovieIntercutCoverageKinds.Insert, ShotOne, "The red notebook"),
                Coverage(PrimaryOne, ShotOne, SceneOne, 1, 1, 4, MovieIntercutCoverageKinds.Primary),
                Coverage(Reaction, ShotThree, SceneOne, 1, 2, 2, MovieIntercutCoverageKinds.Reaction, ShotOne, "Mara reacts"),
            ],
        };

        var first = MovieIntercutPlanner.Build(ProjectId, request);
        var second = MovieIntercutPlanner.Build(ProjectId, request);

        Assert.True(first.StoryOrderPreserved);
        Assert.False(first.RequiresUserApproval);
        Assert.Equal(new[] { PrimaryOne, Reaction, Insert, PrimaryTwo }, first.ProposedTimeline.Clips.Select(item => item.ClipId));
        Assert.Equal(new[] { ShotOne, ShotThree, ShotThree, ShotTwo }, first.ProposedTimeline.Clips.Select(item => item.MovieShotId));
        Assert.Equal(first.ProvenanceHash, second.ProvenanceHash);
        Assert.Equal(first.ProposedTimeline.TimelineId, second.ProposedTimeline.TimelineId);
        Assert.Equal(first.ProposedTimeline.Version, second.ProposedTimeline.Version);
        Assert.Equal(first.ProposedTimeline.Clips, second.ProposedTimeline.Clips);
        Assert.Equal(5, first.ProposedTimeline.Version);
        Assert.Equal(10m, first.ProposedTimeline.Clips.Sum(item => item.DurationSeconds));
        Assert.Equal(3, first.ProposedTimeline.Transitions.Count);
        Assert.DoesNotContain("provider", JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Requested_primary_reorder_is_review_only_until_explicitly_approved()
    {
        var request = new MovieIntercutPlanRequest
        {
            BaseTimelineVersion = 7,
            Clips =
            [
                Coverage(PrimaryOne, ShotOne, SceneOne, 1, 1, 2, MovieIntercutCoverageKinds.Primary),
                Coverage(PrimaryTwo, ShotTwo, SceneTwo, 2, 1, 2, MovieIntercutCoverageKinds.Primary),
                Coverage(Reaction, ShotThree, SceneTwo, 2, 2, 1, MovieIntercutCoverageKinds.Reaction, ShotTwo),
            ],
            RequestedClipOrder = [PrimaryTwo, Reaction, PrimaryOne],
        };

        var proposal = MovieIntercutPlanner.Build(ProjectId, request);
        Assert.False(proposal.StoryOrderPreserved);
        Assert.True(proposal.RequiresUserApproval);
        Assert.Equal(new[] { PrimaryOne, PrimaryTwo, Reaction }, proposal.ProposedTimeline.Clips.Select(item => item.ClipId));

        var rejected = MovieIntercutPlanner.ApplyUserApproval(proposal, new MovieIntercutApproval(false, "Keep the approved story order."));
        Assert.Equal(proposal.ProposedTimeline, rejected);

        var approved = MovieIntercutPlanner.ApplyUserApproval(proposal, new MovieIntercutApproval(true, "The director approved the reaction-first intercut for pacing."));
        Assert.Equal(new[] { PrimaryTwo, Reaction, PrimaryOne }, approved.Clips.Select(item => item.ClipId));
        Assert.Equal(proposal.ProposedTimeline.Version + 1, approved.Version);
        Assert.Equal(approved.Clips.Count - 1, approved.Transitions.Count);
    }

    [Fact]
    public void Invalid_anchor_and_duplicate_primary_are_blocked_before_a_proposal_is_created()
    {
        var invalidAnchor = new MovieIntercutPlanRequest
        {
            Clips =
            [Coverage(PrimaryOne, ShotOne, SceneOne, 1, 1, 2, MovieIntercutCoverageKinds.Primary), Coverage(Reaction, ShotThree, SceneOne, 1, 2, 1, MovieIntercutCoverageKinds.Reaction, ShotTwo)],
        };
        var duplicatePrimary = new MovieIntercutPlanRequest
        {
            Clips =
            [Coverage(PrimaryOne, ShotOne, SceneOne, 1, 1, 2, MovieIntercutCoverageKinds.Primary), Coverage(Insert, ShotOne, SceneOne, 1, 1, 1, MovieIntercutCoverageKinds.Primary)],
        };

        var anchorException = Assert.Throws<MovieIntercutPlanningException>(() => MovieIntercutPlanner.Build(ProjectId, invalidAnchor));
        var duplicateException = Assert.Throws<MovieIntercutPlanningException>(() => MovieIntercutPlanner.Build(ProjectId, duplicatePrimary));
        Assert.Equal(MovieIntercutPlanCodes.AnchorInvalid, anchorException.Code);
        Assert.Equal(MovieIntercutPlanCodes.StoryOrderInvalid, duplicateException.Code);
    }

    [Fact]
    public void Two_hour_intercut_timeline_keeps_all_short_clips_and_deterministic_provenance()
    {
        // Planning 720 ten-second clips must not be confused with one 7,200-second
        // provider submission; this pure planner never queues a video generation.
        var request = new MovieIntercutPlanRequest
        {
            Clips = Enumerable.Range(1, 720).Select(index =>
                Coverage(Guid.NewGuid(), Guid.NewGuid(), SceneOne, 1, index, 10m, MovieIntercutCoverageKinds.Primary)).ToArray(),
        };

        var proposal = MovieIntercutPlanner.Build(ProjectId, request);
        var replay = MovieIntercutPlanner.Build(ProjectId, request);

        Assert.Equal(720, proposal.ProposedTimeline.Clips.Count);
        Assert.Equal(7_200m, proposal.ProposedTimeline.Clips.Sum(clip => clip.DurationSeconds));
        Assert.Equal(7_190m, proposal.ProposedTimeline.Clips.Last().StartSeconds);
        Assert.Equal(719, proposal.ProposedTimeline.Transitions.Count);
        Assert.Equal(proposal.ProvenanceHash, replay.ProvenanceHash);
        Assert.Equal(proposal.ProposedTimeline.Clips, replay.ProposedTimeline.Clips);
    }

    [Fact]
    public void Intercut_rejects_more_than_two_hours_even_when_each_clip_is_short()
    {
        var request = new MovieIntercutPlanRequest
        {
            Clips = Enumerable.Range(1, 721).Select(index =>
                Coverage(Guid.NewGuid(), Guid.NewGuid(), SceneOne, 1, index, 10m, MovieIntercutCoverageKinds.Primary)).ToArray(),
        };

        var exception = Assert.Throws<MovieIntercutPlanningException>(() => MovieIntercutPlanner.Build(ProjectId, request));

        Assert.Equal(MovieIntercutPlanCodes.DurationInvalid, exception.Code);
        Assert.Contains("7,200", exception.Message);
    }

    [Fact]
    public void Two_hour_timeline_cap_does_not_relax_individual_coverage_duration()
    {
        var request = new MovieIntercutPlanRequest
        {
            Clips = [Coverage(PrimaryOne, ShotOne, SceneOne, 1, 1, 3_601m, MovieIntercutCoverageKinds.Primary)],
        };

        var exception = Assert.Throws<MovieIntercutPlanningException>(() => MovieIntercutPlanner.Build(ProjectId, request));

        Assert.Equal(MovieIntercutPlanCodes.DurationInvalid, exception.Code);
        Assert.Contains("3,600", exception.Message);
    }

    [Fact]
    public void Distinct_timelines_with_identical_coverage_receive_distinct_proposal_ids()
    {
        // A review proposal must be bound to its canonical timeline identity, not
        // just its movie project, base version and coverage candidates.
        var firstTimelineId = Guid.Parse("50000000-0000-0000-0000-000000000101");
        var secondTimelineId = Guid.Parse("50000000-0000-0000-0000-000000000102");
        var request = new MovieIntercutPlanRequest
        {
            TimelineId = firstTimelineId,
            BaseTimelineVersion = 1,
            Clips = [Coverage(PrimaryOne, ShotOne, SceneOne, 1, 1, 2m, MovieIntercutCoverageKinds.Primary)],
        };

        var first = MovieIntercutPlanner.Build(ProjectId, request);
        request.TimelineId = secondTimelineId;
        var second = MovieIntercutPlanner.Build(ProjectId, request);
        request.TimelineId = firstTimelineId;
        var replay = MovieIntercutPlanner.Build(ProjectId, request);

        Assert.NotEqual(first.ProposalId, second.ProposalId);
        Assert.NotEqual(first.ProvenanceHash, second.ProvenanceHash);
        Assert.Equal(first.ProposalId, replay.ProposalId);
        Assert.Equal(first.ProvenanceHash, replay.ProvenanceHash);
    }

    private static MovieIntercutCoverageRequest Coverage(Guid clipId, Guid shotId, Guid sceneId, int sceneSequence, int shotSequence, decimal duration, string kind, Guid? anchorShotId = null, string? label = null) => new()
    {
        ClipId = clipId,
        MovieShotId = shotId,
        MovieSceneId = sceneId,
        SceneSequence = sceneSequence,
        ShotSequence = shotSequence,
        DurationSeconds = duration,
        CoverageKind = kind,
        AnchorShotId = anchorShotId,
        Label = label,
    };
}
