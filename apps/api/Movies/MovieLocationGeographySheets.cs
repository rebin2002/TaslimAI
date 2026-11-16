using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieLocationGeographySheetStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
}

public static class MovieLocationGeographyVariantStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
}

public static class MovieLocationGeographyLimits
{
    public const int MaxEntriesPerGroup = 24;
    public const int MaxLabelLength = 160;
    public const int MaxDescriptionLength = 2_000;
    public const int MaxOrientationLength = 120;
    public const int MaxPositionLength = 240;
    public const int MaxContinuityNotesLength = 2_000;
    public const int MaxSheetJsonLength = 60_000;
}

public sealed record MovieLocationGeographyEntry(
    string Label,
    string Description,
    string? Orientation = null,
    string? RelativePosition = null,
    string? ContinuityNotes = null,
    Guid? ReferenceAssetId = null);

public sealed record MovieLocationGeographyOpening(
    string Label,
    string Kind,
    string Description,
    string? Orientation = null,
    string? Connection = null,
    string? ContinuityNotes = null,
    Guid? ReferenceAssetId = null);

public sealed record MovieLocationGeographyPath(
    string Label,
    string From,
    string To,
    string Description,
    string? Orientation = null,
    string? ContinuityNotes = null,
    Guid? ReferenceAssetId = null);

public sealed record MovieLocationGeographyVariantDto(
    Guid Id,
    Guid MovieLocationGeographySheetId,
    int VersionNumber,
    string Name,
    string Status,
    string? Description,
    string? TimeOfDay,
    string? Weather,
    string? Lighting,
    string? ColorPalette,
    Guid? ReferenceAssetId,
    string? ContinuityNotes,
    int GuideRevisionNumber,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ApprovedAt);

public sealed record MovieLocationGeographySheetDto(
    Guid Id,
    Guid MovieLocationId,
    int VersionNumber,
    string Status,
    Guid? EstablishingReferenceAssetId,
    string? EstablishingReferenceNotes,
    Guid? WideThreeQuarterReferenceAssetId,
    string? WideThreeQuarterReferenceNotes,
    IReadOnlyList<MovieLocationGeographyOpening> EntrancesExits,
    IReadOnlyList<MovieLocationGeographyOpening> Windows,
    IReadOnlyList<MovieLocationGeographyPath> Paths,
    IReadOnlyList<MovieLocationGeographyEntry> MajorObjects,
    IReadOnlyList<MovieLocationGeographyEntry> LightSources,
    IReadOnlyList<MovieLocationGeographyEntry> OrientationAnchors,
    int GuideRevisionNumber,
    string? WorldBibleJson,
    string? VisualBibleJson,
    string? ContinuitySnapshotHash,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ApprovedAt,
    IReadOnlyList<MovieLocationGeographyVariantDto> Variants);

public sealed class MovieLocationGeographySheetRequest
{
    public Guid? EstablishingReferenceAssetId { get; set; }
    public string? EstablishingReferenceNotes { get; set; }
    public Guid? WideThreeQuarterReferenceAssetId { get; set; }
    public string? WideThreeQuarterReferenceNotes { get; set; }
    public List<MovieLocationGeographyOpening> EntrancesExits { get; set; } = [];
    public List<MovieLocationGeographyOpening> Windows { get; set; } = [];
    public List<MovieLocationGeographyPath> Paths { get; set; } = [];
    public List<MovieLocationGeographyEntry> MajorObjects { get; set; } = [];
    public List<MovieLocationGeographyEntry> LightSources { get; set; } = [];
    public List<MovieLocationGeographyEntry> OrientationAnchors { get; set; } = [];
}

public sealed record MovieLocationGeographyVariantRequest(
    string Name,
    string? Description,
    string? TimeOfDay,
    string? Weather,
    string? Lighting,
    string? ColorPalette,
    Guid? ReferenceAssetId,
    string? ContinuityNotes);

