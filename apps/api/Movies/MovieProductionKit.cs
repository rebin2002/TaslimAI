using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieProductionKitStatuses
{
    public const string Draft = "Draft";
    public const string Review = "Review";
    public const string Approved = "Approved";
    public const string Locked = "Locked";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft, Review, Approved, Locked,
    };
}

public static class MovieProductionKitReferenceTypes
{
    public const string Character = "character";
    public const string CharacterState = "character_state";
    public const string Location = "location";
    public const string Set = "set";
    public const string Prop = "prop";
    public const string WorldReference = "world_reference";
    public const string ContinuitySnapshot = "continuity_snapshot";
    public const string Asset = "asset";
    public const string ApprovedKeyframe = "approved_keyframe";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Character, CharacterState, Location, Set, Prop, WorldReference,
        ContinuitySnapshot, Asset, ApprovedKeyframe,
    };
}

public static class MovieProductionKitReadinessCodes
{
    public const string GuideMissing = "guide_missing";
    public const string GuideNotLocked = "guide_not_locked";
    public const string GuideRevisionStale = "guide_revision_stale";
    public const string RequiredReferenceMissing = "required_reference_missing";
    public const string ReferenceNotFound = "reference_not_found";
    public const string ReferenceNotReady = "reference_not_ready";
    public const string KitNotApproved = "kit_not_approved";
    public const string KitLocked = "kit_locked";
}

public sealed class MovieProductionKit
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public int CurrentRevisionNumber { get; set; }
    public int? LockedRevisionNumber { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser? LockedByUser { get; set; }
    public ICollection<MovieProductionKitRevision> Revisions { get; set; } = [];
}

