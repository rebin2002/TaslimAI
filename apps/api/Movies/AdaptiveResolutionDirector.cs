using System.Collections.ObjectModel;

namespace Taslim.Api.Movies;

/// <summary>
/// Stable, provider-neutral vocabulary for the Adaptive Resolution Director.
/// Resolution values are image-height intents so aspect ratio remains a separate movie concern.
/// </summary>
public static class AdaptiveResolutionContract
{
    public const int Version = 1;
    public const int MinimumDurationSeconds = 1;
    public const int MaximumDurationSeconds = 3_600;
    public const int MinimumScore = 0;
    public const int MaximumScore = 100;
    public const int MaximumQualityDimensions = 12;
    public const int MaximumAlternatives = 4;

    public static readonly IReadOnlySet<string> SourceResolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdaptiveResolutionSourceResolutions.P480,
        AdaptiveResolutionSourceResolutions.P720,
        AdaptiveResolutionSourceResolutions.P1080,
        AdaptiveResolutionSourceResolutions.P1440,
        AdaptiveResolutionSourceResolutions.P2160,
    };

    public static readonly IReadOnlySet<string> MasterResolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdaptiveResolutionMasterResolutions.P1080,
        AdaptiveResolutionMasterResolutions.P2K,
        AdaptiveResolutionMasterResolutions.P4K,
    };

    public static readonly IReadOnlySet<string> Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdaptiveResolutionPaths.Native,
        AdaptiveResolutionPaths.Upscale,
        AdaptiveResolutionPaths.NativeHighQualityThenMaster,
    };

    public static readonly IReadOnlySet<string> QualityDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdaptiveResolutionQualityDimensions.Sharpness,
        AdaptiveResolutionQualityDimensions.TemporalStability,
        AdaptiveResolutionQualityDimensions.DetailFidelity,
        AdaptiveResolutionQualityDimensions.FaceIdentity,
        AdaptiveResolutionQualityDimensions.Anatomy,
        AdaptiveResolutionQualityDimensions.Continuity,
        AdaptiveResolutionQualityDimensions.TextLegibility,
        AdaptiveResolutionQualityDimensions.LipSync,
        AdaptiveResolutionQualityDimensions.Composition,
    };

    public static int PixelsFor(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        AdaptiveResolutionSourceResolutions.P480 => 480,
        AdaptiveResolutionSourceResolutions.P720 => 720,
        AdaptiveResolutionSourceResolutions.P1080 => 1080,
        AdaptiveResolutionSourceResolutions.P1440 => 1440,
        AdaptiveResolutionSourceResolutions.P2160 => 2160,
        AdaptiveResolutionMasterResolutions.P2K => 1440,
        AdaptiveResolutionMasterResolutions.P4K => 2160,
        _ => 0,
    };

    public static string SourceResolutionFor(string? masterResolution) => masterResolution?.Trim().ToLowerInvariant() switch
    {
        AdaptiveResolutionMasterResolutions.P1080 => AdaptiveResolutionSourceResolutions.P1080,
        AdaptiveResolutionMasterResolutions.P2K => AdaptiveResolutionSourceResolutions.P1440,
        AdaptiveResolutionMasterResolutions.P4K => AdaptiveResolutionSourceResolutions.P2160,
        _ => string.Empty,
    };

    public static string MasterResolutionFor(string? sourceResolution) => sourceResolution?.Trim().ToLowerInvariant() switch
    {
        AdaptiveResolutionSourceResolutions.P1080 => AdaptiveResolutionMasterResolutions.P1080,
        AdaptiveResolutionSourceResolutions.P1440 => AdaptiveResolutionMasterResolutions.P2K,
        AdaptiveResolutionSourceResolutions.P2160 => AdaptiveResolutionMasterResolutions.P4K,
        _ => string.Empty,
    };
}

public static class AdaptiveResolutionMasterResolutions
{
    public const string P1080 = "1080p";
    public const string P2K = "2k";
    public const string P4K = "4k";
}

public static class AdaptiveResolutionSourceResolutions
{
    public const string P480 = "480p";
    public const string P720 = "720p";
    public const string P1080 = "1080p";
    public const string P1440 = "1440p";
    public const string P2160 = "2160p";
}

public static class AdaptiveResolutionPaths
{
    public const string Native = "native";
    public const string Upscale = "upscale";
    public const string NativeHighQualityThenMaster = "native_high_quality_then_4k_master";
}

public static class AdaptiveResolutionQualityDimensions
{
    public const string Sharpness = "sharpness";
    public const string TemporalStability = "temporal_stability";
    public const string DetailFidelity = "detail_fidelity";
    public const string FaceIdentity = "face_identity";
    public const string Anatomy = "anatomy";
    public const string Continuity = "continuity";
    public const string TextLegibility = "text_legibility";
    public const string LipSync = "lip_sync";
    public const string Composition = "composition";
}

