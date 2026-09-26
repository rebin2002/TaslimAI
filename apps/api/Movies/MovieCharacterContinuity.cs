using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieContinuitySnapshotLimits
{
    public const int MaxCharacters = 32;
    public const int MaxReferencesPerCharacter = 8;
    public const int MaxFactsPerCharacter = 12;
    public const int MaxWarnings = 64;
    public const int MaxValueLength = 2_000;
    public const int MaxSnapshotJsonLength = 60_000;
}

public sealed class MovieCharacterContinuitySnapshot
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? MovieSceneId { get; set; }
    public Guid? MovieShotId { get; set; }
    public int Version { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public MovieScene? MovieScene { get; set; }
    public MovieShot? MovieShot { get; set; }
}

public sealed record MovieCharacterContinuityWarningDto(
    string Code,
    string Severity,
    Guid? CharacterId,
    string? CharacterName,
    Guid? TargetSceneId,
    Guid? TargetShotId,
    string Field,
    string? ExpectedValue,
    string? ActualValue,
    string SourceType,
    Guid? SourceId,
    string Message);

public sealed record MovieCharacterContinuityLockFactDto(
    Guid Id,
    Guid? CharacterStateId,
    string FieldKey,
    string LockedValue,
    DateTime ApprovedAt);

public sealed record MovieCharacterContinuityStateDto(
    Guid Id,
    string Key,
    string? Label,
    string? Wardrobe,
    string? AgeOrTimeState,
    string? Appearance,
    string? InjuryOrCondition,
    string? LocationOrStoryState,
    string? ContinuityNotes);

public sealed record MovieCharacterContinuityFactDto(
    Guid Id,
    string FactKey,
    string FactValue,
    string? Notes,
    string ScopeType,
    Guid? ScopeId);

public sealed record MovieCharacterContinuityCharacterDto(
    Guid Id,
    string Name,
    string? Role,
    string Description,
    string? Appearance,
    string? PhysicalDescription,
    string? Wardrobe,
    string? VoiceReference,
    string? PersonalityAndStoryNotes,
    string? VoiceAndPerformance,
    string? ContinuityNotes,
    IReadOnlyList<Guid> ReferenceAssetIds,
    MovieCharacterContinuityStateDto? RelevantState,
    IReadOnlyList<MovieCharacterContinuityLockFactDto> Locks,
    IReadOnlyList<MovieCharacterContinuityFactDto> Facts);

public sealed record MovieCharacterContinuityProjectionDto(
    Guid MovieProjectId,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    IReadOnlyList<MovieCharacterContinuityCharacterDto> Characters,
    IReadOnlyList<MovieCharacterContinuityWarningDto> Warnings);

public sealed record MovieCharacterContinuitySnapshotDto(
    Guid? SnapshotId,
    Guid MovieProjectId,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    int? Version,
    string SnapshotHash,
    string SnapshotJson,
    IReadOnlyList<MovieCharacterContinuityCharacterDto> Characters,
    IReadOnlyList<MovieCharacterContinuityWarningDto> Warnings,
    DateTime CreatedAt);

public sealed record MovieCharacterContinuitySnapshotRequest(Guid? MovieSceneId, Guid? MovieShotId);

public sealed class MovieContinuityTargetException(string message) : Exception(message);

public interface IMovieCharacterContinuityService
{
    Task<MovieCharacterContinuityProjectionDto?> ProjectAsync(Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken = default);
    Task<MovieCharacterContinuitySnapshotDto?> BuildSnapshotAsync(Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken = default);
    Task<MovieCharacterContinuitySnapshotDto?> GetSnapshotAsync(Guid userId, Guid snapshotId, CancellationToken cancellationToken = default);
    Task<MovieCharacterContinuitySnapshotDto> BuildSnapshotForTargetAsync(Guid movieProjectId, Guid? sceneId, Guid? shotId, bool persist, CancellationToken cancellationToken = default);
}

