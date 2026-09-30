using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieWave2CreativeValidationReasonCodes
{
    public const string SchemaInvalid = "MOVIE_CREATIVE_SCHEMA_INVALID";
    public const string RequiredFieldMissing = "MOVIE_CREATIVE_REQUIRED_FIELD_MISSING";
    public const string EmptyOutput = "MOVIE_CREATIVE_OUTPUT_EMPTY";
    public const string MalformedStructuredOutput = "MOVIE_CREATIVE_STRUCTURED_OUTPUT_MALFORMED";
    public const string ExcessiveLength = "MOVIE_CREATIVE_OUTPUT_TOO_LONG";
    public const string DuplicateScene = "MOVIE_CREATIVE_DUPLICATE_SCENE";
    public const string DuplicateShot = "MOVIE_CREATIVE_DUPLICATE_SHOT";
    public const string DuplicateShotOrder = "MOVIE_CREATIVE_DUPLICATE_SHOT_ORDER";
    public const string InvalidDuration = "MOVIE_CREATIVE_INVALID_DURATION";
    public const string InvalidOrder = "MOVIE_CREATIVE_INVALID_ORDER";
    public const string InvalidEnum = "MOVIE_CREATIVE_INVALID_ENUM";
    public const string InvalidCategory = "MOVIE_CREATIVE_INVALID_CATEGORY";
    public const string SourceMismatch = "MOVIE_CREATIVE_SOURCE_MISMATCH";
    public const string TargetMismatch = "MOVIE_CREATIVE_TARGET_MISMATCH";
    public const string StaleBase = "MOVIE_CREATIVE_STALE_BASE";
    public const string LockedCanonConflict = "MOVIE_CREATIVE_LOCKED_CANON_CONFLICT";
    public const string GenericTemplate = "MOVIE_CREATIVE_GENERIC_TEMPLATE";
    public const string UnsupportedLanguage = "MOVIE_CREATIVE_LANGUAGE_UNSUPPORTED";
    public const string OutputLanguageMismatch = "MOVIE_CREATIVE_LANGUAGE_MISMATCH";
    public const string StructuredRepairApplied = "MOVIE_CREATIVE_STRUCTURAL_REPAIR_APPLIED";
}

public static class MovieWave2CreativeOutputSchema
{
    public const int CurrentVersion = 1;

    // This is intentionally a small provider-facing schema. Semantic checks live in the
    // validator below so a provider cannot bypass source, target, canon, or base checks.
    public const string Json = """
    {"type":"object","additionalProperties":false,"required":["schemaVersion","movieProjectId","language","scenes"],"properties":{"schemaVersion":{"type":"integer","const":1},"movieProjectId":{"type":"string","format":"uuid"},"language":{"type":"string","enum":["en","ar","ku"]},"baseRevisionId":{"type":["string","null"],"format":"uuid"},"baseVersion":{"type":["integer","null"]},"scenes":{"type":"array","minItems":1,"maxItems":500}}}
    """;
}

public sealed class MovieWave2CreativeOutputValidationOptions
{
    public int MaxRepairAttempts { get; set; }
    public int MaxFindings { get; set; } = 64;
    public int MaxScenes { get; set; } = 500;
    public int MaxShotsPerScene { get; set; } = 500;
    public int MaxSceneTitleCharacters { get; set; } = 160;
    public int MaxSceneSummaryCharacters { get; set; } = 8_000;
    public int MaxShotDescriptionCharacters { get; set; } = 8_000;
    public int MaxShotTextCharacters { get; set; } = 4_000;
    public int MaxClaimCharacters { get; set; } = 2_000;
    public int MaxSceneDurationSeconds { get; set; } = 24 * 60 * 60;
    public int MaxShotDurationSeconds { get; set; } = 60 * 60;
    public int MinimumLanguageDetectionLetters { get; set; } = 12;
}