public static class AdaptiveResolutionReasonCodes
{
    public const string HighShotImportance = "high_shot_importance";
    public const string HighMotionComplexity = "high_motion_complexity";
    public const string HighCameraComplexity = "high_camera_complexity";
    public const string FaceSensitive = "face_sensitive";
    public const string AnatomySensitive = "anatomy_sensitive";
    public const string FineDetailSensitive = "fine_detail_sensitive";
    public const string ContinuitySensitive = "continuity_sensitive";
    public const string EnvironmentComplex = "environment_complex";
    public const string VfxComplex = "vfx_complex";
    public const string TextSensitive = "text_signage_sensitive";
    public const string LipSyncDependent = "lip_sync_dependent";
    public const string UpscaleRisk = "upscale_risk";
    public const string SpeedPreferred = "speed_preferred";
    public const string CostPreferred = "cost_preferred";
    public const string NativeSourceSelected = "native_source_selected";
    public const string UpscaleSelected = "upscale_selected";
    public const string NativeHighQualityMastering = "native_high_quality_mastering";
    public const string QualityConfidenceBelowRequirement = "quality_confidence_below_requirement";
    public const string UserOverrideApplied = "user_override_applied";
    public const string CostDataNotAvailable = "cost_data_not_available";
}

public sealed record AdaptiveResolutionQualityDimensionRequirement(
    string Dimension,
    int Importance = 50);

public sealed record AdaptiveResolutionCostConstraints(
    bool PreferLowerCost = false,
    decimal? MaxEstimatedCostUsd = null,
    bool AllowQualityEscalation = true);

public sealed record AdaptiveResolutionOverride(
    string? SourceResolution = null,
    string? UpscaleTarget = null,
    string? Path = null);

/// <summary>
/// All fields describe the shot and the user's intent. No provider, model, price catalog,
/// credential, or provider operation identifier belongs in this request.
/// Scores use an inclusive 0..100 scale; higher means more importance, complexity, dependency,
/// or suitability as named by the field.
/// </summary>
public sealed record AdaptiveResolutionRequest
{
    public string TargetMasterResolution { get; init; } = AdaptiveResolutionMasterResolutions.P1080;
    public string ProjectQualityTier { get; init; } = DirectorQualityLevels.Standard;
    public int ShotImportance { get; init; } = 50;
    public int DurationSeconds { get; init; } = 5;
    public int MotionComplexity { get; init; } = 50;
    public int CameraComplexity { get; init; } = 50;
    public int FaceImportance { get; init; } = 0;
    public int HandBodyComplexity { get; init; } = 0;
    public int FineDetailImportance { get; init; } = 50;
    public int ContinuitySensitivity { get; init; } = 50;
    public int EnvironmentComplexity { get; init; } = 50;
    public int VfxComplexity { get; init; } = 0;
    public int TextSignageSensitivity { get; init; } = 0;
    public int LipSyncDependency { get; init; } = 0;
    public int UpscaleSuitability { get; init; } = 50;
    public IReadOnlyList<AdaptiveResolutionQualityDimensionRequirement> RequiredQualityDimensions { get; init; } = [];
    public int SpeedPreference { get; init; } = 50;
    public AdaptiveResolutionCostConstraints CostConstraints { get; init; } = new();
    public AdaptiveResolutionOverride? UserOverride { get; init; }
}

public sealed record AdaptiveResolutionQualityRequirement(
    string ProjectQualityTier,
    IReadOnlyList<AdaptiveResolutionQualityDimensionRequirement> RequiredDimensions,
    decimal MinimumConfidence);

public sealed record AdaptiveResolutionEscalation(
    bool Required,
    string Trigger,
    string? EscalateToSourceResolution,
    IReadOnlyList<string> ReasonCodes);

public sealed record AdaptiveResolutionOverrideResult(
    bool Requested,
    bool Applied,
    string? SourceResolution,
    string? UpscaleTarget,
    string? Path,
    string? StatusCode);

public sealed record AdaptiveResolutionCostEvaluation(
    bool Evaluated,
    bool? ConstraintSatisfied,
    string? ReasonCode);

public sealed record AdaptiveResolutionRationale(string Code, string Explanation);

public sealed record AdaptiveResolutionAlternative(
    string SourceResolution,
    string? UpscaleTarget,
    string Path,
    decimal QualityConfidence,
    string Tradeoff,
    IReadOnlyList<string> ReasonCodes);

public sealed record AdaptiveResolutionRecommendation(
    int ContractVersion,
    string RecommendedSourceResolution,
    string IntendedUpscaleTarget,
    string ProcessingPath,
    AdaptiveResolutionQualityRequirement QualityRequirement,
    decimal QualityConfidence,
    bool QcEscalationRequired,
    AdaptiveResolutionEscalation Escalation,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<AdaptiveResolutionRationale> Rationale,
    IReadOnlyList<AdaptiveResolutionAlternative> Alternatives,
    AdaptiveResolutionOverrideResult UserOverride,
    AdaptiveResolutionCostEvaluation CostEvaluation);

