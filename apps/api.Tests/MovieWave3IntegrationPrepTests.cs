using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Movies.Wave3Integration;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-neutral sentinels for composing the independent Wave 3 branches.
/// These tests intentionally use the compatibility seam on origin/main; they do
/// not merge or guess the missing Wave-2 persistence contracts.
/// </summary>
public sealed class MovieWave3IntegrationPrepTests
{
    [Fact]
    public void Adaptive_recommendation_is_deterministic_and_uses_economical_draft_path()
    {
        var request = new MovieWave3AdaptiveResolutionRequest(
            MovieWave3ResolutionContract.P2160,
            MovieQualityLevels.Standard,
            Importance: 20,
            MotionComplexity: 15,
            CameraComplexity: 20,
            UpscaleSuitability: 95,
            EconomicalDraft: true);

        var first = MovieWave3AdaptiveResolutionCompatibility.Recommend(request);
        var second = MovieWave3AdaptiveResolutionCompatibility.Recommend(request);

        Assert.Equal(MovieWave3ResolutionContract.P720, first.SourceResolution);
        Assert.Equal(MovieWave3ResolutionContract.P2160, first.MasterTargetResolution);
        Assert.Equal(MovieWave3ResolutionContract.Upscale, first.PipelinePath);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.DoesNotContain("provider", MovieWave3AdaptiveResolutionCompatibility.SerializeProductSafe(first), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", MovieWave3AdaptiveResolutionCompatibility.SerializeProductSafe(first), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", MovieWave3AdaptiveResolutionCompatibility.SerializeProductSafe(first), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_demand_low_upscale_suitability_requires_qc_escalation()
    {
        var recommendation = MovieWave3AdaptiveResolutionCompatibility.Recommend(new MovieWave3AdaptiveResolutionRequest(
            MovieWave3ResolutionContract.P2160,
            MovieQualityLevels.Standard,
            Importance: 95,
            MotionComplexity: 90,
            CameraComplexity: 90,
            FaceImportance: 95,
            FineDetailImportance: 92,
            ContinuitySensitivity: 90,
            UpscaleSuitability: 0));

        Assert.True(recommendation.QcEscalationRequired);
        Assert.Contains("confidence_below_requirement", recommendation.ReasonCodes);
        Assert.NotNull(recommendation.EscalateToSourceResolution);
    }

    [Fact]
    public void Cost_estimate_remains_explicitly_unknown_until_benchmark_economics_are_integrated()
    {
        var recommendation = MovieWave3AdaptiveResolutionCompatibility.Recommend(new MovieWave3AdaptiveResolutionRequest(
            MovieWave3ResolutionContract.P1080,
            MovieQualityLevels.Cinematic));

        Assert.False(recommendation.CostEstimate.IsKnown);
        Assert.Null(recommendation.CostEstimate.AmountUsd);
        Assert.Equal("cost_data_unavailable", recommendation.CostEstimate.Reason);
    }

    [Fact]
    public void Only_the_explicitly_selected_or_final_ready_take_is_upscale_eligible()
    {
        var candidate = new MovieTake { Id = Guid.NewGuid(), Status = MovieTakeStatuses.Ready };
        var selected = new MovieTake { Id = Guid.NewGuid(), Status = MovieTakeStatuses.Approved };
        var shot = new MovieShot { Id = Guid.NewGuid(), SelectedTakeId = selected.Id };

        var blocked = MovieWave3UpscaleEligibilityCompatibility.Evaluate(candidate, shot);
        var eligible = MovieWave3UpscaleEligibilityCompatibility.Evaluate(selected, shot);
        selected.Status = MovieTakeStatuses.Draft;
        var notReady = MovieWave3UpscaleEligibilityCompatibility.Evaluate(selected, shot);

        Assert.Equal(MovieWave3UpscaleEligibilityCodes.TakeNotSelected, blocked.Code);
        Assert.True(eligible.Eligible);
        Assert.True(eligible.IsSelected);
        Assert.False(notReady.Eligible);
        Assert.Equal(MovieWave3UpscaleEligibilityCodes.TakeNotReady, notReady.Code);
    }

    [Fact]
    public void Existing_movie_vocabulary_keeps_planning_separate_from_generation()
    {
        Assert.Equal(
            new[] { MovieQualityLevels.Cinematic, MovieQualityLevels.Fast, MovieQualityLevels.Standard, MovieQualityLevels.Studio },
            MovieQualityLevels.Supported.OrderBy(value => value, StringComparer.Ordinal));
        Assert.False(MovieProductionWorkflow.IsGenerationJobTypeAllowed(
            MovieProductionStages.StoryboardCandidate,
            GenerationJobTypes.MovieClipGenerate));
        Assert.Equal(MovieProductionStages.SelectedFinalTake, MovieProductionWorkflow.ApprovalResult(MovieProductionStages.ProductionRender)?.NextStage);
    }
}