public sealed class MovieWave2CreativeOutput
{
    public int SchemaVersion { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? ProjectId { get; set; }
    public string? Language { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public long? BaseVersion { get; set; }
    public long? ContextVersion { get; set; }
    public MovieWave2CreativeOutputReference? Source { get; set; }
    public MovieWave2CreativeOutputReference? Target { get; set; }
    public Guid? SourceMovieProjectId { get; set; }
    public Guid? SourceSceneId { get; set; }
    public Guid? SourceShotId { get; set; }
    public Guid? TargetMovieProjectId { get; set; }
    public Guid? TargetSceneId { get; set; }
    public Guid? TargetShotId { get; set; }
    public List<MovieWave2SceneOutput>? Scenes { get; set; } = [];
    public List<MovieWave2CanonClaim>? LockedCanonClaims { get; set; } = [];
}

public sealed class MovieWave2CreativeOutputReference
{
    public Guid? MovieProjectId { get; set; }
    public Guid? SceneId { get; set; }
    public Guid? ShotId { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public long? BaseVersion { get; set; }
}

public sealed class MovieWave2SceneOutput
{
    // Both Id/SceneId and Order/Sequence are accepted to make the boundary tolerant of
    // provider naming differences; the validator rejects conflicting aliases.
    public Guid? Id { get; set; }
    public Guid? SceneId { get; set; }
    public Guid? SourceSceneId { get; set; }
    public Guid? TargetSceneId { get; set; }
    public int? Order { get; set; }
    public int? Sequence { get; set; }
    public string? SceneIdentifier { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? Slugline { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Category { get; set; }
    public string? SceneType { get; set; }
    public string? Status { get; set; }
    public string? ContinuityNotes { get; set; }
    public string? Narration { get; set; }
    public string? Dialogue { get; set; }
    public List<MovieWave2ShotOutput>? Shots { get; set; } = [];
}

public sealed class MovieWave2ShotOutput
{
    public Guid? Id { get; set; }
    public Guid? ShotId { get; set; }
    public Guid? SourceShotId { get; set; }
    public Guid? TargetShotId { get; set; }
    public int? Order { get; set; }
    public int? Sequence { get; set; }
    public string? Description { get; set; }
    public string? Purpose { get; set; }
    public string? Subjects { get; set; }
    public string? LocationSet { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Category { get; set; }
    public string? ShotType { get; set; }
    public string? Status { get; set; }
    public string? ProductionStage { get; set; }
    public string? CameraAndFraming { get; set; }
    public string? CameraMotion { get; set; }
    public string? Narration { get; set; }
    public string? Dialogue { get; set; }
    public string? VisualContinuityNotes { get; set; }
    public string? ContinuityReferences { get; set; }
    public string? ProductionRequirements { get; set; }
    public string? QualityLevel { get; set; }
    public CinematographyIntentSelection? Cinematography { get; set; }
}

public sealed record MovieWave2CanonClaim(Guid? EntityId, string? EntityType, string? FieldName, string? Value);

public sealed class MovieWave2CreativeOutputValidationContext
{
    public Guid MovieProjectId { get; init; }
    public Guid? SourceMovieProjectId { get; init; }
    public Guid? SourceSceneId { get; init; }
    public Guid? SourceShotId { get; init; }
    public Guid? TargetMovieProjectId { get; init; }
    public Guid? TargetSceneId { get; init; }
    public Guid? TargetShotId { get; init; }
    public Guid? CurrentBaseRevisionId { get; init; }
    public long? CurrentBaseVersion { get; init; }
    public long? CurrentContextVersion { get; init; }
    public string Language { get; init; } = LanguageCodes.English;
    public bool RequireTargetIdentity { get; init; }
    public IReadOnlySet<Guid> KnownSceneIds { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> KnownShotIds { get; init; } = new HashSet<Guid>();
    public IReadOnlyList<MovieWave2LockedCanonConstraint> LockedCanon { get; init; } = [];
}

public sealed record MovieWave2LockedCanonConstraint(
    Guid? EntityId,
    string? EntityType,
    string FieldName,
    string LockedValue);

public sealed record MovieWave2CreativeValidationFinding(string ReasonCode, string Path);

public enum MovieWave2CreativeValidationOutcome
{
    Passed,
    Repaired,
    Rejected,
}

public sealed record MovieWave2CreativeOutputValidationResult(
    MovieWave2CreativeValidationOutcome Outcome,
    MovieWave2CreativeOutput? Output,
    IReadOnlyList<MovieWave2CreativeValidationFinding> Findings)
{
    public bool IsValid => Outcome is MovieWave2CreativeValidationOutcome.Passed or MovieWave2CreativeValidationOutcome.Repaired;
    public bool WasRepaired => Outcome == MovieWave2CreativeValidationOutcome.Repaired;
    public IReadOnlyList<string> ReasonCodes => Findings.Select(item => item.ReasonCode).Distinct(StringComparer.Ordinal).ToArray();
}

public interface IMovieWave2CreativeOutputValidator
{
    MovieWave2CreativeOutputValidationResult ValidateJson(string? serializedOutput, MovieWave2CreativeOutputValidationContext context);
    MovieWave2CreativeOutputValidationResult ValidateAndRepair(MovieWave2CreativeOutput output, MovieWave2CreativeOutputValidationContext context);
}

/// <summary>
/// Deterministic semantic boundary for Wave 2 Scene and Shot planning output. It never
/// calls a provider and never invents, truncates, or rewrites creative text.
/// </summary>
public sealed class MovieWave2CreativeOutputValidator : IMovieWave2CreativeOutputValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly IReadOnlySet<string> SceneCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "interior", "exterior", "mixed", "montage", "transition", "flashback", "dream",
    };

    private static readonly IReadOnlySet<string> ShotCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "establishing", "wide", "medium", "close_up", "extreme_close_up", "over_shoulder",
        "two_shot", "insert", "cutaway", "reaction", "tracking", "aerial", "point_of_view",
    };

    private static readonly IReadOnlySet<string> ProductionStages = MovieProductionStages.Persisted;

    private readonly MovieWave2CreativeOutputValidationOptions settings;

    public MovieWave2CreativeOutputValidator(MovieWave2CreativeOutputValidationOptions? options = null)
    {
        settings = options ?? new MovieWave2CreativeOutputValidationOptions();
    }

    public MovieWave2CreativeOutputValidationResult ValidateJson(string? serializedOutput, MovieWave2CreativeOutputValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(serializedOutput))
            return Rejected([Finding(MovieWave2CreativeValidationReasonCodes.EmptyOutput, "payload")]);

        MovieWave2CreativeOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<MovieWave2CreativeOutput>(serializedOutput, JsonOptions);
        }
        catch (JsonException)
        {
            return Rejected([Finding(MovieWave2CreativeValidationReasonCodes.MalformedStructuredOutput, "payload")]);
        }
        catch (NotSupportedException)
        {
            return Rejected([Finding(MovieWave2CreativeValidationReasonCodes.MalformedStructuredOutput, "payload")]);
        }

        return output is null
            ? Rejected([Finding(MovieWave2CreativeValidationReasonCodes.SchemaInvalid, "payload")])
            : ValidateAndRepair(output, context);
    }

    public MovieWave2CreativeOutputValidationResult ValidateAndRepair(MovieWave2CreativeOutput output, MovieWave2CreativeOutputValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidate = output;
        var repairFindings = new List<MovieWave2CreativeValidationFinding>();
        var attempts = Math.Clamp(settings.MaxRepairAttempts, 0, 1);

        for (var attempt = 0; attempt <= attempts; attempt++)
        {
            var findings = Validate(candidate, context);
            if (findings.Count == 0)
            {
                return new(
                    repairFindings.Count == 0 ? MovieWave2CreativeValidationOutcome.Passed : MovieWave2CreativeValidationOutcome.Repaired,
                    candidate,
                    repairFindings);
            }

            if (attempt >= attempts || !CanRepair(candidate, findings))
                return Rejected(findings);

            var repaired = RepairExactStructuralDuplicates(candidate);
            if (repaired is null)
                return Rejected(findings);

            candidate = repaired;
            repairFindings.Add(Finding(MovieWave2CreativeValidationReasonCodes.StructuredRepairApplied, "payload"));
        }

        return Rejected([Finding(MovieWave2CreativeValidationReasonCodes.SchemaInvalid, "payload")]);
    }

    private IReadOnlyList<MovieWave2CreativeValidationFinding> Validate(MovieWave2CreativeOutput? output, MovieWave2CreativeOutputValidationContext context)
    {
        var findings = new List<MovieWave2CreativeValidationFinding>();
        if (output is null)
            return [Finding(MovieWave2CreativeValidationReasonCodes.SchemaInvalid, "payload")];

        var projectId = output.MovieProjectId != Guid.Empty ? output.MovieProjectId : output.ProjectId ?? Guid.Empty;
        AddIf(findings, output.SchemaVersion != MovieWave2CreativeOutputSchema.CurrentVersion, MovieWave2CreativeValidationReasonCodes.SchemaInvalid, "schemaVersion");
        AddIf(findings, projectId == Guid.Empty, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, "movieProjectId");
        AddIf(findings, projectId != Guid.Empty && projectId != context.MovieProjectId, MovieWave2CreativeValidationReasonCodes.TargetMismatch, "movieProjectId");
        AddIf(findings, !LanguageCodes.Supported.Contains(output.Language ?? string.Empty), MovieWave2CreativeValidationReasonCodes.UnsupportedLanguage, "language");
        AddIf(findings, !string.Equals(Normalize(output.Language), Normalize(context.Language), StringComparison.OrdinalIgnoreCase), MovieWave2CreativeValidationReasonCodes.OutputLanguageMismatch, "language");

        ValidateBaseAndReferences(findings, output, context);
        ValidateLockedCanon(findings, output, context);

        if (output.Scenes is null || output.Scenes.Count == 0)
        {
            Add(findings, MovieWave2CreativeValidationReasonCodes.EmptyOutput, "scenes");
            return findings.Take(MaxFindings()).ToArray();
        }
        AddIf(findings, output.Scenes.Count > Math.Max(1, settings.MaxScenes), MovieWave2CreativeValidationReasonCodes.ExcessiveLength, "scenes");

        var seenSceneIds = new HashSet<Guid>();
        var seenSceneOrders = new HashSet<int>();
        var seenSceneFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scene, index) in output.Scenes.Select((item, index) => (item, index)))
        {
            var path = $"scenes[{index}]";
            if (scene is null)
            {
                Add(findings, MovieWave2CreativeValidationReasonCodes.SchemaInvalid, path);
                continue;
            }

            var sceneId = ResolveId(scene.Id, scene.SceneId, findings, $"{path}.sceneId");
            var sceneOrder = ResolveOrder(scene.Order, scene.Sequence, findings, $"{path}.order");
            AddIf(findings, sceneId == Guid.Empty, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, $"{path}.sceneId");
            AddIf(findings, sceneOrder is null, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, $"{path}.order");
            if (sceneId != Guid.Empty && !seenSceneIds.Add(sceneId)) Add(findings, MovieWave2CreativeValidationReasonCodes.DuplicateScene, $"{path}.sceneId");
            if (sceneOrder is not null && !seenSceneOrders.Add(sceneOrder.Value)) Add(findings, MovieWave2CreativeValidationReasonCodes.DuplicateScene, $"{path}.order");
            if (sceneOrder is <= 0) Add(findings, MovieWave2CreativeValidationReasonCodes.InvalidOrder, $"{path}.order");

            ValidateText(findings, scene.Title, true, settings.MaxSceneTitleCharacters, $"{path}.title");
            ValidateText(findings, scene.Summary ?? scene.Slugline, true, settings.MaxSceneSummaryCharacters, $"{path}.summary");
            ValidateText(findings, scene.SceneIdentifier, false, 80, $"{path}.sceneIdentifier");
            ValidateText(findings, scene.ContinuityNotes, false, settings.MaxShotTextCharacters, $"{path}.continuityNotes");
            ValidateText(findings, scene.Narration, false, settings.MaxShotTextCharacters, $"{path}.narration");
            ValidateText(findings, scene.Dialogue, false, settings.MaxShotTextCharacters, $"{path}.dialogue");
            ValidateDuration(findings, scene.DurationSeconds, settings.MaxSceneDurationSeconds, $"{path}.durationSeconds");
            ValidateEnum(findings, scene.Category ?? scene.SceneType, SceneCategories, MovieWave2CreativeValidationReasonCodes.InvalidCategory, $"{path}.category");
            ValidateEnum(findings, scene.Status, MovieHierarchyStatuses.Supported, MovieWave2CreativeValidationReasonCodes.InvalidEnum, $"{path}.status");
            ValidateNodeReferences(findings, scene.SourceSceneId, scene.TargetSceneId, context, sceneId, path, isShot: false);

            var fingerprint = Normalize(string.Join("|", scene.Title, scene.Summary ?? scene.Slugline, scene.DurationSeconds));
            if (fingerprint.Length > 0 && !seenSceneFingerprints.Add(fingerprint)) Add(findings, MovieWave2CreativeValidationReasonCodes.DuplicateScene, path);

            if (scene.Shots is null || scene.Shots.Count == 0)
            {
                Add(findings, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, $"{path}.shots");
                continue;
            }
            AddIf(findings, scene.Shots.Count > Math.Max(1, settings.MaxShotsPerScene), MovieWave2CreativeValidationReasonCodes.ExcessiveLength, $"{path}.shots");
            ValidateShots(findings, scene, context, path);
        }

        ValidateTextQuality(findings, output, context);
        return findings.Take(MaxFindings()).ToArray();
    }