public sealed record AdaptiveResolutionValidationError(
    string Code,
    string Field,
    string Message);

public sealed class AdaptiveResolutionRequestValidationException(
    IReadOnlyList<AdaptiveResolutionValidationError> errors)
    : Exception("The adaptive resolution request is invalid.")
{
    public IReadOnlyList<AdaptiveResolutionValidationError> Errors { get; } = errors;
}

public sealed class AdaptiveResolutionRecommendationValidationException(
    IReadOnlyList<AdaptiveResolutionValidationError> errors)
    : Exception("The adaptive resolution recommendation is invalid.")
{
    public IReadOnlyList<AdaptiveResolutionValidationError> Errors { get; } = errors;
}

public static class AdaptiveResolutionRequestValidator
{
    public static IReadOnlyList<AdaptiveResolutionValidationError> Validate(AdaptiveResolutionRequest? request)
    {
        var errors = new List<AdaptiveResolutionValidationError>();
        if (request is null)
        {
            errors.Add(new("required", "request", "A request is required."));
            return errors;
        }

        var target = request.TargetMasterResolution?.Trim() ?? string.Empty;
        if (!AdaptiveResolutionContract.MasterResolutions.Contains(target))
            errors.Add(new("unsupported_value", nameof(request.TargetMasterResolution), "Target master resolution must be 1080p, 2k, or 4k."));

        var tier = request.ProjectQualityTier?.Trim() ?? string.Empty;
        if (!DirectorQualityLevels.QualityTiers.Contains(tier))
            errors.Add(new("unsupported_value", nameof(request.ProjectQualityTier), "Project quality tier must be Fast, Standard, Cinematic, or Studio."));

        ValidateRange(errors, nameof(request.ShotImportance), request.ShotImportance);
        ValidateRange(errors, nameof(request.DurationSeconds), request.DurationSeconds, AdaptiveResolutionContract.MinimumDurationSeconds, AdaptiveResolutionContract.MaximumDurationSeconds);
        ValidateRange(errors, nameof(request.MotionComplexity), request.MotionComplexity);
        ValidateRange(errors, nameof(request.CameraComplexity), request.CameraComplexity);
        ValidateRange(errors, nameof(request.FaceImportance), request.FaceImportance);
        ValidateRange(errors, nameof(request.HandBodyComplexity), request.HandBodyComplexity);
        ValidateRange(errors, nameof(request.FineDetailImportance), request.FineDetailImportance);
        ValidateRange(errors, nameof(request.ContinuitySensitivity), request.ContinuitySensitivity);
        ValidateRange(errors, nameof(request.EnvironmentComplexity), request.EnvironmentComplexity);
        ValidateRange(errors, nameof(request.VfxComplexity), request.VfxComplexity);
        ValidateRange(errors, nameof(request.TextSignageSensitivity), request.TextSignageSensitivity);
        ValidateRange(errors, nameof(request.LipSyncDependency), request.LipSyncDependency);
        ValidateRange(errors, nameof(request.UpscaleSuitability), request.UpscaleSuitability);
        ValidateRange(errors, nameof(request.SpeedPreference), request.SpeedPreference);

        if (request.RequiredQualityDimensions is null)
        {
            errors.Add(new("required", nameof(request.RequiredQualityDimensions), "Required quality dimensions cannot be null."));
        }
        else
        {
            if (request.RequiredQualityDimensions.Count > AdaptiveResolutionContract.MaximumQualityDimensions)
                errors.Add(new("too_many_items", nameof(request.RequiredQualityDimensions), $"No more than {AdaptiveResolutionContract.MaximumQualityDimensions} quality dimensions are allowed."));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < request.RequiredQualityDimensions.Count; index++)
            {
                var requirement = request.RequiredQualityDimensions[index];
                var field = $"{nameof(request.RequiredQualityDimensions)}[{index}]";
                var dimension = requirement?.Dimension?.Trim() ?? string.Empty;
                if (!AdaptiveResolutionContract.QualityDimensions.Contains(dimension))
                    errors.Add(new("unsupported_value", $"{field}.Dimension", "The quality dimension is not supported."));
                else if (!seen.Add(dimension))
                    errors.Add(new("duplicate", $"{field}.Dimension", "Each quality dimension may appear only once."));
                ValidateRange(errors, $"{field}.Importance", requirement?.Importance ?? -1);
            }
        }

        var costs = request.CostConstraints;
        if (costs is null)
        {
            errors.Add(new("required", nameof(request.CostConstraints), "Cost constraints cannot be null."));
        }
        else if (costs.MaxEstimatedCostUsd is < 0)
        {
            errors.Add(new("invalid_value", $"{nameof(request.CostConstraints)}.{nameof(costs.MaxEstimatedCostUsd)}", "Maximum estimated cost cannot be negative."));
        }