public sealed class MovieLocationGeographySheetLockedException : Exception
{
    public MovieLocationGeographySheetLockedException() : base("The approved location geography sheet is locked. Create a new location version before changing it.") { }
}

public static class MovieLocationGeographySheetPolicy
{
    public static string? Validate(MovieLocationGeographySheetRequest request)
    {
        if (request is null) return "A location geography sheet is required.";
        if (request.EntrancesExits.Count > MovieLocationGeographyLimits.MaxEntriesPerGroup ||
            request.Windows.Count > MovieLocationGeographyLimits.MaxEntriesPerGroup ||
            request.Paths.Count > MovieLocationGeographyLimits.MaxEntriesPerGroup ||
            request.MajorObjects.Count > MovieLocationGeographyLimits.MaxEntriesPerGroup ||
            request.LightSources.Count > MovieLocationGeographyLimits.MaxEntriesPerGroup ||
            request.OrientationAnchors.Count > MovieLocationGeographyLimits.MaxEntriesPerGroup)
            return $"A geography sheet cannot contain more than {MovieLocationGeographyLimits.MaxEntriesPerGroup} items in one group.";
        if (!string.IsNullOrWhiteSpace(request.EstablishingReferenceNotes) && request.EstablishingReferenceNotes.Length > MovieLocationGeographyLimits.MaxDescriptionLength)
            return "Establishing reference notes are too long.";
        if (!string.IsNullOrWhiteSpace(request.WideThreeQuarterReferenceNotes) && request.WideThreeQuarterReferenceNotes.Length > MovieLocationGeographyLimits.MaxDescriptionLength)
            return "Wide 3/4 reference notes are too long.";
        foreach (var item in request.EntrancesExits) if (ValidateOpening(item) is { } error) return error;
        foreach (var item in request.Windows) if (ValidateOpening(item) is { } error) return error;
        foreach (var item in request.Paths) if (ValidatePath(item) is { } error) return error;
        foreach (var item in request.MajorObjects.Concat(request.LightSources).Concat(request.OrientationAnchors)) if (ValidateEntry(item) is { } error) return error;
        return null;
    }

    private static string? ValidateEntry(MovieLocationGeographyEntry item) =>
        ValidateText(item.Label, MovieLocationGeographyLimits.MaxLabelLength, "A geography item label") ??
        ValidateText(item.Description, MovieLocationGeographyLimits.MaxDescriptionLength, "A geography item description") ??
        ValidateOptional(item.Orientation, MovieLocationGeographyLimits.MaxOrientationLength, "An orientation") ??
        ValidateOptional(item.RelativePosition, MovieLocationGeographyLimits.MaxPositionLength, "A relative position") ??
        ValidateOptional(item.ContinuityNotes, MovieLocationGeographyLimits.MaxContinuityNotesLength, "Continuity notes");

    private static string? ValidateOpening(MovieLocationGeographyOpening item) =>
        ValidateText(item.Label, MovieLocationGeographyLimits.MaxLabelLength, "An opening label") ??
        ValidateText(item.Kind, 40, "An opening kind") ??
        ValidateText(item.Description, MovieLocationGeographyLimits.MaxDescriptionLength, "An opening description") ??
        ValidateOptional(item.Orientation, MovieLocationGeographyLimits.MaxOrientationLength, "An opening orientation") ??
        ValidateOptional(item.Connection, MovieLocationGeographyLimits.MaxPositionLength, "An opening connection") ??
        ValidateOptional(item.ContinuityNotes, MovieLocationGeographyLimits.MaxContinuityNotesLength, "Continuity notes");

