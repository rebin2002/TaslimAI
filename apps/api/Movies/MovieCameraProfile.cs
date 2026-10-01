namespace Taslim.Api.Movies;

public static class MovieCameraProfileSources
{
    public const string Planner = "planner";
    public const string MovieGuide = "movie_guide";
    public const string UserOverride = "user_override";
}

/// <summary>
/// Explicit per-shot Camera Profile changes. Every field is optional so callers can change one
/// production decision without replacing the full canonical cinematography plan.
/// </summary>
public sealed class CinematographyCameraProfileOverride
{
    public string? ShotSize { get; set; }
    public string? CameraAngle { get; set; }
    public string? CameraMovement { get; set; }
    public string? FocalLengthIntent { get; set; }
    public string? LensIntent { get; set; }
    public string? DepthOfField { get; set; }
    public string? LightingIntent { get; set; }
    public string? ExposureLook { get; set; }
    public IReadOnlyList<string>? ContinuityConstraints { get; set; }
}

public sealed record CinematographyOverrideAudit(
    bool Requested,
    IReadOnlyList<string> RequestedFields,
    IReadOnlyList<string> AppliedFields,
    IReadOnlyList<string> BlockedByLockedCanonFields,
    int? LockedGuideRevisionNumber);

/// <summary>
/// Read-model shape for the structured camera decisions. It is intentionally independent of
/// provider capabilities and contains only production intent and precedence provenance.
/// </summary>
public sealed record MovieCameraProfileDto(
    string ShotSize,
    string Angle,
    string Movement,
    string FocalIntent,
    string LensIntent,
    string DepthOfField,
    string LightingIntent,
    string ExposureLook,
    IReadOnlyList<string> ContinuityConstraints,
    string Source,
    bool UserOverrideApplied,
    IReadOnlyList<string> UserOverrideFields,
    IReadOnlyList<string> LockedFields,
    int? LockedGuideRevisionNumber);

public static class CinematographyCameraProfileContract
{
    private const int MaxContinuityConstraints = 24;
    private const int MaxConstraintLength = 800;