        ValidateOverride(errors, request);
        return errors;
    }

    private static void ValidateOverride(List<AdaptiveResolutionValidationError> errors, AdaptiveResolutionRequest request)
    {
        var overrideRequest = request.UserOverride;
        if (overrideRequest is null) return;

        var source = overrideRequest.SourceResolution?.Trim();
        if (source is not null && !AdaptiveResolutionContract.SourceResolutions.Contains(source))
            errors.Add(new("unsupported_value", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.SourceResolution)}", "Override source resolution is unsupported."));

        var target = request.TargetMasterResolution?.Trim() ?? string.Empty;
        var upscaleTarget = overrideRequest.UpscaleTarget?.Trim();
        if (upscaleTarget is not null && (!AdaptiveResolutionContract.MasterResolutions.Contains(upscaleTarget) || !string.Equals(upscaleTarget, target, StringComparison.OrdinalIgnoreCase)))
            errors.Add(new("conflict", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.UpscaleTarget)}", "Override upscale target must match the requested master resolution."));

        var path = overrideRequest.Path?.Trim();
        if (path is not null && !AdaptiveResolutionContract.Paths.Contains(path))
            errors.Add(new("unsupported_value", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.Path)}", "Override path is unsupported."));
        if (path is not null && source is null)
            errors.Add(new("required", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.SourceResolution)}", "An override path requires an override source resolution."));

        if (source is not null && AdaptiveResolutionContract.PixelsFor(source) > AdaptiveResolutionContract.PixelsFor(target))
            errors.Add(new("conflict", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.SourceResolution)}", "Override source resolution cannot exceed the requested master resolution."));

        if (source is not null && path is not null)
        {
            var sourcePixels = AdaptiveResolutionContract.PixelsFor(source);
            var targetPixels = AdaptiveResolutionContract.PixelsFor(target);
            if (string.Equals(path, AdaptiveResolutionPaths.Native, StringComparison.OrdinalIgnoreCase) && sourcePixels != targetPixels)
                errors.Add(new("conflict", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.Path)}", "A native override requires source and master resolutions to match."));
            if (string.Equals(path, AdaptiveResolutionPaths.Upscale, StringComparison.OrdinalIgnoreCase) && sourcePixels >= targetPixels)
                errors.Add(new("conflict", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.Path)}", "An upscale override requires source resolution below the master resolution."));
            if (string.Equals(path, AdaptiveResolutionPaths.NativeHighQualityThenMaster, StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(target, AdaptiveResolutionMasterResolutions.P4K, StringComparison.OrdinalIgnoreCase) || sourcePixels >= targetPixels))
                errors.Add(new("conflict", $"{nameof(request.UserOverride)}.{nameof(overrideRequest.Path)}", "Native high-quality mastering is only valid for a sub-4K source mastered to 4K."));
        }
    }

    private static void ValidateRange(List<AdaptiveResolutionValidationError> errors, string field, int value, int minimum = 0, int maximum = 100)
    {
        if (value < minimum || value > maximum)
            errors.Add(new("out_of_range", field, $"Value must be between {minimum} and {maximum}."));
    }
}

