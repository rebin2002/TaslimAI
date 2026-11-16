using System.Text.Json;

namespace Taslim.Api.Movies.Wave3Integration;

/// <summary>
/// Compatibility-only vocabulary for composing Wave 3 branches. It carries product
/// intent and safety decisions, never a provider, model, prompt, credential, or
/// paid execution identifier.
/// </summary>
public static class MovieWave3ResolutionContract
{
    public const string P480 = "480p";
    public const string P720 = "720p";
    public const string P1080 = "1080p";
    public const string P1440 = "1440p";
    public const string P2160 = "2160p";
    public const string Native = "native";
    public const string Upscale = "upscale";
    public const string NativeHighQualityThenMaster = "native_high_quality_then_master";

    public static readonly IReadOnlyList<string> SupportedResolutions = [P480, P720, P1080, P1440, P2160];

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "2k" => P1440,
        "4k" => P2160,
        P480 => P480,
        P720 => P720,
        P1080 => P1080,
        P1440 => P1440,
        P2160 => P2160,
        _ => string.Empty,
    };

    public static int Height(string resolution) => resolution switch
    {
        P480 => 480,
        P720 => 720,
        P1080 => 1080,
        P1440 => 1440,
        P2160 => 2160,
        _ => 0,
    };
}

public sealed record MovieWave3AdaptiveResolutionRequest(
    string MasterTargetResolution,
    string QualityTier,
    int Importance = 50,
    int MotionComplexity = 50,
    int CameraComplexity = 50,
    int FaceImportance = 0,
    int FineDetailImportance = 50,
    int ContinuitySensitivity = 50,
    int UpscaleSuitability = 50,
    bool EconomicalDraft = false,
    bool AllowQualityEscalation = true);

public sealed record MovieWave3CostEstimate(
    bool IsKnown,
    decimal? AmountUsd,
    string Currency,
    string? Reason);

public sealed record MovieWave3AdaptiveResolutionRecommendation(
    string SourceResolution,
    string MasterTargetResolution,
    string PipelinePath,
    decimal QualityConfidence,
    bool QcEscalationRequired,
    string? EscalateToSourceResolution,
    MovieWave3CostEstimate CostEstimate,
    IReadOnlyList<string> ReasonCodes);