    private void ValidateShots(List<MovieWave2CreativeValidationFinding> findings, MovieWave2SceneOutput scene, MovieWave2CreativeOutputValidationContext context, string scenePath)
    {
        var seenIds = new HashSet<Guid>();
        var seenOrders = new HashSet<int>();
        var seenFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalDuration = 0L;
        var hasAllDurations = true;
        foreach (var (shot, index) in scene.Shots!.Select((item, index) => (item, index)))
        {
            var path = $"{scenePath}.shots[{index}]";
            if (shot is null)
            {
                Add(findings, MovieWave2CreativeValidationReasonCodes.SchemaInvalid, path);
                continue;
            }
            var shotId = ResolveId(shot.Id, shot.ShotId, findings, $"{path}.shotId");
            var shotOrder = ResolveOrder(shot.Order, shot.Sequence, findings, $"{path}.order");
            AddIf(findings, shotId == Guid.Empty, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, $"{path}.shotId");
            AddIf(findings, shotOrder is null, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, $"{path}.order");
            if (shotId != Guid.Empty && !seenIds.Add(shotId)) Add(findings, MovieWave2CreativeValidationReasonCodes.DuplicateShot, $"{path}.shotId");
            if (shotOrder is not null && !seenOrders.Add(shotOrder.Value)) Add(findings, MovieWave2CreativeValidationReasonCodes.DuplicateShotOrder, $"{path}.order");
            if (shotOrder is <= 0) Add(findings, MovieWave2CreativeValidationReasonCodes.InvalidOrder, $"{path}.order");

            ValidateText(findings, shot.Description, true, settings.MaxShotDescriptionCharacters, $"{path}.description");
            ValidateText(findings, shot.Purpose, false, settings.MaxShotTextCharacters, $"{path}.purpose");
            ValidateText(findings, shot.Subjects, false, settings.MaxShotTextCharacters, $"{path}.subjects");
            ValidateText(findings, shot.LocationSet, false, settings.MaxShotTextCharacters, $"{path}.locationSet");
            ValidateText(findings, shot.CameraAndFraming, false, settings.MaxShotTextCharacters, $"{path}.cameraAndFraming");
            ValidateText(findings, shot.CameraMotion, false, settings.MaxShotTextCharacters, $"{path}.cameraMotion");
            ValidateText(findings, shot.Narration, false, settings.MaxShotTextCharacters, $"{path}.narration");
            ValidateText(findings, shot.Dialogue, false, settings.MaxShotTextCharacters, $"{path}.dialogue");
            ValidateText(findings, shot.VisualContinuityNotes, false, settings.MaxShotTextCharacters, $"{path}.visualContinuityNotes");
            ValidateText(findings, shot.ContinuityReferences, false, settings.MaxShotTextCharacters, $"{path}.continuityReferences");
            ValidateText(findings, shot.ProductionRequirements, false, settings.MaxShotTextCharacters, $"{path}.productionRequirements");
            ValidateText(findings, shot.QualityLevel, false, 32, $"{path}.qualityLevel");
            ValidateDuration(findings, shot.DurationSeconds, settings.MaxShotDurationSeconds, $"{path}.durationSeconds");
            if (shot.DurationSeconds is null) hasAllDurations = false;
            else if (shot.DurationSeconds > 0) totalDuration += shot.DurationSeconds.Value;
            ValidateEnum(findings, shot.Category ?? shot.ShotType, ShotCategories, MovieWave2CreativeValidationReasonCodes.InvalidCategory, $"{path}.category");
            ValidateEnum(findings, shot.Status, MovieShotStatuses.Supported, MovieWave2CreativeValidationReasonCodes.InvalidEnum, $"{path}.status");
            ValidateEnum(findings, shot.ProductionStage, ProductionStages, MovieWave2CreativeValidationReasonCodes.InvalidEnum, $"{path}.productionStage");
            ValidateEnum(findings, shot.QualityLevel, MovieQualityLevels.Supported, MovieWave2CreativeValidationReasonCodes.InvalidEnum, $"{path}.qualityLevel");
            if (shot.Cinematography is not null && CinematographyIntentValidator.Validate(shot.Cinematography) is not null)
                Add(findings, MovieWave2CreativeValidationReasonCodes.InvalidEnum, $"{path}.cinematography");
            ValidateNodeReferences(findings, shot.SourceShotId, shot.TargetShotId, context, shotId, path, isShot: true);

            var fingerprint = Normalize(shot.Description);
            if (fingerprint.Length > 0 && !seenFingerprints.Add(fingerprint)) Add(findings, MovieWave2CreativeValidationReasonCodes.DuplicateShot, path);
        }

        if (hasAllDurations && scene.DurationSeconds is > 0 && totalDuration > scene.DurationSeconds.Value)
            Add(findings, MovieWave2CreativeValidationReasonCodes.InvalidDuration, $"{scenePath}.shots");
    }