public sealed class MovieCharacterContinuityService(TaslimDbContext db, WorkspaceAccessService access) : IMovieCharacterContinuityService
{
    public async Task<MovieCharacterContinuityProjectionDto?> ProjectAsync(Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking().Where(item => item.Id == movieProjectId).Select(item => new { item.Id, item.WorkspaceId }).FirstOrDefaultAsync(cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        var target = await ResolveTargetAsync(movieProjectId, sceneId, shotId, cancellationToken);
        return await BuildProjectionAsync(movieProjectId, target, cancellationToken);
    }

    public async Task<MovieCharacterContinuitySnapshotDto?> BuildSnapshotAsync(Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking().Where(item => item.Id == movieProjectId).Select(item => new { item.Id, item.WorkspaceId }).FirstOrDefaultAsync(cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        return await BuildSnapshotForTargetAsync(movieProjectId, sceneId, shotId, true, cancellationToken);
    }

    public async Task<MovieCharacterContinuitySnapshotDto?> GetSnapshotAsync(Guid userId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await db.MovieCharacterContinuitySnapshots.AsNoTracking().Include(item => item.MovieProject)
            .FirstOrDefaultAsync(item => item.Id == snapshotId, cancellationToken);
        return snapshot is null || !await access.IsMemberAsync(userId, snapshot.MovieProject.WorkspaceId, cancellationToken)
            ? null
            : DeserializeSnapshot(snapshot);
    }

    public async Task<MovieCharacterContinuitySnapshotDto> BuildSnapshotForTargetAsync(Guid movieProjectId, Guid? sceneId, Guid? shotId, bool persist, CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(movieProjectId, sceneId, shotId, cancellationToken);
        var projection = await BuildProjectionAsync(movieProjectId, target, cancellationToken)
            ?? throw new MovieContinuityTargetException("The continuity target project was not found.");
        var payload = new
        {
            schemaVersion = 1,
            movieProjectId,
            movieSceneId = target.SceneId,
            movieShotId = target.ShotId,
            characters = projection.Characters,
            warnings = projection.Warnings,
        };
        var json = JsonSerializer.Serialize(payload);
        if (json.Length > MovieContinuitySnapshotLimits.MaxSnapshotJsonLength)
            throw new MovieContinuityTargetException("The continuity snapshot exceeded its bounded size.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var createdAt = DateTime.UtcNow;
        if (!persist) return new MovieCharacterContinuitySnapshotDto(null, movieProjectId, target.SceneId, target.ShotId, null, hash, json, projection.Characters, projection.Warnings, createdAt);

        var version = (await db.MovieCharacterContinuitySnapshots
            .Where(item => item.MovieProjectId == movieProjectId && item.MovieSceneId == target.SceneId && item.MovieShotId == target.ShotId)
            .MaxAsync(item => (int?)item.Version, cancellationToken) ?? 0) + 1;
        var entity = new MovieCharacterContinuitySnapshot
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, MovieSceneId = target.SceneId, MovieShotId = target.ShotId,
            Version = version, SnapshotJson = json, SnapshotHash = hash, CreatedAt = createdAt,
        };
        db.MovieCharacterContinuitySnapshots.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new MovieCharacterContinuitySnapshotDto(entity.Id, movieProjectId, target.SceneId, target.ShotId, version, hash, json, projection.Characters, projection.Warnings, createdAt);
    }

    private async Task<MovieContinuityTarget> ResolveTargetAsync(Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken)
    {
        if (shotId.HasValue)
        {
            var shot = await db.MovieShots.AsNoTracking().Where(item => item.Id == shotId).Select(item => new { item.Id, item.MovieSceneId, ProjectId = item.Scene.MovieProjectId }).FirstOrDefaultAsync(cancellationToken);
            if (shot is null || shot.ProjectId != movieProjectId) throw new MovieContinuityTargetException("The continuity shot must belong to the selected movie project.");
            if (sceneId.HasValue && sceneId != shot.MovieSceneId) throw new MovieContinuityTargetException("The continuity scene and shot do not belong to the same target.");
            return new MovieContinuityTarget(shot.MovieSceneId, shot.Id);
        }
        if (sceneId.HasValue)
        {
            if (!await db.MovieScenes.AsNoTracking().AnyAsync(item => item.Id == sceneId && item.MovieProjectId == movieProjectId, cancellationToken))
                throw new MovieContinuityTargetException("The continuity scene must belong to the selected movie project.");
            return new MovieContinuityTarget(sceneId, null);
        }
        if (!await db.MovieProjects.AsNoTracking().AnyAsync(item => item.Id == movieProjectId, cancellationToken))
            throw new MovieContinuityTargetException("The continuity project was not found.");
        return new MovieContinuityTarget(null, null);
    }

    private async Task<MovieCharacterContinuityProjectionDto?> BuildProjectionAsync(Guid movieProjectId, MovieContinuityTarget target, CancellationToken cancellationToken)
    {
        var sceneText = target.SceneId.HasValue
            ? await db.MovieScenes.AsNoTracking().Where(item => item.Id == target.SceneId).Select(item => new { item.Title, item.Summary, item.ContinuityNotes, item.Narration, item.Dialogue }).FirstOrDefaultAsync(cancellationToken)
            : null;
        var shotText = target.ShotId.HasValue
            ? await db.MovieShots.AsNoTracking().Where(item => item.Id == target.ShotId).Select(item => new { item.Description, item.CameraAndFraming, item.CameraMotion, item.Narration, item.Dialogue, item.VisualContinuityNotes }).FirstOrDefaultAsync(cancellationToken)
            : null;
        var screenplayElements = target.SceneId.HasValue
            ? await db.MovieScreenplayScenes.AsNoTracking().Where(item => item.MovieSceneId == target.SceneId && item.Revision.Status == MovieStoryRevisionStatuses.Approved)
                .SelectMany(item => item.Elements).Select(item => new { item.CharacterName, item.Content }).Take(160).ToArrayAsync(cancellationToken)
            : [];
        var sceneTextValue = string.Join(" ", sceneText?.Title, sceneText?.Summary, sceneText?.ContinuityNotes, sceneText?.Narration, sceneText?.Dialogue);
        var shotTextValue = string.Join(" ", shotText?.Description, shotText?.CameraAndFraming, shotText?.CameraMotion, shotText?.Narration, shotText?.Dialogue, shotText?.VisualContinuityNotes);
        var targetText = string.Join(" ", sceneTextValue, shotTextValue, string.Join(" ", screenplayElements.Select(item => item.Content)));
        var characterNames = screenplayElements.Select(item => item.CharacterName?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidateRows = await db.MovieCharacters.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId)
            .OrderBy(item => item.CreatedAt).Take(MovieContinuitySnapshotLimits.MaxCharacters * 8).Select(item => new { item.Id, item.Name }).ToArrayAsync(cancellationToken);
        var relevantCharacterIds = candidateRows.Where(item => characterNames.Any(name => string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase)) || NameMatchesTarget(targetText, item.Name)).Select(item => item.Id).Take(MovieContinuitySnapshotLimits.MaxCharacters).ToArray();
        var characters = await db.MovieCharacters.AsNoTracking().Where(item => relevantCharacterIds.Contains(item.Id)).OrderBy(item => item.CreatedAt)
            .Include(item => item.States).ThenInclude(item => item.ContinuityLocks).Include(item => item.ContinuityLocks).Include(item => item.ReferenceAssets).ToArrayAsync(cancellationToken);
        if (characters.Length == 0) return new MovieCharacterContinuityProjectionDto(movieProjectId, target.SceneId, target.ShotId, [], []);

        var applicableFacts = target.SceneId.HasValue || target.ShotId.HasValue
            ? await db.MovieContinuityFacts.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId &&
                (item.ScopeType == MovieWorldScopes.Project || (item.ScopeType == MovieWorldScopes.Scene && item.ScopeId == target.SceneId) || (item.ScopeType == MovieWorldScopes.Shot && item.ScopeId == target.ShotId)))
                .OrderBy(item => item.CreatedAt).Take(MovieContinuitySnapshotLimits.MaxCharacters * MovieContinuitySnapshotLimits.MaxFactsPerCharacter).ToArrayAsync(cancellationToken)
            : [];
        var warnings = new List<MovieCharacterContinuityWarningDto>();
        var projections = characters.Select(character => ProjectCharacter(character, target, sceneTextValue, shotTextValue, targetText, applicableFacts, warnings)).ToArray();
        return new MovieCharacterContinuityProjectionDto(movieProjectId, target.SceneId, target.ShotId, projections, warnings.Take(MovieContinuitySnapshotLimits.MaxWarnings).ToArray());
    }

