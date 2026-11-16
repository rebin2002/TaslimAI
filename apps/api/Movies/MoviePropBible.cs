using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MoviePropBibleStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Locked = "Locked";
    public const string Rejected = "Rejected";
}

public static class MoviePropBibleLimits
{
    public const int SchemaVersion = 1;
    public const int MaxItems = 128;
    public const int MaxReferences = 32;
    public const int MaxVariants = 32;
    public const int MaxVersions = 64;
    public const int MaxText = 8_000;
    public const int MaxShortText = 160;
}

public sealed class MoviePropBible
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid MoviePropId { get; set; }
    public string IdentityKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? VisualIdentity { get; set; }
    public string? ContinuityRules { get; set; }
    public string ApprovalState { get; set; } = MoviePropBibleStatuses.Draft;
    public int CurrentVersionNumber { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public int? ApprovedVersionNumber { get; set; }
    public Guid? ApprovedVersionId { get; set; }
    public int? LockedVersionNumber { get; set; }
    public Guid? LockedVersionId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public MovieProp MovieProp { get; set; } = null!;
    public ApplicationUser? ApprovedByUser { get; set; }
    public ApplicationUser? LockedByUser { get; set; }
    public ICollection<MoviePropBibleReference> References { get; set; } = [];
    public ICollection<MoviePropBibleVariant> Variants { get; set; } = [];
    public ICollection<MoviePropBibleVersion> Versions { get; set; } = [];
}

public sealed class MoviePropBibleReference
{
    public Guid Id { get; set; }
    public Guid MoviePropBibleId { get; set; }
    public Guid AssetId { get; set; }
    public string Role { get; set; } = "visual_reference";
    public string? Notes { get; set; }
    public string ProvenanceJson { get; set; } = "{}";
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public MoviePropBible PropBible { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
}

public sealed class MoviePropBibleVariant
{
    public Guid Id { get; set; }
    public Guid MoviePropBibleId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? State { get; set; }
    public string? VisualNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public string ProvenanceJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MoviePropBible PropBible { get; set; } = null!;
    public Asset? ReferenceAsset { get; set; }
}

public sealed class MoviePropBibleVersion
{
    public Guid Id { get; set; }
    public Guid MoviePropBibleId { get; set; }
    public int VersionNumber { get; set; }
    public string Status { get; set; } = MoviePropBibleStatuses.Draft;
    public string IdentityKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? VisualIdentity { get; set; }
    public string? ContinuityRules { get; set; }
    public string ReferencesJson { get; set; } = "[]";
    public string VariantsJson { get; set; } = "[]";
    public string UsagesJson { get; set; } = "[]";
    public string ProvenanceJson { get; set; } = "{}";
    public string ContentHash { get; set; } = string.Empty;
    public string? ReviewReason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }

