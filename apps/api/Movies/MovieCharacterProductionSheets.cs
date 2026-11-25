using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieCharacterProductionSheetStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Locked = "Locked";
}

public static class MovieCharacterProductionSheetSlots
{
    public const string CanonicalIdentityFace = "canonical_identity_face";
    public const string Body = "body";
    public const string Front = "front";
    public const string Side = "side";
    public const string Back = "back";
}

public sealed class MovieCharacterProductionSheet
{
    public Guid Id { get; set; }
    public Guid MovieCharacterId { get; set; }
    public int CurrentVersionNumber { get; set; }
    public string Status { get; set; } = MovieCharacterProductionSheetStatuses.Draft;
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieCharacter Character { get; set; } = null!;
    public ApplicationUser? ApprovedByUser { get; set; }
    public ApplicationUser? LockedByUser { get; set; }
    public ICollection<MovieCharacterProductionSheetVersion> Versions { get; set; } = [];
}

public sealed class MovieCharacterProductionSheetVersion
{
    public Guid Id { get; set; }
    public Guid MovieCharacterProductionSheetId { get; set; }
    public int VersionNumber { get; set; }
    public string Status { get; set; } = MovieCharacterProductionSheetStatuses.Draft;
    public Guid? CanonicalIdentityFaceAssetId { get; set; }
    public Guid? BodyReferenceAssetId { get; set; }
    public Guid? FrontReferenceAssetId { get; set; }
    public Guid? SideReferenceAssetId { get; set; }
    public Guid? BackReferenceAssetId { get; set; }
    public DateTime SourceCharacterUpdatedAt { get; set; }
    public string ProvenanceJson { get; set; } = "{}";
    public string ProvenanceHash { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public MovieCharacterProductionSheet Sheet { get; set; } = null!;
    public Asset? CanonicalIdentityFaceAsset { get; set; }
    public Asset? BodyReferenceAsset { get; set; }
    public Asset? FrontReferenceAsset { get; set; }
    public Asset? SideReferenceAsset { get; set; }
    public Asset? BackReferenceAsset { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ApprovedByUser { get; set; }
    public ApplicationUser? LockedByUser { get; set; }
    public ICollection<MovieCharacterProductionSheetLook> Looks { get; set; } = [];
}

public sealed class MovieCharacterProductionSheetLook
{
    public Guid Id { get; set; }
    public Guid MovieCharacterProductionSheetVersionId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Wardrobe { get; set; }
    public string? Appearance { get; set; }
    public string? ReferenceNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public int SortOrder { get; set; }
    public MovieCharacterProductionSheetVersion Version { get; set; } = null!;
    public Asset? ReferenceAsset { get; set; }
}

public sealed record MovieCharacterProductionSheetLookRequest(
    string Key,
    string Name,
    string? Wardrobe = null,
    string? Appearance = null,
    string? ReferenceNotes = null,
    Guid? ReferenceAssetId = null);

public sealed class MovieCharacterProductionSheetRequest
{
    public Guid? CanonicalIdentityFaceAssetId { get; set; }
    public Guid? BodyReferenceAssetId { get; set; }
    public Guid? FrontReferenceAssetId { get; set; }
    public Guid? SideReferenceAssetId { get; set; }
    public Guid? BackReferenceAssetId { get; set; }
    public IReadOnlyList<MovieCharacterProductionSheetLookRequest> Looks { get; set; } = [];
    public string? ProvenanceNote { get; set; }
}

public sealed record MovieCharacterProductionSheetLookDto(
    Guid Id,
    string Key,
    string Name,
    string? Wardrobe,
    string? Appearance,
    string? ReferenceNotes,
    Guid? ReferenceAssetId,
    int SortOrder);

public sealed record MovieCharacterProductionSheetVersionDto(
    Guid Id,
    int VersionNumber,
    string Status,
    Guid? CanonicalIdentityFaceAssetId,
    Guid? BodyReferenceAssetId,
    Guid? FrontReferenceAssetId,
    Guid? SideReferenceAssetId,
    Guid? BackReferenceAssetId,
    DateTime SourceCharacterUpdatedAt,
    string ProvenanceHash,
    string ProvenanceJson,
    Guid CreatedByUserId,
    DateTime CreatedAt,
    DateTime? ApprovedAt,
    Guid? ApprovedByUserId,
    DateTime? LockedAt,
    Guid? LockedByUserId,
    IReadOnlyList<MovieCharacterProductionSheetLookDto> Looks);

public sealed record MovieCharacterProductionSheetDto(
    Guid Id,
    Guid MovieCharacterId,
    string Status,
    int CurrentVersionNumber,
    MovieCharacterProductionSheetVersionDto? CurrentVersion,
    IReadOnlyList<MovieCharacterProductionSheetVersionDto> Versions,
    DateTime? ApprovedAt,
    Guid? ApprovedByUserId,
    DateTime? LockedAt,
    Guid? LockedByUserId,
    DateTime UpdatedAt);

public sealed class MovieCharacterProductionSheetValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MovieCharacterProductionSheetLockedException() : Exception("The character production sheet is locked. Unlock it explicitly before creating a new version.");

public interface IMovieCharacterProductionSheetService
{
    Task<MovieCharacterProductionSheetDto?> GetAsync(Guid userId, Guid characterId, CancellationToken cancellationToken = default);
    Task<MovieCharacterProductionSheetDto?> SaveDraftAsync(Guid userId, Guid characterId, MovieCharacterProductionSheetRequest request, CancellationToken cancellationToken = default);
    Task<MovieCharacterProductionSheetDto?> ApproveAsync(Guid userId, Guid sheetId, CancellationToken cancellationToken = default);
    Task<MovieCharacterProductionSheetDto?> LockAsync(Guid userId, Guid sheetId, CancellationToken cancellationToken = default);
    Task<MovieCharacterProductionSheetDto?> UnlockAsync(Guid userId, Guid sheetId, CancellationToken cancellationToken = default);
}

public static class MovieCharacterProductionSheetMapper
{
    public static MovieCharacterProductionSheetDto ToDto(MovieCharacterProductionSheet sheet)
    {
        var versions = sheet.Versions.OrderByDescending(item => item.VersionNumber).Select(ToDto).ToArray();
        return new MovieCharacterProductionSheetDto(sheet.Id, sheet.MovieCharacterId, sheet.Status, sheet.CurrentVersionNumber, versions.FirstOrDefault(item => item.VersionNumber == sheet.CurrentVersionNumber), versions, sheet.ApprovedAt, sheet.ApprovedByUserId, sheet.LockedAt, sheet.LockedByUserId, sheet.UpdatedAt);
    }

    public static MovieCharacterProductionSheetVersionDto ToDto(MovieCharacterProductionSheetVersion version) => new(version.Id, version.VersionNumber, version.Status, version.CanonicalIdentityFaceAssetId, version.BodyReferenceAssetId, version.FrontReferenceAssetId, version.SideReferenceAssetId, version.BackReferenceAssetId, version.SourceCharacterUpdatedAt, version.ProvenanceHash, version.ProvenanceJson, version.CreatedByUserId, version.CreatedAt, version.ApprovedAt, version.ApprovedByUserId, version.LockedAt, version.LockedByUserId, version.Looks.OrderBy(item => item.SortOrder).Select(item => new MovieCharacterProductionSheetLookDto(item.Id, item.Key, item.Name, item.Wardrobe, item.Appearance, item.ReferenceNotes, item.ReferenceAssetId, item.SortOrder)).ToArray());
}

public sealed class MovieCharacterProductionSheetService(TaslimDbContext db, MovieCollaborationAccess collaboration) : IMovieCharacterProductionSheetService
{
    private static readonly JsonSerializerOptions ProvenanceOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void ValidateRequest(MovieCharacterProductionSheetRequest request, bool requireComplete = false) => NormalizeAndValidate(request, requireComplete);

    public async Task<MovieCharacterProductionSheetDto?> GetAsync(Guid userId, Guid characterId, CancellationToken cancellationToken = default)
    {
        var sheet = await Query().FirstOrDefaultAsync(item => item.MovieCharacterId == characterId, cancellationToken);
        return sheet is null || !await collaboration.HasPermissionAsync(userId, sheet.Character.MovieProjectId, MoviePermissions.View, cancellationToken) ? null : MovieCharacterProductionSheetMapper.ToDto(sheet);
    }

    public async Task<MovieCharacterProductionSheetDto?> SaveDraftAsync(Guid userId, Guid characterId, MovieCharacterProductionSheetRequest request, CancellationToken cancellationToken = default)
    {
        var character = await db.MovieCharacters.Include(item => item.MovieProject).Include(item => item.ProductionSheet).ThenInclude(item => item!.Versions).ThenInclude(item => item.Looks).FirstOrDefaultAsync(item => item.Id == characterId, cancellationToken);
        if (character is null || !await collaboration.HasPermissionAsync(userId, character.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (character.ProductionSheet?.Status == MovieCharacterProductionSheetStatuses.Locked) throw new MovieCharacterProductionSheetLockedException();
        var normalized = NormalizeAndValidate(request, requireComplete: false);
        await ValidateAssetsAsync(character.MovieProject.WorkspaceId, normalized.AssetIds, cancellationToken);
        var now = DateTime.UtcNow;
        var sheet = character.ProductionSheet ?? new MovieCharacterProductionSheet { Id = Guid.NewGuid(), MovieCharacterId = character.Id, CreatedAt = now };
        var versionNumber = (sheet.Versions.Count == 0 ? 0 : sheet.Versions.Max(item => item.VersionNumber)) + 1;
        var provenance = new
        {
            schemaVersion = 1,
            source = "character-bible",
            characterId,
            characterUpdatedAt = character.UpdatedAt,
            versionNumber,
            slots = normalized.Slots,
            looks = normalized.Looks,
            note = MovieStudioHelpers.Clean(request.ProvenanceNote),
        };
        var provenanceJson = JsonSerializer.Serialize(provenance, ProvenanceOptions);
        var version = new MovieCharacterProductionSheetVersion
        {
            Id = Guid.NewGuid(), MovieCharacterProductionSheetId = sheet.Id, VersionNumber = versionNumber,
            CanonicalIdentityFaceAssetId = normalized.CanonicalIdentityFaceAssetId, BodyReferenceAssetId = normalized.BodyReferenceAssetId,
            FrontReferenceAssetId = normalized.FrontReferenceAssetId, SideReferenceAssetId = normalized.SideReferenceAssetId, BackReferenceAssetId = normalized.BackReferenceAssetId,
            SourceCharacterUpdatedAt = character.UpdatedAt, ProvenanceJson = provenanceJson,
            ProvenanceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provenanceJson))).ToLowerInvariant(),
            CreatedByUserId = userId, CreatedAt = now,
            Looks = normalized.Looks.Select((look, index) => new MovieCharacterProductionSheetLook
            {
                Id = Guid.NewGuid(), Key = look.Key, Name = look.Name, Wardrobe = look.Wardrobe, Appearance = look.Appearance,
                ReferenceNotes = look.ReferenceNotes, ReferenceAssetId = look.ReferenceAssetId, SortOrder = index,
            }).ToList(),
        };
        sheet.CurrentVersionNumber = versionNumber;
        sheet.Status = MovieCharacterProductionSheetStatuses.Draft;
        sheet.ApprovedAt = null; sheet.ApprovedByUserId = null; sheet.LockedAt = null; sheet.LockedByUserId = null; sheet.UpdatedAt = now;
        sheet.Versions.Add(version);
        if (character.ProductionSheet is null) db.MovieCharacterProductionSheets.Add(sheet);
        await db.SaveChangesAsync(cancellationToken);
        return MovieCharacterProductionSheetMapper.ToDto(await Query().FirstAsync(item => item.Id == sheet.Id, cancellationToken));
    }

    public async Task<MovieCharacterProductionSheetDto?> ApproveAsync(Guid userId, Guid sheetId, CancellationToken cancellationToken = default)
    {
        var sheet = await Query().FirstOrDefaultAsync(item => item.Id == sheetId, cancellationToken);
        if (sheet is null || !await collaboration.HasPermissionAsync(userId, sheet.Character.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (sheet.Status == MovieCharacterProductionSheetStatuses.Locked) throw new MovieCharacterProductionSheetLockedException();
        var version = sheet.Versions.SingleOrDefault(item => item.VersionNumber == sheet.CurrentVersionNumber) ?? throw new MovieCharacterProductionSheetValidationException("VERSION_MISSING", "The current production-sheet version is missing.");
        EnsureComplete(version);
        var now = DateTime.UtcNow;
        sheet.Status = MovieCharacterProductionSheetStatuses.Approved; sheet.ApprovedAt = now; sheet.ApprovedByUserId = userId; sheet.UpdatedAt = now;
        version.Status = MovieCharacterProductionSheetStatuses.Approved; version.ApprovedAt = now; version.ApprovedByUserId = userId;
        await db.SaveChangesAsync(cancellationToken);
        return MovieCharacterProductionSheetMapper.ToDto(sheet);
    }

    public async Task<MovieCharacterProductionSheetDto?> LockAsync(Guid userId, Guid sheetId, CancellationToken cancellationToken = default)
    {
        var sheet = await Query().FirstOrDefaultAsync(item => item.Id == sheetId, cancellationToken);
        if (sheet is null || !await collaboration.HasPermissionAsync(userId, sheet.Character.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (sheet.Status == MovieCharacterProductionSheetStatuses.Locked) return MovieCharacterProductionSheetMapper.ToDto(sheet);
        if (sheet.Status != MovieCharacterProductionSheetStatuses.Approved) throw new MovieCharacterProductionSheetValidationException("APPROVAL_REQUIRED", "Approve the current production-sheet version before locking it.");
        var version = sheet.Versions.Single(item => item.VersionNumber == sheet.CurrentVersionNumber);
        var now = DateTime.UtcNow;
        sheet.Status = MovieCharacterProductionSheetStatuses.Locked; sheet.LockedAt = now; sheet.LockedByUserId = userId; sheet.UpdatedAt = now;
        version.Status = MovieCharacterProductionSheetStatuses.Locked; version.LockedAt = now; version.LockedByUserId = userId;
        await db.SaveChangesAsync(cancellationToken);
        return MovieCharacterProductionSheetMapper.ToDto(sheet);
    }

    public async Task<MovieCharacterProductionSheetDto?> UnlockAsync(Guid userId, Guid sheetId, CancellationToken cancellationToken = default)
    {
        var sheet = await Query().FirstOrDefaultAsync(item => item.Id == sheetId, cancellationToken);
        if (sheet is null || !await collaboration.HasPermissionAsync(userId, sheet.Character.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (sheet.Status != MovieCharacterProductionSheetStatuses.Locked) return MovieCharacterProductionSheetMapper.ToDto(sheet);
        var version = sheet.Versions.Single(item => item.VersionNumber == sheet.CurrentVersionNumber);
        var now = DateTime.UtcNow;
        sheet.Status = MovieCharacterProductionSheetStatuses.Draft; sheet.LockedAt = null; sheet.LockedByUserId = null; sheet.ApprovedAt = null; sheet.ApprovedByUserId = null; sheet.UpdatedAt = now;
        version.Status = MovieCharacterProductionSheetStatuses.Draft; version.LockedAt = null; version.LockedByUserId = null; version.ApprovedAt = null; version.ApprovedByUserId = null;
        await db.SaveChangesAsync(cancellationToken);
        return MovieCharacterProductionSheetMapper.ToDto(sheet);
    }

    private IQueryable<MovieCharacterProductionSheet> Query() => db.MovieCharacterProductionSheets.AsNoTracking().AsSplitQuery()
        .Include(item => item.Character).ThenInclude(item => item.MovieProject)
        .Include(item => item.Versions).ThenInclude(item => item.Looks);

    private async Task ValidateAssetsAsync(Guid workspaceId, IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0) return;
        var valid = await db.Assets.CountAsync(item => item.WorkspaceId == workspaceId && assetIds.Contains(item.Id), cancellationToken);
        if (valid != assetIds.Distinct().Count()) throw new MovieCharacterProductionSheetValidationException("ASSET_NOT_IN_WORKSPACE", "Every production-sheet reference must belong to the movie workspace.");
    }

    private static NormalizedSheet NormalizeAndValidate(MovieCharacterProductionSheetRequest request, bool requireComplete)
    {
        var slots = new[] { request.CanonicalIdentityFaceAssetId, request.BodyReferenceAssetId, request.FrontReferenceAssetId, request.SideReferenceAssetId, request.BackReferenceAssetId };
        var duplicates = slots.Where(item => item.HasValue).GroupBy(item => item!.Value).FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null) throw new MovieCharacterProductionSheetValidationException("DUPLICATE_REFERENCE_ASSET", "Each identity-sheet slot must use a distinct reference asset; the canonical face cannot be duplicated into another slot.");
        if (requireComplete && slots.Any(item => !item.HasValue)) throw new MovieCharacterProductionSheetValidationException("INCOMPLETE_REFERENCE_SHEET", "Approval requires canonical face, body, front, side, and back references.");
        var looks = (request.Looks ?? []).Select((look, index) => new NormalizedLook(
            Required(look.Key, "LOOK_KEY_REQUIRED", "Every look variant needs a key."),
            Required(look.Name, "LOOK_NAME_REQUIRED", "Every look variant needs a name."),
            MovieStudioHelpers.Clean(look.Wardrobe), MovieStudioHelpers.Clean(look.Appearance), MovieStudioHelpers.Clean(look.ReferenceNotes), look.ReferenceAssetId)).ToArray();
        if (looks.Length > 12) throw new MovieCharacterProductionSheetValidationException("TOO_MANY_LOOKS", "A production sheet can contain at most 12 look variants.");
        if (looks.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) throw new MovieCharacterProductionSheetValidationException("DUPLICATE_LOOK_KEY", "Look variant keys must be unique within a production-sheet version.");
        var assetIds = slots.Concat(looks.Select(item => item.ReferenceAssetId)).Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        return new NormalizedSheet(request.CanonicalIdentityFaceAssetId, request.BodyReferenceAssetId, request.FrontReferenceAssetId, request.SideReferenceAssetId, request.BackReferenceAssetId,
            new Dictionary<string, Guid?> { [MovieCharacterProductionSheetSlots.CanonicalIdentityFace] = request.CanonicalIdentityFaceAssetId, [MovieCharacterProductionSheetSlots.Body] = request.BodyReferenceAssetId, [MovieCharacterProductionSheetSlots.Front] = request.FrontReferenceAssetId, [MovieCharacterProductionSheetSlots.Side] = request.SideReferenceAssetId, [MovieCharacterProductionSheetSlots.Back] = request.BackReferenceAssetId }, looks, assetIds);
    }

    private static void EnsureComplete(MovieCharacterProductionSheetVersion version)
    {
        if (new[] { version.CanonicalIdentityFaceAssetId, version.BodyReferenceAssetId, version.FrontReferenceAssetId, version.SideReferenceAssetId, version.BackReferenceAssetId }.Any(item => !item.HasValue))
            throw new MovieCharacterProductionSheetValidationException("INCOMPLETE_REFERENCE_SHEET", "Approval requires canonical face, body, front, side, and back references.");
    }

    private static string Required(string? value, string code, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new MovieCharacterProductionSheetValidationException(code, message);
        if (normalized.Length > 120) throw new MovieCharacterProductionSheetValidationException("LOOK_VALUE_TOO_LONG", "Look keys and names must be 120 characters or fewer.");
        return normalized;
    }

    private sealed record NormalizedLook(string Key, string Name, string? Wardrobe, string? Appearance, string? ReferenceNotes, Guid? ReferenceAssetId);
    private sealed record NormalizedSheet(Guid? CanonicalIdentityFaceAssetId, Guid? BodyReferenceAssetId, Guid? FrontReferenceAssetId, Guid? SideReferenceAssetId, Guid? BackReferenceAssetId, IReadOnlyDictionary<string, Guid?> Slots, IReadOnlyList<NormalizedLook> Looks, IReadOnlyList<Guid> AssetIds);
}
