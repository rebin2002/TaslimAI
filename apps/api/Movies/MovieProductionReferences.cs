using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieProductionReferenceLimits
{
    public const int SchemaVersion = 1;
    public const int MaxPackageJsonLength = 100_000;
    public const int MaxCharacters = 32;
    public const int MaxLocations = 16;
    public const int MaxSets = 16;
    public const int MaxProps = 32;
    public const int MaxFacts = 64;
    public const int MaxLocks = 64;
    public const int MaxPreviousShots = 4;
    public const int MaxWarnings = 64;
    public const int MaxTextLength = 4_000;
    public const int MaxGuideSectionLength = 6_000;
}

public sealed record MovieProductionReferenceProvenanceDto(
    string SourceType,
    Guid? SourceId,
    int? SourceVersion,
    string SourceHash,
    string Precedence,
    DateTime CapturedAtUtc);

public sealed record MovieProductionGuideSectionReferenceDto(string Type, string ContentJson);

public sealed record MovieProductionGuideReferenceDto(
    Guid GuideId,
    bool IsLocked,
    int? RevisionNumber,
    IReadOnlyList<MovieProductionGuideSectionReferenceDto> Sections,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionStyleReferenceDto(
    string ProjectStyle,
    string? VisualLanguage,
    string? CameraLanguage,
    string? ColorAndLighting,
    string? ContinuityRules,
    string? VisualBibleJson,
    string? CinematographyBibleJson,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionCharacterReferenceDto(
    Guid CharacterId,
    string Name,
    string? Role,
    string Description,
    string? Appearance,
    string? PhysicalDescription,
    string? Wardrobe,
    string? StateWardrobe,
    IReadOnlyList<Guid> ReferenceAssetIds,
    string? StateKey,
    string? StateAppearance,
    IReadOnlyList<MovieCharacterContinuityLockFactDto> Locks,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionWardrobeReferenceDto(
    Guid CharacterId,
    string CharacterName,
    string? Wardrobe,
    string? StateKey,
    bool IsLocked,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionLocationReferenceDto(
    Guid LocationId,
    string Name,
    string Description,
    string? VisualContinuityNotes,
    Guid? ReferenceAssetId,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionPropReferenceDto(
    Guid PropId,
    string Name,
    string Description,
    string? Category,
    string? ContinuityNotes,
    string? State,
    Guid? ReferenceAssetId,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionSetReferenceDto(
    Guid SetId,
    Guid? LocationId,
    string Name,
    string Description,
    string EnvironmentType,
    string? VisualDescription,
    string? TimeOfDay,
    string? Weather,
    string? ContinuityNotes,
    Guid? ReferenceAssetId,
    IReadOnlyList<MovieWorldContinuityVariationSnapshot> Variations,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionWorldReferenceDto(
    int SnapshotVersion,
    string SnapshotHash,
    IReadOnlyList<MovieProductionSetReferenceDto> Sets,
    IReadOnlyList<MovieWorldContinuityFactSnapshot> Facts,
    IReadOnlyList<MovieWorldContinuityLockSnapshot> Locks,
    IReadOnlyList<MovieProductionReferenceWarningDto> Warnings,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionKeyframeReferenceDto(
    Guid VersionId,
    int VersionNumber,
    string Stage,
    string Status,
    string? Label,
    string CompositionJson,
    Guid? AssetId,
    Guid? FirstFrameAssetId,
    Guid? LastFrameAssetId,
    string? ContinuitySnapshotHash,
    string ContentHash,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionPreviousShotReferenceDto(
    Guid ShotId,
    Guid SceneId,
    int SceneSequence,
    int ShotSequence,
    string Description,
    Guid? SelectedOrFinalTakeId,
    Guid? SelectedOrFinalAssetId,
    Guid? ApprovedKeyframeVersionId,
    Guid? ApprovedKeyframeAssetId,
    string ContentHash,
    MovieProductionReferenceProvenanceDto Provenance);

public sealed record MovieProductionReferenceWarningDto(
    string Code,
    string Severity,
    string Message);

public sealed record MovieProductionReferencePackageDto(
    int SchemaVersion,
    Guid MovieProjectId,
    Guid MovieSceneId,
    Guid MovieShotId,
    string PackageHash,
    DateTime CapturedAtUtc,
    MovieProductionGuideReferenceDto Guide,
    MovieProductionStyleReferenceDto Style,
    IReadOnlyList<MovieProductionCharacterReferenceDto> Characters,
    IReadOnlyList<MovieProductionWardrobeReferenceDto> Wardrobe,
    MovieProductionWorldReferenceDto World,
    IReadOnlyList<MovieProductionLocationReferenceDto> Locations,
    IReadOnlyList<MovieProductionPropReferenceDto> Props,
    MovieProductionKeyframeReferenceDto? Keyframe,
    IReadOnlyList<MovieProductionPreviousShotReferenceDto> PreviousShots,
    IReadOnlyList<MovieProductionReferenceWarningDto> Warnings);

public interface IMovieProductionReferencePackageService
{
    Task<MovieProductionReferencePackageDto?> GetForShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default);
}

public sealed class MovieProductionReferencePackageService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IMovieCharacterContinuityService characterContinuity,
    MovieWorldContinuityProjector worldContinuity) : IMovieProductionReferencePackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<MovieProductionReferencePackageDto?> GetForShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default)
    {
        var shot = await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide).ThenInclude(item => item.Revisions)
            .SingleOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var project = shot.Scene.MovieProject;
        var characterSnapshot = await characterContinuity.BuildSnapshotForTargetAsync(project.Id, shot.MovieSceneId, shot.Id, false, cancellationToken);
        var worldSnapshot = await worldContinuity.ProjectAsync(project.Id, shot.MovieSceneId, shot.Id, cancellationToken)
            ?? throw new MovieProductionReferenceException("The production reference target is not valid.");
        var guide = BuildGuide(project.Guide, shot.UpdatedAt);
        var style = BuildStyle(project, project.Guide, guide, shot.UpdatedAt);
        var characters = BuildCharacters(characterSnapshot, shot.UpdatedAt);
        var wardrobe = characters.Select(BuildWardrobe).ToArray();
        var locations = worldSnapshot.Locations.Take(MovieProductionReferenceLimits.MaxLocations).Select(item =>
            new MovieProductionLocationReferenceDto(
                item.Id, Bounded(item.Name)!, Bounded(item.Description)!, Bounded(item.VisualContinuityNotes), item.ReferenceAssetId,
                Provenance("world_location", item.Id, worldSnapshot.SnapshotVersion, Hash(item), "world_continuity", shot.UpdatedAt))).ToArray();
        var props = worldSnapshot.Props.Take(MovieProductionReferenceLimits.MaxProps).Select(item =>
            new MovieProductionPropReferenceDto(
                item.Id, Bounded(item.Name)!, Bounded(item.Description)!, Bounded(item.Category), Bounded(item.ContinuityNotes), Bounded(item.State), item.ReferenceAssetId,
                Provenance("world_prop", item.Id, worldSnapshot.SnapshotVersion, Hash(item), "world_continuity", shot.UpdatedAt))).ToArray();
        var sets = worldSnapshot.Sets.Take(MovieProductionReferenceLimits.MaxSets).Select(item =>
            new MovieProductionSetReferenceDto(
                item.Id, item.MovieLocationId, Bounded(item.Name)!, Bounded(item.Description)!, Bounded(item.EnvironmentType)!, Bounded(item.VisualDescription),
                Bounded(item.TimeOfDay), Bounded(item.Weather), Bounded(item.ContinuityNotes), item.ReferenceAssetId,
                item.Variations.Take(MovieWorldContinuityLimits.MaxVariationsPerSet).ToArray(),
                Provenance("world_set", item.Id, worldSnapshot.SnapshotVersion, Hash(item), "world_continuity", shot.UpdatedAt))).ToArray();
        var world = new MovieProductionWorldReferenceDto(
            worldSnapshot.SnapshotVersion, worldSnapshot.SnapshotHash, sets,
            worldSnapshot.Facts.Take(MovieProductionReferenceLimits.MaxFacts).ToArray(),
            worldSnapshot.Locks.Take(MovieProductionReferenceLimits.MaxLocks).ToArray(),
            worldSnapshot.Warnings.Take(MovieProductionReferenceLimits.MaxWarnings).Select(ToWarning).ToArray(),
            Provenance("world_continuity", shot.Id, worldSnapshot.SnapshotVersion, worldSnapshot.SnapshotHash, "world_continuity", shot.UpdatedAt));

        var keyframe = await BuildKeyframeAsync(shot, cancellationToken);
        var previousShots = await BuildPreviousShotsAsync(shot, cancellationToken);
        var warnings = BuildWarnings(project.Guide, characterSnapshot, worldSnapshot, keyframe);
        var capturedAt = DateTime.UtcNow;
        var package = new MovieProductionReferencePackageDto(
            MovieProductionReferenceLimits.SchemaVersion, project.Id, shot.MovieSceneId, shot.Id, string.Empty, capturedAt,
            guide, style, characters, wardrobe, world, locations, props, keyframe, previousShots, warnings);
        var hash = Hash(package with { PackageHash = string.Empty, CapturedAtUtc = DateTime.UnixEpoch });
        package = package with { PackageHash = hash };
        if (JsonSerializer.Serialize(package, JsonOptions).Length > MovieProductionReferenceLimits.MaxPackageJsonLength)
            throw new MovieProductionReferenceException("The production reference package exceeded its bounded size.");
        return package;
    }

    private async Task<MovieProductionKeyframeReferenceDto?> BuildKeyframeAsync(MovieShot shot, CancellationToken cancellationToken)
    {
        var version = await db.MovieProductionVersions.AsNoTracking()
            .Include(item => item.AssetReferences)
            .Where(item => item.MovieShotId == shot.Id && item.Stage == MovieProductionStages.ApprovedKeyframe && item.Status == MovieProductionVersionStatuses.Approved)
            .OrderByDescending(item => item.VersionNumber)
            .ThenByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (version is null) return null;
        var composition = Bounded(version.CompositionJson, 20_000)!;
        var contentHash = Hash(new
        {
            version.Id, version.VersionNumber, version.Stage, version.Status, version.Label, composition,
            version.AssetId, version.FirstFrameAssetId, version.LastFrameAssetId, version.ContinuitySnapshotHash,
            assets = version.AssetReferences.OrderBy(item => item.Role).ThenBy(item => item.AssetId).Select(item => new { item.AssetId, item.Role }),
        });
        return new MovieProductionKeyframeReferenceDto(
            version.Id, version.VersionNumber, version.Stage, version.Status, Bounded(version.Label), composition,
            version.AssetId, version.FirstFrameAssetId, version.LastFrameAssetId, version.ContinuitySnapshotHash, contentHash,
            Provenance("approved_keyframe", version.Id, version.VersionNumber, contentHash, "approved_production_version", version.UpdatedAt));
    }

    private async Task<IReadOnlyList<MovieProductionPreviousShotReferenceDto>> BuildPreviousShotsAsync(MovieShot shot, CancellationToken cancellationToken)
    {
        var ordered = await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene)
            .Include(item => item.Takes)
            .Include(item => item.ProductionVersions)
            .Where(item => item.Scene.MovieProjectId == shot.Scene.MovieProjectId)
            .ToListAsync(cancellationToken);
        var previous = ordered
            .OrderBy(item => item.Scene.Sequence).ThenBy(item => item.Sequence).ThenBy(item => item.Id)
            .TakeWhile(item => item.Id != shot.Id)
            .TakeLast(MovieProductionReferenceLimits.MaxPreviousShots)
            .ToArray();
        return previous.Select(item =>
        {
            var take = item.Takes.Where(candidate => candidate.Id == item.FinalTakeId || candidate.Id == item.SelectedTakeId)
                .OrderByDescending(candidate => candidate.FinalizedAt ?? candidate.SelectedAt ?? candidate.UpdatedAt).FirstOrDefault();
            var keyframe = item.ProductionVersions
                .Where(version => version.Stage == MovieProductionStages.ApprovedKeyframe && version.Status == MovieProductionVersionStatuses.Approved)
                .OrderByDescending(version => version.VersionNumber).FirstOrDefault();
            var contentHash = Hash(new
            {
                shotId = item.Id, sceneId = item.Scene.Id, sceneSequence = item.Scene.Sequence, shotSequence = item.Sequence, item.Description,
                takeId = take?.Id, takeAssetId = take?.AssetId, keyframeId = keyframe?.Id, keyframeAssetId = keyframe?.AssetId,
                keyframeVersion = keyframe?.VersionNumber, keyframeHash = keyframe?.ContinuitySnapshotHash,
            });
            return new MovieProductionPreviousShotReferenceDto(
                item.Id, item.Scene.Id, item.Scene.Sequence, item.Sequence, Bounded(item.Description)!, take?.Id, take?.AssetId,
                keyframe?.Id, keyframe?.AssetId, contentHash,
                Provenance("previous_shot", item.Id, keyframe?.VersionNumber, contentHash, "shot_sequence", item.UpdatedAt));
        }).ToArray();
    }

    private static MovieProductionGuideReferenceDto BuildGuide(MovieContinuityGuide guide, DateTime fallbackCapturedAt)
    {
        var revision = guide.LockedRevisionNumber is int lockedNumber
            ? guide.Revisions.FirstOrDefault(item => item.RevisionNumber == lockedNumber && item.Status == MovieGuideRevisionStatuses.Locked)
            : null;
        var sections = revision is null
            ? Array.Empty<MovieProductionGuideSectionReferenceDto>()
            : new[]
            {
                new MovieProductionGuideSectionReferenceDto(MovieGuideSectionTypes.VisualBible, BoundedJson(revision.VisualBibleJson, MovieProductionReferenceLimits.MaxGuideSectionLength)),
                new MovieProductionGuideSectionReferenceDto(MovieGuideSectionTypes.CinematographyBible, BoundedJson(revision.CinematographyBibleJson, MovieProductionReferenceLimits.MaxGuideSectionLength)),
                new MovieProductionGuideSectionReferenceDto(MovieGuideSectionTypes.ContinuityBible, BoundedJson(revision.ContinuityBibleJson, MovieProductionReferenceLimits.MaxGuideSectionLength)),
            };
        var sourceHash = Hash(new
        {
            guide.Id, revisionNumber = revision?.RevisionNumber, revision?.Status,
            sections = revision is null ? null : new { revision.VisualBibleJson, revision.CinematographyBibleJson, revision.ContinuityBibleJson },
            guide.VisualLanguage, guide.CameraLanguage, guide.ColorAndLighting, guide.ContinuityRules,
        });
        var capturedAt = revision?.LockedAt ?? revision?.CreatedAt ?? guide.UpdatedAt;
        if (capturedAt == default) capturedAt = fallbackCapturedAt;
        return new MovieProductionGuideReferenceDto(
            guide.Id, revision is not null, revision?.RevisionNumber, sections,
            Provenance(revision is null ? "movie_guide_unlocked" : "locked_movie_guide", guide.Id, revision?.RevisionNumber, sourceHash,
                revision is null ? "unlocked_guide" : "locked_guide", capturedAt));
    }

    private static MovieProductionStyleReferenceDto BuildStyle(MovieProject project, MovieContinuityGuide guide, MovieProductionGuideReferenceDto guideReference, DateTime fallbackCapturedAt)
    {
        var sections = guideReference.Sections.ToDictionary(item => item.Type, item => item.ContentJson, StringComparer.Ordinal);
        var visualJson = sections.GetValueOrDefault(MovieGuideSectionTypes.VisualBible);
        var cinematographyJson = sections.GetValueOrDefault(MovieGuideSectionTypes.CinematographyBible);
        var continuityJson = sections.GetValueOrDefault(MovieGuideSectionTypes.ContinuityBible);
        var authoritativeRevision = guideReference.RevisionNumber is int revisionNumber
            ? guide.Revisions.FirstOrDefault(item => item.RevisionNumber == revisionNumber)
            : null;
        var authoritativeVisualJson = authoritativeRevision?.VisualBibleJson ?? visualJson;
        var authoritativeCinematographyJson = authoritativeRevision?.CinematographyBibleJson ?? cinematographyJson;
        var authoritativeContinuityJson = authoritativeRevision?.ContinuityBibleJson ?? continuityJson;
        var visualLanguage = ReadString(authoritativeVisualJson, "visualLanguage") ?? Bounded(guide.VisualLanguage);
        var cameraLanguage = ReadString(authoritativeCinematographyJson, "cameraLanguage") ?? Bounded(guide.CameraLanguage);
        var color = ReadString(authoritativeVisualJson, "colorAndLighting") ?? Bounded(guide.ColorAndLighting);
        var continuity = ReadString(authoritativeContinuityJson, "continuityRules") ?? Bounded(guide.ContinuityRules);
        var hash = Hash(new { project.Style, visualLanguage, cameraLanguage, color, continuity, visualJson, cinematographyJson, continuityJson, guideReference.Provenance.SourceHash });
        return new MovieProductionStyleReferenceDto(
            Bounded(project.Style)!, visualLanguage, cameraLanguage, color, continuity, visualJson, cinematographyJson,
            Provenance("production_style", guide.Id, guideReference.RevisionNumber, hash, guideReference.Provenance.Precedence, guideReference.Provenance.CapturedAtUtc == default ? fallbackCapturedAt : guideReference.Provenance.CapturedAtUtc));
    }

    private static MovieProductionCharacterReferenceDto[] BuildCharacters(MovieCharacterContinuitySnapshotDto snapshot, DateTime capturedAt)
    {
        return snapshot.Characters.Take(MovieProductionReferenceLimits.MaxCharacters).Select(character =>
        {
            var contentHash = Hash(new { character.Id, character.Name, character.Role, character.Description, character.Appearance, character.PhysicalDescription, character.Wardrobe, character.ReferenceAssetIds, character.RelevantState, character.Locks });
            return new MovieProductionCharacterReferenceDto(
                character.Id, Bounded(character.Name)!, Bounded(character.Role), Bounded(character.Description)!, Bounded(character.Appearance), Bounded(character.PhysicalDescription),
                Bounded(character.Wardrobe), Bounded(character.RelevantState?.Wardrobe), character.ReferenceAssetIds.Take(MovieContinuitySnapshotLimits.MaxReferencesPerCharacter).ToArray(), character.RelevantState?.Key,
                Bounded(character.RelevantState?.Appearance), character.Locks.Take(MovieContinuitySnapshotLimits.MaxFactsPerCharacter).ToArray(),
                Provenance("character_continuity_snapshot", snapshot.SnapshotId, snapshot.Version, contentHash, "character_continuity", capturedAt));
        }).ToArray();
    }

    private static MovieProductionWardrobeReferenceDto BuildWardrobe(MovieProductionCharacterReferenceDto character)
    {
        var locked = character.Locks.FirstOrDefault(item => item.FieldKey.Equals("wardrobe", StringComparison.OrdinalIgnoreCase));
        var wardrobe = locked?.LockedValue ?? character.StateWardrobe ?? character.Wardrobe;
        var hash = Hash(new { character.CharacterId, character.Name, wardrobe, character.Provenance.SourceHash, locked = locked?.Id });
        return new MovieProductionWardrobeReferenceDto(
            character.CharacterId, character.Name, wardrobe, character.StateKey, locked is not null,
            Provenance("character_wardrobe", character.CharacterId, character.Provenance.SourceVersion, hash, locked is null ? "character_continuity" : "continuity_lock", character.Provenance.CapturedAtUtc));
    }

    private static IReadOnlyList<MovieProductionReferenceWarningDto> BuildWarnings(
        MovieContinuityGuide guide,
        MovieCharacterContinuitySnapshotDto characterSnapshot,
        MovieWorldContinuitySnapshotDto worldSnapshot,
        MovieProductionKeyframeReferenceDto? keyframe)
    {
        var warnings = new List<MovieProductionReferenceWarningDto>();
        if (guide.LockedRevisionNumber is null)
            warnings.Add(new("guide_not_locked", "attention", "The production reference package uses the current guide fields because no locked Movie Guide revision exists."));
        warnings.AddRange(characterSnapshot.Warnings.Take(MovieProductionReferenceLimits.MaxWarnings).Select(item => new MovieProductionReferenceWarningDto(item.Code, item.Severity, Bounded(item.Message) ?? "Character continuity warning.")));
        warnings.AddRange(worldSnapshot.Warnings.Take(MovieProductionReferenceLimits.MaxWarnings).Select(ToWarning));
        if (keyframe is null)
            warnings.Add(new("approved_keyframe_missing", "attention", "No approved production keyframe is available for this shot."));
        return warnings.Take(MovieProductionReferenceLimits.MaxWarnings).ToArray();
    }

    private static MovieProductionReferenceWarningDto ToWarning(MovieWorldContinuityWarning warning) =>
        new(warning.Code, warning.Severity, Bounded(warning.Message) ?? "World continuity warning.");

    private static MovieProductionReferenceProvenanceDto Provenance(string sourceType, Guid? sourceId, int? sourceVersion, string sourceHash, string precedence, DateTime capturedAt) =>
        new(sourceType, sourceId, sourceVersion, sourceHash, precedence, capturedAt == default ? DateTime.UnixEpoch : capturedAt);

    private static string? ReadString(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? Bounded(value.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Bounded(string? value, int maxLength = MovieProductionReferenceLimits.MaxTextLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string BoundedJson(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return "{\"truncated\":true}";
    }

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions)))).ToLowerInvariant();
}

public sealed class MovieProductionReferenceException(string message) : Exception(message);