    private static void ValidateBaseAndReferences(List<MovieWave2CreativeValidationFinding> findings, MovieWave2CreativeOutput output, MovieWave2CreativeOutputValidationContext context)
    {
        var sourceProject = output.Source?.MovieProjectId ?? output.SourceMovieProjectId;
        var sourceScene = output.Source?.SceneId ?? output.SourceSceneId;
        var sourceShot = output.Source?.ShotId ?? output.SourceShotId;
        var targetProject = output.Target?.MovieProjectId ?? output.TargetMovieProjectId;
        var targetScene = output.Target?.SceneId ?? output.TargetSceneId;
        var targetShot = output.Target?.ShotId ?? output.TargetShotId;
        AddIf(findings, context.SourceMovieProjectId.HasValue && !sourceProject.HasValue, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, "source.movieProjectId");
        AddIf(findings, context.SourceSceneId.HasValue && !sourceScene.HasValue, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, "source.sceneId");
        AddIf(findings, context.SourceShotId.HasValue && !sourceShot.HasValue, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, "source.shotId");
        AddIf(findings, context.SourceMovieProjectId.HasValue && sourceProject.HasValue && sourceProject != context.SourceMovieProjectId, MovieWave2CreativeValidationReasonCodes.SourceMismatch, "source.movieProjectId");
        AddIf(findings, context.SourceSceneId.HasValue && sourceScene.HasValue && sourceScene != context.SourceSceneId, MovieWave2CreativeValidationReasonCodes.SourceMismatch, "source.sceneId");
        AddIf(findings, context.SourceShotId.HasValue && sourceShot.HasValue && sourceShot != context.SourceShotId, MovieWave2CreativeValidationReasonCodes.SourceMismatch, "source.shotId");
        AddIf(findings, context.TargetMovieProjectId.HasValue && targetProject.HasValue && targetProject != context.TargetMovieProjectId, MovieWave2CreativeValidationReasonCodes.TargetMismatch, "target.movieProjectId");
        AddIf(findings, context.TargetSceneId.HasValue && targetScene.HasValue && targetScene != context.TargetSceneId, MovieWave2CreativeValidationReasonCodes.TargetMismatch, "target.sceneId");
        AddIf(findings, context.TargetShotId.HasValue && targetShot.HasValue && targetShot != context.TargetShotId, MovieWave2CreativeValidationReasonCodes.TargetMismatch, "target.shotId");
        if (context.RequireTargetIdentity)
        {
            AddIf(findings, context.TargetMovieProjectId.HasValue && !targetProject.HasValue, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, "target.movieProjectId");
            AddIf(findings, context.TargetSceneId.HasValue && !targetScene.HasValue, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, "target.sceneId");
            AddIf(findings, context.TargetShotId.HasValue && !targetShot.HasValue, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, "target.shotId");
        }
        var baseRevision = output.BaseRevisionId ?? output.Source?.BaseRevisionId;
        var baseVersion = output.BaseVersion ?? output.ContextVersion ?? output.Source?.BaseVersion;
        AddIf(findings, context.CurrentBaseRevisionId.HasValue && baseRevision != context.CurrentBaseRevisionId, MovieWave2CreativeValidationReasonCodes.StaleBase, "baseRevisionId");
        AddIf(findings, context.CurrentBaseVersion.HasValue && baseVersion != context.CurrentBaseVersion, MovieWave2CreativeValidationReasonCodes.StaleBase, "baseVersion");
        AddIf(findings, context.CurrentContextVersion.HasValue && output.ContextVersion.HasValue && output.ContextVersion != context.CurrentContextVersion, MovieWave2CreativeValidationReasonCodes.StaleBase, "contextVersion");
    }