    private static string? ValidatePath(MovieLocationGeographyPath item) =>
        ValidateText(item.Label, MovieLocationGeographyLimits.MaxLabelLength, "A path label") ??
        ValidateText(item.From, MovieLocationGeographyLimits.MaxLabelLength, "A path origin") ??
        ValidateText(item.To, MovieLocationGeographyLimits.MaxLabelLength, "A path destination") ??
        ValidateText(item.Description, MovieLocationGeographyLimits.MaxDescriptionLength, "A path description") ??
        ValidateOptional(item.Orientation, MovieLocationGeographyLimits.MaxOrientationLength, "A path orientation") ??
        ValidateOptional(item.ContinuityNotes, MovieLocationGeographyLimits.MaxContinuityNotesLength, "Continuity notes");

    private static string? ValidateText(string value, int max, string label) => string.IsNullOrWhiteSpace(value) ? $"{label} is required." : value.Trim().Length > max ? $"{label} is too long." : null;
    private static string? ValidateOptional(string? value, int max, string label) => value?.Trim().Length > max ? $"{label} is too long." : null;
}

public static class MovieLocationGeographySheetSerialization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Entries(IEnumerable<MovieLocationGeographyEntry> value) => Serialize(value);
    public static string Openings(IEnumerable<MovieLocationGeographyOpening> value) => Serialize(value);
    public static string Paths(IEnumerable<MovieLocationGeographyPath> value) => Serialize(value);
    public static IReadOnlyList<MovieLocationGeographyEntry> ReadEntries(string? value) => Read<MovieLocationGeographyEntry>(value);
    public static IReadOnlyList<MovieLocationGeographyOpening> ReadOpenings(string? value) => Read<MovieLocationGeographyOpening>(value);
    public static IReadOnlyList<MovieLocationGeographyPath> ReadPaths(string? value) => Read<MovieLocationGeographyPath>(value);

    private static string Serialize<T>(IEnumerable<T> value) => JsonSerializer.Serialize(value.Take(MovieLocationGeographyLimits.MaxEntriesPerGroup).ToArray(), JsonOptions);
    private static IReadOnlyList<T> Read<T>(string? value) => string.IsNullOrWhiteSpace(value) ? [] : JsonSerializer.Deserialize<List<T>>(value, JsonOptions) ?? [];
}

public static class MovieLocationGeographySheetMapper
{
    public static MovieLocationGeographySheetDto ToDto(MovieLocationGeographySheet sheet) => new(
        sheet.Id, sheet.MovieLocationId, sheet.VersionNumber, sheet.Status, sheet.EstablishingReferenceAssetId,
        sheet.EstablishingReferenceNotes, sheet.WideThreeQuarterReferenceAssetId, sheet.WideThreeQuarterReferenceNotes,
        MovieLocationGeographySheetSerialization.ReadOpenings(sheet.EntrancesExitsJson),
        MovieLocationGeographySheetSerialization.ReadOpenings(sheet.WindowsJson),
        MovieLocationGeographySheetSerialization.ReadPaths(sheet.PathsJson),
        MovieLocationGeographySheetSerialization.ReadEntries(sheet.MajorObjectsJson),
        MovieLocationGeographySheetSerialization.ReadEntries(sheet.LightSourcesJson),
        MovieLocationGeographySheetSerialization.ReadEntries(sheet.OrientationAnchorsJson),
        sheet.GuideRevisionNumber, sheet.WorldBibleJson, sheet.VisualBibleJson, sheet.ContinuitySnapshotHash,
        sheet.CreatedAt, sheet.UpdatedAt, sheet.ApprovedAt,
        sheet.Variants.OrderByDescending(item => item.VersionNumber).Select(ToDto).ToArray());

    public static MovieLocationGeographyVariantDto ToDto(MovieLocationGeographyVariant variant) => new(
        variant.Id, variant.MovieLocationGeographySheetId, variant.VersionNumber, variant.Name, variant.Status,
        variant.Description, variant.TimeOfDay, variant.Weather, variant.Lighting, variant.ColorPalette,
        variant.ReferenceAssetId, variant.ContinuityNotes, variant.GuideRevisionNumber, variant.CreatedAt,
        variant.UpdatedAt, variant.ApprovedAt);
}