/// <summary>
/// An append-only assembly of pointers to canonical Movie Guide, cast, world,
/// continuity, keyframe, and Asset records. It deliberately contains no Guide
/// section copies or provider execution fields.
/// </summary>
public sealed class MovieProductionKitRevision
{
    public Guid Id { get; set; }
    public Guid MovieProductionKitId { get; set; }
    public int RevisionNumber { get; set; }
    public string Status { get; set; } = MovieProductionKitStatuses.Draft;
    public Guid SourceGuideRevisionId { get; set; }
    public int SourceGuideRevisionNumber { get; set; }
    public string SourceGuideHash { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? ReviewNote { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public DateTime? LockedAt { get; set; }
    public string RevisionHash { get; set; } = string.Empty;

    public MovieProductionKit Kit { get; set; } = null!;
    public MovieGuideRevision SourceGuideRevision { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ReviewedByUser { get; set; }
    public ApplicationUser? LockedByUser { get; set; }
    public ICollection<MovieProductionKitReference> References { get; set; } = [];
}

/// <summary>
/// A typed, project-scoped pointer into an existing canonical record. SourceId
/// is polymorphic by ReferenceType because the existing Movie domains are
/// intentionally not collapsed into a duplicate Production Kit hierarchy.
/// </summary>
public sealed class MovieProductionKitReference
{
    public Guid Id { get; set; }
    public Guid MovieProductionKitRevisionId { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public int? SourceRevision { get; set; }
    public string? Label { get; set; }
    public string? Role { get; set; }
    public bool IsRequired { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public string ProvenanceJson { get; set; } = "{}";
    public int SortOrder { get; set; }

    public MovieProductionKitRevision Revision { get; set; } = null!;
}

public sealed record MovieProductionKitReadiness(
    bool Ready,
    string Status,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Warnings,
    int ReferenceCount,
    int RequiredReferenceCount,
    int ResolvedReferenceCount,
    bool SourceGuideLocked,
    int SourceGuideRevisionNumber);

public sealed class MovieProductionKitService(
    TaslimDbContext db,
    MovieAuthorizationService authorization) : IMovieProductionKitService
{
    public async Task<MovieProductionKitDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var kit = await LoadKitAsync(movieProjectId, cancellationToken);
        if (kit is null || !await authorization.CanPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return ToDto(kit);
    }

    public async Task<MovieProductionKitDto?> CreateRevisionAsync(
        Guid userId,
        Guid movieProjectId,
        MovieProductionKitRevisionRequest request,
        CancellationToken cancellationToken)
    {
        if (!await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.ProductionVersionEdit, cancellationToken)) return null;
        var movie = await db.MovieProjects
            .Include(item => item.Guide)
            .ThenInclude(item => item.Revisions)
            .SingleOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;

        var kit = await db.MovieProductionKits
            .Include(item => item.Revisions)
            .ThenInclude(item => item.References)
            .SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        if (kit?.LockedRevisionNumber is not null) throw new MovieProductionKitLifecycleException("MOVIE_PRODUCTION_KIT_LOCKED", "The Production Kit is locked; create a new hand-off only after it is unlocked.");

        var guide = movie.Guide;
        if (guide is null) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_GUIDE_MISSING", "A Movie Guide is required before creating a Production Kit.");
        var sourceRevisionNumber = request.SourceGuideRevisionNumber ?? guide.CurrentRevisionNumber;
        var sourceRevision = guide.Revisions.SingleOrDefault(item => item.RevisionNumber == sourceRevisionNumber);
        if (sourceRevision is null) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_GUIDE_REVISION_NOT_FOUND", "The selected Movie Guide revision was not found.");
        if (guide.LockedRevisionNumber is int lockedGuideRevision && lockedGuideRevision != sourceRevisionNumber)
            throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_GUIDE_REVISION_STALE", "The Production Kit must reference the locked Movie Guide revision.");

        var now = DateTime.UtcNow;
        kit ??= new MovieProductionKit
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, CurrentRevisionNumber = 0,
            CreatedAt = now, UpdatedAt = now,
        };
        var revisionNumber = kit.CurrentRevisionNumber + 1;
        var revision = new MovieProductionKitRevision
        {
            Id = Guid.NewGuid(), MovieProductionKitId = kit.Id, RevisionNumber = revisionNumber,
            Status = MovieProductionKitStatuses.Draft, SourceGuideRevisionId = sourceRevision.Id,
            SourceGuideRevisionNumber = sourceRevision.RevisionNumber,
            SourceGuideHash = HashGuide(sourceRevision), Notes = Clean(request.Notes, 4_000),
            CreatedByUserId = userId, CreatedAt = now,
        };
        var references = request.References ?? [];
        if (references.Count > 128) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_TOO_MANY_REFERENCES", "A Production Kit cannot contain more than 128 references.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, index) in references.Select((item, index) => (item, index)))
        {
            var referenceType = (string.IsNullOrWhiteSpace(item.ReferenceType) ? item.SourceType : item.ReferenceType)?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!MovieProductionKitReferenceTypes.Supported.Contains(referenceType)) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_REFERENCE_TYPE_INVALID", $"Unsupported Production Kit reference type '{item.ReferenceType}'.");
            if (item.SourceId == Guid.Empty) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_REFERENCE_INVALID", "Every Production Kit reference must identify a canonical source record.");
            if (!seen.Add($"{referenceType}:{item.SourceId:N}:{item.SourceRevision}")) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_REFERENCE_DUPLICATE", "A Production Kit cannot contain the same source reference twice.");
            await EnsureReferenceBelongsToMovieAsync(movie, item, referenceType, cancellationToken);
            var provenance = NormalizeProvenance(item.ProvenanceJson);
            revision.References.Add(new MovieProductionKitReference
            {
                Id = Guid.NewGuid(), MovieProductionKitRevisionId = revision.Id,
                ReferenceType = referenceType, SourceId = item.SourceId, SourceRevision = item.SourceRevision,
                Label = Clean(item.Label, 300), Role = Clean(item.Role, 160), IsRequired = item.IsRequired,
                SourceHash = HashReference(referenceType, item.SourceId, item.SourceRevision, provenance),
                ProvenanceJson = provenance, SortOrder = item.SortOrder ?? index,
            });
        }
        revision.RevisionHash = HashRevision(revision, revision.References);
        kit.CurrentRevisionNumber = revisionNumber;
        kit.UpdatedAt = now;
        if (kit.Id == Guid.Empty) throw new InvalidOperationException("Production Kit id was not initialized.");
        if (db.Entry(kit).State == EntityState.Detached) db.MovieProductionKits.Add(kit);
        db.MovieProductionKitRevisions.Add(revision);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(await LoadKitAsync(movieProjectId, cancellationToken) ?? kit);
    }