    private static void ValidateNodeReferences(List<MovieWave2CreativeValidationFinding> findings, Guid? sourceId, Guid? targetId, MovieWave2CreativeOutputValidationContext context, Guid resolvedId, string path, bool isShot)
    {
        var expectedSource = isShot ? context.SourceShotId : context.SourceSceneId;
        var expectedTarget = isShot ? context.TargetShotId : context.TargetSceneId;
        var knownIds = isShot ? context.KnownShotIds : context.KnownSceneIds;
        var kind = isShot ? "shot" : "scene";
        AddIf(findings, expectedSource.HasValue && sourceId.HasValue && sourceId != expectedSource, MovieWave2CreativeValidationReasonCodes.SourceMismatch, $"{path}.source{kind}Id");
        AddIf(findings, expectedTarget.HasValue && targetId.HasValue && targetId != expectedTarget, MovieWave2CreativeValidationReasonCodes.TargetMismatch, $"{path}.target{kind}Id");
        AddIf(findings, expectedTarget.HasValue && resolvedId != Guid.Empty && resolvedId != expectedTarget, MovieWave2CreativeValidationReasonCodes.TargetMismatch, $"{path}.{kind}Id");
        AddIf(findings, knownIds.Count > 0 && resolvedId != Guid.Empty && !knownIds.Contains(resolvedId) && !expectedTarget.HasValue, MovieWave2CreativeValidationReasonCodes.SourceMismatch, $"{path}.{kind}Id");
    }