public interface IMovieLocationGeographySheetService
{
    Task<MovieLocationGeographySheetDto?> GetAsync(Guid userId, Guid locationId, CancellationToken cancellationToken = default);
    Task<MovieLocationGeographySheetDto?> UpsertAsync(Guid userId, Guid locationId, MovieLocationGeographySheetRequest request, CancellationToken cancellationToken = default);
    Task<MovieLocationGeographyVariantDto?> AddVariantAsync(Guid userId, Guid locationId, MovieLocationGeographyVariantRequest request, CancellationToken cancellationToken = default);
    Task<MovieLocationGeographyVariantDto?> ApproveVariantAsync(Guid userId, Guid variantId, CancellationToken cancellationToken = default);
    Task<MovieLocationGeographySheetDto?> ApproveSheetAsync(Guid userId, Guid locationId, CancellationToken cancellationToken = default);
}

public sealed class MovieLocationGeographySheetService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration) : IMovieLocationGeographySheetService
{
    public async Task<MovieLocationGeographySheetDto?> GetAsync(Guid userId, Guid locationId, CancellationToken cancellationToken = default)
    {
        var location = await Query().FirstOrDefaultAsync(item => item.Id == locationId, cancellationToken);
        return location is null || !await collaboration.HasPermissionAsync(userId, location.MovieProjectId, MoviePermissions.View, cancellationToken) || location.GeographySheet is null
            ? null
            : MovieLocationGeographySheetMapper.ToDto(location.GeographySheet);
    }

