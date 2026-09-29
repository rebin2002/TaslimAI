using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taslim.Api.Movies;

public static class MovieShotProductionContractSchema
{
    public const int CurrentVersion = 1;
}

public static class MovieShotNarrativeImportance
{
    public const string Background = "background";
    public const string Supporting = "supporting";
    public const string Primary = "primary";
    public const string Critical = "critical";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Background, Supporting, Primary, Critical,
    };
}

public static class MovieShotComplexityLevels
{
    public const string Low = "low";
    public const string Moderate = "moderate";
    public const string High = "high";
    public const string Critical = "critical";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Low, Moderate, High, Critical,
    };
}

public static class MovieShotContinuitySensitivities
{
    public const string Low = "low";
    public const string Moderate = "moderate";
    public const string High = "high";
    public const string Locked = "locked";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Low, Moderate, High, Locked,
    };
}

public static class MovieShotUpscaleSuitabilities
{
    public const string Preferred = "preferred";
    public const string Conditional = "conditional";
    public const string NotRecommended = "not_recommended";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Preferred, Conditional, NotRecommended,
    };
}

public sealed record MovieShotComplexityProfile(
    string? Level,
    IReadOnlyList<string>? Drivers = null,
    string? Notes = null);

public sealed record MovieShotQualityRequirements(
    string? MinimumLevel,
    IReadOnlyList<string>? AcceptanceCriteria = null,
    string? Notes = null);

public sealed record MovieShotTargetOutputRequirements(
    string? AspectRatio = null,
    string? ResolutionIntent = null,
    string? FrameRateIntent = null,
    string? ColorIntent = null,
    string? AudioIntent = null,
    string? FormatIntent = null,
    string? Notes = null);

public sealed record MovieShotProductionApprovalProvenance(
    string Status,
    Guid ProductionVersionId,
    string Stage,
    Guid? SourceVersionId,
    Guid? ReviewedByUserId,
    DateTime? ReviewedAt,
    string? StageProvenanceJson,
    Guid? ContinuitySnapshotId,
    int? ContinuitySnapshotVersion,
    string? ContinuitySnapshotHash,
    string? CinematographyReferenceJson);

/// <summary>
/// Stable, provider-neutral planning contract for a shot. Existing Wave 1 fields
/// are composed with the Wave 2 additions; provider/model routing is intentionally absent.
/// </summary>
public sealed record MovieShotProductionContractDto(
    int SchemaVersion,
    int? DurationSeconds,
    string? NarrativeImportance,
    CinematographyIntentSelection? Cinematography,
    MovieShotComplexityProfile? ProductionComplexity,
    MovieShotQualityRequirements? QualityRequirements,
    string? ContinuitySensitivity,
    string? UpscaleSuitability,
    MovieShotTargetOutputRequirements? TargetOutputRequirements,
    string Status,
    string PlanningStatus,
    string ProductionStage,
    MovieShotProductionApprovalProvenance? Approval);

