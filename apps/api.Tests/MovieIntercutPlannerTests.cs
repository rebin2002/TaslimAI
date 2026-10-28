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