    private static void ValidateLockedCanon(List<MovieWave2CreativeValidationFinding> findings, MovieWave2CreativeOutput output, MovieWave2CreativeOutputValidationContext context)
    {
        if (context.LockedCanon.Count == 0) return;
        foreach (var (claim, index) in (output.LockedCanonClaims ?? []).Select((item, index) => (item, index)))
        {
            if (claim is null || string.IsNullOrWhiteSpace(claim.FieldName) || string.IsNullOrWhiteSpace(claim.Value))
            {
                Add(findings, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, $"lockedCanonClaims[{index}]");
                continue;
            }
            var match = context.LockedCanon.FirstOrDefault(item => Same(item.EntityId, claim.EntityId) && string.Equals(item.EntityType, claim.EntityType, StringComparison.OrdinalIgnoreCase) && string.Equals(item.FieldName, claim.FieldName, StringComparison.OrdinalIgnoreCase));
            AddIf(findings, match is null || !string.Equals(Normalize(match.LockedValue), Normalize(claim.Value), StringComparison.Ordinal), MovieWave2CreativeValidationReasonCodes.LockedCanonConflict, $"lockedCanonClaims[{index}]");
        }

        var text = OutputText(output);
        foreach (var lockItem in context.LockedCanon)
        {
            var value = Normalize(lockItem.LockedValue);
            if (value.Length < 4) continue;
            var contradiction = new[]
            {
                $"not {value}", $"without {value}", $"instead of {value}", $"replaces {value}",
                $"changed from {value}", $"no {value}", $"never {value}",
            }.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            AddIf(findings, contradiction, MovieWave2CreativeValidationReasonCodes.LockedCanonConflict, "output");
        }
    }

