using System.Collections.ObjectModel;

namespace Taslim.Api.Movies.AdaptiveResolution;

/// <summary>
/// Wave-3 compatibility contract for adaptive resolution planning. The vocabulary is intentionally
/// independent of providers, models, prompts, credentials, and live generation operations.
/// </summary>
public static class AdaptiveResolutionWave3Contract
{
    public const int Version = 1;
    public const int MinimumDurationSeconds = 1;
    public const int MaximumDurationSeconds = 3_600;
    public const int MinimumScore = 0;
    public const int MaximumScore = 100;
    public const int MaximumQualityDimensions = 12;
    public const int MaximumAlternatives = 4;

    public static readonly IReadOnlySet<string> Resolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdaptiveResolutionWave3Resolutions.P480,
        AdaptiveResolutionWave3Resolutions.P720,
        AdaptiveResolutionWave3Resolutions.P1080,
        AdaptiveResolutionWave3Resolutions.P1440,
        AdaptiveResolutionWave3Resolutions.P2160,
    };

    public static readonly IReadOnlySet<string> QualityDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdaptiveResolutionWave3QualityDimensions.Sharpness,
        AdaptiveResolutionWave3QualityDimensions.TemporalStability,
        AdaptiveResolutionWave3QualityDimensions.DetailFidelity,
        AdaptiveResolutionWave3QualityDimensions.FaceIdentity,
        AdaptiveResolutionWave3QualityDimensions.Anatomy,
        AdaptiveResolutionWave3QualityDimensions.Continuity,
        AdaptiveResolutionWave3QualityDimensions.TextLegibility,
        AdaptiveResolutionWave3QualityDimensions.LipSync,
        AdaptiveResolutionWave3QualityDimensions.Composition,
    };

    public static readonly IReadOnlySet<string> PipelinePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdaptiveResolutionWave3PipelinePaths.Native,
        AdaptiveResolutionWave3PipelinePaths.Upscale,
        AdaptiveResolutionWave3PipelinePaths.NativeHighQualityThenMaster,
    };

    public static int PixelsFor(string? resolution) => NormalizeResolution(resolution) switch
    {
        AdaptiveResolutionWave3Resolutions.P480 => 480,
        AdaptiveResolutionWave3Resolutions.P720 => 720,
        AdaptiveResolutionWave3Resolutions.P1080 => 1080,
        AdaptiveResolutionWave3Resolutions.P1440 => 1440,
        AdaptiveResolutionWave3Resolutions.P2160 => 2160,
        _ => 0,
    };

    public static string NormalizeResolution(string? resolution) => resolution?.Trim().ToLowerInvariant() switch
    {
        "2k" => AdaptiveResolutionWave3Resolutions.P1440,
        "4k" => AdaptiveResolutionWave3Resolutions.P2160,
        AdaptiveResolutionWave3Resolutions.P480 => AdaptiveResolutionWave3Resolutions.P480,
        AdaptiveResolutionWave3Resolutions.P720 => AdaptiveResolutionWave3Resolutions.P720,
        AdaptiveResolutionWave3Resolutions.P1080 => AdaptiveResolutionWave3Resolutions.P1080,
        AdaptiveResolutionWave3Resolutions.P1440 => AdaptiveResolutionWave3Resolutions.P1440,
        AdaptiveResolutionWave3Resolutions.P2160 => AdaptiveResolutionWave3Resolutions.P2160,
        _ => string.Empty,
    };
}

public static class AdaptiveResolutionWave3Resolutions
{
    public const string P480 = "480p";
    public const string P720 = "720p";
    public const string P1080 = "1080p";
    public const string P1440 = "1440p";
    public const string P2160 = "2160p";
}

public static class AdaptiveResolutionWave3PipelinePaths
{
    public const string Native = "native";
    public const string Upscale = "upscale";
    public const string NativeHighQualityThenMaster = "native_high_quality_then_master";
}

public static class AdaptiveResolutionWave3QualityDimensions
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