public static class AdaptiveResolutionRecommendationValidator
{
    public static IReadOnlyList<AdaptiveResolutionValidationError> Validate(
        AdaptiveResolutionRequest request,
        AdaptiveResolutionRecommendation? recommendation)
    {
        var errors = new List<AdaptiveResolutionValidationError>();
        if (recommendation is null)
        {
            errors.Add(new("required", "recommendation", "A recommendation is required."));
            return errors;
        }

        var target = request.TargetMasterResolution.Trim();
        var sourcePixels = AdaptiveResolutionContract.PixelsFor(recommendation.RecommendedSourceResolution);
        var targetPixels = AdaptiveResolutionContract.PixelsFor(target);
        if (!AdaptiveResolutionContract.SourceResolutions.Contains(recommendation.RecommendedSourceResolution ?? string.Empty))
            errors.Add(new("unsupported_value", nameof(recommendation.RecommendedSourceResolution), "Recommended source resolution is unsupported."));
        if (!string.Equals(recommendation.IntendedUpscaleTarget, target, StringComparison.OrdinalIgnoreCase))
            errors.Add(new("conflict", nameof(recommendation.IntendedUpscaleTarget), "Intended upscale target must match the requested master resolution."));
        if (sourcePixels > 0 && targetPixels > 0 && sourcePixels > targetPixels)
            errors.Add(new("conflict", nameof(recommendation.RecommendedSourceResolution), "Recommended source resolution cannot exceed the master resolution."));
        if (!AdaptiveResolutionContract.Paths.Contains(recommendation.ProcessingPath ?? string.Empty))
            errors.Add(new("unsupported_value", nameof(recommendation.ProcessingPath), "Processing path is unsupported."));
        if (string.Equals(recommendation.ProcessingPath, AdaptiveResolutionPaths.Native, StringComparison.OrdinalIgnoreCase) && sourcePixels != targetPixels)
            errors.Add(new("conflict", nameof(recommendation.ProcessingPath), "A native path requires source and master resolutions to match."));
        if (string.Equals(recommendation.ProcessingPath, AdaptiveResolutionPaths.Upscale, StringComparison.OrdinalIgnoreCase) && sourcePixels >= targetPixels)
            errors.Add(new("conflict", nameof(recommendation.ProcessingPath), "An upscale path requires source resolution below the master resolution."));
        if (string.Equals(recommendation.ProcessingPath, AdaptiveResolutionPaths.NativeHighQualityThenMaster, StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(target, AdaptiveResolutionMasterResolutions.P4K, StringComparison.OrdinalIgnoreCase) || sourcePixels >= targetPixels))
            errors.Add(new("conflict", nameof(recommendation.ProcessingPath), "Native high-quality mastering is only valid for a sub-4K source mastered to 4K."));
        if (recommendation.QualityConfidence is < 0 or > 1)
            errors.Add(new("out_of_range", nameof(recommendation.QualityConfidence), "Quality confidence must be between 0 and 1."));
        if (recommendation.Alternatives is null)
            errors.Add(new("required", nameof(recommendation.Alternatives), "Alternatives cannot be null."));
        else if (recommendation.Alternatives.Count > AdaptiveResolutionContract.MaximumAlternatives)
            errors.Add(new("too_many_items", nameof(recommendation.Alternatives), $"No more than {AdaptiveResolutionContract.MaximumAlternatives} alternatives are allowed."));
        if (recommendation.Escalation is null)
            errors.Add(new("required", nameof(recommendation.Escalation), "Escalation cannot be null."));
        else if (recommendation.Escalation.Required && string.IsNullOrWhiteSpace(recommendation.Escalation.Trigger))
            errors.Add(new("required", $"{nameof(recommendation.Escalation)}.{nameof(recommendation.Escalation.Trigger)}", "An escalation trigger is required when escalation is enabled."));
        if (recommendation.ContractVersion != AdaptiveResolutionContract.Version)
            errors.Add(new("unsupported_value", nameof(recommendation.ContractVersion), "Recommendation contract version is unsupported."));
        return errors;
    }
}