    public async Task<MovieLocationGeographySheetDto?> UpsertAsync(Guid userId, Guid locationId, MovieLocationGeographySheetRequest request, CancellationToken cancellationToken = default)
    {
        var location = await Query().FirstOrDefaultAsync(item => item.Id == locationId, cancellationToken);
        if (location is null || !await collaboration.HasPermissionAsync(userId, location.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var validation = MovieLocationGeographySheetPolicy.Validate(request);
        if (validation is not null) throw new MovieStudioValidationException(validation);
        await ValidateAssetsAsync(location.MovieProject.WorkspaceId, request, cancellationToken);
        if (location.GeographySheet?.Status == MovieLocationGeographySheetStatuses.Approved) throw new MovieLocationGeographySheetLockedException();

        var now = DateTime.UtcNow;
        var guideRevision = await db.MovieGuideRevisions.AsNoTracking()
            .Where(item => item.MovieContinuityGuideId == location.MovieProject.Guide.Id && item.RevisionNumber == location.MovieProject.Guide.CurrentRevisionNumber)
            .SingleOrDefaultAsync(cancellationToken);
        var sheet = location.GeographySheet ?? new MovieLocationGeographySheet
        {
            Id = Guid.NewGuid(), MovieLocationId = location.Id, VersionNumber = 1,
            Status = MovieLocationGeographySheetStatuses.Draft, CreatedAt = now,
        };
        sheet.EstablishingReferenceAssetId = request.EstablishingReferenceAssetId;
        sheet.EstablishingReferenceNotes = Clean(request.EstablishingReferenceNotes);
        sheet.WideThreeQuarterReferenceAssetId = request.WideThreeQuarterReferenceAssetId;
        sheet.WideThreeQuarterReferenceNotes = Clean(request.WideThreeQuarterReferenceNotes);
        sheet.EntrancesExitsJson = MovieLocationGeographySheetSerialization.Openings(request.EntrancesExits);
        sheet.WindowsJson = MovieLocationGeographySheetSerialization.Openings(request.Windows);
        sheet.PathsJson = MovieLocationGeographySheetSerialization.Paths(request.Paths);
        sheet.MajorObjectsJson = MovieLocationGeographySheetSerialization.Entries(request.MajorObjects);
        sheet.LightSourcesJson = MovieLocationGeographySheetSerialization.Entries(request.LightSources);
        sheet.OrientationAnchorsJson = MovieLocationGeographySheetSerialization.Entries(request.OrientationAnchors);
        sheet.GuideRevisionNumber = location.MovieProject.Guide.CurrentRevisionNumber;
        sheet.WorldBibleJson = guideRevision?.WorldBibleReferencesJson;
        sheet.VisualBibleJson = guideRevision?.VisualBibleJson;
        sheet.ContinuitySnapshotHash = MovieLocationGeographyContinuity.Hash(location.Id, sheet, location.MovieProject.Guide);
        sheet.UpdatedAt = now;
        if (location.GeographySheet is null) db.MovieLocationGeographySheets.Add(sheet);
        location.UpdatedAt = now;
        location.MovieProject.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        var savedSheet = await db.MovieLocationGeographySheets.Include(item => item.Variants).SingleAsync(item => item.MovieLocationId == locationId, cancellationToken);
        return MovieLocationGeographySheetMapper.ToDto(savedSheet);
    }

    public async Task<MovieLocationGeographyVariantDto?> AddVariantAsync(Guid userId, Guid locationId, MovieLocationGeographyVariantRequest request, CancellationToken cancellationToken = default)
    {
        var location = await Query().FirstOrDefaultAsync(item => item.Id == locationId, cancellationToken);
        if (location is null || !await collaboration.HasPermissionAsync(userId, location.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (location.GeographySheet is null) throw new MovieStudioValidationException("Save the location geography sheet before adding an approved variant.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > MovieLocationGeographyLimits.MaxLabelLength) throw new MovieStudioValidationException("Variant name is required and must be short.");
        await EnsureAssetAsync(request.ReferenceAssetId, location.MovieProject.WorkspaceId, cancellationToken);
        var now = DateTime.UtcNow;
        var variant = new MovieLocationGeographyVariant
        {
            Id = Guid.NewGuid(), MovieLocationGeographySheetId = location.GeographySheet.Id,
            VersionNumber = (await db.MovieLocationGeographyVariants.Where(item => item.MovieLocationGeographySheetId == location.GeographySheet!.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1,
            Name = request.Name.Trim(), Status = MovieLocationGeographyVariantStatuses.Draft,
            Description = Clean(request.Description), TimeOfDay = Clean(request.TimeOfDay), Weather = Clean(request.Weather),
            Lighting = Clean(request.Lighting), ColorPalette = Clean(request.ColorPalette), ReferenceAssetId = request.ReferenceAssetId,
            ContinuityNotes = Clean(request.ContinuityNotes), GuideRevisionNumber = location.MovieProject.Guide.CurrentRevisionNumber,
            CreatedAt = now, UpdatedAt = now,
        };
        db.MovieLocationGeographyVariants.Add(variant);
        location.GeographySheet.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return MovieLocationGeographySheetMapper.ToDto(variant);
    }

    public async Task<MovieLocationGeographyVariantDto?> ApproveVariantAsync(Guid userId, Guid variantId, CancellationToken cancellationToken = default)
    {
        var variant = await db.MovieLocationGeographyVariants.Include(item => item.Sheet).ThenInclude(item => item!.Location).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide).FirstOrDefaultAsync(item => item.Id == variantId, cancellationToken);
        if (variant is null || !await collaboration.HasPermissionAsync(userId, variant.Sheet!.Location.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (variant.Status == MovieLocationGeographyVariantStatuses.Approved) return MovieLocationGeographySheetMapper.ToDto(variant);
        variant.Status = MovieLocationGeographyVariantStatuses.Approved;
        variant.ApprovedAt = DateTime.UtcNow;
        variant.ApprovedByUserId = userId;
        variant.UpdatedAt = variant.ApprovedAt.Value;
        variant.Sheet.ContinuitySnapshotHash = MovieLocationGeographyContinuity.Hash(variant.Sheet.MovieLocationId, variant.Sheet, variant.Sheet.Location.MovieProject.Guide);
        variant.Sheet.UpdatedAt = variant.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return MovieLocationGeographySheetMapper.ToDto(variant);
    }

    public async Task<MovieLocationGeographySheetDto?> ApproveSheetAsync(Guid userId, Guid locationId, CancellationToken cancellationToken = default)
    {
        var location = await Query().FirstOrDefaultAsync(item => item.Id == locationId, cancellationToken);
        if (location is null || !await collaboration.HasPermissionAsync(userId, location.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var sheet = location.GeographySheet;
        if (sheet is null) throw new MovieStudioValidationException("Save the location geography sheet before approving it.");
        if (!sheet.EstablishingReferenceAssetId.HasValue && string.IsNullOrWhiteSpace(sheet.EstablishingReferenceNotes)) throw new MovieStudioValidationException("An establishing reference is required before approval.");
        if (!sheet.WideThreeQuarterReferenceAssetId.HasValue && string.IsNullOrWhiteSpace(sheet.WideThreeQuarterReferenceNotes)) throw new MovieStudioValidationException("A wide 3/4 spatial reference is required before approval.");
        var now = DateTime.UtcNow;
        sheet.Status = MovieLocationGeographySheetStatuses.Approved;
        sheet.ApprovedAt = now;
        sheet.ApprovedByUserId = userId;
        sheet.UpdatedAt = now;
        sheet.ContinuitySnapshotHash = MovieLocationGeographyContinuity.Hash(location.Id, sheet, location.MovieProject.Guide);
        await db.SaveChangesAsync(cancellationToken);
        return MovieLocationGeographySheetMapper.ToDto(sheet);
    }

    private IQueryable<MovieLocation> Query() => db.MovieLocations
        .Include(item => item.MovieProject).ThenInclude(item => item.Guide)
        .Include(item => item.GeographySheet).ThenInclude(item => item!.Variants);

    private async Task ValidateAssetsAsync(Guid workspaceId, MovieLocationGeographySheetRequest request, CancellationToken cancellationToken)
    {
        var ids = new List<Guid?> { request.EstablishingReferenceAssetId, request.WideThreeQuarterReferenceAssetId };
        ids.AddRange(request.EntrancesExits.Select(item => item.ReferenceAssetId));
        ids.AddRange(request.Windows.Select(item => item.ReferenceAssetId));
        ids.AddRange(request.Paths.Select(item => item.ReferenceAssetId));
        ids.AddRange(request.MajorObjects.Concat(request.LightSources).Concat(request.OrientationAnchors).Select(item => item.ReferenceAssetId));
        await ValidateAssetIdsAsync(workspaceId, ids, cancellationToken);
    }

    private async Task EnsureAssetAsync(Guid? assetId, Guid workspaceId, CancellationToken cancellationToken) => await ValidateAssetIdsAsync(workspaceId, [assetId], cancellationToken);

    private async Task ValidateAssetIdsAsync(Guid workspaceId, IEnumerable<Guid?> values, CancellationToken cancellationToken)
    {
        var ids = values.Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        if (ids.Length > 0 && await db.Assets.CountAsync(item => item.WorkspaceId == workspaceId && ids.Contains(item.Id), cancellationToken) != ids.Length)
            throw new MovieStudioValidationException("Every geography reference asset must belong to the movie workspace.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class MovieLocationGeographyContinuity
{
    public static string Hash(Guid locationId, MovieLocationGeographySheet sheet, MovieContinuityGuide guide)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, locationId, sheet.VersionNumber, sheet.Status,
            sheet.EstablishingReferenceAssetId, sheet.WideThreeQuarterReferenceAssetId,
            sheet.EntrancesExitsJson, sheet.WindowsJson, sheet.PathsJson, sheet.MajorObjectsJson,
            sheet.LightSourcesJson, sheet.OrientationAnchorsJson,
            guideRevisionNumber = guide.CurrentRevisionNumber,
            guide.VisualLanguage, guide.ColorAndLighting, guide.ContinuityRules,
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