public static class AdaptiveResolutionWave3ReasonCodes
{
    public const string HighImportance = "high_importance";
    public const string HighMotion = "high_motion";
    public const string HighCameraComplexity = "high_camera_complexity";
    public const string FaceSensitive = "face_sensitive";
    public const string AnatomySensitive = "anatomy_sensitive";
    public const string FineDetailSensitive = "fine_detail_sensitive";
    public const string ContinuitySensitive = "continuity_sensitive";
    public const string EnvironmentComplex = "environment_complex";
    public const string VfxComplex = "vfx_complex";
    public const string TextSensitive = "text_sensitive";
    public const string LipSyncDependent = "lip_sync_dependent";
    public const string UpscaleRisk = "upscale_risk";
    public const string SpeedPreferred = "speed_preferred";
    public const string CostPreferred = "cost_preferred";
    public const string NativeSelected = "native_selected";
    public const string UpscaleSelected = "upscale_selected";
    public const string NativeHighQualityMastering = "native_high_quality_mastering";
    public const string ConfidenceBelowRequirement = "confidence_below_requirement";
    public const string CostDataUnavailable = "cost_data_unavailable";
}

public sealed record AdaptiveResolutionWave3QualityDimensionRequirement(
    string Dimension,
    int Importance = 50);

/// <summary>
/// Cost inputs are preferences and user constraints only. This wave deliberately does not
/// evaluate money or call a catalog; future economics/routing code can consume this seam.
/// </summary>
public sealed record AdaptiveResolutionWave3Constraints(
    bool PreferLowerCost = false,
    decimal? MaxEstimatedCostUsd = null,
    bool AllowQualityEscalation = true);

/// <summary>
/// All scores use an inclusive 0..100 scale. A higher value means more importance, complexity,
/// dependency, or tolerance as named by the field.
/// </summary>
public sealed record AdaptiveResolutionDirectorRequest
{
    public string MasterTargetResolution { get; init; } = AdaptiveResolutionWave3Resolutions.P1080;
    public string QualityTier { get; init; } = DirectorQualityLevels.Standard;
    public int Importance { get; init; } = 50;
    public int DurationSeconds { get; init; } = 5;
    public int MotionComplexity { get; init; } = 50;
    public int CameraComplexity { get; init; } = 50;
    public int FaceImportance { get; init; }
    public int HandsBodyComplexity { get; init; }
    public int FineDetailImportance { get; init; } = 50;
    public int ContinuitySensitivity { get; init; } = 50;
    public int EnvironmentComplexity { get; init; } = 50;
    public int VfxComplexity { get; init; }
    public int TextSignageSensitivity { get; init; }
    public int LipSyncDependency { get; init; }
    public int UpscaleSuitability { get; init; } = 50;
    public IReadOnlyList<AdaptiveResolutionWave3QualityDimensionRequirement> RequiredQualityDimensions { get; init; } = [];
    public int SpeedPreference { get; init; } = 50;
    public AdaptiveResolutionWave3Constraints Constraints { get; init; } = new();
}

public sealed record AdaptiveResolutionWave3QualityRequirement(
    string QualityTier,
    IReadOnlyList<AdaptiveResolutionWave3QualityDimensionRequirement> RequiredDimensions,
    decimal MinimumConfidence);

public sealed record AdaptiveResolutionWave3Escalation(
    bool Required,
    string Trigger,
    string? EscalateToSourceResolution,
    IReadOnlyList<string> ReasonCodes);

public sealed record AdaptiveResolutionWave3Alternative(
    string SourceResolution,
    string MasterTargetResolution,
    string PipelinePath,
    decimal QualityConfidence,
    string Tradeoff,
    IReadOnlyList<string> ReasonCodes);

public sealed record AdaptiveResolutionWave3CostEvaluation(
    bool Evaluated,
    bool? ConstraintSatisfied,
    string? ReasonCode);

public sealed record AdaptiveResolutionWave3Rationale(string Code, string Explanation);

