using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieFinalMasteringProfiles
{
    public const string Uhd4K = "Uhd4K";
    public const int Uhd4KWidth = 3_840;
    public const int Uhd4KHeight = 2_160;

    private static readonly IReadOnlyDictionary<string, MovieFinalMasteringProfile> Profiles =
        new Dictionary<string, MovieFinalMasteringProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [Uhd4K] = new(Uhd4K, Uhd4KWidth, Uhd4KHeight),
        };

    public static bool TryGet(string? key, out MovieFinalMasteringProfile profile) =>
        Profiles.TryGetValue(key?.Trim() ?? string.Empty, out profile!);
}

public sealed record MovieFinalMasteringProfile(string Key, int Width, int Height);

public static class MovieFinalMasteringStates
{
    public const string AwaitingProvider = "AwaitingProvider";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Blocked = "Blocked";
    public const string Superseded = "Superseded";
}

public static class MovieFinalMasteringQcStatuses
{
    public const string NotRun = "NotRun";
    public const string Pending = "Pending";
    public const string Passed = "Passed";
    public const string Failed = "Failed";
}

public sealed class MovieFinalMaster
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid MovieShotId { get; set; }
    public Guid SourceTakeId { get; set; }
    public Guid? SourceMovieClipId { get; set; }
    public Guid? SourceAssetId { get; set; }
    public Guid? OutputAssetId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public string TargetProfile { get; set; } = MovieFinalMasteringProfiles.Uhd4K;
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public string State { get; set; } = MovieFinalMasteringStates.AwaitingProvider;
    public string? StateReason { get; set; }
    public string QcStatus { get; set; } = MovieFinalMasteringQcStatuses.NotRun;
    public string? QcResultJson { get; set; }
    public string? ProvenanceJson { get; set; }
    public Guid? SupersedesMasterId { get; set; }
    public Guid? SupersededByMasterId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? SupersededAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public MovieShot MovieShot { get; set; } = null!;
    public MovieTake SourceTake { get; set; } = null!;
    public MovieClip? SourceMovieClip { get; set; }
    public Asset? SourceAsset { get; set; }
    public Asset? OutputAsset { get; set; }
    public GenerationJob? GenerationJob { get; set; }
    public MovieFinalMaster? SupersedesMaster { get; set; }
    public ApplicationUser RequestedByUser { get; set; } = null!;
}

public sealed record MovieFinalMasterDto(
    Guid Id,
    Guid MovieProjectId,
    Guid MovieShotId,
    Guid SourceTakeId,
    Guid? SourceMovieClipId,
    Guid? SourceAssetId,
    Guid? OutputAssetId,
    Guid? GenerationJobId,
    string TargetProfile,
    int? SourceWidth,
    int? SourceHeight,
    int TargetWidth,
    int TargetHeight,
    string State,
    string? StateReason,
    string QcStatus,
    string? QcResultJson,
    string? ProvenanceJson,
    Guid? SupersedesMasterId,
    Guid? SupersededByMasterId,
    DateTime RequestedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    DateTime? SupersededAt);

public sealed class MovieFinalMasteringRequest
{
    public string TargetProfile { get; set; } = MovieFinalMasteringProfiles.Uhd4K;
    public Guid? SupersedesMasterId { get; set; }
}

public interface IMovieFinalMasteringService
{
    Task<MovieFinalMasterDto?> GetForShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken);
    Task<MovieFinalMasterDto?> RequestAsync(Guid userId, Guid takeId, MovieFinalMasteringRequest request, CancellationToken cancellationToken);
}

public sealed class MovieFinalMasteringService(TaslimDbContext db, MovieCollaborationAccess collaboration) : IMovieFinalMasteringService
{
    public async Task<MovieFinalMasterDto?> GetForShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var master = await db.MovieFinalMasters.AsNoTracking()
            .Where(item => item.MovieShotId == shotId)
            .OrderByDescending(item => item.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return master is null ? null : ToDto(master);
    }