    public static string? Validate(CinematographyCameraProfileOverride? profile)
    {
        if (profile is null) return null;
        if (!IsSupported(profile.ShotSize, CinematographyPlanningValues.ShotSizes) ||
            !IsSupported(profile.CameraAngle, CinematographyPlanningValues.CameraAngles) ||
            !IsSupported(profile.CameraMovement, CinematographyPlanningValues.CameraMovements) ||
            !IsSupported(profile.FocalLengthIntent, CinematographyPlanningValues.FocalLengthIntents) ||
            !IsSupported(profile.LensIntent, CinematographyPlanningValues.LensLookIntents) ||
            !IsSupported(profile.DepthOfField, CinematographyPlanningValues.Depths) ||
            !IsSupported(profile.LightingIntent, CinematographyPlanningValues.LightingIntents) ||
            !IsSupported(profile.ExposureLook, CinematographyPlanningValues.ExposureLooks))
            return "Every Camera Profile field must use a supported structured value.";
        if (profile.ContinuityConstraints is { Count: > MaxContinuityConstraints })
            return "Camera Profile continuity constraints are limited to 24 items.";
        if (profile.ContinuityConstraints is not null && profile.ContinuityConstraints.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > MaxConstraintLength))
            return "Camera Profile continuity constraints must be non-empty and 800 characters or fewer.";
        return null;
    }

    public static MovieCameraProfileDto FromPlan(CinematographyShotPlan plan)
    {
        var constraints = plan.ContinuityConstraints ?? [];
        var overrideFields = plan.UserOverrideFields ?? [];
        return new(
            plan.ShotSize,
            plan.CameraAngle,
            plan.CameraMovement,
            plan.FocalLengthIntent ?? FocalIntentForLens(plan.LensLookIntent),
            plan.LensLookIntent,
            plan.Depth,
            plan.LightingIntent,
            plan.ExposureLook ?? "balanced_cinematic",
            constraints,
            plan.ProfileSource ?? (plan.LockedGuideRevisionNumber.HasValue ? MovieCameraProfileSources.MovieGuide : MovieCameraProfileSources.Planner),
            overrideFields.Count > 0,
            overrideFields,
            plan.LockedCanonFields ?? [],
            plan.LockedGuideRevisionNumber);
    }

    public static MovieCameraProfileDto? FromLegacy(CinematographyIntentSelection? selection, int? lockedGuideRevisionNumber = null)
    {
        if (selection is null) return null;
        var normalized = CinematographyIntentValidator.Normalize(selection)!;
        return new(
            ValueOr(normalized.ShotSize, "medium"),
            ValueOr(normalized.CameraAngle, "eye_level"),
            ValueOr(normalized.CameraMovement, "locked_off"),
            FocalIntentFromText(normalized.FocalLength),
            FocalIntentFromText(normalized.LensIntent),
            DepthFromText(normalized.ApertureDepthOfField),
            ValueOr(normalized.Lighting, "naturalistic"),
            normalized.ExposureLook ?? "balanced_cinematic",
            normalized.ContinuityConstraints ?? [],
            lockedGuideRevisionNumber.HasValue ? MovieCameraProfileSources.MovieGuide : MovieCameraProfileSources.UserOverride,
            !lockedGuideRevisionNumber.HasValue,
            lockedGuideRevisionNumber.HasValue ? [] : ["legacy_cinematography"],
            [],
            lockedGuideRevisionNumber);
    }

    public static string? ValidateConstraints(IEnumerable<string>? values)
    {
        var profile = new CinematographyCameraProfileOverride { ContinuityConstraints = values?.ToArray() };
        return Validate(profile);
    }

    public static IReadOnlyList<string> RequestedFields(CinematographyCameraProfileOverride? profile)
    {
        if (profile is null) return [];
        var fields = new List<string>();
        Add(fields, nameof(CinematographyShotPlan.ShotSize), profile.ShotSize);
        Add(fields, nameof(CinematographyShotPlan.CameraAngle), profile.CameraAngle);
        Add(fields, nameof(CinematographyShotPlan.CameraMovement), profile.CameraMovement);
        Add(fields, nameof(CinematographyShotPlan.FocalLengthIntent), profile.FocalLengthIntent);
        Add(fields, nameof(CinematographyShotPlan.LensLookIntent), profile.LensIntent);
        Add(fields, nameof(CinematographyShotPlan.Depth), profile.DepthOfField);
        Add(fields, nameof(CinematographyShotPlan.LightingIntent), profile.LightingIntent);
        Add(fields, nameof(CinematographyShotPlan.ExposureLook), profile.ExposureLook);
        if (profile.ContinuityConstraints is { Count: > 0 }) fields.Add(nameof(CinematographyShotPlan.ContinuityConstraints));
        return fields;
    }

    public static string NormalizeValue(string value) => value.Trim().ToLowerInvariant();

    private static bool IsSupported(string? value, IReadOnlySet<string> values) => string.IsNullOrWhiteSpace(value) || values.Contains(value.Trim());
    private static void Add(ICollection<string> fields, string field, string? value) { if (!string.IsNullOrWhiteSpace(value)) fields.Add(field); }
    private static string ValueOr(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string FocalIntentForLens(string lens) => lens switch
    {
        "wide_expansive" => "wide",
        "compressed_telephoto" => "telephoto",
        "portrait_natural" => "portrait",
        "macro_detail" => "macro",
        _ => "normal",
    };
    private static string FocalIntentFromText(string? value)
    {
        var text = value?.ToLowerInvariant() ?? string.Empty;
        if (text.Contains("macro")) return "macro";
        if (text.Contains("tele")) return "telephoto";
        if (text.Contains("portrait") || text.Contains("50") || text.Contains("85")) return "portrait";
        if (text.Contains("wide") || text.Contains("18") || text.Contains("24") || text.Contains("28")) return "wide";
        return "normal";
    }
    private static string DepthFromText(string? value)
    {
        var text = value?.ToLowerInvariant() ?? string.Empty;
        if (text.Contains("shallow")) return "shallow";
        if (text.Contains("deep")) return "deep";
        return "layered";
    }
}