    private void ValidateTextQuality(List<MovieWave2CreativeValidationFinding> findings, MovieWave2CreativeOutput output, MovieWave2CreativeOutputValidationContext context)
    {
        var text = OutputText(output);
        if (string.IsNullOrWhiteSpace(text))
        {
            Add(findings, MovieWave2CreativeValidationReasonCodes.EmptyOutput, "output");
            return;
        }
        var genericPatterns = new[]
        {
            "[insert", "<insert", "{{", "}}", "lorem ipsum", "placeholder", "to be determined", "tbd", "todo:",
            "your protagonist", "your character", "replace this", "generated response", "as an ai", "write a scene here",
        };
        AddIf(findings, genericPatterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase)), MovieWave2CreativeValidationReasonCodes.GenericTemplate, "output");
        AddIf(findings, !MatchesLanguage(text, context.Language), MovieWave2CreativeValidationReasonCodes.OutputLanguageMismatch, "output");
    }

    private static bool CanRepair(MovieWave2CreativeOutput output, IReadOnlyList<MovieWave2CreativeValidationFinding> findings)
    {
        if (findings.Count == 0 || findings.Any(item => item.ReasonCode is not MovieWave2CreativeValidationReasonCodes.DuplicateScene and not MovieWave2CreativeValidationReasonCodes.DuplicateShot)) return false;
        var sceneFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in output.Scenes ?? [])
        {
            if (scene is null) continue;
            var fingerprint = Normalize(string.Join("|", scene.Title, scene.Summary ?? scene.Slugline, scene.DurationSeconds));
            if (fingerprint.Length > 0 && !sceneFingerprints.Add(fingerprint)) return true;
            var shotFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shot in scene.Shots ?? [])
            {
                if (shot is null) continue;
                var shotFingerprint = Normalize(string.Join("|", shot.Description, shot.DurationSeconds, shot.Category ?? shot.ShotType));
                if (shotFingerprint.Length > 0 && !shotFingerprints.Add(shotFingerprint)) return true;
            }
        }
        return false;
    }

    private static MovieWave2CreativeOutput? RepairExactStructuralDuplicates(MovieWave2CreativeOutput output)
    {
        var clone = Clone(output);
        var scenes = new List<MovieWave2SceneOutput>();
        var seenScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in clone.Scenes ?? [])
        {
            if (scene is null) continue;
            var sceneFingerprint = Normalize(string.Join("|", scene.Title, scene.Summary ?? scene.Slugline, scene.DurationSeconds));
            if (sceneFingerprint.Length > 0 && !seenScenes.Add(sceneFingerprint)) continue;
            var shots = new List<MovieWave2ShotOutput>();
            var seenShots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shot in scene.Shots ?? [])
            {
                if (shot is null) continue;
                var shotFingerprint = Normalize(string.Join("|", shot.Description, shot.DurationSeconds, shot.Category ?? shot.ShotType));
                if (shotFingerprint.Length > 0 && !seenShots.Add(shotFingerprint)) continue;
                shots.Add(shot);
            }
            scene.Shots = shots;
            scenes.Add(scene);
        }
        clone.Scenes = scenes;
        return clone;
    }

    private static MovieWave2CreativeOutput Clone(MovieWave2CreativeOutput output) => JsonSerializer.Deserialize<MovieWave2CreativeOutput>(JsonSerializer.Serialize(output, JsonOptions), JsonOptions)!;

    private static Guid ResolveId(Guid? first, Guid? second, List<MovieWave2CreativeValidationFinding> findings, string path)
    {
        AddIf(findings, first.HasValue && second.HasValue && first != second, MovieWave2CreativeValidationReasonCodes.SchemaInvalid, path);
        return first ?? second ?? Guid.Empty;
    }

    private static int? ResolveOrder(int? first, int? second, List<MovieWave2CreativeValidationFinding> findings, string path)
    {
        AddIf(findings, first.HasValue && second.HasValue && first != second, MovieWave2CreativeValidationReasonCodes.SchemaInvalid, path);
        return first ?? second;
    }

    private static void ValidateText(List<MovieWave2CreativeValidationFinding> findings, string? value, bool required, int maximum, string path)
    {
        AddIf(findings, required && string.IsNullOrWhiteSpace(value), MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, path);
        if (!string.IsNullOrWhiteSpace(value)) AddIf(findings, value.Trim().Length > maximum, MovieWave2CreativeValidationReasonCodes.ExcessiveLength, path);
    }

    private static void ValidateDuration(List<MovieWave2CreativeValidationFinding> findings, int? value, int maximum, string path)
    {
        AddIf(findings, !value.HasValue, MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing, path);
        AddIf(findings, value is <= 0 or > int.MaxValue, MovieWave2CreativeValidationReasonCodes.InvalidDuration, path);
        AddIf(findings, value.HasValue && value.Value > maximum, MovieWave2CreativeValidationReasonCodes.InvalidDuration, path);
    }

    private static void ValidateEnum(List<MovieWave2CreativeValidationFinding> findings, string? value, IReadOnlySet<string> supported, string reasonCode, string path) =>
        AddIf(findings, !string.IsNullOrWhiteSpace(value) && !supported.Contains(value.Trim()), reasonCode, path);

    private int MaxFindings() => Math.Clamp(settings.MaxFindings, 1, 256);
    private static string OutputText(MovieWave2CreativeOutput output) => string.Join(" ", (output.Scenes ?? []).Where(item => item is not null).SelectMany(scene => new[] { scene!.Title, scene.Summary, scene.Slugline, scene.ContinuityNotes, scene.Narration, scene.Dialogue }.Concat((scene.Shots ?? []).Where(item => item is not null).SelectMany(shot => new[] { shot!.Description, shot.Purpose, shot.Subjects, shot.LocationSet, shot.CameraAndFraming, shot.CameraMotion, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes, shot.ContinuityReferences, shot.ProductionRequirements })) .Concat((output.LockedCanonClaims ?? []).Where(item => item is not null).Select(claim => claim!.Value))).Where(value => !string.IsNullOrWhiteSpace(value)));
    private static bool MatchesLanguage(string text, string? language)
    {
        var expected = string.IsNullOrWhiteSpace(language) ? LanguageCodes.English : language.Trim().ToLowerInvariant();
        var letters = text.Count(char.IsLetter);
        if (letters < 12) return true;
        var arabic = text.Count(IsArabicLetter);
        var latin = text.Count(IsLatinLetter);
        return expected switch
        {
            LanguageCodes.Arabic => arabic >= Math.Max(4, (int)Math.Ceiling(letters * 0.15)),
            LanguageCodes.English => arabic == 0 && latin >= Math.Max(4, (int)Math.Ceiling(letters * 0.45)),
            LanguageCodes.Kurdish => true,
            _ => false,
        };
    }

    private static bool IsArabicLetter(char value) => value is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\u08A0' and <= '\u08FF';
    private static bool IsLatinLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || value is >= '\u00C0' and <= '\u024F';
    private static bool Same(Guid? left, Guid? right) => left.HasValue && right.HasValue ? left == right : !left.HasValue && !right.HasValue;
    private static string Normalize(string? value) => string.Join(' ', Regex.Split((value ?? string.Empty).Trim(), @"\s+")).ToLowerInvariant();
    private static MovieWave2CreativeValidationFinding Finding(string reasonCode, string path) => new(reasonCode, path);
    private static void AddIf(List<MovieWave2CreativeValidationFinding> findings, bool condition, string reasonCode, string path) { if (condition) Add(findings, reasonCode, path); }
    private static void Add(List<MovieWave2CreativeValidationFinding> findings, string reasonCode, string path) { if (!findings.Any(item => item.ReasonCode == reasonCode && item.Path == path)) findings.Add(Finding(reasonCode, path)); }
    private static MovieWave2CreativeOutputValidationResult Rejected(IReadOnlyList<MovieWave2CreativeValidationFinding> findings) => new(MovieWave2CreativeValidationOutcome.Rejected, null, findings);
}