    public async Task<MovieProductionKitDto?> SubmitForReviewAsync(Guid userId, Guid movieProjectId, int revisionNumber, CancellationToken cancellationToken)
    {
        if (!await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.ProductionVersionEdit, cancellationToken)) return null;
        var kit = await LoadKitAsync(movieProjectId, cancellationToken);
        if (kit is null) return null;
        var revision = CurrentRevision(kit, revisionNumber);
        EnsureStatus(revision, MovieProductionKitStatuses.Draft);
        revision.Status = MovieProductionKitStatuses.Review;
        kit.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(kit);
    }

    public async Task<MovieProductionKitDto?> ApproveAsync(Guid userId, Guid movieProjectId, int revisionNumber, string? note, CancellationToken cancellationToken)
    {
        if (!await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.ProductionReview, cancellationToken)) return null;
        var kit = await LoadKitAsync(movieProjectId, cancellationToken);
        if (kit is null) return null;
        var revision = CurrentRevision(kit, revisionNumber);
        EnsureStatus(revision, MovieProductionKitStatuses.Review);
        var readiness = EvaluateReadiness(kit, revision);
        if (!readiness.Ready) throw new MovieProductionKitLifecycleException("MOVIE_PRODUCTION_KIT_NOT_READY", "The Production Kit must pass readiness checks before approval.", readiness.Missing);
        revision.Status = MovieProductionKitStatuses.Approved;
        revision.ReviewedByUserId = userId;
        revision.ReviewedAt = DateTime.UtcNow;
        revision.ReviewNote = Clean(note, 4_000);
        kit.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(kit);
    }

    public async Task<MovieProductionKitDto?> LockAsync(Guid userId, Guid movieProjectId, int revisionNumber, CancellationToken cancellationToken)
    {
        if (!await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.ProductionReview, cancellationToken)) return null;
        var kit = await LoadKitAsync(movieProjectId, cancellationToken);
        if (kit is null) return null;
        var revision = CurrentRevision(kit, revisionNumber);
        EnsureStatus(revision, MovieProductionKitStatuses.Approved);
        var readiness = EvaluateReadiness(kit, revision);
        if (!readiness.Ready) throw new MovieProductionKitLifecycleException("MOVIE_PRODUCTION_KIT_NOT_READY", "The Production Kit must pass readiness checks before locking.", readiness.Missing);
        var now = DateTime.UtcNow;
        revision.Status = MovieProductionKitStatuses.Locked;
        revision.LockedByUserId = userId;
        revision.LockedAt = now;
        kit.LockedRevisionNumber = revision.RevisionNumber;
        kit.LockedByUserId = userId;
        kit.LockedAt = now;
        kit.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(kit);
    }

    public async Task<MovieProductionKitDto?> UnlockAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (!await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.ProductionReview, cancellationToken)) return null;
        var kit = await LoadKitAsync(movieProjectId, cancellationToken);
        if (kit is null) return null;
        if (kit.LockedRevisionNumber is not int lockedNumber) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_NOT_LOCKED", "The Production Kit is already unlocked.");
        var revision = kit.Revisions.Single(item => item.RevisionNumber == lockedNumber);
        revision.Status = MovieProductionKitStatuses.Approved;
        revision.LockedAt = null;
        revision.LockedByUserId = null;
        kit.LockedRevisionNumber = null;
        kit.LockedAt = null;
        kit.LockedByUserId = null;
        kit.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(kit);
    }

    private async Task<MovieProductionKit?> LoadKitAsync(Guid movieProjectId, CancellationToken cancellationToken) =>
        await db.MovieProductionKits
            .Include(item => item.Revisions.OrderBy(revision => revision.RevisionNumber))
            .ThenInclude(revision => revision.References.OrderBy(reference => reference.SortOrder))
            .Include(item => item.MovieProject).ThenInclude(project => project.Guide).ThenInclude(guide => guide.Revisions)
            .SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);

    private static MovieProductionKitRevision CurrentRevision(MovieProductionKit kit, int revisionNumber)
    {
        if (revisionNumber == 0) revisionNumber = kit.CurrentRevisionNumber;
        if (revisionNumber != kit.CurrentRevisionNumber) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_REVISION_NOT_CURRENT", "Only the current Production Kit revision can change lifecycle state.");
        return kit.Revisions.SingleOrDefault(item => item.RevisionNumber == revisionNumber)
            ?? throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_REVISION_NOT_FOUND", "The Production Kit revision was not found.");
    }

    private static void EnsureStatus(MovieProductionKitRevision revision, string expected)
    {
        if (!string.Equals(revision.Status, expected, StringComparison.OrdinalIgnoreCase))
            throw new MovieProductionKitLifecycleException("MOVIE_PRODUCTION_KIT_INVALID_TRANSITION", $"A Production Kit revision in {revision.Status} cannot transition to the requested state.");
    }

    private static MovieProductionKitDto ToDto(MovieProductionKit kit)
    {
        var current = kit.Revisions.OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
        var readiness = current is null ? new MovieProductionKitReadiness(false, MovieProductionKitReadinessCodes.GuideMissing, [MovieProductionKitReadinessCodes.GuideMissing], [], 0, 0, 0, false, 0) : EvaluateReadiness(kit, current);
        return new MovieProductionKitDto(
            kit.Id, kit.MovieProjectId, kit.CurrentRevisionNumber, kit.LockedRevisionNumber,
            kit.LockedAt, kit.LockedByUserId, current is null ? null : ToRevisionDto(current), ToReadinessDto(readiness),
            kit.Revisions.OrderByDescending(item => item.RevisionNumber).Select(ToRevisionDto).ToArray());
    }

    private static MovieProductionKitReadinessDto ToReadinessDto(MovieProductionKitReadiness readiness) =>
        new(readiness.Ready, readiness.Status, readiness.Missing, readiness.Warnings, readiness.ReferenceCount,
            readiness.RequiredReferenceCount, readiness.ResolvedReferenceCount, readiness.SourceGuideLocked,
            readiness.SourceGuideRevisionNumber);

    private static MovieProductionKitRevisionDto ToRevisionDto(MovieProductionKitRevision revision) =>
        new(revision.Id, revision.RevisionNumber, revision.Status, revision.SourceGuideRevisionId,
            revision.SourceGuideRevisionNumber, revision.SourceGuideHash, revision.Notes, revision.ReviewNote,
            revision.CreatedByUserId, revision.CreatedAt, revision.ReviewedByUserId, revision.ReviewedAt,
            revision.LockedByUserId, revision.LockedAt, revision.RevisionHash,
            revision.References.OrderBy(item => item.SortOrder).Select(item => new MovieProductionKitReferenceDto(
                item.Id, item.ReferenceType, item.SourceId, item.SourceRevision, item.Label, item.Role,
                item.IsRequired, item.SourceHash, item.ProvenanceJson, item.SortOrder)).ToArray());

    private static MovieProductionKitReadiness EvaluateReadiness(MovieProductionKit kit, MovieProductionKitRevision revision)
    {
        var missing = new List<string>();
        var warnings = new List<string>();
        var guide = kit.MovieProject.Guide;
        var sourceGuideLocked = guide?.LockedRevisionNumber == revision.SourceGuideRevisionNumber && revision.SourceGuideRevisionId != Guid.Empty;
        if (guide is null) missing.Add(MovieProductionKitReadinessCodes.GuideMissing);
        else if (guide.LockedRevisionNumber is null) missing.Add(MovieProductionKitReadinessCodes.GuideNotLocked);
        else if (guide.LockedRevisionNumber != revision.SourceGuideRevisionNumber) missing.Add(MovieProductionKitReadinessCodes.GuideRevisionStale);
        var resolved = 0;
        foreach (var reference in revision.References)
        {
            if (reference.SourceId == Guid.Empty) { missing.Add(MovieProductionKitReadinessCodes.ReferenceNotFound); continue; }
            // Source existence and project ownership are validated when the revision is created.
            resolved++;
        }
        var required = revision.References.Count(item => item.IsRequired);
        if (required > resolved) missing.Add(MovieProductionKitReadinessCodes.RequiredReferenceMissing);
        if (revision.Status is not MovieProductionKitStatuses.Approved and not MovieProductionKitStatuses.Locked) warnings.Add(MovieProductionKitReadinessCodes.KitNotApproved);
        if (kit.LockedRevisionNumber is not null && revision.RevisionNumber != kit.LockedRevisionNumber) warnings.Add(MovieProductionKitReadinessCodes.KitLocked);
        return new MovieProductionKitReadiness(missing.Count == 0, missing.Count == 0 ? "ready" : "blocked", missing.Distinct().ToArray(), warnings.Distinct().ToArray(), revision.References.Count, required, resolved, sourceGuideLocked, revision.SourceGuideRevisionNumber);
    }

    private async Task EnsureReferenceBelongsToMovieAsync(MovieProject movie, MovieProductionKitReferenceRequest item, string referenceType, CancellationToken cancellationToken)
    {
        var exists = referenceType switch
        {
            MovieProductionKitReferenceTypes.Character => await db.MovieCharacters.AnyAsync(source => source.Id == item.SourceId && source.MovieProjectId == movie.Id, cancellationToken),
            MovieProductionKitReferenceTypes.CharacterState => await db.MovieCharacterStates.AnyAsync(source => source.Id == item.SourceId && source.Character.MovieProjectId == movie.Id, cancellationToken),
            MovieProductionKitReferenceTypes.Location => await db.MovieLocations.AnyAsync(source => source.Id == item.SourceId && source.MovieProjectId == movie.Id, cancellationToken),
            MovieProductionKitReferenceTypes.Set => await db.MovieSets.AnyAsync(source => source.Id == item.SourceId && source.MovieProjectId == movie.Id, cancellationToken),
            MovieProductionKitReferenceTypes.Prop => await db.MovieProps.AnyAsync(source => source.Id == item.SourceId && source.MovieProjectId == movie.Id, cancellationToken),
            MovieProductionKitReferenceTypes.WorldReference => await db.MovieWorldReferences.AnyAsync(source => source.Id == item.SourceId && source.MovieProjectId == movie.Id, cancellationToken),
            MovieProductionKitReferenceTypes.ContinuitySnapshot => await db.MovieCharacterContinuitySnapshots.AnyAsync(source => source.Id == item.SourceId && source.MovieProjectId == movie.Id, cancellationToken),
            MovieProductionKitReferenceTypes.ApprovedKeyframe => await db.MovieProductionVersions.AnyAsync(source => source.Id == item.SourceId && source.MovieShot.Scene.MovieProjectId == movie.Id && source.Status == MovieProductionVersionStatuses.Approved && source.Stage == MovieProductionStages.ApprovedKeyframe, cancellationToken),
            MovieProductionKitReferenceTypes.Asset => await db.Assets.AnyAsync(source => source.Id == item.SourceId && source.WorkspaceId == movie.WorkspaceId && (source.ProjectId == null || source.ProjectId == movie.ProjectId), cancellationToken),
            _ => false,
        };
        if (!exists) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_REFERENCE_NOT_FOUND", "Every Production Kit reference must belong to the movie project and workspace.");
    }

    private static string NormalizeProvenance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "{}";
        if (value.Length > 4_000) throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_PROVENANCE_INVALID", "Reference provenance cannot exceed 4,000 characters.");
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            ValidateSafeProvenance(document.RootElement);
            return document.RootElement.GetRawText();
        }
        catch (JsonException) { throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_PROVENANCE_INVALID", "Reference provenance must be a JSON object."); }
    }
    private static void ValidateSafeProvenance(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Contains("provider", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("model", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase))
                throw new JsonException();
            if (property.Value.ValueKind == JsonValueKind.Object) ValidateSafeProvenance(property.Value);
            else if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) ValidateSafeProvenance(item);
        }
    }

    private static string HashGuide(MovieGuideRevision revision) => Hash([revision.Id.ToString("N"), revision.RevisionNumber.ToString(), revision.Status]);
    private static string HashReference(string type, Guid id, int? revision, string provenance) => Hash([type, id.ToString("N"), revision?.ToString() ?? string.Empty, provenance]);
    private static string HashRevision(MovieProductionKitRevision revision, IEnumerable<MovieProductionKitReference> references) => Hash([revision.SourceGuideRevisionId.ToString("N"), revision.SourceGuideRevisionNumber.ToString(), string.Join('|', references.OrderBy(item => item.SortOrder).Select(item => item.SourceHash))]);
    private static string Hash(IEnumerable<string> parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)))).ToLowerInvariant();
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new MovieProductionKitValidationException("MOVIE_PRODUCTION_KIT_TEXT_TOO_LONG", $"Production Kit text cannot exceed {max} characters.");
}

public interface IMovieProductionKitService
{
    Task<MovieProductionKitDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieProductionKitDto?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieProductionKitRevisionRequest request, CancellationToken cancellationToken);
    Task<MovieProductionKitDto?> SubmitForReviewAsync(Guid userId, Guid movieProjectId, int revisionNumber, CancellationToken cancellationToken);
    Task<MovieProductionKitDto?> ApproveAsync(Guid userId, Guid movieProjectId, int revisionNumber, string? note, CancellationToken cancellationToken);
    Task<MovieProductionKitDto?> LockAsync(Guid userId, Guid movieProjectId, int revisionNumber, CancellationToken cancellationToken);
    Task<MovieProductionKitDto?> UnlockAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
}

public sealed class MovieProductionKitValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MovieProductionKitLifecycleException(string code, string message, IReadOnlyList<string>? reasons = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<string> Reasons { get; } = reasons ?? [];
}
