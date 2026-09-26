using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieWorldContinuityLimits
{
    public const int SnapshotVersion = 1;
    public const int MaxUsages = 128;
    public const int MaxLocations = 16;
    public const int MaxSets = 16;
    public const int MaxVariationsPerSet = 8;
    public const int MaxProps = 32;
    public const int MaxFacts = 64;
    public const int MaxLocks = 64;
    public const int MaxWarnings = 64;
}

public sealed record MovieWorldContinuityTarget(
    string ScopeType,
    Guid MovieProjectId,
    Guid? SceneId,
    Guid? ShotId);

public sealed record MovieWorldContinuitySource(
    string EntityType,
    Guid? EntityId,
    Guid? RecordId,
    string FieldName,
    string Value);

public sealed record MovieWorldContinuityWarning(
    string Code,
    string Severity,
    string Message,
    MovieWorldContinuitySource Source,
    MovieWorldContinuityTarget Target);

public sealed record MovieWorldContinuityLocationSnapshot(
    Guid Id,
    string Name,
    string Description,
    string? VisualContinuityNotes,
    Guid? ReferenceAssetId);

public sealed record MovieWorldContinuityVariationSnapshot(
    Guid Id,
    Guid MovieSetId,
    string Name,
    string? VisualDescription,
    string? TimeOfDay,
    string? Weather,
    string? Lighting,
    string? ContinuityNotes,
    Guid? ReferenceAssetId,
    bool IsDefault);

public sealed record MovieWorldContinuitySetSnapshot(
    Guid Id,
    Guid? MovieLocationId,
    string Name,
    string Description,
    string EnvironmentType,
    string? VisualDescription,
    string? TimeOfDay,
    string? Weather,
    string? ContinuityNotes,
    Guid? ReferenceAssetId,
    IReadOnlyList<MovieWorldContinuityVariationSnapshot> Variations);

public sealed record MovieWorldContinuityPropSnapshot(
    Guid Id,
    string Name,
    string Description,
    string? Category,
    string? ContinuityNotes,
    Guid? ReferenceAssetId,
    string? State);

public sealed record MovieWorldContinuityFactSnapshot(
    Guid Id,
    string ScopeType,
    Guid? ScopeId,
    string FactKey,
    string FactValue,
    string? Notes,
    DateTime UpdatedAt);

public sealed record MovieWorldContinuityLockSnapshot(
    Guid Id,
    string EntityType,
    Guid? EntityId,
    string FieldName,
    string LockedValue,
    string Strength,
    string? Reason,
    DateTime CreatedAt);

public sealed record MovieWorldContinuitySnapshot(
    Guid MovieProjectId,
    Guid? SceneId,
    Guid? ShotId,
    int SnapshotVersion,
    DateTime CreatedAt,
    string SnapshotHash,
    IReadOnlyList<MovieWorldContinuityLocationSnapshot> Locations,
    IReadOnlyList<MovieWorldContinuitySetSnapshot> Sets,
    IReadOnlyList<MovieWorldContinuityPropSnapshot> Props,
    IReadOnlyList<MovieWorldContinuityFactSnapshot> Facts,
    IReadOnlyList<MovieWorldContinuityLockSnapshot> Locks,
    IReadOnlyList<MovieWorldContinuityWarning> Warnings)
{
    public string ToJson() => JsonSerializer.Serialize(this, MovieWorldContinuityJson.Options);
}

// The DTO suffix is retained for API consumers; the immutable record is also used internally
// so production and generation receive the exact same bounded contract.
public sealed record MovieWorldContinuitySnapshotDto(
    Guid MovieProjectId,
    Guid? SceneId,
    Guid? ShotId,
    int SnapshotVersion,
    DateTime CreatedAt,
    string SnapshotHash,
    IReadOnlyList<MovieWorldContinuityLocationSnapshot> Locations,
    IReadOnlyList<MovieWorldContinuitySetSnapshot> Sets,
    IReadOnlyList<MovieWorldContinuityPropSnapshot> Props,
    IReadOnlyList<MovieWorldContinuityFactSnapshot> Facts,
    IReadOnlyList<MovieWorldContinuityLockSnapshot> Locks,
    IReadOnlyList<MovieWorldContinuityWarning> Warnings)
{
    public static MovieWorldContinuitySnapshotDto From(MovieWorldContinuitySnapshot snapshot) => new(
        snapshot.MovieProjectId, snapshot.SceneId, snapshot.ShotId, snapshot.SnapshotVersion, snapshot.CreatedAt,
        snapshot.SnapshotHash, snapshot.Locations, snapshot.Sets, snapshot.Props, snapshot.Facts, snapshot.Locks, snapshot.Warnings);

    public string ToJson() => JsonSerializer.Serialize(this, MovieWorldContinuityJson.Options);
}

