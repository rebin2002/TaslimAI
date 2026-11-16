using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionWorkflowTests
{
    [Fact]
    public void Storyboard_candidate_is_the_first_explicit_production_operation()
    {
        Assert.Null(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.StoryboardCandidate, null));
        Assert.Equal((MovieProductionStages.ApprovedStoryboard, MovieProductionVersionStatuses.Approved), MovieProductionWorkflow.ApprovalResult(MovieProductionStages.StoryboardCandidate));
        Assert.Null(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ProductionKeyframe, new MovieProductionVersion
        {
            Stage = MovieProductionStages.ApprovedStoryboard,
            Status = MovieProductionVersionStatuses.Approved,
        }));
    }

    [Fact]
    public void Keyframe_and_motion_stages_require_the_previous_approved_stage()
    {
        var rejectedStoryboard = new MovieProductionVersion { Stage = MovieProductionStages.StoryboardCandidate, Status = MovieProductionVersionStatuses.Rejected };
        Assert.NotNull(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ProductionKeyframe, rejectedStoryboard));
        var approvedKeyframe = new MovieProductionVersion { Stage = MovieProductionStages.ApprovedKeyframe, Status = MovieProductionVersionStatuses.Approved };
        Assert.Null(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.MotionPreview, approvedKeyframe));
        Assert.NotNull(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ProductionRender, approvedKeyframe));
    }

    [Fact]
    public void Terminal_and_approval_only_stages_cannot_be_created_as_versions()
    {
        Assert.NotNull(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ShotPlan, null));
        Assert.NotNull(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ApprovedStoryboard, null));
        Assert.NotNull(MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.SelectedFinalTake, null));
        Assert.Equal((MovieProductionStages.SelectedFinalTake, MovieProductionVersionStatuses.Selected), MovieProductionWorkflow.ApprovalResult(MovieProductionStages.ProductionRender));
    }

    [Fact]
    public void Composition_and_regeneration_payloads_must_be_json_objects()
    {
        Assert.True(MovieProductionWorkflow.IsJsonObject("{\"seed\":42}"));
        Assert.False(MovieProductionWorkflow.IsJsonObject("[1,2,3]"));
        Assert.False(MovieProductionWorkflow.IsJsonObject("not-json"));
    }

    [Fact]
    public void Keyframes_only_accept_shared_image_generation_jobs()
    {
        Assert.True(MovieProductionWorkflow.IsGenerationJobTypeAllowed(MovieProductionStages.ProductionKeyframe, GenerationJobTypes.ImageGenerate));
        Assert.False(MovieProductionWorkflow.IsGenerationJobTypeAllowed(MovieProductionStages.ProductionKeyframe, GenerationJobTypes.SystemTest));
        Assert.True(MovieProductionWorkflow.IsGenerationJobTypeAllowed(MovieProductionStages.ProductionRender, GenerationJobTypes.MovieClipGenerate));
    }
}