public sealed record AdaptiveResolutionDirectorRecommendation(
    int ContractVersion,
    string SourceResolution,
    string MasterTargetResolution,
    string PipelinePath,
    AdaptiveResolutionWave3QualityRequirement QualityRequirement,
    decimal QualityConfidence,
    bool QcEscalationRequired,
    AdaptiveResolutionWave3Escalation Escalation,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<AdaptiveResolutionWave3Rationale> Rationale,
    IReadOnlyList<AdaptiveResolutionWave3Alternative> Alternatives,
    AdaptiveResolutionWave3CostEvaluation CostEvaluation);

public sealed record AdaptiveResolutionWave3ValidationError(
    string Code,
    string Field,
    string Message);

public sealed class AdaptiveResolutionDirectorRequestValidationException(
    IReadOnlyList<AdaptiveResolutionWave3ValidationError> errors)
    : Exception("The adaptive resolution director request is invalid.")
{
    public IReadOnlyList<AdaptiveResolutionWave3ValidationError> Errors { get; } = errors;
}

public sealed class AdaptiveResolutionDirectorRecommendationValidationException(
    IReadOnlyList<AdaptiveResolutionWave3ValidationError> errors)
    : Exception("The adaptive resolution director recommendation is invalid.")
{
    public IReadOnlyList<AdaptiveResolutionWave3ValidationError> Errors { get; } = errors;
}

public static class AdaptiveResolutionDirectorRequestValidator
{
    public static IReadOnlyList<AdaptiveResolutionWave3ValidationError> Validate(AdaptiveResolutionDirectorRequest? request)
    {
        var errors = new List<AdaptiveResolutionWave3ValidationError>();
        if (request is null)
        {
            errors.Add(new("required", "request", "A request is required."));
            return errors;
        }

        var target = AdaptiveResolutionWave3Contract.NormalizeResolution(request.MasterTargetResolution);
        if (!AdaptiveResolutionWave3Contract.Resolutions.Contains(target))
            errors.Add(new("unsupported_value", nameof(request.MasterTargetResolution), "Master target resolution must be 480p, 720p, 1080p, 1440p, or 2160p."));

        var tier = request.QualityTier?.Trim() ?? string.Empty;
        if (!DirectorQualityLevels.QualityTiers.Contains(tier))
            errors.Add(new("unsupported_value", nameof(request.QualityTier), "Quality tier must be Fast, Standard, Cinematic, or Studio."));

        ValidateRange(errors, nameof(request.Importance), request.Importance);
        ValidateRange(errors, nameof(request.DurationSeconds), request.DurationSeconds, AdaptiveResolutionWave3Contract.MinimumDurationSeconds, AdaptiveResolutionWave3Contract.MaximumDurationSeconds);
        ValidateRange(errors, nameof(request.MotionComplexity), request.MotionComplexity);
        ValidateRange(errors, nameof(request.CameraComplexity), request.CameraComplexity);
        ValidateRange(errors, nameof(request.FaceImportance), request.FaceImportance);
        ValidateRange(errors, nameof(request.HandsBodyComplexity), request.HandsBodyComplexity);
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
            if (request.RequiredQualityDimensions.Count > AdaptiveResolutionWave3Contract.MaximumQualityDimensions)
                errors.Add(new("too_many_items", nameof(request.RequiredQualityDimensions), $"No more than {AdaptiveResolutionWave3Contract.MaximumQualityDimensions} quality dimensions are allowed."));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < request.RequiredQualityDimensions.Count; index++)
            {
                var item = request.RequiredQualityDimensions[index];
                var field = $"{nameof(request.RequiredQualityDimensions)}[{index}]";
                var dimension = item?.Dimension?.Trim() ?? string.Empty;
                if (!AdaptiveResolutionWave3Contract.QualityDimensions.Contains(dimension))
                    errors.Add(new("unsupported_value", $"{field}.{nameof(item.Dimension)}", "The quality dimension is unsupported."));
                else if (!seen.Add(dimension))
                    errors.Add(new("duplicate", $"{field}.{nameof(item.Dimension)}", "Each quality dimension may appear only once."));
                ValidateRange(errors, $"{field}.{nameof(item.Importance)}", item?.Importance ?? -1);
            }
        }

        if (request.Constraints is null)
        {
            errors.Add(new("required", nameof(request.Constraints), "Resolution constraints cannot be null."));
        }
        else if (request.Constraints.MaxEstimatedCostUsd is < 0)
        {
            errors.Add(new("invalid_value", $"{nameof(request.Constraints)}.{nameof(request.Constraints.MaxEstimatedCostUsd)}", "Maximum estimated cost cannot be negative."));
        }

        return errors;
    }

    private static void ValidateRange(List<AdaptiveResolutionWave3ValidationError> errors, string field, int value, int minimum = 0, int maximum = 100)
    {
        if (value < minimum || value > maximum)
            errors.Add(new("out_of_range", field, $"Value must be between {minimum} and {maximum}."));
    }
}