/// <summary>
/// A deterministic seam, not a provider router. Integrated resolution and
/// benchmark branches can replace this implementation while preserving the
/// request/response boundary and acceptance vectors.
/// </summary>
public static class MovieWave3AdaptiveResolutionCompatibility
{
    public static MovieWave3AdaptiveResolutionRecommendation Recommend(MovieWave3AdaptiveResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var target = MovieWave3ResolutionContract.Normalize(request.MasterTargetResolution);
        var tierFloor = request.QualityTier.Trim() switch
        {
            MovieQualityLevels.Fast => 480,
            MovieQualityLevels.Standard => 720,
            MovieQualityLevels.Cinematic => 1080,
            MovieQualityLevels.Studio => 1440,
            _ => throw new ArgumentException("Unsupported quality tier.", nameof(request)),
        };
        var demand = Math.Max(
            Math.Max(Math.Max(request.Importance, request.MotionComplexity), Math.Max(request.CameraComplexity, request.FaceImportance)),
            Math.Max(Math.Max(request.FineDetailImportance, request.ContinuitySensitivity), 100 - request.UpscaleSuitability));
        var targetHeight = MovieWave3ResolutionContract.Height(target);
        var sourceHeight = request.EconomicalDraft
            ? Math.Max(720, Math.Min(targetHeight, 720))
            : Math.Max(tierFloor, targetHeight >= 2160 && demand >= 70 ? 1440 : targetHeight >= 1440 && demand >= 80 ? 1080 : Math.Min(targetHeight, tierFloor));
        var source = MovieWave3ResolutionContract.SupportedResolutions
            .OrderBy(value => MovieWave3ResolutionContract.Height(value))
            .First(value => MovieWave3ResolutionContract.Height(value) >= sourceHeight);
        var sourcePixels = MovieWave3ResolutionContract.Height(source);
        var path = sourcePixels == targetHeight
            ? MovieWave3ResolutionContract.Native
            : sourcePixels < targetHeight && sourcePixels >= 1440 && targetHeight >= 2160
                ? MovieWave3ResolutionContract.NativeHighQualityThenMaster
                : MovieWave3ResolutionContract.Upscale;
        var coverage = targetHeight == 0 ? 0m : Math.Min(1m, sourcePixels / (decimal)targetHeight);
        var confidence = Math.Clamp(
            Math.Round(0.55m + (coverage * 0.25m) + (request.UpscaleSuitability / 100m * 0.20m) - (demand / 100m * 0.18m), 2),
            0m,
            1m);
        var escalation = confidence < 0.72m;
        var nextSource = escalation && request.AllowQualityEscalation
            ? MovieWave3ResolutionContract.SupportedResolutions
                .Where(value => MovieWave3ResolutionContract.Height(value) > sourcePixels)
                .OrderBy(value => MovieWave3ResolutionContract.Height(value))
                .FirstOrDefault()
            : null;
        var reasons = new List<string>();
        if (request.EconomicalDraft) reasons.Add("economical_draft");
        if (path != MovieWave3ResolutionContract.Native) reasons.Add("upscale_selected");
        if (demand >= 80) reasons.Add("high_demand");
        if (request.UpscaleSuitability < 40) reasons.Add("upscale_risk");
        if (escalation) reasons.Add("confidence_below_requirement");
        return new(
            source,
            target,
            path,
            confidence,
            escalation,
            nextSource,
            new MovieWave3CostEstimate(false, null, "USD", "cost_data_unavailable"),
            reasons);
    }

    public static string SerializeProductSafe(MovieWave3AdaptiveResolutionRecommendation recommendation) =>
        JsonSerializer.Serialize(recommendation, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static void Validate(MovieWave3AdaptiveResolutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(MovieWave3ResolutionContract.Normalize(request.MasterTargetResolution)))
            throw new ArgumentException("Master target resolution is unsupported.", nameof(request));
        if (!MovieQualityLevels.Supported.Contains(request.QualityTier.Trim()))
            throw new ArgumentException("Quality tier is unsupported.", nameof(request));
        foreach (var value in new[] { request.Importance, request.MotionComplexity, request.CameraComplexity, request.FaceImportance, request.FineDetailImportance, request.ContinuitySensitivity, request.UpscaleSuitability })
            if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Scores must be between 0 and 100.");
    }
}

public static class MovieWave3UpscaleEligibilityCodes
{
    public const string Eligible = "eligible";
    public const string TakeNotSelected = "take_not_selected";
    public const string TakeNotReady = "take_not_ready";
}

public sealed record MovieWave3UpscaleEligibilityDecision(bool Eligible, string Code, bool IsSelected, bool IsFinal);

public static class MovieWave3UpscaleEligibilityCompatibility
{
    private static readonly IReadOnlySet<string> ReadyStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        MovieTakeStatuses.Ready,
        MovieTakeStatuses.ReviewRequired,
        MovieTakeStatuses.Approved,
    };

    public static MovieWave3UpscaleEligibilityDecision Evaluate(MovieTake take, MovieShot shot)
    {
        ArgumentNullException.ThrowIfNull(take);
        ArgumentNullException.ThrowIfNull(shot);
        var selected = shot.SelectedTakeId == take.Id;
        var final = shot.FinalTakeId == take.Id;
        if (!selected && !final) return new(false, MovieWave3UpscaleEligibilityCodes.TakeNotSelected, false, false);
        if (!ReadyStatuses.Contains(take.Status)) return new(false, MovieWave3UpscaleEligibilityCodes.TakeNotReady, selected, final);
        return new(true, MovieWave3UpscaleEligibilityCodes.Eligible, selected, final);
    }
}
