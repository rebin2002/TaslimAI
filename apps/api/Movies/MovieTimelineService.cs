using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieTimelineService
{
    Task<MovieTimelineDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieTimelineRevisionDto?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieTimelineRevisionRequest request, CancellationToken cancellationToken);
    Task<MovieTimelineTrackDto?> AddTrackAsync(Guid userId, Guid revisionId, MovieTimelineTrackRequest request, CancellationToken cancellationToken);
    Task<MovieTimelineItemDto?> AddItemAsync(Guid userId, Guid trackId, MovieTimelineItemRequest request, CancellationToken cancellationToken);
    Task<MovieTimelineRevisionDto?> LockRevisionAsync(Guid userId, Guid revisionId, CancellationToken cancellationToken);
}

public sealed class MovieTimelineService(TaslimDbContext db, MovieCollaborationAccess collaboration) : IMovieTimelineService
{
    public async Task<MovieTimelineDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var timeline = await QueryTimeline().FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        return timeline is null ? null : ToDto(timeline);
    }

    public async Task<MovieTimelineRevisionDto?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieTimelineRevisionRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        ValidateBounded(request.Label, 160, "Timeline revision label");
        ValidateBounded(request.ChangeSummary, 2_000, "Timeline revision change summary");

        var timeline = await db.MovieTimelines
            .Include(item => item.Revisions).ThenInclude(item => item.Tracks).ThenInclude(item => item.Items)
            .FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        var isNewTimeline = timeline is null;
        if (timeline is null)
        {
            timeline = new MovieTimeline
            {
                Id = Guid.NewGuid(), MovieProjectId = movieProjectId, CurrentRevisionNumber = 0,
                CreatedAt = now, UpdatedAt = now,
            };
        }

        var baseRevision = request.BaseRevisionId.HasValue
            ? timeline.Revisions.FirstOrDefault(item => item.Id == request.BaseRevisionId.Value)
            : timeline.CurrentRevisionId.HasValue
                ? timeline.Revisions.FirstOrDefault(item => item.Id == timeline.CurrentRevisionId.Value)
                : timeline.Revisions.OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
        if (request.BaseRevisionId.HasValue && baseRevision is null)
            throw Invalid("The base timeline revision does not belong to this movie project.");

        var revision = new MovieTimelineRevision
        {
            Id = Guid.NewGuid(), MovieTimelineId = timeline.Id, RevisionNumber = timeline.CurrentRevisionNumber + 1,
            BaseRevisionId = baseRevision?.Id, Status = MovieTimelineRevisionStatuses.Draft,
            Label = Clean(request.Label), ChangeSummary = Clean(request.ChangeSummary),
            CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now, Timeline = timeline,
        };

        if (request.Tracks is null)
        {
            if (baseRevision is not null)
                foreach (var sourceTrack in baseRevision.Tracks.OrderBy(item => item.TrackNumber))
                    CloneTrack(revision, sourceTrack, now);
            revision.DurationMilliseconds = baseRevision?.DurationMilliseconds ?? 0;
        }
        else
        {
            if (request.Tracks.Count > 64 || request.Tracks.Sum(item => item.Items.Count) > MovieTimelineContract.MaxClips)
                throw Invalid("A timeline revision is limited to 64 tracks and 10,000 items.");
            await AddRequestedTracksAsync(revision, request.Tracks, movie.WorkspaceId, movie.ProjectId, movieProjectId, now, cancellationToken);
            revision.DurationMilliseconds = CalculateDuration(revision);
        }

        foreach (var previous in timeline.Revisions.Where(item => item.Id != revision.Id && item.Status == MovieTimelineRevisionStatuses.Draft))
            previous.Status = MovieTimelineRevisionStatuses.Superseded;
        if (isNewTimeline)
        {
            db.MovieTimelines.Add(timeline);
            await db.SaveChangesAsync(cancellationToken);
        }
        db.MovieTimelineRevisions.Add(revision);
        await db.SaveChangesAsync(cancellationToken);
        timeline.CurrentRevisionNumber = revision.RevisionNumber;
        timeline.CurrentRevisionId = revision.Id;
        timeline.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(await QueryRevision().SingleAsync(item => item.Id == revision.Id, cancellationToken));
    }

    public async Task<MovieTimelineTrackDto?> AddTrackAsync(Guid userId, Guid revisionId, MovieTimelineTrackRequest request, CancellationToken cancellationToken)
    {
        var revision = await QueryRevision().FirstOrDefaultAsync(item => item.Id == revisionId, cancellationToken);
        if (revision is null || !await CanEditAsync(userId, revision, cancellationToken)) return null;
        EnsureDraft(revision);
        ValidateTrackKind(request.Kind);
        ValidateBounded(request.Name, 160, "Timeline track name");
        if (request.Items.Count > 0) throw Invalid("Create the track before adding timeline items.");
        var now = DateTime.UtcNow;
        var track = new MovieTimelineTrack
        {
            Id = Guid.NewGuid(), MovieTimelineRevisionId = revisionId,
            TrackNumber = request.TrackNumber ?? (revision.Tracks.Select(item => item.TrackNumber).DefaultIfEmpty(0).Max() + 1),
            Kind = NormalizeTrackKind(request.Kind), Name = Clean(request.Name), IsMuted = request.IsMuted,
            CreatedAt = now, UpdatedAt = now,
        };
        if (revision.Tracks.Any(item => item.TrackNumber == track.TrackNumber))
            throw Invalid("Track numbers must be unique within a revision.");
        revision.Tracks.Add(track);
        Touch(revision, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(track);
    }

    public async Task<MovieTimelineItemDto?> AddItemAsync(Guid userId, Guid trackId, MovieTimelineItemRequest request, CancellationToken cancellationToken)
    {
        var track = await db.MovieTimelineTracks
            .Include(item => item.Revision).ThenInclude(item => item.Timeline)
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        if (track is null || !await CanEditAsync(userId, track.Revision, cancellationToken)) return null;
        EnsureDraft(track.Revision);
        var movie = await db.MovieProjects.AsNoTracking().FirstAsync(item => item.Id == track.Revision.Timeline.MovieProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        var materialized = await MaterializeItemAsync(track.Revision, track, request, movie.WorkspaceId, movie.ProjectId, now, cancellationToken);
        ValidateNoOverlaps(track.Items.Append(materialized));
        track.Items.Add(materialized);
        track.UpdatedAt = now;
        Touch(track.Revision, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(materialized);
    }

    public async Task<MovieTimelineRevisionDto?> LockRevisionAsync(Guid userId, Guid revisionId, CancellationToken cancellationToken)
    {
        var revision = await QueryRevision().FirstOrDefaultAsync(item => item.Id == revisionId, cancellationToken);
        if (revision is null || !await collaboration.HasPermissionAsync(userId, revision.Timeline.MovieProjectId, MoviePermissions.FinalApproval, cancellationToken)) return null;
        if (revision.Status == MovieTimelineRevisionStatuses.Locked)
            return ToDto(revision);
        EnsureDraft(revision);
        ValidateRevision(revision);
        var now = DateTime.UtcNow;
        revision.Status = MovieTimelineRevisionStatuses.Locked;
        revision.LockedAt = now;
        revision.LockedByUserId = userId;
        revision.UpdatedAt = now;
        var timeline = revision.Timeline;
        timeline.LockedRevisionNumber = revision.RevisionNumber;
        timeline.LockedRevisionId = revision.Id;
        timeline.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(revision);
    }

    private async Task AddRequestedTracksAsync(
        MovieTimelineRevision revision,
        IReadOnlyList<MovieTimelineTrackRequest> requests,
        Guid workspaceId,
        Guid? projectId,
        Guid movieProjectId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var usedNumbers = new HashSet<int>();
        foreach (var request in requests)
        {
            ValidateTrackKind(request.Kind);
            ValidateBounded(request.Name, 160, "Timeline track name");
            var trackNumber = request.TrackNumber ?? (usedNumbers.Count == 0 ? 1 : usedNumbers.Max() + 1);
            if (!usedNumbers.Add(trackNumber) || trackNumber < 1) throw Invalid("Track numbers must be unique and start at 1.");
            var track = new MovieTimelineTrack
            {
                Id = Guid.NewGuid(), MovieTimelineRevisionId = revision.Id, TrackNumber = trackNumber,
                Kind = NormalizeTrackKind(request.Kind), Name = Clean(request.Name), IsMuted = request.IsMuted,
                CreatedAt = now, UpdatedAt = now,
            };
            revision.Tracks.Add(track);
            foreach (var itemRequest in request.Items)
            {
                var item = await MaterializeItemAsync(revision, track, itemRequest, workspaceId, projectId, now, cancellationToken);
                track.Items.Add(item);
            }
            ValidateNoOverlaps(track.Items);
        }
    }

    private async Task<MovieTimelineItem> MaterializeItemAsync(
        MovieTimelineRevision revision,
        MovieTimelineTrack track,
        MovieTimelineItemRequest request,
        Guid workspaceId,
        Guid? projectId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var kind = NormalizeItemKind(request.Kind);
        ValidateKindForTrack(kind, track.Kind);
        ValidateBounded(request.Label, 160, "Timeline item label");
        ValidateMetadata(request.MetadataJson);
        if (request.TimelineInMilliseconds < 0 || request.TimelineInMilliseconds > 86_400_000 || request.TimelineOutMilliseconds is > 86_400_000)
            throw Invalid("Timeline points must be within the supported 24-hour range.");
        if (request.SourceInMilliseconds is < 0 or > 86_400_000 || request.SourceOutMilliseconds is < 0 or > 86_400_000)
            throw Invalid("Source in/out points cannot be negative.");
        if (kind == MovieTimelineItemKinds.Gap)
        {
            if (request.SourceTakeId.HasValue || request.SourceAssetId.HasValue || request.SourceInMilliseconds.HasValue || request.SourceOutMilliseconds.HasValue)
                throw Invalid("A gap cannot reference a source take or asset.");
            if (!request.TimelineOutMilliseconds.HasValue || request.TimelineOutMilliseconds.Value <= request.TimelineInMilliseconds)
                throw Invalid("A gap requires a positive timeline in/out range.");
        }

        Guid? sourceTakeId = null;
        Guid? sourceAssetId = null;
        int? sourceIn = null;
        int? sourceOut = null;
        var sourceDuration = 0;
        if (kind == MovieTimelineItemKinds.VisualTake)
        {
            if (!request.SourceTakeId.HasValue) throw SourceInvalid("A visual timeline item must reference a selected take.");
            var take = await db.MovieTakes
                .Include(item => item.MovieShot).ThenInclude(item => item.Scene)
                .Include(item => item.Asset)
                .Include(item => item.MovieClip).ThenInclude(item => item!.Asset)
                .FirstOrDefaultAsync(item => item.Id == request.SourceTakeId.Value, cancellationToken);
            if (take is null || take.MovieShot.Scene.MovieProjectId != revision.Timeline.MovieProjectId)
                throw SourceInvalid("The selected take does not belong to this movie project.");
            if (!(take.SelectedAt.HasValue || take.MovieShot.SelectedTakeId == take.Id || take.MovieShot.FinalTakeId == take.Id)
                || take.Status is not (MovieTakeStatuses.Approved or MovieTakeStatuses.Selected))
                throw SourceInvalid("Only an approved selected or finalized take can be placed on the timeline.");
            var asset = take.Asset ?? take.MovieClip?.Asset;
            if (asset is null || asset.WorkspaceId != workspaceId || asset.ProjectId.HasValue && asset.ProjectId != projectId || !MovieTimelineSourceRules.IsApprovedAsset(asset)
                || !string.Equals(asset.AssetType, AssetTypes.Video, StringComparison.OrdinalIgnoreCase))
                throw SourceInvalid("The selected take must have an approved active video asset.");
            sourceTakeId = take.Id;
            sourceAssetId = asset.Id;
            sourceDuration = await ResolveTakeDurationAsync(take, asset, cancellationToken);
        }
        else if (kind is MovieTimelineItemKinds.AudioAsset or MovieTimelineItemKinds.CaptionAsset)
        {
            if (!request.SourceAssetId.HasValue) throw SourceInvalid("An audio or caption timeline item must reference an asset.");
            var asset = await db.Assets.FirstOrDefaultAsync(item => item.Id == request.SourceAssetId.Value && item.WorkspaceId == workspaceId, cancellationToken);
            if (asset is null || asset.ProjectId.HasValue && asset.ProjectId != projectId || !MovieTimelineSourceRules.IsApprovedAsset(asset)) throw SourceInvalid("The source asset must be active and approved in this movie project.");
            if (kind == MovieTimelineItemKinds.AudioAsset && !MovieTimelineSourceRules.IsAudioAsset(asset))
                throw SourceInvalid("The source asset is not an approved audio asset.");
            if (kind == MovieTimelineItemKinds.CaptionAsset && !MovieTimelineSourceRules.IsCaptionAsset(asset))
                throw SourceInvalid("The source asset is not a supported caption file.");
            sourceAssetId = asset.Id;
            sourceDuration = MovieTimelineSourceRules.TryReadDurationMilliseconds(asset, out var assetDuration) ? assetDuration : 0;
        }

        if (kind != MovieTimelineItemKinds.Gap)
        {
            sourceIn = request.SourceInMilliseconds ?? 0;
            sourceOut = request.SourceOutMilliseconds ?? (sourceDuration > 0 ? sourceDuration : null);
            if (!sourceOut.HasValue)
                throw SourceInvalid("The source duration is required to derive a deterministic out-point.");
            if (sourceOut <= sourceIn || (sourceDuration > 0 && sourceOut > sourceDuration))
                throw Invalid("Source in/out points must be positive, ordered, and within the source duration.");
        }

        var duration = kind == MovieTimelineItemKinds.Gap
            ? request.TimelineOutMilliseconds!.Value - request.TimelineInMilliseconds
            : sourceOut!.Value - sourceIn!.Value;
        var timelineOut = request.TimelineOutMilliseconds ?? checked(request.TimelineInMilliseconds + duration);
        if (timelineOut <= request.TimelineInMilliseconds || timelineOut - request.TimelineInMilliseconds != duration)
            throw Invalid("Timeline in/out points must match the trimmed source duration.");
        return new MovieTimelineItem
        {
            Id = Guid.NewGuid(), MovieTimelineTrackId = track.Id, Sequence = track.Items.Count + 1,
            Kind = kind, SourceTakeId = sourceTakeId, SourceAssetId = sourceAssetId,
            TimelineInMilliseconds = request.TimelineInMilliseconds, TimelineOutMilliseconds = timelineOut,
            SourceInMilliseconds = sourceIn, SourceOutMilliseconds = sourceOut, DurationMilliseconds = duration,
            Label = Clean(request.Label), MetadataJson = request.MetadataJson?.Trim(), CreatedAt = now, UpdatedAt = now,
        };
    }

    private async Task<int> ResolveTakeDurationAsync(MovieTake take, Asset asset, CancellationToken cancellationToken)
    {
        if (MovieTimelineSourceRules.TryReadDurationMilliseconds(asset, out var assetDuration)) return assetDuration;
        if (take.MovieClip?.DurationSeconds is > 0) return checked(take.MovieClip.DurationSeconds.Value * 1000);
        if (take.MetadataJson is not null && TryReadDuration(take.MetadataJson, out var takeDuration)) return takeDuration;
        var shotDuration = await db.MovieShots.Where(item => item.Id == take.MovieShotId).Select(item => item.DurationSeconds).SingleAsync(cancellationToken);
        if (shotDuration is > 0) return checked(shotDuration.Value * 1000);
        throw SourceInvalid("The selected take has no trusted duration metadata.");
    }

    private static bool TryReadDuration(string json, out int duration)
    {
        duration = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("durationMilliseconds", out var milliseconds) && milliseconds.TryGetInt32(out duration) && duration > 0) return true;
            if (root.TryGetProperty("durationSeconds", out var seconds) && seconds.TryGetDouble(out var value) && value > 0 && value * 1000 <= int.MaxValue)
            {
                duration = checked((int)Math.Round(value * 1000, MidpointRounding.AwayFromZero));
                return duration > 0;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static void ValidateRevision(MovieTimelineRevision revision)
    {
        foreach (var track in revision.Tracks) ValidateNoOverlaps(track.Items);
        revision.DurationMilliseconds = CalculateDuration(revision);
    }

    private static void ValidateNoOverlaps(IEnumerable<MovieTimelineItem> items)
    {
        var ordered = items.OrderBy(item => item.TimelineInMilliseconds).ThenBy(item => item.Sequence).ToArray();
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index].TimelineInMilliseconds < ordered[index - 1].TimelineOutMilliseconds)
                throw Invalid("Items may overlap across tracks, but not within the same track.");
    }

    private static int CalculateDuration(MovieTimelineRevision revision) =>
        revision.Tracks.SelectMany(item => item.Items).Select(item => item.TimelineOutMilliseconds).DefaultIfEmpty(0).Max();

    private static void CloneTrack(MovieTimelineRevision revision, MovieTimelineTrack source, DateTime now)
    {
        var track = new MovieTimelineTrack
        {
            Id = Guid.NewGuid(), MovieTimelineRevisionId = revision.Id, TrackNumber = source.TrackNumber,
            Kind = source.Kind, Name = source.Name, IsMuted = source.IsMuted, CreatedAt = now, UpdatedAt = now,
        };
        foreach (var sourceItem in source.Items.OrderBy(item => item.Sequence))
        {
            track.Items.Add(new MovieTimelineItem
            {
                Id = Guid.NewGuid(), MovieTimelineTrackId = track.Id, Sequence = sourceItem.Sequence,
                Kind = sourceItem.Kind, SourceTakeId = sourceItem.SourceTakeId, SourceAssetId = sourceItem.SourceAssetId,
                TimelineInMilliseconds = sourceItem.TimelineInMilliseconds, TimelineOutMilliseconds = sourceItem.TimelineOutMilliseconds,
                SourceInMilliseconds = sourceItem.SourceInMilliseconds, SourceOutMilliseconds = sourceItem.SourceOutMilliseconds,
                DurationMilliseconds = sourceItem.DurationMilliseconds, Label = sourceItem.Label, MetadataJson = sourceItem.MetadataJson,
                CreatedAt = now, UpdatedAt = now,
            });
        }
        revision.Tracks.Add(track);
    }

    private async Task<bool> CanEditAsync(Guid userId, MovieTimelineRevision revision, CancellationToken cancellationToken) =>
        await collaboration.HasPermissionAsync(userId, revision.Timeline.MovieProjectId, MoviePermissions.Edit, cancellationToken);

    private IQueryable<MovieTimeline> QueryTimeline() => db.MovieTimelines.AsNoTracking()
        .Include(item => item.Revisions).ThenInclude(item => item.Tracks).ThenInclude(item => item.Items)
        .Include(item => item.CurrentRevision)
        .Include(item => item.LockedRevision);

    private IQueryable<MovieTimelineRevision> QueryRevision() => db.MovieTimelineRevisions
        .Include(item => item.Timeline)
        .Include(item => item.Tracks).ThenInclude(item => item.Items);

    private static MovieTimelineDto ToDto(MovieTimeline timeline)
    {
        var revisions = timeline.Revisions.OrderBy(item => item.RevisionNumber).Select(ToDto).ToArray();
        return new(timeline.Id, timeline.MovieProjectId, timeline.CurrentRevisionNumber, timeline.CurrentRevisionId,
            timeline.LockedRevisionNumber, timeline.LockedRevisionId, revisions,
            revisions.FirstOrDefault(item => item.Id == timeline.CurrentRevisionId));
    }

    private static MovieTimelineRevisionDto ToDto(MovieTimelineRevision revision) => new(
        revision.Id, revision.Timeline.MovieProjectId, revision.RevisionNumber, revision.BaseRevisionId, revision.Status,
        revision.Label, revision.ChangeSummary, revision.DurationMilliseconds, revision.CreatedAt, revision.UpdatedAt,
        revision.LockedAt, revision.LockedByUserId, revision.Tracks.OrderBy(item => item.TrackNumber).Select(ToDto).ToArray());

    private static MovieTimelineTrackDto ToDto(MovieTimelineTrack track) => new(
        track.Id, track.TrackNumber, track.Kind, track.Name, track.IsMuted,
        track.Items.OrderBy(item => item.TimelineInMilliseconds).ThenBy(item => item.Sequence).Select(ToDto).ToArray());

    private static MovieTimelineItemDto ToDto(MovieTimelineItem item) => new(
        item.Id, item.Sequence, item.Kind, item.SourceTakeId, item.SourceAssetId, item.TimelineInMilliseconds,
        item.TimelineOutMilliseconds, item.SourceInMilliseconds, item.SourceOutMilliseconds, item.DurationMilliseconds,
        item.Label, item.MetadataJson, item.Kind == MovieTimelineItemKinds.Gap);

    private static void Touch(MovieTimelineRevision revision, DateTime now)
    {
        revision.DurationMilliseconds = CalculateDuration(revision);
        revision.UpdatedAt = now;
        revision.Timeline.UpdatedAt = now;
    }

    private static void EnsureDraft(MovieTimelineRevision revision)
    {
        if (revision.Status != MovieTimelineRevisionStatuses.Draft)
            throw new MovieTimelineValidationException(MovieTimelineErrors.Locked, "Locked or superseded timeline revisions are immutable; create a new revision first.");
    }

    private static void ValidateTrackKind(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !MovieTimelineTrackKinds.Supported.Contains(value.Trim()))
            throw Invalid("Choose Video, Audio, or Captions for the track kind.");
    }

    private static string NormalizeTrackKind(string value) =>
        MovieTimelineTrackKinds.Supported.First(item => item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeItemKind(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !MovieTimelineItemKinds.Supported.Contains(value.Trim()))
            throw Invalid("Choose VisualTake, AudioAsset, CaptionAsset, or Gap for the item kind.");
        return MovieTimelineItemKinds.Supported.First(item => item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateKindForTrack(string itemKind, string trackKind)
    {
        if (itemKind == MovieTimelineItemKinds.Gap) return;
        var valid = (itemKind == MovieTimelineItemKinds.VisualTake && trackKind == MovieTimelineTrackKinds.Video)
            || (itemKind == MovieTimelineItemKinds.AudioAsset && trackKind == MovieTimelineTrackKinds.Audio)
            || (itemKind == MovieTimelineItemKinds.CaptionAsset && trackKind == MovieTimelineTrackKinds.Captions);
        if (!valid) throw Invalid("The item kind does not match the track kind.");
    }

    private static void ValidateBounded(string? value, int maximum, string field)
    {
        if (value is not null && value.Trim().Length > maximum) throw Invalid($"{field} must be {maximum} characters or fewer.");
    }

    private static void ValidateMetadata(string? json)
    {
        if (json is null) return;
        if (json.Length > 20_000) throw Invalid("Timeline item metadata must be 20,000 characters or fewer.");
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException) { throw Invalid("Timeline item metadata must be valid JSON."); }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static MovieTimelineValidationException Invalid(string message) => new(MovieTimelineErrors.Invalid, message);
    private static MovieTimelineValidationException SourceInvalid(string message) => new(MovieTimelineErrors.SourceInvalid, message);
}