public static class AdaptiveResolutionDirectorRecommendationValidator
{
    public static IReadOnlyList<AdaptiveResolutionWave3ValidationError> Validate(
        AdaptiveResolutionDirectorRequest request,
        AdaptiveResolutionDirectorRecommendation? recommendation)
    {
        var errors = new List<AdaptiveResolutionWave3ValidationError>();
        if (recommendation is null)
        {
            errors.Add(new("required", "recommendation", "A recommendation is required."));
            return errors;
        }

        var sourcePixels = AdaptiveResolutionWave3Contract.PixelsFor(recommendation.SourceResolution);
        var target = AdaptiveResolutionWave3Contract.NormalizeResolution(request.MasterTargetResolution);
        var targetPixels = AdaptiveResolutionWave3Contract.PixelsFor(target);
        if (!AdaptiveResolutionWave3Contract.Resolutions.Contains(recommendation.SourceResolution ?? string.Empty))
            errors.Add(new("unsupported_value", nameof(recommendation.SourceResolution), "Source resolution is unsupported."));
        if (!string.Equals(recommendation.MasterTargetResolution, target, StringComparison.OrdinalIgnoreCase))
            errors.Add(new("conflict", nameof(recommendation.MasterTargetResolution), "Master target must match the requested target."));
        if (sourcePixels > targetPixels && targetPixels > 0)
            errors.Add(new("conflict", nameof(recommendation.SourceResolution), "Source resolution cannot exceed the master target."));
        if (!AdaptiveResolutionWave3Contract.PipelinePaths.Contains(recommendation.PipelinePath ?? string.Empty))
            errors.Add(new("unsupported_value", nameof(recommendation.PipelinePath), "Pipeline path is unsupported."));
        if (string.Equals(recommendation.PipelinePath, AdaptiveResolutionWave3PipelinePaths.Native, StringComparison.OrdinalIgnoreCase) && sourcePixels != targetPixels)
            errors.Add(new("conflict", nameof(recommendation.PipelinePath), "Native processing requires source and target resolutions to match."));
        if (string.Equals(recommendation.PipelinePath, AdaptiveResolutionWave3PipelinePaths.Upscale, StringComparison.OrdinalIgnoreCase) && sourcePixels >= targetPixels)
            errors.Add(new("conflict", nameof(recommendation.PipelinePath), "Upscale processing requires source resolution below the target."));
        if (string.Equals(recommendation.PipelinePath, AdaptiveResolutionWave3PipelinePaths.NativeHighQualityThenMaster, StringComparison.OrdinalIgnoreCase)
            && (targetPixels != 2160 || sourcePixels >= targetPixels))
            errors.Add(new("conflict", nameof(recommendation.PipelinePath), "High-quality mastering requires a sub-2160p source and a 2160p target."));
        if (recommendation.QualityConfidence is < 0 or > 1)
            errors.Add(new("out_of_range", nameof(recommendation.QualityConfidence), "Quality confidence must be between 0 and 1."));
        if (recommendation.Alternatives is null)
            errors.Add(new("required", nameof(recommendation.Alternatives), "Alternatives cannot be null."));
        else if (recommendation.Alternatives.Count > AdaptiveResolutionWave3Contract.MaximumAlternatives)
            errors.Add(new("too_many_items", nameof(recommendation.Alternatives), $"No more than {AdaptiveResolutionWave3Contract.MaximumAlternatives} alternatives are allowed."));
        if (recommendation.Escalation is null)
            errors.Add(new("required", nameof(recommendation.Escalation), "Escalation cannot be null."));
        else if (recommendation.Escalation.Required && string.IsNullOrWhiteSpace(recommendation.Escalation.Trigger))
            errors.Add(new("required", $"{nameof(recommendation.Escalation)}.{nameof(recommendation.Escalation.Trigger)}", "An escalation trigger is required when escalation is required."));
        if (recommendation.ContractVersion != AdaptiveResolutionWave3Contract.Version)
            errors.Add(new("unsupported_value", nameof(recommendation.ContractVersion), "Recommendation contract version is unsupported."));
        return errors;
    }
}