    public async Task<MovieFinalMasterDto?> RequestAsync(Guid userId, Guid takeId, MovieFinalMasteringRequest request, CancellationToken cancellationToken)
    {
        if (!MovieFinalMasteringProfiles.TryGet(request.TargetProfile, out var profile))
            throw new MovieFinalMasteringValidationException("MASTERING_TARGET_UNSUPPORTED", "Choose a supported final mastering target.");

        var take = await db.MovieTakes
            .Include(item => item.MovieShot)
                .ThenInclude(item => item.Scene)
                    .ThenInclude(item => item.MovieProject)
            .Include(item => item.Asset)
                .ThenInclude(item => item!.StoredFile)
            .Include(item => item.MovieClip)
                .ThenInclude(item => item!.Asset)
                    .ThenInclude(item => item!.StoredFile)
            .FirstOrDefaultAsync(item => item.Id == takeId, cancellationToken);
        if (take is null) return null;

        var movie = take.MovieShot.Scene.MovieProject;
        await collaboration.RequireAsync(userId, movie.Id, MoviePermissions.FinalApproval, cancellationToken);
        var isSelected = take.SelectedAt.HasValue || take.MovieShot.SelectedTakeId == take.Id || take.MovieShot.FinalTakeId == take.Id;
        var isApproved = string.Equals(take.Status, MovieTakeStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(take.Status, MovieTakeStatuses.Selected, StringComparison.OrdinalIgnoreCase);
        if (!isApproved || !isSelected)
            throw new MovieFinalMasteringValidationException("MASTERING_SOURCE_NOT_APPROVED_OR_SELECTED", "Only an approved and selected take can be sent to final mastering.");

        var sourceAsset = take.Asset ?? take.MovieClip?.Asset;
        var sourceAssetId = sourceAsset?.Id;
        var sourceClipId = take.MovieClipId;
        var sourceResolution = ReadSourceResolution(take, sourceAsset);
        var existing = await db.MovieFinalMasters
            .FirstOrDefaultAsync(item => item.SourceTakeId == take.Id && item.TargetProfile == profile.Key && item.SupersededByMasterId == null, cancellationToken);
        if (request.SupersedesMasterId is null && existing is not null) return ToDto(existing);

        MovieFinalMaster? superseded = null;
        if (request.SupersedesMasterId is Guid supersedesId)
        {
            superseded = await db.MovieFinalMasters.FirstOrDefaultAsync(item => item.Id == supersedesId && item.MovieShotId == take.MovieShotId, cancellationToken)
                ?? throw new MovieFinalMasteringValidationException("MASTERING_SUPERSESSION_INVALID", "The replacement must reference an existing master for the same shot.");
            if (superseded.SupersededByMasterId is not null)
                throw new MovieFinalMasteringValidationException("MASTERING_SUPERSESSION_INVALID", "The referenced master has already been superseded.");
        }

        var now = DateTime.UtcNow;
        var blockedReason = GetBlockedReason(sourceAsset, sourceResolution);
        var master = new MovieFinalMaster
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movie.Id,
            MovieShotId = take.MovieShotId,
            SourceTakeId = take.Id,
            SourceMovieClipId = sourceClipId,
            SourceAssetId = sourceAssetId,
            TargetProfile = profile.Key,
            SourceWidth = sourceResolution?.Width,
            SourceHeight = sourceResolution?.Height,
            TargetWidth = profile.Width,
            TargetHeight = profile.Height,
            State = blockedReason is null ? MovieFinalMasteringStates.AwaitingProvider : MovieFinalMasteringStates.Blocked,
            StateReason = blockedReason ?? "The provider-neutral mastering request is recorded; no provider execution has been started.",
            QcStatus = MovieFinalMasteringQcStatuses.NotRun,
            ProvenanceJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                workflow = "final-mastering",
                source = new
                {
                    kind = "movie_take",
                    takeId = take.Id,
                    clipId = sourceClipId,
                    assetId = sourceAssetId,
                    width = sourceResolution?.Width,
                    height = sourceResolution?.Height,
                    status = take.Status,
                    selected = isSelected,
                },
                target = new { profile = profile.Key, width = profile.Width, height = profile.Height },
                supersedesMasterId = superseded?.Id,
            }),
            SupersedesMasterId = superseded?.Id,
            RequestedByUserId = userId,
            RequestedAt = now,
            UpdatedAt = now,
        };

        if (superseded is not null)
        {
            superseded.State = MovieFinalMasteringStates.Superseded;
            superseded.StateReason = "Replaced by a newer final mastering request.";
            superseded.SupersededAt = now;
            superseded.SupersededByMasterId = master.Id;
            superseded.UpdatedAt = now;
        }

        db.MovieFinalMasters.Add(master);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(master);
    }

    private static string? GetBlockedReason(Asset? sourceAsset, SourceResolution? resolution)
    {
        if (sourceAsset is null) return "A source video asset is required before mastering can be scheduled.";
        if (!string.Equals(sourceAsset.AssetType, AssetTypes.Video, StringComparison.OrdinalIgnoreCase)) return "The source asset is not a video.";
        return resolution is null ? "Source video dimensions are required before mastering can be scheduled." : null;
    }

    private static SourceResolution? ReadSourceResolution(MovieTake take, Asset? sourceAsset)
    {
        foreach (var json in new[]
        {
            sourceAsset?.MetadataJson,
            sourceAsset?.StoredFile?.MetadataJson,
            take.MovieClip?.MetadataJson,
            take.MetadataJson,
        })
        {
            var resolution = ParseResolution(json);
            if (resolution is not null) return resolution;
        }
        return null;
    }

    private static SourceResolution? ParseResolution(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (TryReadDimensions(root, out var resolution)) return resolution;
            if (root.TryGetProperty("dimensions", out var dimensions) && TryReadDimensions(dimensions, out resolution)) return resolution;
            if (root.TryGetProperty("resolution", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var parts = value.GetString()!.Split(new[] { 'x', 'X', '×' }, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height) && width > 0 && height > 0)
                    return new SourceResolution(width, height);
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool TryReadDimensions(JsonElement element, out SourceResolution? resolution)
    {
        resolution = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("width", out var widthValue) || !element.TryGetProperty("height", out var heightValue)) return false;
        if (!widthValue.TryGetInt32(out var width) || !heightValue.TryGetInt32(out var height) || width <= 0 || height <= 0) return false;
        resolution = new SourceResolution(width, height);
        return true;
    }

    private static MovieFinalMasterDto ToDto(MovieFinalMaster master) => new(
        master.Id, master.MovieProjectId, master.MovieShotId, master.SourceTakeId, master.SourceMovieClipId, master.SourceAssetId, master.OutputAssetId, master.GenerationJobId,
        master.TargetProfile, master.SourceWidth, master.SourceHeight, master.TargetWidth, master.TargetHeight, master.State, master.StateReason, master.QcStatus,
        master.QcResultJson, master.ProvenanceJson, master.SupersedesMasterId, master.SupersededByMasterId, master.RequestedAt, master.UpdatedAt, master.CompletedAt, master.SupersededAt);

    private sealed record SourceResolution(int Width, int Height);
}

public sealed class MovieFinalMasteringValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
