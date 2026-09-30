using System.Text.Json;
using Taslim.Api.Movies;
using Taslim.Api.Movies.AdaptiveResolution;
using Xunit;
using Wave3AdaptiveResolutionDirector = Taslim.Api.Movies.AdaptiveResolution.AdaptiveResolutionDirector;

namespace Taslim.Api.Tests;

public sealed class AdaptiveResolutionDirectorWave3Tests
{
    private readonly Wave3AdaptiveResolutionDirector director = new();

    [Fact]
    public void Fast_1080p_insert_uses_low_source_when_quality_floor_is_met()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = AdaptiveResolutionWave3Resolutions.P1080,
            QualityTier = DirectorQualityLevels.Fast,
            Importance = 10,
            DurationSeconds = 4,
            MotionComplexity = 10,
            CameraComplexity = 10,
            FineDetailImportance = 10,
            UpscaleSuitability = 95,
            SpeedPreference = 90,
        });

        Assert.Equal(AdaptiveResolutionWave3Resolutions.P480, recommendation.SourceResolution);
        Assert.Equal(AdaptiveResolutionWave3Resolutions.P1080, recommendation.MasterTargetResolution);
        Assert.Equal(AdaptiveResolutionWave3PipelinePaths.Upscale, recommendation.PipelinePath);
        Assert.False(recommendation.QcEscalationRequired);
        Assert.Contains(AdaptiveResolutionWave3ReasonCodes.UpscaleSelected, recommendation.ReasonCodes);
    }

    [Fact]
    public void Face_hands_detail_continuity_and_vfx_raise_source_and_reasons()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = AdaptiveResolutionWave3Resolutions.P2160,
            QualityTier = DirectorQualityLevels.Studio,
            Importance = 95,
            DurationSeconds = 12,
            MotionComplexity = 88,
            CameraComplexity = 82,
            FaceImportance = 95,
            HandsBodyComplexity = 90,
            FineDetailImportance = 94,
            ContinuitySensitivity = 92,
            EnvironmentComplexity = 80,
            VfxComplexity = 86,
            TextSignageSensitivity = 78,
            LipSyncDependency = 80,
            UpscaleSuitability = 40,
            RequiredQualityDimensions =
            [
                new(AdaptiveResolutionWave3QualityDimensions.FaceIdentity, 100),
                new(AdaptiveResolutionWave3QualityDimensions.Anatomy, 95),
                new(AdaptiveResolutionWave3QualityDimensions.Continuity, 90),
            ],
        });

        Assert.Contains(recommendation.SourceResolution, new[]
        {
            AdaptiveResolutionWave3Resolutions.P1440,
            AdaptiveResolutionWave3Resolutions.P2160,
        });
        Assert.Equal(AdaptiveResolutionWave3Resolutions.P2160, recommendation.MasterTargetResolution);
        Assert.Contains(AdaptiveResolutionWave3ReasonCodes.FaceSensitive, recommendation.ReasonCodes);
        Assert.Contains(AdaptiveResolutionWave3ReasonCodes.AnatomySensitive, recommendation.ReasonCodes);
        Assert.Contains(AdaptiveResolutionWave3ReasonCodes.ContinuitySensitive, recommendation.ReasonCodes);
        Assert.Contains(AdaptiveResolutionWave3ReasonCodes.VfxComplex, recommendation.ReasonCodes);
        Assert.Equal(3, recommendation.QualityRequirement.RequiredDimensions.Count);
    }

    [Fact]
    public void Low_upscale_suitability_requires_qc_escalation_when_confidence_is_below_tier()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = AdaptiveResolutionWave3Resolutions.P2160,
            QualityTier = DirectorQualityLevels.Standard,
            Importance = 90,
            MotionComplexity = 90,
            CameraComplexity = 90,
            FaceImportance = 90,
            HandsBodyComplexity = 90,
            FineDetailImportance = 90,
            ContinuitySensitivity = 90,
            EnvironmentComplexity = 80,
            VfxComplexity = 80,
            UpscaleSuitability = 0,
        });

        Assert.True(recommendation.QcEscalationRequired);
        Assert.Equal(AdaptiveResolutionWave3ReasonCodes.ConfidenceBelowRequirement, recommendation.Escalation.Trigger);
        Assert.Equal(AdaptiveResolutionWave3Resolutions.P2160, recommendation.Escalation.EscalateToSourceResolution);
        Assert.Contains(AdaptiveResolutionWave3ReasonCodes.UpscaleRisk, recommendation.ReasonCodes);
    }

    [Fact]
    public void Cost_constraints_are_recorded_without_evaluating_pricing()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = AdaptiveResolutionWave3Resolutions.P1440,
            QualityTier = DirectorQualityLevels.Standard,
            Constraints = new AdaptiveResolutionWave3Constraints(PreferLowerCost: true, MaxEstimatedCostUsd: 12m),
        });

        Assert.False(recommendation.CostEvaluation.Evaluated);
        Assert.Null(recommendation.CostEvaluation.ConstraintSatisfied);
        Assert.Equal(AdaptiveResolutionWave3ReasonCodes.CostDataUnavailable, recommendation.CostEvaluation.ReasonCode);
        Assert.Contains(AdaptiveResolutionWave3ReasonCodes.CostPreferred, recommendation.ReasonCodes);
    }

    [Theory]
    [InlineData(AdaptiveResolutionWave3Resolutions.P480)]
    [InlineData(AdaptiveResolutionWave3Resolutions.P720)]
    [InlineData(AdaptiveResolutionWave3Resolutions.P1080)]
    [InlineData(AdaptiveResolutionWave3Resolutions.P1440)]
    [InlineData(AdaptiveResolutionWave3Resolutions.P2160)]
    public void Every_supported_master_resolution_produces_a_valid_plan(string target)
    {
        var recommendation = director.Recommend(new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = target,
            QualityTier = DirectorQualityLevels.Fast,
        });

        Assert.Equal(target, recommendation.MasterTargetResolution);
        Assert.NotEmpty(recommendation.SourceResolution);
        Assert.NotEmpty(recommendation.PipelinePath);
        Assert.InRange(recommendation.QualityConfidence, 0m, 1m);
        Assert.Empty(AdaptiveResolutionDirectorRecommendationValidator.Validate(
            new AdaptiveResolutionDirectorRequest { MasterTargetResolution = target, QualityTier = DirectorQualityLevels.Fast },
            recommendation));
    }

    [Fact]
    public void Invalid_request_returns_stable_validation_errors()
    {
        var request = new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = "8k",
            QualityTier = "Auto",
            Importance = -1,
            DurationSeconds = 0,
            RequiredQualityDimensions =
            [
                new("unknown", 50),
                new(AdaptiveResolutionWave3QualityDimensions.FaceIdentity, 110),
                new(AdaptiveResolutionWave3QualityDimensions.FaceIdentity, 50),
            ],
            Constraints = new AdaptiveResolutionWave3Constraints(MaxEstimatedCostUsd: -1m),
        };

        var errors = AdaptiveResolutionDirectorRequestValidator.Validate(request);

        Assert.Equal(nameof(request.MasterTargetResolution), errors[0].Field);
        Assert.Equal(nameof(request.QualityTier), errors[1].Field);
        Assert.Equal(nameof(request.Importance), errors[2].Field);
        Assert.Equal(nameof(request.DurationSeconds), errors[3].Field);
        Assert.Contains(errors, error => error.Code == "duplicate");
        Assert.Contains(errors, error => error.Field.Contains(nameof(request.Constraints), StringComparison.Ordinal));
    }

    [Fact]
    public void Recommendation_is_deterministic_and_contains_no_routing_or_pricing_identifiers()
    {
        var request = new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = "4k",
            QualityTier = DirectorQualityLevels.Cinematic,
            FaceImportance = 80,
            RequiredQualityDimensions = [new(AdaptiveResolutionWave3QualityDimensions.FaceIdentity, 90)],
        };

        var first = director.Recommend(request);
        var second = director.Recommend(request);
        var json = JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            JsonSerializer.Serialize(second, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Contains("sourceResolution", json, StringComparison.Ordinal);
        Assert.Contains("qualityConfidence", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("price", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escalation_can_be_disabled_without_silently_claiming_quality()
    {
        var recommendation = director.Recommend(new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = AdaptiveResolutionWave3Resolutions.P2160,
            QualityTier = DirectorQualityLevels.Studio,
            Importance = 100,
            MotionComplexity = 100,
            FaceImportance = 100,
            FineDetailImportance = 100,
            ContinuitySensitivity = 100,
            UpscaleSuitability = 0,
            Constraints = new AdaptiveResolutionWave3Constraints(AllowQualityEscalation: false),
        });

        Assert.True(recommendation.QcEscalationRequired);
        Assert.Null(recommendation.Escalation.EscalateToSourceResolution);
    }
}