/// <summary>
/// Pure deterministic planner. It produces a resolution intent only; it never benchmarks or
/// invokes a provider and never derives a price from a provider or model catalog.
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

    private static readonly int[] ResolutionSteps = [480, 720, 1080, 1440, 2160];

    public AdaptiveResolutionDirectorRecommendation Recommend(AdaptiveResolutionDirectorRequest request)
    {
        var requestErrors = AdaptiveResolutionDirectorRequestValidator.Validate(request);
        if (requestErrors.Count > 0)
            throw new AdaptiveResolutionDirectorRequestValidationException(requestErrors);

        var target = AdaptiveResolutionWave3Contract.NormalizeResolution(request.MasterTargetResolution);
        var targetPixels = AdaptiveResolutionWave3Contract.PixelsFor(target);
        var tier = request.QualityTier.Trim();
        var demand = CalculateDemand(request);
        var sourcePixels = SelectInitialSource(targetPixels, tier, demand, request);
        var minimumConfidence = TierMinimumConfidence[tier];

        if (request.SpeedPreference >= 80 || request.Constraints.PreferLowerCost)
        {
            var lower = PreviousSourcePixels(sourcePixels, TierSourceFloor[tier]);
            if (lower >= TierSourceFloor[tier] && QualityConfidence(request, lower, targetPixels, demand, SelectPipeline(lower, targetPixels, tier, demand)) >= minimumConfidence)
                sourcePixels = lower;
        }

        var pipeline = SelectPipeline(sourcePixels, targetPixels, tier, demand);
        var confidence = QualityConfidence(request, sourcePixels, targetPixels, demand, pipeline);
        var escalationRequired = confidence < minimumConfidence;
        var escalationTarget = escalationRequired && request.Constraints.AllowQualityEscalation
            ? NextSourcePixels(sourcePixels, targetPixels)
            : 0;
        var reasonCodes = BuildReasonCodes(request, pipeline, confidence, minimumConfidence);
        var escalation = new AdaptiveResolutionWave3Escalation(
            escalationRequired,
            escalationRequired ? AdaptiveResolutionWave3ReasonCodes.ConfidenceBelowRequirement : string.Empty,
            escalationTarget > 0 ? SourceForPixels(escalationTarget) : null,
            escalationRequired ? [AdaptiveResolutionWave3ReasonCodes.ConfidenceBelowRequirement] : []);
        var recommendation = new AdaptiveResolutionDirectorRecommendation(
            AdaptiveResolutionWave3Contract.Version,
            SourceForPixels(sourcePixels),
            target,
            pipeline,
            new AdaptiveResolutionWave3QualityRequirement(
                tier,
                request.RequiredQualityDimensions.Select(item => new AdaptiveResolutionWave3QualityDimensionRequirement(item.Dimension.Trim().ToLowerInvariant(), item.Importance)).ToArray(),
                minimumConfidence),
            confidence,
            escalationRequired,
            escalation,
            reasonCodes,
            BuildRationale(reasonCodes, request, SourceForPixels(sourcePixels), target, confidence, minimumConfidence),
            BuildAlternatives(request, targetPixels, sourcePixels, tier, demand),
            new AdaptiveResolutionWave3CostEvaluation(
                false,
                null,
                request.Constraints.PreferLowerCost || request.Constraints.MaxEstimatedCostUsd.HasValue
                    ? AdaptiveResolutionWave3ReasonCodes.CostDataUnavailable
                    : null));

        var recommendationErrors = AdaptiveResolutionDirectorRecommendationValidator.Validate(request, recommendation);
        if (recommendationErrors.Count > 0)
            throw new AdaptiveResolutionDirectorRecommendationValidationException(recommendationErrors);
        return recommendation;
    }

    private static decimal CalculateDemand(AdaptiveResolutionDirectorRequest request)
    {
        var durationPressure = Math.Clamp((request.DurationSeconds - 1) / 119m * 100m, 0m, 100m);
        var requiredPressure = request.RequiredQualityDimensions.Count == 0
            ? 0m
            : request.RequiredQualityDimensions.Max(item => item.Importance);
        var weighted =
            request.Importance * 0.13m
            + request.MotionComplexity * 0.10m
            + request.CameraComplexity * 0.08m
            + request.FaceImportance * 0.10m
            + request.HandsBodyComplexity * 0.10m
            + request.FineDetailImportance * 0.11m
            + request.ContinuitySensitivity * 0.09m
            + request.EnvironmentComplexity * 0.06m
            + request.VfxComplexity * 0.07m
            + request.TextSignageSensitivity * 0.05m
            + request.LipSyncDependency * 0.06m
            + durationPressure * 0.03m
            + requiredPressure * 0.02m;
        return Math.Clamp(decimal.Round(weighted / 100m, 3), 0m, 1m);
    }

    private static int SelectInitialSource(int targetPixels, string tier, decimal demand, AdaptiveResolutionDirectorRequest request)
    {
        var baseline = targetPixels switch
        {
            480 => 480,
            720 when demand >= 0.55m => 720,
            720 => 480,
            1080 when demand >= 0.72m => 1080,
            1080 when demand >= 0.40m => 720,
            1080 => 480,
            1440 when demand >= 0.80m => 1440,
            1440 when demand >= 0.50m => 1080,
            1440 => 720,
            2160 when demand >= 0.82m => 2160,
            2160 when demand >= 0.62m => 1440,
            2160 when demand >= 0.40m => 1080,
            _ => 720,
        };
        var floor = TierSourceFloor[tier];
        var source = Math.Min(Math.Max(baseline, floor), targetPixels);
        // Suitability is a risk signal, not permission to bypass QC by automatically
        // selecting the highest source. Let the confidence gate decide whether escalation
        // is required for demanding shots.
        if (request.UpscaleSuitability < 35 && demand >= 0.80m && source < targetPixels)
            source = NextSourcePixels(source, targetPixels);
        if (request.UpscaleSuitability < 15 && demand >= 0.92m && source < targetPixels)
            source = NextSourcePixels(source, targetPixels);
        return source;
    }

    private static string SelectPipeline(int sourcePixels, int targetPixels, string tier, decimal demand)
    {
        if (sourcePixels == targetPixels)
            return AdaptiveResolutionWave3PipelinePaths.Native;
        if (targetPixels == 2160 && sourcePixels >= 1080 && (tier.Equals(DirectorQualityLevels.Studio, StringComparison.OrdinalIgnoreCase) || demand >= 0.65m))
            return AdaptiveResolutionWave3PipelinePaths.NativeHighQualityThenMaster;
        return AdaptiveResolutionWave3PipelinePaths.Upscale;
    }

    private static decimal QualityConfidence(AdaptiveResolutionDirectorRequest request, int sourcePixels, int targetPixels, decimal demand, string pipeline)
    {
        var sourceCoverage = targetPixels == 0 ? 0m : (decimal)sourcePixels / targetPixels;
        var confidence = 0.76m
            + sourceCoverage * 0.17m
            - demand * 0.14m
            - ((100 - request.UpscaleSuitability) / 100m * 0.16m)
            + (pipeline == AdaptiveResolutionWave3PipelinePaths.Native ? 0.08m : pipeline == AdaptiveResolutionWave3PipelinePaths.NativeHighQualityThenMaster ? 0.04m : 0m);
        if (request.FaceImportance >= 75) confidence -= 0.03m;
        if (request.HandsBodyComplexity >= 75) confidence -= 0.03m;
        if (request.TextSignageSensitivity >= 75) confidence -= 0.02m;
        return decimal.Round(Math.Clamp(confidence, 0.05m, 0.99m), 2);
    }

    private static IReadOnlyList<string> BuildReasonCodes(AdaptiveResolutionDirectorRequest request, string pipeline, decimal confidence, decimal minimumConfidence)
    {
        var codes = new List<string>();
        AddIf(codes, request.Importance >= 70, AdaptiveResolutionWave3ReasonCodes.HighImportance);
        AddIf(codes, request.MotionComplexity >= 70, AdaptiveResolutionWave3ReasonCodes.HighMotion);
        AddIf(codes, request.CameraComplexity >= 70, AdaptiveResolutionWave3ReasonCodes.HighCameraComplexity);
        AddIf(codes, request.FaceImportance >= 70, AdaptiveResolutionWave3ReasonCodes.FaceSensitive);
        AddIf(codes, request.HandsBodyComplexity >= 70, AdaptiveResolutionWave3ReasonCodes.AnatomySensitive);
        AddIf(codes, request.FineDetailImportance >= 70, AdaptiveResolutionWave3ReasonCodes.FineDetailSensitive);
        AddIf(codes, request.ContinuitySensitivity >= 70, AdaptiveResolutionWave3ReasonCodes.ContinuitySensitive);
        AddIf(codes, request.EnvironmentComplexity >= 70, AdaptiveResolutionWave3ReasonCodes.EnvironmentComplex);
        AddIf(codes, request.VfxComplexity >= 70, AdaptiveResolutionWave3ReasonCodes.VfxComplex);
        AddIf(codes, request.TextSignageSensitivity >= 70, AdaptiveResolutionWave3ReasonCodes.TextSensitive);
        AddIf(codes, request.LipSyncDependency >= 70, AdaptiveResolutionWave3ReasonCodes.LipSyncDependent);
        AddIf(codes, request.UpscaleSuitability < 35, AdaptiveResolutionWave3ReasonCodes.UpscaleRisk);
        AddIf(codes, request.SpeedPreference >= 80, AdaptiveResolutionWave3ReasonCodes.SpeedPreferred);
        AddIf(codes, request.Constraints.PreferLowerCost || request.Constraints.MaxEstimatedCostUsd.HasValue, AdaptiveResolutionWave3ReasonCodes.CostPreferred);
        codes.Add(pipeline switch
        {
            AdaptiveResolutionWave3PipelinePaths.Native => AdaptiveResolutionWave3ReasonCodes.NativeSelected,
            AdaptiveResolutionWave3PipelinePaths.NativeHighQualityThenMaster => AdaptiveResolutionWave3ReasonCodes.NativeHighQualityMastering,
            _ => AdaptiveResolutionWave3ReasonCodes.UpscaleSelected,
        });
        AddIf(codes, confidence < minimumConfidence, AdaptiveResolutionWave3ReasonCodes.ConfidenceBelowRequirement);
        return codes;
    }

    private static IReadOnlyList<AdaptiveResolutionWave3Rationale> BuildRationale(
        IReadOnlyList<string> codes,
        AdaptiveResolutionDirectorRequest request,
        string source,
        string target,
        decimal confidence,
        decimal minimumConfidence)
    {
        return codes.Select(code => new AdaptiveResolutionWave3Rationale(code, code switch
        {
            AdaptiveResolutionWave3ReasonCodes.HighImportance => "Shot importance favors preserving source detail.",
            AdaptiveResolutionWave3ReasonCodes.HighMotion => "Motion complexity increases temporal artifact risk.",
            AdaptiveResolutionWave3ReasonCodes.HighCameraComplexity => "Camera complexity increases stability and detail demands.",
            AdaptiveResolutionWave3ReasonCodes.FaceSensitive => "Face importance raises identity and facial-detail requirements.",
            AdaptiveResolutionWave3ReasonCodes.AnatomySensitive => "Hands and body complexity raise anatomy requirements.",
            AdaptiveResolutionWave3ReasonCodes.FineDetailSensitive => "Fine-detail importance reduces tolerance for aggressive upscaling.",
            AdaptiveResolutionWave3ReasonCodes.ContinuitySensitive => "Continuity sensitivity favors a stronger, stable source.",
            AdaptiveResolutionWave3ReasonCodes.EnvironmentComplex => "Environment complexity increases texture and edge-detail demands.",
            AdaptiveResolutionWave3ReasonCodes.VfxComplex => "VFX complexity increases temporal and compositing-detail demands.",
            AdaptiveResolutionWave3ReasonCodes.TextSensitive => "Text or signage sensitivity raises legibility requirements.",
            AdaptiveResolutionWave3ReasonCodes.LipSyncDependent => "Lip-sync dependency raises facial and temporal quality requirements.",
            AdaptiveResolutionWave3ReasonCodes.UpscaleRisk => "Low upscale suitability favors a higher source resolution.",
            AdaptiveResolutionWave3ReasonCodes.SpeedPreferred => "Speed preference was honored only when the quality floor remained satisfied.",
            AdaptiveResolutionWave3ReasonCodes.CostPreferred => "Lower-cost intent is recorded, but no pricing data was evaluated.",
            AdaptiveResolutionWave3ReasonCodes.NativeSelected => $"The {source} source matches the {target} master target.",
            AdaptiveResolutionWave3ReasonCodes.NativeHighQualityMastering => "A high-quality source is reserved for final mastering to the target.",
            AdaptiveResolutionWave3ReasonCodes.UpscaleSelected => $"The {source} source is intended for an upscale to the {target} master.",
            AdaptiveResolutionWave3ReasonCodes.ConfidenceBelowRequirement => $"Predicted confidence {confidence:0.##} is below the {minimumConfidence:0.##} requirement; QC review is required.",
            _ => "The director recorded a deterministic resolution decision.",
        })).ToArray();
    }

    private static IReadOnlyList<AdaptiveResolutionWave3Alternative> BuildAlternatives(AdaptiveResolutionDirectorRequest request, int targetPixels, int sourcePixels, string tier, decimal demand)
    {
        var alternatives = new List<AdaptiveResolutionWave3Alternative>();
        var lower = PreviousSourcePixels(sourcePixels, TierSourceFloor[tier]);
        if (lower >= TierSourceFloor[tier])
        {
            var path = SelectPipeline(lower, targetPixels, tier, demand);
            alternatives.Add(new(SourceForPixels(lower), SourceForPixels(targetPixels), path, QualityConfidence(request, lower, targetPixels, demand, path), "faster_or_lower_cost", [AdaptiveResolutionWave3ReasonCodes.SpeedPreferred]));
        }
        var higher = NextSourcePixels(sourcePixels, targetPixels);
        if (higher > sourcePixels)
        {
            var path = SelectPipeline(higher, targetPixels, tier, demand);
            alternatives.Add(new(SourceForPixels(higher), SourceForPixels(targetPixels), path, QualityConfidence(request, higher, targetPixels, demand, path), "higher_quality_headroom", [AdaptiveResolutionWave3ReasonCodes.HighImportance]));
        }
        return alternatives.Take(AdaptiveResolutionWave3Contract.MaximumAlternatives).ToArray();
    }

    private static int NextSourcePixels(int sourcePixels, int targetPixels) => ResolutionSteps.FirstOrDefault(value => value > sourcePixels && value <= targetPixels);

    private static int PreviousSourcePixels(int sourcePixels, int floor) => ResolutionSteps.Reverse().FirstOrDefault(value => value < sourcePixels && value >= floor);

    private static string SourceForPixels(int pixels) => pixels switch
    {
        480 => AdaptiveResolutionWave3Resolutions.P480,
        720 => AdaptiveResolutionWave3Resolutions.P720,
        1080 => AdaptiveResolutionWave3Resolutions.P1080,
        1440 => AdaptiveResolutionWave3Resolutions.P1440,
        2160 => AdaptiveResolutionWave3Resolutions.P2160,
        _ => string.Empty,
    };

    private static void AddIf(List<string> codes, bool condition, string code)
    {
        if (condition)
            codes.Add(code);
    }
}