    public MoviePropBible PropBible { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ReviewedByUser { get; set; }
    public ApplicationUser? LockedByUser { get; set; }
}

public sealed record MoviePropBibleProvenanceDto(
    string SourceType,
    Guid? SourceId,
    int? SourceVersion,
    string SourceHash,
    Guid ActorUserId,
    DateTime CapturedAtUtc);

public sealed record MoviePropBibleReferenceDto(
    Guid Id,
    Guid AssetId,
    string Role,
    string? Notes,
    string ProvenanceJson,
    DateTime CreatedAt);

public sealed record MoviePropBibleVariantDto(
    Guid Id,
    string Key,
    string Label,
    string? Description,
    string? State,
    string? VisualNotes,
    Guid? ReferenceAssetId,
    string ProvenanceJson,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MoviePropBibleUsageDto(
    Guid Id,
    Guid MovieSceneId,
    Guid? MovieShotId,
    int SceneSequence,
    string SceneTitle,
    int? ShotSequence,
    string? ShotDescription,
    string? Role);

public sealed record MoviePropBibleVersionDto(
    Guid Id,
    Guid MoviePropBibleId,
    int VersionNumber,
    string Status,
    string IdentityKey,
    string? Role,
    string? VisualIdentity,
    string? ContinuityRules,
    string ReferencesJson,
    string VariantsJson,
    string UsagesJson,
    string ProvenanceJson,
    string ContentHash,
    string? ReviewReason,
    Guid CreatedByUserId,
    DateTime CreatedAt,
    DateTime? ReviewedAt,
    Guid? ReviewedByUserId,
    DateTime? LockedAt,
    Guid? LockedByUserId);

public sealed record MoviePropBibleDto(
    Guid Id,
    Guid MovieProjectId,
    Guid MoviePropId,
    MoviePropDto Prop,
    string IdentityKey,
    string? Role,
    string? VisualIdentity,
    string? ContinuityRules,
    string ApprovalState,
    int CurrentVersionNumber,
    int? ApprovedVersionNumber,
    int? LockedVersionNumber,
    DateTime? ApprovedAt,
    Guid? ApprovedByUserId,
    DateTime? LockedAt,
    Guid? LockedByUserId,
    IReadOnlyList<MoviePropBibleReferenceDto> References,
    IReadOnlyList<MoviePropBibleVariantDto> Variants,
    IReadOnlyList<MoviePropBibleUsageDto> Usages,
    IReadOnlyList<MoviePropBibleVersionDto> Versions,
    MoviePropBibleProvenanceDto? CurrentProvenance,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MoviePropBibleCollectionDto(
    int SchemaVersion,
    Guid MovieProjectId,
    IReadOnlyList<MoviePropBibleDto> Items,
    MovieRecurringPropDetectionDto Detection);

public sealed record MovieRecurringPropCandidateDto(
    Guid PropId,
    string PropName,
    bool IsRecurring,
    int UsageCount,
    int SceneCount,
    IReadOnlyList<Guid> SceneIds,
    IReadOnlyList<Guid> ShotIds,
    string Reason);

public sealed record MovieRecurringPropDetectionDto(
    int DetectorVersion,
    bool ProviderFree,
    DateTime EvaluatedAtUtc,
    IReadOnlyList<MovieRecurringPropCandidateDto> Candidates);

public sealed class MoviePropBibleRequest
{
    public string? IdentityKey { get; set; }
    public string? Role { get; set; }
    public string? VisualIdentity { get; set; }
    public string? ContinuityRules { get; set; }
}

public sealed record MoviePropBibleReferenceRequest(Guid AssetId, string? Role, string? Notes);
public sealed record MoviePropBibleVariantRequest(string Key, string Label, string? Description, string? State, string? VisualNotes, Guid? ReferenceAssetId);
public sealed record MoviePropBibleVersionRequest(string? IdentityKey, string? Role, string? VisualIdentity, string? ContinuityRules);
public sealed record MoviePropBibleReviewRequest(bool Approve, string? Reason);

public sealed record MoviePropDetectionInput(Guid Id, string Name);
public sealed record MoviePropDetectionUsageInput(Guid PropId, Guid SceneId, Guid? ShotId);
public sealed record MoviePropDetectionShotInput(Guid SceneId, Guid ShotId, string SearchText);

public interface IMovieRecurringPropDetector
{
    MovieRecurringPropDetectionDto Detect(
        IReadOnlyList<MoviePropDetectionInput> props,
        IReadOnlyList<MoviePropDetectionUsageInput> usages,
        IReadOnlyList<MoviePropDetectionShotInput> shots,
        DateTime evaluatedAtUtc);
}

public sealed class DeterministicMovieRecurringPropDetector : IMovieRecurringPropDetector
{
    public MovieRecurringPropDetectionDto Detect(
        IReadOnlyList<MoviePropDetectionInput> props,
        IReadOnlyList<MoviePropDetectionUsageInput> usages,
        IReadOnlyList<MoviePropDetectionShotInput> shots,
        DateTime evaluatedAtUtc)
    {
        var candidates = props
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Select(prop =>
            {
                var explicitUsages = usages.Where(item => item.PropId == prop.Id).ToArray();
                var mentionedShots = shots
                    .Where(item => ContainsPropName(item.SearchText, prop.Name))
                    .ToArray();
                var sceneIds = explicitUsages.Select(item => item.SceneId)
                    .Concat(mentionedShots.Select(item => item.SceneId))
                    .Distinct()
                    .OrderBy(item => item)
                    .ToArray();
                var shotIds = explicitUsages.Where(item => item.ShotId.HasValue).Select(item => item.ShotId!.Value)
                    .Concat(mentionedShots.Select(item => item.ShotId))
                    .Distinct()
                    .OrderBy(item => item)
                    .ToArray();
                var usageCount = explicitUsages.Length + mentionedShots.Count(item => explicitUsages.All(usage => usage.ShotId != item.ShotId));
                var recurring = sceneIds.Length >= 2 || usageCount >= 2;
                var reason = recurring
                    ? sceneIds.Length >= 2 ? "The prop is present across multiple scenes." : "The prop has multiple planned scene or shot usages."
                    : "The prop does not yet have repeated planned usage.";
                return new MovieRecurringPropCandidateDto(prop.Id, prop.Name, recurring, usageCount, sceneIds.Length, sceneIds, shotIds, reason);
            })
            .Where(item => item.IsRecurring)
            .Take(MoviePropBibleLimits.MaxItems)
            .ToArray();
        return new MovieRecurringPropDetectionDto(1, true, evaluatedAtUtc, candidates);
    }

    private static bool ContainsPropName(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name)) return false;
        return text.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

public interface IMoviePropBibleService
{
    Task<MoviePropBibleCollectionDto?> GetProjectAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default);
    Task<MoviePropBibleDto?> GetPropAsync(Guid userId, Guid propId, CancellationToken cancellationToken = default);
    Task<MoviePropBibleDto?> UpsertAsync(Guid userId, Guid propId, MoviePropBibleRequest request, CancellationToken cancellationToken = default);
    Task<MoviePropBibleDto?> AddReferenceAsync(Guid userId, Guid propId, MoviePropBibleReferenceRequest request, CancellationToken cancellationToken = default);
    Task<MoviePropBibleDto?> AddVariantAsync(Guid userId, Guid propId, MoviePropBibleVariantRequest request, CancellationToken cancellationToken = default);
    Task<MoviePropBibleDto?> CreateVersionAsync(Guid userId, Guid propId, MoviePropBibleVersionRequest request, CancellationToken cancellationToken = default);
    Task<MoviePropBibleDto?> ReviewVersionAsync(Guid userId, Guid versionId, MoviePropBibleReviewRequest request, CancellationToken cancellationToken = default);
    Task<MoviePropBibleDto?> LockVersionAsync(Guid userId, Guid versionId, CancellationToken cancellationToken = default);
    Task<MovieRecurringPropDetectionDto?> DetectRecurringAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default);
}

public sealed class MoviePropBibleService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IMovieRecurringPropDetector detector) : IMoviePropBibleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<MoviePropBibleCollectionDto?> GetProjectAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default)
    {
        var project = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var props = await db.MovieProps.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var bibles = await db.MoviePropBibles.AsNoTracking().Include(item => item.References).Include(item => item.Variants).Include(item => item.Versions).Where(item => item.MovieProjectId == movieProjectId).ToArrayAsync(cancellationToken);
        var usages = await UsageQuery(movieProjectId).ToArrayAsync(cancellationToken);
        var items = props.Take(MoviePropBibleLimits.MaxItems).Select(prop => ToDto(prop, bibles.FirstOrDefault(item => item.MoviePropId == prop.Id), usages.Where(item => item.EntityId == prop.Id).ToArray())).ToArray();
        var detection = await DetectRecurringInternalAsync(movieProjectId, props.Select(item => new MoviePropDetectionInput(item.Id, item.Name)).ToArray(), usages.Select(item => new MoviePropDetectionUsageInput(item.EntityId, item.MovieSceneId, item.MovieShotId)).ToArray(), cancellationToken);
        return new MoviePropBibleCollectionDto(MoviePropBibleLimits.SchemaVersion, movieProjectId, items, detection);
    }

    public async Task<MoviePropBibleDto?> GetPropAsync(Guid userId, Guid propId, CancellationToken cancellationToken = default)
    {
        var prop = await db.MovieProps.AsNoTracking().Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == propId, cancellationToken);
        if (prop is null || !await collaboration.HasPermissionAsync(userId, prop.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var bible = await db.MoviePropBibles.AsNoTracking().Include(item => item.References).Include(item => item.Variants).Include(item => item.Versions).FirstOrDefaultAsync(item => item.MoviePropId == propId, cancellationToken);
        var usages = await UsageQuery(prop.MovieProjectId).Where(item => item.EntityId == propId).ToArrayAsync(cancellationToken);
        return ToDto(prop, bible, usages);
    }

    public async Task<MoviePropBibleDto?> UpsertAsync(Guid userId, Guid propId, MoviePropBibleRequest request, CancellationToken cancellationToken = default)
    {
        var prop = await GetEditablePropAsync(userId, propId, cancellationToken);
        if (prop is null) return null;
        var bible = await LoadBibleAsync(propId, cancellationToken);
        EnsureEditable(bible);
        var values = Normalize(request.IdentityKey ?? prop.Name, request.Role, request.VisualIdentity, request.ContinuityRules);
        var now = DateTime.UtcNow;
        bible ??= new MoviePropBible { Id = Guid.NewGuid(), MovieProjectId = prop.MovieProjectId, MoviePropId = propId, CreatedAt = now };
        bible.IdentityKey = values.IdentityKey;
        bible.Role = values.Role;
        bible.VisualIdentity = values.VisualIdentity;
        bible.ContinuityRules = values.ContinuityRules;
        bible.ApprovalState = MoviePropBibleStatuses.Draft;
        bible.UpdatedAt = now;
        if (bible.Id != Guid.Empty && db.Entry(bible).State == EntityState.Detached) db.MoviePropBibles.Add(bible);
        await CreateVersionEntityAsync(bible, userId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetPropAsync(userId, propId, cancellationToken);
    }

    public async Task<MoviePropBibleDto?> AddReferenceAsync(Guid userId, Guid propId, MoviePropBibleReferenceRequest request, CancellationToken cancellationToken = default)
    {
        var prop = await GetEditablePropAsync(userId, propId, cancellationToken);
        if (prop is null) return null;
        var bible = await RequireBibleAsync(prop, cancellationToken);
        EnsureEditable(bible);
        if (request.AssetId == Guid.Empty || !await db.Assets.AnyAsync(item => item.Id == request.AssetId && item.WorkspaceId == prop.MovieProject.WorkspaceId, cancellationToken)) throw new MoviePropBibleValidationException("The visual reference asset must belong to this workspace.");
        if (await db.MoviePropBibleReferences.AnyAsync(item => item.MoviePropBibleId == bible.Id && item.AssetId == request.AssetId && item.Role == (request.Role ?? "visual_reference").Trim(), cancellationToken)) throw new MoviePropBibleValidationException("This visual reference is already linked to the Prop Bible.");
        var now = DateTime.UtcNow;
        db.MoviePropBibleReferences.Add(new MoviePropBibleReference { Id = Guid.NewGuid(), MoviePropBibleId = bible.Id, AssetId = request.AssetId, Role = CleanShort(request.Role) ?? "visual_reference", Notes = Clean(request.Notes), ProvenanceJson = ProvenanceJson("visual_reference", request.AssetId, null, userId, now), CreatedByUserId = userId, CreatedAt = now });
        bible.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetPropAsync(userId, propId, cancellationToken);
    }

    public async Task<MoviePropBibleDto?> AddVariantAsync(Guid userId, Guid propId, MoviePropBibleVariantRequest request, CancellationToken cancellationToken = default)
    {
        var prop = await GetEditablePropAsync(userId, propId, cancellationToken);
        if (prop is null) return null;
        var bible = await RequireBibleAsync(prop, cancellationToken);
        EnsureEditable(bible);
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.Label)) throw new MoviePropBibleValidationException("Variant key and label are required.");
        if (await db.MoviePropBibleVariants.AnyAsync(item => item.MoviePropBibleId == bible.Id && item.Key == request.Key.Trim(), cancellationToken)) throw new MoviePropBibleValidationException("A variant with this key already exists.");
        if (request.ReferenceAssetId.HasValue && !await db.Assets.AnyAsync(item => item.Id == request.ReferenceAssetId && item.WorkspaceId == prop.MovieProject.WorkspaceId, cancellationToken)) throw new MoviePropBibleValidationException("The variant reference asset must belong to this workspace.");
        var now = DateTime.UtcNow;
        db.MoviePropBibleVariants.Add(new MoviePropBibleVariant { Id = Guid.NewGuid(), MoviePropBibleId = bible.Id, Key = CleanShort(request.Key)!, Label = CleanShort(request.Label)!, Description = Clean(request.Description), State = CleanShort(request.State), VisualNotes = Clean(request.VisualNotes), ReferenceAssetId = request.ReferenceAssetId, ProvenanceJson = ProvenanceJson("variant", bible.Id, null, userId, now), CreatedAt = now, UpdatedAt = now });
        bible.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetPropAsync(userId, propId, cancellationToken);
    }

    public async Task<MoviePropBibleDto?> CreateVersionAsync(Guid userId, Guid propId, MoviePropBibleVersionRequest request, CancellationToken cancellationToken = default)
    {
        var prop = await GetEditablePropAsync(userId, propId, cancellationToken);
        if (prop is null) return null;
        var bible = await RequireBibleAsync(prop, cancellationToken);
        var values = Normalize(request.IdentityKey ?? bible.IdentityKey, request.Role ?? bible.Role, request.VisualIdentity ?? bible.VisualIdentity, request.ContinuityRules ?? bible.ContinuityRules);
        var now = DateTime.UtcNow;
        bible.IdentityKey = values.IdentityKey;
        bible.Role = values.Role;
        bible.VisualIdentity = values.VisualIdentity;
        bible.ContinuityRules = values.ContinuityRules;
        bible.ApprovalState = MoviePropBibleStatuses.Draft;
        bible.UpdatedAt = now;
        await CreateVersionEntityAsync(bible, userId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetPropAsync(userId, propId, cancellationToken);
    }

    public async Task<MoviePropBibleDto?> ReviewVersionAsync(Guid userId, Guid versionId, MoviePropBibleReviewRequest request, CancellationToken cancellationToken = default)
    {
        var version = await db.MoviePropBibleVersions.Include(item => item.PropBible).ThenInclude(item => item.MovieProp).Include(item => item.PropBible).ThenInclude(item => item.References).Include(item => item.PropBible).ThenInclude(item => item.Variants).FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null || !await collaboration.HasPermissionAsync(userId, version.PropBible.MovieProjectId, MoviePermissions.Approve, cancellationToken)) return null;
        if (version.Status is MoviePropBibleStatuses.Locked) throw new MoviePropBibleLockedException();
        var now = DateTime.UtcNow;
        version.Status = request.Approve ? MoviePropBibleStatuses.Approved : MoviePropBibleStatuses.Rejected;
        version.ReviewReason = Clean(request.Reason);
        version.ReviewedAt = now;
        version.ReviewedByUserId = userId;
        var bible = version.PropBible;
        if (request.Approve)
        {
            bible.ApprovalState = MoviePropBibleStatuses.Approved;
            bible.ApprovedVersionId = version.Id;
            bible.ApprovedVersionNumber = version.VersionNumber;
            bible.ApprovedAt = now;
            bible.ApprovedByUserId = userId;
        }
        else if (bible.ApprovedVersionId == version.Id)
        {
            bible.ApprovalState = MoviePropBibleStatuses.Rejected;
            bible.ApprovedVersionId = null;
            bible.ApprovedVersionNumber = null;
        }
        bible.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetPropAsync(userId, bible.MoviePropId, cancellationToken);
    }

    public async Task<MoviePropBibleDto?> LockVersionAsync(Guid userId, Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await db.MoviePropBibleVersions.Include(item => item.PropBible).ThenInclude(item => item.MovieProp).FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null || !await collaboration.HasPermissionAsync(userId, version.PropBible.MovieProjectId, MoviePermissions.Approve, cancellationToken)) return null;
        if (version.Status != MoviePropBibleStatuses.Approved) throw new MoviePropBibleValidationException("Only an approved Prop Bible version can be locked.");
        var now = DateTime.UtcNow;
        version.Status = MoviePropBibleStatuses.Locked;
        version.LockedAt = now;
        version.LockedByUserId = userId;
        var bible = version.PropBible;
        bible.ApprovalState = MoviePropBibleStatuses.Locked;
        bible.LockedVersionId = version.Id;
        bible.LockedVersionNumber = version.VersionNumber;
        bible.LockedAt = now;
        bible.LockedByUserId = userId;
        bible.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetPropAsync(userId, bible.MoviePropId, cancellationToken);
    }

    public async Task<MovieRecurringPropDetectionDto?> DetectRecurringAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default)
    {
        var project = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var props = await db.MovieProps.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).Select(item => new MoviePropDetectionInput(item.Id, item.Name)).ToArrayAsync(cancellationToken);
        var usages = await UsageQuery(movieProjectId).Select(item => new MoviePropDetectionUsageInput(item.EntityId, item.MovieSceneId, item.MovieShotId)).ToArrayAsync(cancellationToken);
        return await DetectRecurringInternalAsync(movieProjectId, props, usages, cancellationToken);
    }

    private async Task<MovieRecurringPropDetectionDto> DetectRecurringInternalAsync(Guid movieProjectId, IReadOnlyList<MoviePropDetectionInput> props, IReadOnlyList<MoviePropDetectionUsageInput> usages, CancellationToken cancellationToken)
    {
        var shots = await db.MovieShots.AsNoTracking().Where(item => item.Scene.MovieProjectId == movieProjectId).Select(item => new MoviePropDetectionShotInput(item.MovieSceneId, item.Id, (item.Description + " " + item.Subjects + " " + item.ProductionRequirements + " " + item.ContinuityReferences + " " + item.VisualContinuityNotes).Trim())).ToArrayAsync(cancellationToken);
        return detector.Detect(props, usages, shots, DateTime.UtcNow);
    }

    private IQueryable<MovieWorldUsage> UsageQuery(Guid projectId) => db.MovieWorldUsages.AsNoTracking().Include(item => item.MovieScene).Include(item => item.MovieShot).Where(item => item.MovieProjectId == projectId && item.EntityType == MovieWorldEntityTypes.Prop);

    private async Task<MovieProp> GetEditablePropAsync(Guid userId, Guid propId, CancellationToken cancellationToken)
    {
        var prop = await db.MovieProps.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == propId, cancellationToken);
        return prop is null || !await collaboration.HasPermissionAsync(userId, prop.MovieProjectId, MoviePermissions.Edit, cancellationToken) ? null! : prop;
    }

    private async Task<MoviePropBible> RequireBibleAsync(MovieProp prop, CancellationToken cancellationToken)
    {
        var bible = await LoadBibleAsync(prop.Id, cancellationToken);
        if (bible is not null) return bible;
        var now = DateTime.UtcNow;
        bible = new MoviePropBible { Id = Guid.NewGuid(), MovieProjectId = prop.MovieProjectId, MoviePropId = prop.Id, IdentityKey = prop.Name, CreatedAt = now, UpdatedAt = now };
        db.MoviePropBibles.Add(bible);
        await db.SaveChangesAsync(cancellationToken);
        return bible;
    }

    private Task<MoviePropBible?> LoadBibleAsync(Guid propId, CancellationToken cancellationToken) => db.MoviePropBibles.Include(item => item.References).Include(item => item.Variants).Include(item => item.Versions).FirstOrDefaultAsync(item => item.MoviePropId == propId, cancellationToken);

    private async Task CreateVersionEntityAsync(MoviePropBible bible, Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var usages = await UsageQuery(bible.MovieProjectId).Where(item => item.EntityId == bible.MoviePropId).Include(item => item.MovieScene).Include(item => item.MovieShot).ToArrayAsync(cancellationToken);
        var references = bible.References.OrderBy(item => item.CreatedAt).Take(MoviePropBibleLimits.MaxReferences).Select(item => new { item.Id, item.AssetId, item.Role, item.Notes, item.ProvenanceJson }).ToArray();
        var variants = bible.Variants.OrderBy(item => item.Key).Take(MoviePropBibleLimits.MaxVariants).Select(item => new { item.Id, item.Key, item.Label, item.Description, item.State, item.VisualNotes, item.ReferenceAssetId, item.ProvenanceJson }).ToArray();
        var usageSnapshot = usages.OrderBy(item => item.MovieScene.Sequence).ThenBy(item => item.MovieShot != null ? item.MovieShot.Sequence : 0).ThenBy(item => item.Id).Take(MoviePropBibleLimits.MaxItems).Select(item => new { item.Id, item.MovieSceneId, item.MovieShotId, sceneSequence = item.MovieScene.Sequence, sceneTitle = item.MovieScene.Title, shotSequence = item.MovieShot == null ? (int?)null : item.MovieShot.Sequence, shotDescription = item.MovieShot == null ? null : item.MovieShot.Description, item.Role }).ToArray();
        var content = new { bible.IdentityKey, bible.Role, bible.VisualIdentity, bible.ContinuityRules, references, variants, usages = usageSnapshot };
        var contentHash = Hash(content);
        var versionNumber = (await db.MoviePropBibleVersions.Where(item => item.MoviePropBibleId == bible.Id).Select(item => (int?)item.VersionNumber).MaxAsync(cancellationToken) ?? 0) + 1;
        var version = new MoviePropBibleVersion
        {
            Id = Guid.NewGuid(), MoviePropBibleId = bible.Id, VersionNumber = versionNumber, Status = MoviePropBibleStatuses.Draft,
            IdentityKey = bible.IdentityKey, Role = bible.Role, VisualIdentity = bible.VisualIdentity, ContinuityRules = bible.ContinuityRules,
            ReferencesJson = JsonSerializer.Serialize(references, JsonOptions), VariantsJson = JsonSerializer.Serialize(variants, JsonOptions), UsagesJson = JsonSerializer.Serialize(usageSnapshot, JsonOptions),
            ContentHash = contentHash, ProvenanceJson = ProvenanceJson("prop_bible", bible.MoviePropId, versionNumber, userId, now, contentHash), CreatedByUserId = userId, CreatedAt = now,
        };
        db.MoviePropBibleVersions.Add(version);
        bible.CurrentVersionNumber = versionNumber;
        bible.CurrentVersionId = version.Id;
        bible.ApprovalState = MoviePropBibleStatuses.Draft;
    }

    private static MoviePropBibleDto ToDto(MovieProp prop, MoviePropBible? bible, IReadOnlyList<MovieWorldUsage> usages)
    {
        var references = bible?.References.OrderBy(item => item.CreatedAt).Take(MoviePropBibleLimits.MaxReferences).Select(item => new MoviePropBibleReferenceDto(item.Id, item.AssetId, item.Role, item.Notes, item.ProvenanceJson, item.CreatedAt)).ToArray() ?? (prop.ReferenceAssetId.HasValue ? [new MoviePropBibleReferenceDto(Guid.Empty, prop.ReferenceAssetId.Value, "primary", "Inherited from the canonical prop record.", "{\"sourceType\":\"movie_prop\"}", prop.UpdatedAt)] : []);
        var variants = bible?.Variants.OrderBy(item => item.Key).Take(MoviePropBibleLimits.MaxVariants).Select(item => new MoviePropBibleVariantDto(item.Id, item.Key, item.Label, item.Description, item.State, item.VisualNotes, item.ReferenceAssetId, item.ProvenanceJson, item.CreatedAt, item.UpdatedAt)).ToArray() ?? [];
        var versionDtos = bible?.Versions.OrderByDescending(item => item.VersionNumber).Take(MoviePropBibleLimits.MaxVersions).Select(ToVersionDto).ToArray() ?? [];
        var provenance = bible?.CurrentVersionId is Guid currentId ? bible.Versions.FirstOrDefault(item => item.Id == currentId) is { } current ? ParseProvenance(current.ProvenanceJson) : null : null;
        return new MoviePropBibleDto(bible?.Id ?? Guid.Empty, prop.MovieProjectId, prop.Id, new MoviePropDto(prop.Id, prop.Name, prop.Description, prop.Category, prop.ContinuityNotes, prop.ReferenceAssetId), bible?.IdentityKey ?? prop.Name, bible?.Role, bible?.VisualIdentity, bible?.ContinuityRules ?? prop.ContinuityNotes, bible?.ApprovalState ?? MoviePropBibleStatuses.Draft, bible?.CurrentVersionNumber ?? 0, bible?.ApprovedVersionNumber, bible?.LockedVersionNumber, bible?.ApprovedAt, bible?.ApprovedByUserId, bible?.LockedAt, bible?.LockedByUserId, references, variants, usages.OrderBy(item => item.MovieScene.Sequence).ThenBy(item => item.MovieShot?.Sequence ?? 0).Select(item => new MoviePropBibleUsageDto(item.Id, item.MovieSceneId, item.MovieShotId, item.MovieScene.Sequence, item.MovieScene.Title, item.MovieShot?.Sequence, item.MovieShot?.Description, item.Role)).ToArray(), versionDtos, provenance, bible?.CreatedAt ?? prop.CreatedAt, bible?.UpdatedAt ?? prop.UpdatedAt);
    }

    private static MoviePropBibleVersionDto ToVersionDto(MoviePropBibleVersion item) => new(item.Id, item.MoviePropBibleId, item.VersionNumber, item.Status, item.IdentityKey, item.Role, item.VisualIdentity, item.ContinuityRules, item.ReferencesJson, item.VariantsJson, item.UsagesJson, item.ProvenanceJson, item.ContentHash, item.ReviewReason, item.CreatedByUserId, item.CreatedAt, item.ReviewedAt, item.ReviewedByUserId, item.LockedAt, item.LockedByUserId);

    private static MoviePropBibleProvenanceDto? ParseProvenance(string json)
    {
        try { return JsonSerializer.Deserialize<MoviePropBibleProvenanceDto>(json, JsonOptions); } catch (JsonException) { return null; }
    }

    private static (string IdentityKey, string? Role, string? VisualIdentity, string? ContinuityRules) Normalize(string identityKey, string? role, string? visualIdentity, string? continuityRules)
    {
        if (string.IsNullOrWhiteSpace(identityKey) || identityKey.Trim().Length > MoviePropBibleLimits.MaxShortText) throw new MoviePropBibleValidationException("A bounded Prop Bible identity key is required.");
        return (identityKey.Trim(), CleanShort(role), Clean(visualIdentity), Clean(continuityRules));
    }

    private static void EnsureEditable(MoviePropBible? bible)
    {
        if (bible?.LockedVersionId is not null) throw new MoviePropBibleLockedException();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= MoviePropBibleLimits.MaxText ? value.Trim() : throw new MoviePropBibleValidationException("Prop Bible text is too long.");
    private static string? CleanShort(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= MoviePropBibleLimits.MaxShortText ? value.Trim() : throw new MoviePropBibleValidationException("Prop Bible label is too long.");
    private static string ProvenanceJson(string sourceType, Guid sourceId, int? version, Guid actorUserId, DateTime capturedAt, string? sourceHash = null) => JsonSerializer.Serialize(new MoviePropBibleProvenanceDto(sourceType, sourceId, version, sourceHash ?? Hash(new { sourceType, sourceId, version }), actorUserId, capturedAt), JsonOptions);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions)))).ToLowerInvariant();
}

public sealed class MoviePropBibleValidationException(string message) : Exception(message);
public sealed class MoviePropBibleLockedException() : Exception("This Prop Bible is locked. Create an explicit new version before changing it.");
