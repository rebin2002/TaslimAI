using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AdaptiveResolutionDirectorTests
{
    private readonly AdaptiveResolutionDirector director = new();

    [Fact]
    public void Simple_fast_1080p_shot_uses_480p_source_and_upscale()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P1080,
            ProjectQualityTier = DirectorQualityLevels.Fast,
            ShotImportance = 10,
            DurationSeconds = 4,
            MotionComplexity = 10,
            CameraComplexity = 10,
            FineDetailImportance = 10,
            UpscaleSuitability = 95,
            SpeedPreference = 90,
        });

        Assert.Equal(AdaptiveResolutionSourceResolutions.P480, recommendation.RecommendedSourceResolution);
        Assert.Equal(AdaptiveResolutionMasterResolutions.P1080, recommendation.IntendedUpscaleTarget);
        Assert.Equal(AdaptiveResolutionPaths.Upscale, recommendation.ProcessingPath);
        Assert.Contains(AdaptiveResolutionReasonCodes.UpscaleSelected, recommendation.ReasonCodes);
        Assert.False(recommendation.UserOverride.Requested);
    }

    [Fact]
    public void Complex_studio_4k_shot_protects_detail_and_continuity()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P4K,
            ProjectQualityTier = DirectorQualityLevels.Studio,
            ShotImportance = 95,
            DurationSeconds = 12,
            MotionComplexity = 88,
            CameraComplexity = 82,
            FaceImportance = 95,
            HandBodyComplexity = 90,
            FineDetailImportance = 94,
            ContinuitySensitivity = 92,
            EnvironmentComplexity = 80,
            VfxComplexity = 86,
            TextSignageSensitivity = 78,
            LipSyncDependency = 80,
            UpscaleSuitability = 40,
            RequiredQualityDimensions =
            [
                new(AdaptiveResolutionQualityDimensions.FaceIdentity, 100),
                new(AdaptiveResolutionQualityDimensions.Anatomy, 95),
                new(AdaptiveResolutionQualityDimensions.Continuity, 90),
            ],
        });

        Assert.Contains(
            recommendation.RecommendedSourceResolution,
            new[] { AdaptiveResolutionSourceResolutions.P1440, AdaptiveResolutionSourceResolutions.P2160 });
        Assert.Equal(AdaptiveResolutionMasterResolutions.P4K, recommendation.IntendedUpscaleTarget);
        Assert.Contains(AdaptiveResolutionReasonCodes.FaceSensitive, recommendation.ReasonCodes);
        Assert.Contains(AdaptiveResolutionReasonCodes.AnatomySensitive, recommendation.ReasonCodes);
        Assert.Contains(AdaptiveResolutionReasonCodes.ContinuitySensitive, recommendation.ReasonCodes);
        Assert.Equal(3, recommendation.QualityRequirement.RequiredDimensions.Count);
        Assert.True(recommendation.QualityConfidence > 0m);
    }

    [Fact]
    public void Low_upscale_suitability_can_require_higher_source_qc_escalation()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P4K,
            ProjectQualityTier = DirectorQualityLevels.Standard,
            ShotImportance = 90,
            MotionComplexity = 90,
            CameraComplexity = 90,
            FaceImportance = 90,
            HandBodyComplexity = 90,
            FineDetailImportance = 90,
            ContinuitySensitivity = 90,
            EnvironmentComplexity = 80,
            VfxComplexity = 80,
            UpscaleSuitability = 0,
        });

        Assert.True(recommendation.QcEscalationRequired);
        Assert.Equal(AdaptiveResolutionReasonCodes.QualityConfidenceBelowRequirement, recommendation.Escalation.Trigger);
        Assert.Equal(AdaptiveResolutionSourceResolutions.P2160, recommendation.Escalation.EscalateToSourceResolution);
        Assert.Contains(AdaptiveResolutionReasonCodes.UpscaleRisk, recommendation.ReasonCodes);
    }

    [Fact]
    public void Cost_constraint_can_block_automatic_source_escalation_but_not_qc_review()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P4K,
            ProjectQualityTier = DirectorQualityLevels.Standard,
            ShotImportance = 90,
            MotionComplexity = 90,
            CameraComplexity = 90,
            FaceImportance = 90,
            FineDetailImportance = 90,
            ContinuitySensitivity = 90,
            UpscaleSuitability = 0,
            CostConstraints = new AdaptiveResolutionCostConstraints(AllowQualityEscalation: false),
        });

        Assert.True(recommendation.QcEscalationRequired);
        Assert.Null(recommendation.Escalation.EscalateToSourceResolution);
    }

    [Fact]
    public void User_override_is_applied_and_remains_visible()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P4K,
            ProjectQualityTier = DirectorQualityLevels.Cinematic,
            ShotImportance = 80,
            FineDetailImportance = 80,
            UpscaleSuitability = 80,
            UserOverride = new AdaptiveResolutionOverride(
                SourceResolution: AdaptiveResolutionSourceResolutions.P1080,
                UpscaleTarget: AdaptiveResolutionMasterResolutions.P4K,
                Path: AdaptiveResolutionPaths.Upscale),
        });

        Assert.Equal(AdaptiveResolutionSourceResolutions.P1080, recommendation.RecommendedSourceResolution);
        Assert.True(recommendation.UserOverride.Requested);
        Assert.True(recommendation.UserOverride.Applied);
        Assert.Equal(AdaptiveResolutionReasonCodes.UserOverrideApplied, recommendation.UserOverride.StatusCode);
        Assert.Contains(AdaptiveResolutionReasonCodes.UserOverrideApplied, recommendation.ReasonCodes);
    }

    [Fact]
    public void Invalid_request_returns_stable_ordered_validation_errors()
    {
        var request = new AdaptiveResolutionRequest
        {
            TargetMasterResolution = "8k",
            ProjectQualityTier = "Auto",
            ShotImportance = -1,
            DurationSeconds = 0,
            RequiredQualityDimensions =
            [
                new("unknown", 50),
                new(AdaptiveResolutionQualityDimensions.FaceIdentity, 110),
                new(AdaptiveResolutionQualityDimensions.FaceIdentity, 50),
            ],
            CostConstraints = new AdaptiveResolutionCostConstraints(MaxEstimatedCostUsd: -1m),
            UserOverride = new AdaptiveResolutionOverride(
                SourceResolution: AdaptiveResolutionSourceResolutions.P2160,
                UpscaleTarget: AdaptiveResolutionMasterResolutions.P2K),
        };

        var errors = AdaptiveResolutionRequestValidator.Validate(request);

        Assert.Equal(
            new[]
            {
                nameof(request.TargetMasterResolution),
                nameof(request.ProjectQualityTier),
                nameof(request.ShotImportance),
                nameof(request.DurationSeconds),
            },
            errors.Take(4).Select(error => error.Field));
        Assert.Contains(errors, error => error.Code == "duplicate");
        Assert.Contains(errors, error => error.Field.Contains(nameof(request.CostConstraints), StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Field.Contains(nameof(request.UserOverride), StringComparison.Ordinal));
    }

    [Fact]
    public void Recommendation_validator_rejects_target_mismatch()
    {
        var request = new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P1080,
            ProjectQualityTier = DirectorQualityLevels.Standard,
        };
        var recommendation = director.Recommend(request) with
        {
            IntendedUpscaleTarget = AdaptiveResolutionMasterResolutions.P4K,
        };

        var errors = AdaptiveResolutionRecommendationValidator.Validate(request, recommendation);

        Assert.Contains(errors, error => error.Field == nameof(AdaptiveResolutionRecommendation.IntendedUpscaleTarget));
    }

    [Fact]
    public void Recommendation_validator_rejects_native_path_for_upscaled_source()
    {
        var request = new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P1080,
            ProjectQualityTier = DirectorQualityLevels.Fast,
        };
        var recommendation = director.Recommend(request) with
        {
            ProcessingPath = AdaptiveResolutionPaths.Native,
        };

        var errors = AdaptiveResolutionRecommendationValidator.Validate(request, recommendation);

        Assert.Contains(errors, error => error.Field == nameof(AdaptiveResolutionRecommendation.ProcessingPath));
    }

    [Fact]
    public void Serialized_contract_contains_no_provider_or_model_fields()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionRequest
        {
            TargetMasterResolution = AdaptiveResolutionMasterResolutions.P2K,
            ProjectQualityTier = DirectorQualityLevels.Cinematic,
            FaceImportance = 80,
            RequiredQualityDimensions = [new(AdaptiveResolutionQualityDimensions.FaceIdentity, 90)],
        });
        var json = JsonSerializer.Serialize(recommendation, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("recommendedSourceResolution", json, StringComparison.Ordinal);
        Assert.Contains("qualityConfidence", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("price", json, StringComparison.OrdinalIgnoreCase);
    }
}