    private static MovieCharacterContinuityCharacterDto ProjectCharacter(
        MovieCharacter character,
        MovieContinuityTarget target,
        string sceneText,
        string shotText,
        string targetText,
        IReadOnlyList<MovieContinuityFact> applicableFacts,
        ICollection<MovieCharacterContinuityWarningDto> warnings)
    {
        var stateMatches = character.States.Where(state => ContainsState(targetText, state)).OrderByDescending(state => state.UpdatedAt).ToArray();
        var sceneStateMatches = character.States.Where(state => ContainsState(sceneText, state)).OrderByDescending(state => state.UpdatedAt).ToArray();
        var shotStateMatches = character.States.Where(state => ContainsState(shotText, state)).OrderByDescending(state => state.UpdatedAt).ToArray();
        var state = stateMatches.FirstOrDefault() ?? character.States.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (stateMatches.Length > 1)
            AddWarning(warnings, "incompatible_character_state", "error", character, target, "state", stateMatches[0].Key, string.Join(", ", stateMatches.Skip(1).Select(item => item.Key)), "target text", null, $"Character '{character.Name}' is associated with multiple persisted states in the target.");
        if (target.ShotId.HasValue && sceneStateMatches.Length > 0 && shotStateMatches.Length > 0 && sceneStateMatches[0].Id != shotStateMatches[0].Id)
            AddWarning(warnings, "chronology_state_inconsistency", "warning", character, target, "state", sceneStateMatches[0].Key, shotStateMatches[0].Key, "scene/shot persisted text", null, $"Character '{character.Name}' has different persisted states at the scene and shot scopes.");

        foreach (var lockFact in character.ContinuityLocks.Where(item => item.MovieCharacterStateId is null))
        {
            var actual = CardFieldValue(character, lockFact.FieldKey);
            if (!string.Equals(actual, lockFact.LockedValue, StringComparison.Ordinal))
                AddWarning(warnings, "locked_character_fact_conflict", "error", character, target, lockFact.FieldKey, lockFact.LockedValue, actual, "character continuity lock", lockFact.Id, $"Character '{character.Name}' conflicts with its locked {lockFact.FieldKey}.");
        }
        if (state is not null)
        {
            foreach (var lockFact in state.ContinuityLocks)
            {
                var actual = StateFieldValue(state, lockFact.FieldKey);
                if (!string.Equals(actual, lockFact.LockedValue, StringComparison.Ordinal))
                    AddWarning(warnings, "locked_character_state_conflict", "error", character, target, lockFact.FieldKey, lockFact.LockedValue, actual, "character state continuity lock", lockFact.Id, $"Character '{character.Name}' conflicts with its locked state field {lockFact.FieldKey}.");
            }
        }
        var refs = character.ReferenceAssets.OrderBy(item => item.SortOrder).Select(item => item.AssetId).Take(MovieContinuitySnapshotLimits.MaxReferencesPerCharacter).ToArray();
        if (character.ReferenceAssetId is Guid primary && !character.ReferenceAssets.Any(item => item.AssetId == primary))
            AddWarning(warnings, "character_reference_mismatch", "warning", character, target, "referenceAssetIds", primary.ToString(), string.Join(",", refs), "character card", character.Id, $"Character '{character.Name}' primary reference asset is not in its persisted reference set.");
        var facts = applicableFacts.Where(fact => ContainsIgnoreCase(fact.FactKey, character.Name) || ContainsIgnoreCase(fact.FactValue, character.Name) || ContainsIgnoreCase(fact.Notes, character.Name)).Take(MovieContinuitySnapshotLimits.MaxFactsPerCharacter).ToArray();
        foreach (var group in facts.GroupBy(item => item.FactKey, StringComparer.OrdinalIgnoreCase))
        {
            var values = group.Select(item => item.FactValue).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > 1)
                AddWarning(warnings, "conflicting_character_fact", "warning", character, target, group.Key, values[0], string.Join(" | ", values.Skip(1)), "persisted continuity facts", group.First().Id, $"Character '{character.Name}' has conflicting persisted values for {group.Key}.");
        }
        return new MovieCharacterContinuityCharacterDto(
            character.Id, Bounded(character.Name)!, Bounded(character.Role), Bounded(character.Description)!, Bounded(character.Appearance), Bounded(character.PhysicalDescription),
            Bounded(character.Wardrobe), Bounded(character.VoiceReference), Bounded(character.PersonalityAndStoryNotes), Bounded(character.VoiceAndPerformance), Bounded(character.ContinuityNotes),
            refs, state is null ? null : new MovieCharacterContinuityStateDto(state.Id, Bounded(state.Key)!, Bounded(state.Label), Bounded(state.Wardrobe), Bounded(state.AgeOrTimeState), Bounded(state.Appearance), Bounded(state.InjuryOrCondition), Bounded(state.LocationOrStoryState), Bounded(state.ContinuityNotes)),
            character.ContinuityLocks.Concat(character.States.SelectMany(item => item.ContinuityLocks)).GroupBy(item => item.Id).Select(item => item.First()).OrderBy(item => item.ApprovedAt).Select(item => new MovieCharacterContinuityLockFactDto(item.Id, item.MovieCharacterStateId, Bounded(item.FieldKey)!, Bounded(item.LockedValue)!, item.ApprovedAt)).ToArray(),
            facts.Select(item => new MovieCharacterContinuityFactDto(item.Id, Bounded(item.FactKey)!, Bounded(item.FactValue)!, Bounded(item.Notes), Bounded(item.ScopeType)!, item.ScopeId)).ToArray());
    }

    private static bool ContainsState(string text, MovieCharacterState state) => ContainsIgnoreCase(text, state.Key) || ContainsIgnoreCase(text, state.Label);
    private static bool NameMatchesTarget(string targetText, string name) => ContainsIgnoreCase(targetText, name) || name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token => token.Length >= 3 && ContainsIgnoreCase(targetText, token));
    private static bool ContainsIgnoreCase(string? value, string? search) => !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(search) && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    private static string? Bounded(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= MovieContinuitySnapshotLimits.MaxValueLength ? value : value[..MovieContinuitySnapshotLimits.MaxValueLength];
    private static string? CardFieldValue(MovieCharacter character, string key) => key switch
    {
        "name" => character.Name, "role" => character.Role, "description" => character.Description, "appearance" => character.Appearance,
        "physicalDescription" => character.PhysicalDescription, "wardrobe" => character.Wardrobe, "voiceReference" => character.VoiceReference,
        "personalityAndStoryNotes" => character.PersonalityAndStoryNotes, "voiceAndPerformance" => character.VoiceAndPerformance, "continuityNotes" => character.ContinuityNotes, _ => null,
    };
    private static string? StateFieldValue(MovieCharacterState state, string key) => key switch
    {
        "label" => state.Label, "wardrobe" => state.Wardrobe, "ageOrTimeState" => state.AgeOrTimeState, "appearance" => state.Appearance,
        "injuryOrCondition" => state.InjuryOrCondition, "locationOrStoryState" => state.LocationOrStoryState, "continuityNotes" => state.ContinuityNotes, _ => null,
    };
    private static void AddWarning(ICollection<MovieCharacterContinuityWarningDto> warnings, string code, string severity, MovieCharacter character, MovieContinuityTarget target, string field, string? expected, string? actual, string sourceType, Guid? sourceId, string message) => warnings.Add(new MovieCharacterContinuityWarningDto(code, severity, character.Id, Bounded(character.Name), target.SceneId, target.ShotId, Bounded(field)!, Bounded(expected), Bounded(actual), sourceType, sourceId, Bounded(message)!));

    private static MovieCharacterContinuitySnapshotDto DeserializeSnapshot(MovieCharacterContinuitySnapshot snapshot)
    {
        using var document = JsonDocument.Parse(snapshot.SnapshotJson);
        var characters = document.RootElement.TryGetProperty("characters", out var characterJson) ? JsonSerializer.Deserialize<MovieCharacterContinuityCharacterDto[]>(characterJson.GetRawText()) ?? [] : [];
        var warnings = document.RootElement.TryGetProperty("warnings", out var warningJson) ? JsonSerializer.Deserialize<MovieCharacterContinuityWarningDto[]>(warningJson.GetRawText()) ?? [] : [];
        return new MovieCharacterContinuitySnapshotDto(snapshot.Id, snapshot.MovieProjectId, snapshot.MovieSceneId, snapshot.MovieShotId, snapshot.Version, snapshot.SnapshotHash, snapshot.SnapshotJson, characters, warnings, snapshot.CreatedAt);
    }

    private sealed record MovieContinuityTarget(Guid? SceneId, Guid? ShotId);
}