public static class MovieWorldContinuityJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

public sealed class MovieWorldContinuityProjector(TaslimDbContext db)
{
    public async Task<MovieWorldContinuitySnapshotDto?> ProjectAsync(
        Guid movieProjectId,
        Guid? sceneId = null,
        Guid? shotId = null,
        CancellationToken cancellationToken = default)
    {
        if (shotId.HasValue)
        {
            var shotTarget = await db.MovieShots.AsNoTracking()
                .Where(item => item.Id == shotId && item.Scene.MovieProjectId == movieProjectId)
                .Select(item => new { SceneId = item.MovieSceneId })
                .SingleOrDefaultAsync(cancellationToken);
            if (shotTarget is null || (sceneId.HasValue && sceneId.Value != shotTarget.SceneId)) return null;
            sceneId = shotTarget.SceneId;
        }
        else if (sceneId.HasValue && !await db.MovieScenes.AsNoTracking().AnyAsync(item => item.Id == sceneId && item.MovieProjectId == movieProjectId, cancellationToken))
        {
            return null;
        }

        var target = new MovieWorldContinuityTarget(
            shotId.HasValue ? MovieWorldScopes.Shot : sceneId.HasValue ? MovieWorldScopes.Scene : MovieWorldScopes.Project,
            movieProjectId,
            sceneId,
            shotId);

        var usagesQuery = db.MovieWorldUsages.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId &&
                (!sceneId.HasValue || item.MovieSceneId == sceneId) &&
                (!shotId.HasValue || item.MovieShotId == null || item.MovieShotId == shotId));
        var usages = await usagesQuery
            .OrderBy(item => item.MovieShotId.HasValue)
            .ThenBy(item => item.EntityType)
            .ThenBy(item => item.EntityId)
            .ThenBy(item => item.Id)
            .Take(MovieWorldContinuityLimits.MaxUsages)
            .ToListAsync(cancellationToken);