/// <summary>
/// Deterministic baseline planner. It intentionally does not select providers or estimate money.
/// A future capability/cost-aware router can use the recommendation as a requirement and return
/// the same output shape after evaluating available routes.
/// </summary>
public sealed class AdaptiveResolutionDirector
{
    private static readonly IReadOnlyDictionary<string, decimal> TierMinimumConfidence = new ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        [DirectorQualityLevels.Fast] = 0.55m,
        [DirectorQualityLevels.Standard] = 0.65m,
        [DirectorQualityLevels.Cinematic] = 0.75m,
        [DirectorQualityLevels.Studio] = 0.85m,
    });

    private static readonly IReadOnlyDictionary<string, int> TierSourceFloor = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [DirectorQualityLevels.Fast] = 480,
        [DirectorQualityLevels.Standard] = 720,
        [DirectorQualityLevels.Cinematic] = 1080,
        [DirectorQualityLevels.Studio] = 1440,
    });

    public AdaptiveResolutionRecommendation Recommend(AdaptiveResolutionRequest request)
    {
        var requestErrors = AdaptiveResolutionRequestValidator.Validate(request);
        if (requestErrors.Count > 0) throw new AdaptiveResolutionRequestValidationException(requestErrors);

        var target = request.TargetMasterResolution.Trim().ToLowerInvariant();
        var tier = request.ProjectQualityTier.Trim();
        var targetPixels = AdaptiveResolutionContract.PixelsFor(target);
        var demand = Demand(request);
        var sourcePixels = SelectSourcePixels(targetPixels, tier, demand, request);
        var overrideResult = ApplyOverride(request, target, ref sourcePixels, ref demand);
        var source = SourceForPixels(sourcePixels);
        var path = SelectPath(target, sourcePixels, targetPixels, tier, demand, request.UserOverride?.Path);
        var confidence = Confidence(request, sourcePixels, targetPixels, demand, path);
        var minimumConfidence = TierMinimumConfidence[tier];
        var escalationTargetPixels = request.CostConstraints.AllowQualityEscalation && sourcePixels < targetPixels ? NextSourcePixels(sourcePixels, targetPixels) : 0;
        var escalationRequired = confidence < minimumConfidence;
        var reasonCodes = BuildReasonCodes(request, sourcePixels, targetPixels, path, confidence, minimumConfidence, overrideResult.Applied);
        var rationale = BuildRationale(reasonCodes, request, source, target, confidence, minimumConfidence);
        var qualityRequirement = new AdaptiveResolutionQualityRequirement(
            tier,
            request.RequiredQualityDimensions.Select(item => new AdaptiveResolutionQualityDimensionRequirement(item.Dimension.Trim().ToLowerInvariant(), item.Importance)).ToArray(),
            minimumConfidence);
        var escalation = new AdaptiveResolutionEscalation(
            escalationRequired,
            escalationRequired ? AdaptiveResolutionReasonCodes.QualityConfidenceBelowRequirement : "",
            escalationRequired && escalationTargetPixels > 0 ? SourceForPixels(escalationTargetPixels) : null,
            escalationRequired ? [AdaptiveResolutionReasonCodes.QualityConfidenceBelowRequirement] : []);
        var alternatives = BuildAlternatives(request, target, targetPixels, sourcePixels, demand, confidence);
        var costEvaluation = new AdaptiveResolutionCostEvaluation(
            false,
            null,
            request.CostConstraints.PreferLowerCost || request.CostConstraints.MaxEstimatedCostUsd.HasValue
                ? AdaptiveResolutionReasonCodes.CostDataNotAvailable
                : null);
        var recommendation = new AdaptiveResolutionRecommendation(
            AdaptiveResolutionContract.Version,
            source,
            target,
            path,
            qualityRequirement,
            confidence,
            escalationRequired,
            escalation,
            reasonCodes,
            rationale,
            alternatives,
            overrideResult,
            costEvaluation);
        var recommendationErrors = AdaptiveResolutionRecommendationValidator.Validate(request, recommendation);
        if (recommendationErrors.Count > 0) throw new AdaptiveResolutionRecommendationValidationException(recommendationErrors);
        return recommendation;
    }

    private static decimal Demand(AdaptiveResolutionRequest request)
    {
        var weighted =
            request.ShotImportance * 0.15m
            + request.MotionComplexity * 0.10m
            + request.CameraComplexity * 0.08m
            + request.FaceImportance * 0.10m
            + request.HandBodyComplexity * 0.10m
            + request.FineDetailImportance * 0.12m
            + request.ContinuitySensitivity * 0.10m
            + request.EnvironmentComplexity * 0.06m
            + request.VfxComplexity * 0.07m
            + request.TextSignageSensitivity * 0.05m
            + request.LipSyncDependency * 0.07m;
        return Math.Clamp(decimal.Round(weighted / 100m, 3), 0m, 1m);
    }

    private static int SelectSourcePixels(int targetPixels, string tier, decimal demand, AdaptiveResolutionRequest request)
    {
        var baseline = targetPixels switch
        {
            1080 when demand >= 0.72m => 1080,
            1080 when demand >= 0.40m => 720,
            1080 => 480,
            1440 when demand >= 0.78m => 1440,
            1440 when demand >= 0.48m => 1080,
            1440 => 720,
            2160 when demand >= 0.82m => 2160,
            2160 when demand >= 0.62m => 1440,
            2160 when demand >= 0.40m => 1080,
            _ => 720,
        };
        var floor = TierSourceFloor[tier];
        var source = Math.Max(baseline, floor);
        if (request.SpeedPreference >= 80 && demand < 0.70m && source > floor) source = PreviousSourcePixels(source, floor);
        if (request.CostConstraints.PreferLowerCost && demand < 0.60m && source > floor) source = PreviousSourcePixels(source, floor);
        return Math.Min(source, targetPixels);
    }

    private static AdaptiveResolutionOverrideResult ApplyOverride(AdaptiveResolutionRequest request, string target, ref int sourcePixels, ref decimal demand)
    {
        var overrideRequest = request.UserOverride;
        if (overrideRequest is null) return new(false, false, null, null, null, null);
        if (!string.IsNullOrWhiteSpace(overrideRequest.SourceResolution)) sourcePixels = AdaptiveResolutionContract.PixelsFor(overrideRequest.SourceResolution);
        if (!string.IsNullOrWhiteSpace(overrideRequest.Path))
        {
            // The path is validated against the source/target relationship before planning.
            // It is retained exactly as a user intent and re-used by SelectPath.
        }
        demand = Math.Clamp(demand, 0m, 1m);
        return new(true, true, SourceForPixels(sourcePixels), target, overrideRequest.Path?.Trim().ToLowerInvariant(), AdaptiveResolutionReasonCodes.UserOverrideApplied);
    }

    private static string SelectPath(string target, int sourcePixels, int targetPixels, string tier, decimal demand, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath.Trim().ToLowerInvariant();
        if (sourcePixels == targetPixels) return AdaptiveResolutionPaths.Native;
        if (string.Equals(target, AdaptiveResolutionMasterResolutions.P4K, StringComparison.OrdinalIgnoreCase)
            && sourcePixels >= 1080 && (tier.Equals(DirectorQualityLevels.Studio, StringComparison.OrdinalIgnoreCase) || demand >= 0.65m))
            return AdaptiveResolutionPaths.NativeHighQualityThenMaster;
        return AdaptiveResolutionPaths.Upscale;
    }

    private static decimal Confidence(AdaptiveResolutionRequest request, int sourcePixels, int targetPixels, decimal demand, string path)
    {
        var sourceCoverage = targetPixels == 0 ? 0m : (decimal)sourcePixels / targetPixels;
        var confidence = 0.76m
            + (sourceCoverage * 0.17m)
            - (demand * 0.14m)
            - ((100 - request.UpscaleSuitability) / 100m * 0.16m)
            + (path == AdaptiveResolutionPaths.Native ? 0.08m : path == AdaptiveResolutionPaths.NativeHighQualityThenMaster ? 0.04m : 0m);
        if (request.FaceImportance >= 75) confidence -= 0.03m;
        if (request.HandBodyComplexity >= 75) confidence -= 0.03m;
        if (request.TextSignageSensitivity >= 75) confidence -= 0.02m;
        return decimal.Round(Math.Clamp(confidence, 0.05m, 0.99m), 2);
    }

    private static IReadOnlyList<string> BuildReasonCodes(AdaptiveResolutionRequest request, int sourcePixels, int targetPixels, string path, decimal confidence, decimal minimumConfidence, bool overrideApplied)
    {
        var codes = new List<string>();
        AddIf(codes, request.ShotImportance >= 70, AdaptiveResolutionReasonCodes.HighShotImportance);
        AddIf(codes, request.MotionComplexity >= 70, AdaptiveResolutionReasonCodes.HighMotionComplexity);
        AddIf(codes, request.CameraComplexity >= 70, AdaptiveResolutionReasonCodes.HighCameraComplexity);
        AddIf(codes, request.FaceImportance >= 70, AdaptiveResolutionReasonCodes.FaceSensitive);
        AddIf(codes, request.HandBodyComplexity >= 70, AdaptiveResolutionReasonCodes.AnatomySensitive);
        AddIf(codes, request.FineDetailImportance >= 70, AdaptiveResolutionReasonCodes.FineDetailSensitive);
        AddIf(codes, request.ContinuitySensitivity >= 70, AdaptiveResolutionReasonCodes.ContinuitySensitive);
        AddIf(codes, request.EnvironmentComplexity >= 70, AdaptiveResolutionReasonCodes.EnvironmentComplex);
        AddIf(codes, request.VfxComplexity >= 70, AdaptiveResolutionReasonCodes.VfxComplex);
        AddIf(codes, request.TextSignageSensitivity >= 70, AdaptiveResolutionReasonCodes.TextSensitive);
        AddIf(codes, request.LipSyncDependency >= 70, AdaptiveResolutionReasonCodes.LipSyncDependent);
        AddIf(codes, request.UpscaleSuitability < 35, AdaptiveResolutionReasonCodes.UpscaleRisk);
        AddIf(codes, request.SpeedPreference >= 80, AdaptiveResolutionReasonCodes.SpeedPreferred);
        AddIf(codes, request.CostConstraints.PreferLowerCost || request.CostConstraints.MaxEstimatedCostUsd.HasValue, AdaptiveResolutionReasonCodes.CostPreferred);
        codes.Add(path == AdaptiveResolutionPaths.Native ? AdaptiveResolutionReasonCodes.NativeSourceSelected : path == AdaptiveResolutionPaths.NativeHighQualityThenMaster ? AdaptiveResolutionReasonCodes.NativeHighQualityMastering : AdaptiveResolutionReasonCodes.UpscaleSelected);
        AddIf(codes, confidence < minimumConfidence, AdaptiveResolutionReasonCodes.QualityConfidenceBelowRequirement);
        AddIf(codes, overrideApplied, AdaptiveResolutionReasonCodes.UserOverrideApplied);
        return codes;
    }

    private static IReadOnlyList<AdaptiveResolutionRationale> BuildRationale(IReadOnlyList<string> codes, AdaptiveResolutionRequest request, string source, string target, decimal confidence, decimal minimumConfidence)
    {
        var rationale = new List<AdaptiveResolutionRationale>();
        foreach (var code in codes)
        {
            var message = code switch
            {
                AdaptiveResolutionReasonCodes.HighShotImportance => "The shot is important enough to protect with more source detail.",
                AdaptiveResolutionReasonCodes.HighMotionComplexity => "Motion complexity increases temporal artifact risk.",
                AdaptiveResolutionReasonCodes.HighCameraComplexity => "Camera complexity increases the need for source detail and stability.",
                AdaptiveResolutionReasonCodes.FaceSensitive => "Face importance raises identity and facial-detail requirements.",
                AdaptiveResolutionReasonCodes.AnatomySensitive => "Hand/body complexity raises anatomy and fine-detail requirements.",
                AdaptiveResolutionReasonCodes.FineDetailSensitive => "Fine-detail importance makes aggressive upscaling less suitable.",
                AdaptiveResolutionReasonCodes.ContinuitySensitive => "Continuity sensitivity favors a stronger and more stable source.",
                AdaptiveResolutionReasonCodes.EnvironmentComplex => "Environment complexity increases texture and edge-detail demands.",
                AdaptiveResolutionReasonCodes.VfxComplex => "VFX complexity increases temporal and compositing-detail demands.",
                AdaptiveResolutionReasonCodes.TextSensitive => "Text or signage sensitivity raises legibility requirements.",
                AdaptiveResolutionReasonCodes.LipSyncDependent => "Lip-sync dependency raises facial and temporal quality requirements.",
                AdaptiveResolutionReasonCodes.UpscaleRisk => "Low upscale suitability favors a higher source resolution.",
                AdaptiveResolutionReasonCodes.SpeedPreferred => "Speed preference was honored where it did not violate the quality floor.",
                AdaptiveResolutionReasonCodes.CostPreferred => "Lower-cost intent is recorded, but no provider pricing was evaluated.",
                AdaptiveResolutionReasonCodes.NativeSourceSelected => $"The {source} source matches the {target} master target, so no upscale is required.",
                AdaptiveResolutionReasonCodes.NativeHighQualityMastering => "A high-quality source is reserved for a 4K mastering step.",
                AdaptiveResolutionReasonCodes.UpscaleSelected => $"The {source} source is intended for an upscale to the {target} master.",
                AdaptiveResolutionReasonCodes.QualityConfidenceBelowRequirement => $"Predicted quality confidence {confidence:0.##} is below the {minimumConfidence:0.##} requirement; QC escalation is required.",
                AdaptiveResolutionReasonCodes.UserOverrideApplied => "The user override was applied and remains visible in the recommendation.",
                _ => "The director recorded a deterministic resolution decision.",
            };
            rationale.Add(new(code, message));
        }
        return rationale;
    }

    private static IReadOnlyList<AdaptiveResolutionAlternative> BuildAlternatives(AdaptiveResolutionRequest request, string target, int targetPixels, int sourcePixels, decimal demand, decimal confidence)
    {
        var alternatives = new List<AdaptiveResolutionAlternative>();
        var lower = PreviousSourcePixels(sourcePixels, 480);
        if (lower > 0 && lower < sourcePixels)
        {
            var lowerPath = lower == targetPixels ? AdaptiveResolutionPaths.Native : AdaptiveResolutionPaths.Upscale;
            alternatives.Add(new(SourceForPixels(lower), target, lowerPath, Confidence(request, lower, targetPixels, demand, lowerPath), "faster_or_lower_cost", [AdaptiveResolutionReasonCodes.SpeedPreferred]));
        }
        var higher = NextSourcePixels(sourcePixels, targetPixels);
        if (higher > sourcePixels && higher <= targetPixels)
        {
            var higherPath = higher == targetPixels ? AdaptiveResolutionPaths.Native : AdaptiveResolutionPaths.Upscale;
            alternatives.Add(new(SourceForPixels(higher), target, higherPath, Confidence(request, higher, targetPixels, demand, higherPath), "higher_quality_headroom", [AdaptiveResolutionReasonCodes.HighShotImportance]));
        }
        if (targetPixels == 2160 && sourcePixels < 2160 && !alternatives.Any(item => item.SourceResolution == AdaptiveResolutionSourceResolutions.P2160))
        {
            alternatives.Add(new(AdaptiveResolutionSourceResolutions.P2160, target, AdaptiveResolutionPaths.Native, Confidence(request, 2160, targetPixels, demand, AdaptiveResolutionPaths.Native), "native_4k_quality", [AdaptiveResolutionReasonCodes.NativeSourceSelected]));
        }
        return alternatives.Take(AdaptiveResolutionContract.MaximumAlternatives).ToArray();
    }

    private static int NextSourcePixels(int sourcePixels, int targetPixels) => new[] { 480, 720, 1080, 1440, 2160 }.FirstOrDefault(value => value > sourcePixels && value <= targetPixels);
    private static int PreviousSourcePixels(int sourcePixels, int floor) => new[] { 2160, 1440, 1080, 720, 480 }.FirstOrDefault(value => value < sourcePixels && value >= floor);
    private static string SourceForPixels(int pixels) => pixels switch
    {
        480 => AdaptiveResolutionSourceResolutions.P480,
        720 => AdaptiveResolutionSourceResolutions.P720,
        1080 => AdaptiveResolutionSourceResolutions.P1080,
        1440 => AdaptiveResolutionSourceResolutions.P1440,
        2160 => AdaptiveResolutionSourceResolutions.P2160,
        _ => string.Empty,
    };
    private static void AddIf(List<string> codes, bool condition, string code)
    {
        if (condition) codes.Add(code);
    }
}
