using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Small provider-neutral sentinels for composing the independently developed
/// Wave 2 branches. These tests intentionally use existing domain contracts;
/// they do not guess or reserve APIs for branches that are not present.
/// </summary>
public sealed class MovieWave2IntegrationPrepTests
{
    [Fact]
    public void Movie_quality_and_production_stage_vocabularies_remain_canonical()
    {
        Assert.Equal(
            new[] { MovieQualityLevels.Cinematic, MovieQualityLevels.Fast, MovieQualityLevels.Standard, MovieQualityLevels.Studio },
            MovieQualityLevels.Supported.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                MovieProductionStages.ApprovedKeyframe,
                MovieProductionStages.ApprovedStoryboard,
                MovieProductionStages.MotionPreview,
                MovieProductionStages.ProductionKeyframe,
                MovieProductionStages.ProductionRender,
                MovieProductionStages.SelectedFinalTake,
                MovieProductionStages.ShotPlan,
                MovieProductionStages.StoryboardCandidate,
            },
            MovieProductionStages.Persisted.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void Shot_readiness_is_explicit_and_does_not_imply_generation()
    {
        var incomplete = new MovieShot { Description = "A figure waits in the doorway." };
        var incompleteReadiness = MovieShotReadiness.Evaluate(incomplete);

        Assert.False(incompleteReadiness.Ready);
        Assert.Contains("Shot purpose", incompleteReadiness.Missing);
        Assert.Contains("Expected duration", incompleteReadiness.Missing);

        var complete = new MovieShot
        {
            Description = "A figure crosses into the morning light.",
            Purpose = "Introduce the subject and establish the morning mood.",
            Subjects = "Mara carrying a canvas bag",
            LocationSet = "Old city street set",
            DurationSeconds = 8,
            ProductionRequirements = "Restrained crossing performance.",
            ContinuityReferences = "Cool dawn palette follows the empty street.",
            CameraAndFraming = "Medium-wide, eye level",
        };

        Assert.True(MovieShotReadiness.Evaluate(complete).Ready);
        Assert.Equal(MovieShotPlanStates.ReadyForStoryboard, MovieShotReadiness.PlanState(complete));
        Assert.False(MovieProductionWorkflow.IsGenerationJobTypeAllowed(
            MovieProductionStages.StoryboardCandidate,
            GenerationJobTypes.MovieClipGenerate));
    }

    [Fact]
    public void Production_stage_advancement_requires_the_approved_handoff()
    {
        var approvedStoryboard = new MovieProductionVersion
        {
            Stage = MovieProductionStages.ApprovedStoryboard,
            Status = MovieProductionVersionStatuses.Approved,
        };
        var pendingStoryboard = new MovieProductionVersion
        {
            Stage = MovieProductionStages.ApprovedStoryboard,
            Status = MovieProductionVersionStatuses.PendingApproval,
        };

        Assert.Null(MovieProductionWorkflow.ValidateVersionCreation(
            MovieProductionStages.ProductionKeyframe,
            approvedStoryboard));
        Assert.Equal(
            "The source version must be approved before advancing the shot.",
            MovieProductionWorkflow.ValidateVersionCreation(
                MovieProductionStages.ProductionKeyframe,
                pendingStoryboard));
        Assert.Equal(
            (MovieProductionStages.ApprovedStoryboard, MovieProductionVersionStatuses.Approved),
            MovieProductionWorkflow.ApprovalResult(MovieProductionStages.StoryboardCandidate));
    }
}