        var locationIds = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Location).Select(item => item.EntityId).Distinct().ToArray();
        var setIds = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Set).Select(item => item.EntityId).Distinct().Take(MovieWorldContinuityLimits.MaxSets).ToArray();
        var propIds = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Prop).Select(item => item.EntityId).Distinct().Take(MovieWorldContinuityLimits.MaxProps).ToArray();

        var sets = setIds.Length == 0
            ? []
            : await db.MovieSets.AsNoTracking()
                .Where(item => item.MovieProjectId == movieProjectId && setIds.Contains(item.Id))
                .OrderBy(item => item.Id)
                .Take(MovieWorldContinuityLimits.MaxSets)
                .ToListAsync(cancellationToken);
        locationIds = locationIds.Concat(sets.Where(item => item.MovieLocationId.HasValue).Select(item => item.MovieLocationId!.Value)).Distinct().Take(MovieWorldContinuityLimits.MaxLocations).ToArray();

        var locations = locationIds.Length == 0
            ? []
            : await db.MovieLocations.AsNoTracking()
                .Where(item => item.MovieProjectId == movieProjectId && locationIds.Contains(item.Id))
                .OrderBy(item => item.Id)
                .Take(MovieWorldContinuityLimits.MaxLocations)
                .ToListAsync(cancellationToken);
        var props = propIds.Length == 0
            ? []
            : await db.MovieProps.AsNoTracking()
                .Where(item => item.MovieProjectId == movieProjectId && propIds.Contains(item.Id))
                .OrderBy(item => item.Id)
                .Take(MovieWorldContinuityLimits.MaxProps)
                .ToListAsync(cancellationToken);
        var variations = setIds.Length == 0
            ? []
            : await db.MovieSetVariations.AsNoTracking()
                .Where(item => setIds.Contains(item.MovieSetId))
                .OrderBy(item => item.MovieSetId)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);

        var relevantEntityIds = locationIds.Concat(setIds).Concat(propIds).ToArray();
        var factsQuery = db.MovieContinuityFacts.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId &&
            (item.ScopeType == MovieWorldScopes.Project ||
             (sceneId.HasValue && item.ScopeType == MovieWorldScopes.Scene && item.ScopeId == sceneId) ||
             (shotId.HasValue && item.ScopeType == MovieWorldScopes.Shot && item.ScopeId == shotId) ||
             (item.ScopeId.HasValue && relevantEntityIds.Contains(item.ScopeId.Value))));
        var facts = await factsQuery
            .OrderBy(item => item.ScopeType)
            .ThenBy(item => item.ScopeId)
            .ThenBy(item => item.FactKey)
            .ThenBy(item => item.Id)
            .Take(MovieWorldContinuityLimits.MaxFacts)
            .ToListAsync(cancellationToken);

        var locksQuery = db.MovieContinuityLocks.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && item.ReleasedAt == null &&
            ((item.EntityType == MovieWorldScopes.Project && (!item.EntityId.HasValue || item.EntityId == movieProjectId)) ||
             (sceneId.HasValue && item.EntityType == MovieWorldScopes.Scene && item.EntityId == sceneId) ||
             (shotId.HasValue && item.EntityType == MovieWorldScopes.Shot && item.EntityId == shotId) ||
             (item.EntityId.HasValue && relevantEntityIds.Contains(item.EntityId.Value))));
        var locks = await locksQuery
            .OrderBy(item => item.EntityType)
            .ThenBy(item => item.EntityId)
            .ThenBy(item => item.FieldName)
            .ThenBy(item => item.Id)
            .Take(MovieWorldContinuityLimits.MaxLocks)
            .ToListAsync(cancellationToken);

        var locationSnapshots = locations.Select(item => new MovieWorldContinuityLocationSnapshot(item.Id, item.Name, item.Description, item.VisualContinuityNotes, item.ReferenceAssetId)).ToArray();
        var setSnapshots = sets.Select(item => new MovieWorldContinuitySetSnapshot(
            item.Id, item.MovieLocationId, item.Name, item.Description, item.EnvironmentType, item.VisualDescription,
            item.TimeOfDay, item.Weather, item.ContinuityNotes, item.ReferenceAssetId,
            variations.Where(variation => variation.MovieSetId == item.Id)
                .Take(MovieWorldContinuityLimits.MaxVariationsPerSet)
                .Select(variation => new MovieWorldContinuityVariationSnapshot(variation.Id, variation.MovieSetId, variation.Name, variation.VisualDescription, variation.TimeOfDay, variation.Weather, variation.Lighting, variation.ContinuityNotes, variation.ReferenceAssetId, variation.IsDefault)).ToArray())).ToArray();
        var factSnapshots = facts.Select(item => new MovieWorldContinuityFactSnapshot(item.Id, item.ScopeType, item.ScopeId, item.FactKey, item.FactValue, item.Notes, item.UpdatedAt)).ToArray();
        var lockSnapshots = locks.Select(item => new MovieWorldContinuityLockSnapshot(item.Id, item.EntityType, item.EntityId, item.FieldName, item.LockedValue, item.Strength, item.Reason, item.CreatedAt)).ToArray();
        var propSnapshots = props.Select(item => new MovieWorldContinuityPropSnapshot(item.Id, item.Name, item.Description, item.Category, item.ContinuityNotes, item.ReferenceAssetId, ResolvePropState(item.Id, factSnapshots, lockSnapshots))).ToArray();
        var warnings = MovieWorldContinuityConflictDetector.Detect(target, usages, locationSnapshots, setSnapshots, propSnapshots, factSnapshots, lockSnapshots);

        var hashInput = JsonSerializer.Serialize(new
        {
            target,
            locations = locationSnapshots,
            sets = setSnapshots,
            props = propSnapshots,
            facts = factSnapshots,
            locks = lockSnapshots,
            warnings,
        }, MovieWorldContinuityJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();
        var snapshot = new MovieWorldContinuitySnapshot(
            movieProjectId, sceneId, shotId, MovieWorldContinuityLimits.SnapshotVersion, DateTime.UtcNow, hash,
            locationSnapshots, setSnapshots, propSnapshots, factSnapshots, lockSnapshots, warnings);
        return MovieWorldContinuitySnapshotDto.From(snapshot);
    }

    private static string? ResolvePropState(Guid propId, IReadOnlyList<MovieWorldContinuityFactSnapshot> facts, IReadOnlyList<MovieWorldContinuityLockSnapshot> locks)
    {
        var factState = facts.Where(item => IsPropFact(item, propId) && IsStateField(item.FactKey)).OrderBy(item => item.Id).Select(item => item.FactValue).FirstOrDefault();
        var lockedState = locks.Where(item => item.EntityType == MovieWorldEntityTypes.Prop && item.EntityId == propId && IsStateField(item.FieldName)).OrderBy(item => item.Id).Select(item => item.LockedValue).FirstOrDefault();
        return lockedState ?? factState;
    }

    internal static bool IsPropFact(MovieWorldContinuityFactSnapshot fact, Guid propId) =>
        (fact.ScopeType == MovieWorldScopes.Prop && fact.ScopeId == propId) ||
        fact.FactKey.StartsWith($"prop:{propId:N}:", StringComparison.OrdinalIgnoreCase) ||
        fact.FactKey.StartsWith($"prop.{propId:N}.", StringComparison.OrdinalIgnoreCase);

    internal static bool IsStateField(string field) =>
        field.Equals("state", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("condition", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("currentState", StringComparison.OrdinalIgnoreCase) ||
        field.EndsWith(":state", StringComparison.OrdinalIgnoreCase) ||
        field.EndsWith(".state", StringComparison.OrdinalIgnoreCase);
}

public static class MovieWorldContinuityConflictDetector
{
    public static IReadOnlyList<MovieWorldContinuityWarning> Detect(
        MovieWorldContinuityTarget target,
        IReadOnlyList<MovieWorldUsage> usages,
        IReadOnlyList<MovieWorldContinuityLocationSnapshot> locations,
        IReadOnlyList<MovieWorldContinuitySetSnapshot> sets,
        IReadOnlyList<MovieWorldContinuityPropSnapshot> props,
        IReadOnlyList<MovieWorldContinuityFactSnapshot> facts,
        IReadOnlyList<MovieWorldContinuityLockSnapshot> locks)
    {
        var warnings = new List<MovieWorldContinuityWarning>();
        var locationUsages = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Location).OrderBy(item => item.Id).ToArray();
        var setUsages = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Set).OrderBy(item => item.Id).ToArray();

        AddUsageConflict(warnings, target, locationUsages, "conflicting_location_usage", "location");
        AddUsageConflict(warnings, target, setUsages, "conflicting_set_usage", "set");
        foreach (var setUsage in setUsages)
        {
            var set = sets.FirstOrDefault(item => item.Id == setUsage.EntityId);
            if (set?.MovieLocationId is not Guid setLocationId) continue;
            foreach (var locationUsage in locationUsages.Where(item => item.EntityId != setLocationId))
            {
                warnings.Add(new MovieWorldContinuityWarning(
                    "conflicting_location_set_usage", "warning",
                    $"Set '{set.Name}' belongs to location {setLocationId}, but the target also uses location {locationUsage.EntityId}.",
                    new MovieWorldContinuitySource("set", set.Id, setUsage.Id, "movieLocationId", setLocationId.ToString("N")), target));
            }
        }

        foreach (var set in sets)
        {
            var setLocks = locks.Where(item => item.EntityType == MovieWorldEntityTypes.Set && item.EntityId == set.Id && IsVariationField(item.FieldName)).OrderBy(item => item.Id).ToArray();
            var requested = RequestedVariation(set, setUsages.FirstOrDefault(item => item.EntityId == set.Id), facts);
            foreach (var setLock in setLocks)
            {
                var lockedVariation = ResolveVariation(set, setLock.LockedValue);
                if (lockedVariation is null)
                {
                    warnings.Add(new MovieWorldContinuityWarning(
                        "locked_variation_mismatch", "error",
                        $"Locked variation '{setLock.LockedValue}' is not a variation of set '{set.Name}'.",
                        new MovieWorldContinuitySource("continuity_lock", setLock.EntityId, setLock.Id, setLock.FieldName, setLock.LockedValue), target));
                    continue;
                }
                if (requested is not null && requested.Id != lockedVariation.Id)
                {
                    warnings.Add(new MovieWorldContinuityWarning(
                        "locked_variation_mismatch", "error",
                        $"Target selects variation '{requested.Name}' for set '{set.Name}', but the lock requires '{lockedVariation.Name}'.",
                        new MovieWorldContinuitySource("continuity_lock", setLock.EntityId, setLock.Id, setLock.FieldName, setLock.LockedValue), target));
                }
            }
        }

        foreach (var prop in props)
        {
            var stateRecords = facts.Where(item => MovieWorldContinuityProjector.IsPropFact(item, prop.Id) && MovieWorldContinuityProjector.IsStateField(item.FactKey))
                .Select(item => (Value: item.FactValue, Id: item.Id, Kind: "continuity_fact"))
                .Concat(locks.Where(item => item.EntityType == MovieWorldEntityTypes.Prop && item.EntityId == prop.Id && MovieWorldContinuityProjector.IsStateField(item.FieldName))
                    .Select(item => (Value: item.LockedValue, Id: item.Id, Kind: "continuity_lock")))
                .OrderBy(item => item.Id)
                .ToArray();
            var distinct = stateRecords.Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length > 1)
            {
                var first = stateRecords[0];
                warnings.Add(new MovieWorldContinuityWarning(
                    "prop_state_inconsistency", "error",
                    $"Prop '{prop.Name}' has inconsistent target states: {string.Join(", ", distinct)}.",
                    new MovieWorldContinuitySource(first.Kind, prop.Id, first.Id, "state", first.Value), target));
            }
        }

        foreach (var group in locks.GroupBy(item => (item.EntityType, EntityId: item.EntityType == MovieWorldScopes.Project ? null : item.EntityId, Field: item.FieldName.Trim().ToLowerInvariant())))
        {
            var values = group.Select(item => item.LockedValue).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length <= 1) continue;
            var first = group.OrderBy(item => item.Id).First();
            warnings.Add(new MovieWorldContinuityWarning(
                "conflicting_locked_continuity_fact", "error",
                $"Active locks for {group.Key.EntityType} {group.Key.EntityId} field '{group.Key.Field}' disagree: {string.Join(", ", values)}.",
                new MovieWorldContinuitySource("continuity_lock", group.Key.EntityId, first.Id, first.FieldName, first.LockedValue), target));
        }
        foreach (var group in facts.GroupBy(item => (item.ScopeType, item.ScopeId, Field: item.FactKey.Trim().ToLowerInvariant())))
        {
            var values = group.Select(item => item.FactValue).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length <= 1) continue;
            var first = group.OrderBy(item => item.Id).First();
            warnings.Add(new MovieWorldContinuityWarning(
                "conflicting_continuity_fact", "warning",
                $"Continuity facts for {group.Key.ScopeType} {group.Key.ScopeId} field '{group.Key.Field}' disagree: {string.Join(", ", values)}.",
                new MovieWorldContinuitySource("continuity_fact", group.Key.ScopeId, first.Id, first.FactKey, first.FactValue), target));
        }
        return warnings.OrderBy(item => item.Code).ThenBy(item => item.Source.RecordId).ThenBy(item => item.Source.EntityId).Take(MovieWorldContinuityLimits.MaxWarnings).ToArray();
    }

    private static void AddUsageConflict(List<MovieWorldContinuityWarning> warnings, MovieWorldContinuityTarget target, IReadOnlyList<MovieWorldUsage> usages, string code, string entityType)
    {
        var entityIds = usages.Select(item => item.EntityId).Distinct().ToArray();
        if (entityIds.Length <= 1) return;
        var first = usages[0];
        warnings.Add(new MovieWorldContinuityWarning(
            code, "warning",
            $"Target uses multiple {entityType} records: {string.Join(", ", entityIds)}.",
            new MovieWorldContinuitySource(entityType, first.EntityId, first.Id, "usage", string.Join(",", entityIds)), target));
    }

    private static bool IsVariationField(string field) =>
        field.Equals("variation", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("variationId", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("selectedVariationId", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("variationName", StringComparison.OrdinalIgnoreCase);

    private static MovieWorldContinuityVariationSnapshot? RequestedVariation(MovieWorldContinuitySetSnapshot set, MovieWorldUsage? usage, IReadOnlyList<MovieWorldContinuityFactSnapshot> facts)
    {
        var requestedValue = usage is null ? null : ExtractVariationValue(usage.Role);
        requestedValue ??= facts.Where(item => item.ScopeType == MovieWorldScopes.Set && item.ScopeId == set.Id && IsVariationField(item.FactKey)).OrderBy(item => item.Id).Select(item => item.FactValue).FirstOrDefault();
        return ResolveVariation(set, requestedValue) ?? set.Variations.FirstOrDefault(item => item.IsDefault);
    }

    private static string? ExtractVariationValue(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var separator = role.IndexOf('=');
        if (separator < 0) separator = role.IndexOf(':');
        if (separator < 0) return null;
        var key = role[..separator].Trim();
        return IsVariationField(key) ? role[(separator + 1)..].Trim() : null;
    }

    private static MovieWorldContinuityVariationSnapshot? ResolveVariation(MovieWorldContinuitySetSnapshot set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Guid.TryParse(value, out var id)) return set.Variations.FirstOrDefault(item => item.Id == id);
        return set.Variations.FirstOrDefault(item => item.Name.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