public static class MovieShotProductionContractSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? ToJson<T>(T? value) where T : class => value is null ? null : JsonSerializer.Serialize(value, Options);

    public static T? FromJson<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
    }

    public static string? NormalizeChoice(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

public static class MovieShotProductionContractValidation
{
    public static string? Validate(
        int? durationSeconds,
        string? narrativeImportance,
        MovieShotComplexityProfile? productionComplexity,
        MovieShotQualityRequirements? qualityRequirements,
        string? continuitySensitivity,
        string? upscaleSuitability,
        MovieShotTargetOutputRequirements? targetOutputRequirements)
    {
        if (durationSeconds is < 1 or > 3600) return "Shot duration must be between 1 second and 60 minutes.";
        if (!IsSupportedOptional(narrativeImportance, MovieShotNarrativeImportance.Supported)) return "Choose a supported narrative importance.";
        if (!IsSupportedOptional(continuitySensitivity, MovieShotContinuitySensitivities.Supported)) return "Choose a supported continuity sensitivity.";
        if (!IsSupportedOptional(upscaleSuitability, MovieShotUpscaleSuitabilities.Supported)) return "Choose a supported upscale suitability.";

        if (productionComplexity is not null)
        {
            if (!MovieShotComplexityLevels.Supported.Contains(productionComplexity.Level ?? string.Empty)) return "Choose a supported production complexity level.";
            if (productionComplexity.Drivers is { Count: > 16 }) return "Production complexity supports at most 16 drivers.";
            if (productionComplexity.Drivers?.Any(driver => string.IsNullOrWhiteSpace(driver) || driver.Trim().Length > 120) == true) return "Production complexity drivers must be 120 characters or fewer.";
            if (productionComplexity.Notes?.Trim().Length > 2_000) return "Production complexity notes must be 2,000 characters or fewer.";
        }

        if (qualityRequirements is not null)
        {
            if (!MovieQualityLevels.Supported.Contains(qualityRequirements.MinimumLevel ?? string.Empty)) return "Choose a supported minimum quality level.";
            if (qualityRequirements.AcceptanceCriteria is { Count: > 16 }) return "Quality requirements support at most 16 acceptance criteria.";
            if (qualityRequirements.AcceptanceCriteria?.Any(criteria => string.IsNullOrWhiteSpace(criteria) || criteria.Trim().Length > 500) == true) return "Quality acceptance criteria must be 500 characters or fewer.";
            if (qualityRequirements.Notes?.Trim().Length > 2_000) return "Quality requirement notes must be 2,000 characters or fewer.";
        }

        if (targetOutputRequirements is not null)
        {
            if (targetOutputRequirements.AspectRatio?.Trim().Length > 20) return "Target output aspect ratio must be 20 characters or fewer.";
            if (new[]
                {
                    targetOutputRequirements.ResolutionIntent,
                    targetOutputRequirements.FrameRateIntent,
                    targetOutputRequirements.ColorIntent,
                    targetOutputRequirements.AudioIntent,
                    targetOutputRequirements.FormatIntent,
                }.Any(value => value?.Trim().Length > 120)) return "Target output intents must be 120 characters or fewer.";
            if (targetOutputRequirements.Notes?.Trim().Length > 2_000) return "Target output notes must be 2,000 characters or fewer.";
        }

        return null;
    }

    private static bool IsSupportedOptional(string? value, IReadOnlySet<string> supported) =>
        string.IsNullOrWhiteSpace(value) || supported.Contains(value.Trim());
}

public static class MovieShotProductionContractProjection
{
    public static MovieShotProductionContractDto FromShot(MovieShot shot)
    {
        var latestVersion = shot.ProductionVersions
            .OrderByDescending(version => version.VersionNumber)
            .ThenByDescending(version => version.UpdatedAt)
            .FirstOrDefault();
        var approval = latestVersion is null
            ? null
            : new MovieShotProductionApprovalProvenance(
                latestVersion.Status,
                latestVersion.Id,
                latestVersion.Stage,
                latestVersion.SourceVersionId,
                latestVersion.ReviewedByUserId,
                latestVersion.ReviewedAt,
                latestVersion.StageProvenanceJson,
                latestVersion.ContinuitySnapshotId,
                latestVersion.ContinuitySnapshotVersion,
                latestVersion.ContinuitySnapshotHash,
                latestVersion.CinematographyReferenceJson);

        return new MovieShotProductionContractDto(
            MovieShotProductionContractSchema.CurrentVersion,
            shot.DurationSeconds,
            shot.NarrativeImportance,
            CinematographyIntentValidator.FromJson(shot.CinematographyJson),
            MovieShotProductionContractSerialization.FromJson<MovieShotComplexityProfile>(shot.ProductionComplexityJson),
            MovieShotProductionContractSerialization.FromJson<MovieShotQualityRequirements>(shot.QualityRequirementsJson),
            shot.ContinuitySensitivity,
            shot.UpscaleSuitability,
            MovieShotProductionContractSerialization.FromJson<MovieShotTargetOutputRequirements>(shot.TargetOutputRequirementsJson),
            shot.Status,
            MovieShotReadiness.PlanState(shot),
            shot.ProductionStage,
            approval);
    }
}
