using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieMissingInsertProposalTypes
{
    public const string Reaction = "Reaction";
    public const string Detail = "Detail";
    public const string Environment = "Environment";
}

public static class MovieMissingInsertApprovalStatuses
{
    public const string PendingApproval = "PendingApproval";
}

public static class MovieMissingInsertGapKinds
{
    public const string Timeline = "TimelineGap";
}

public sealed record MovieMissingInsertTimelineItemSnapshot(
    Guid Id,
    string Kind,
    Guid? SourceShotId,
    Guid? SourceTakeId,
    int TimelineInMilliseconds,
    int TimelineOutMilliseconds);

public sealed record MovieMissingInsertTimelineTrackSnapshot(
    Guid Id,
    int TrackNumber,
    string Kind,
    bool IsMuted,
    IReadOnlyList<MovieMissingInsertTimelineItemSnapshot> Items);

public sealed record MovieMissingInsertTimelineSnapshot(
    Guid Id,
    int RevisionNumber,
    string Status,
    int DurationMilliseconds,
    IReadOnlyList<MovieMissingInsertTimelineTrackSnapshot> Tracks);

public sealed record MovieMissingInsertStorySnapshot(
    Guid RevisionId,
    int RevisionNumber,
    IReadOnlyDictionary<Guid, Guid> StorySceneIdsByMovieSceneId);

public sealed record MovieMissingInsertGuideSnapshot(Guid RevisionId, int RevisionNumber);

public sealed record MovieMissingInsertShotSnapshot(
    Guid Id,
    Guid SceneId,
    int SceneSequence,
    string SceneTitle,
    int ShotSequence,
    string Description,
    string? Purpose,
    string? Subjects,
    IReadOnlyList<Guid> SubjectCharacterIds,
    string? LocationSet,
    string? ProductionRequirements,
    string? ContinuityReferences,
    string? CameraAndFraming,
    string? CameraMotion,
    string? Dialogue,
    string? VisualContinuityNotes,
    Guid? StorySceneId);

public sealed record MovieMissingInsertProductionKitSnapshot(
    Guid ShotId,
    string PackageHash,
    int SchemaVersion,
    IReadOnlyList<string> CharacterNames,
    IReadOnlyList<string> PropNames);

public sealed record MovieMissingInsertPlannerInput(
    Guid MovieProjectId,
    MovieMissingInsertTimelineSnapshot? Timeline,
    MovieMissingInsertStorySnapshot? ApprovedStory,
    MovieMissingInsertGuideSnapshot? LockedGuide,
    IReadOnlyList<MovieMissingInsertShotSnapshot> Shots,
    IReadOnlyList<MovieMissingInsertProductionKitSnapshot> ProductionKits);

public interface IMovieMissingInsertPlannerService
{
    Task<MovieMissingInsertPlanDto?> GetAsync(Guid userId, Guid movieProjectId, Guid? timelineRevisionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pure coverage analysis. It can only point at existing canonical records and
/// existing references; it never creates a MovieShot, edits a timeline, or calls a
/// media provider.
/// </summary>
public static class MovieMissingInsertPlanner
{
    public const string ContractVersion = "movie-missing-insert.v1";
    private const int MinimumUsefulGapMilliseconds = 250;
    private const int MaximumProposalDurationMilliseconds = 12_000;

    public static MovieMissingInsertPlanDto Build(MovieMissingInsertPlannerInput input, DateTime? assembledAtUtc = null)
    {
        var warnings = new List<MovieMissingInsertWarningDto>();
        var gaps = new List<GapCandidate>();
        var continuityFindings = new List<MovieMissingInsertContinuityFindingDto>();
        var proposals = new List<MovieMissingInsertProposalDto>();
        var shotById = input.Shots.ToDictionary(item => item.Id);
        var kitsByShotId = input.ProductionKits.ToDictionary(item => item.ShotId);
        var timeline = input.Timeline;

        if (timeline is null)
        {
            warnings.Add(new("timeline_missing", "attention", "No canonical timeline revision is available to inspect."));
        }
        else
        {
            var videoTrack = timeline.Tracks
                .Where(item => item.Kind.Equals(MovieTimelineTrackKinds.Video, StringComparison.OrdinalIgnoreCase) && !item.IsMuted)
                .OrderBy(item => item.TrackNumber)
                .ThenBy(item => item.Id)
                .FirstOrDefault();
            if (videoTrack is null)
            {
                if (timeline.DurationMilliseconds > 0)
                    gaps.Add(new GapCandidate(Guid.Empty, 0, timeline.DurationMilliseconds, null, null, null, null, null));
                warnings.Add(new("video_track_missing", "attention", "The selected revision has no active video track."));
            }
            else
            {
                gaps.AddRange(FindTimelineGaps(videoTrack, timeline.DurationMilliseconds));
                FindContinuityBoundaries(videoTrack, shotById, continuityFindings);
            }
        }

        if (gaps.Count == 0 && timeline is not null && timeline.DurationMilliseconds > 0)
            warnings.Add(new("no_timeline_gaps", "info", "The selected canonical video track has continuous coverage."));

        var grounding = new MovieMissingInsertGroundingDto(
            input.ApprovedStory?.RevisionId,
            input.ApprovedStory?.RevisionNumber,
            input.LockedGuide?.RevisionId,
            input.LockedGuide?.RevisionNumber,
            input.ProductionKits
                .OrderBy(item => item.ShotId)
                .Select(item => new MovieMissingInsertProductionKitReferenceDto(item.ShotId, item.PackageHash, item.SchemaVersion))
                .ToArray(),
            input.ApprovedStory is not null && input.LockedGuide is not null && input.ProductionKits.Count > 0);

        if (input.ApprovedStory is null)
            warnings.Add(new("approved_story_missing", "blocking", "Insert proposals are withheld until an approved Story revision is available."));
        if (input.LockedGuide is null)
            warnings.Add(new("locked_guide_missing", "blocking", "Insert proposals are withheld until a locked Movie Guide revision is available."));
        if (input.ProductionKits.Count == 0 && gaps.Count > 0)
            warnings.Add(new("production_kit_missing", "blocking", "Insert proposals are withheld because no existing Production Kit can ground the proposed coverage."));

        foreach (var gap in gaps)
        {
            var dto = ToGapDto(gap, input.Timeline, shotById);
            if (gap.DurationMilliseconds < MinimumUsefulGapMilliseconds)
            {
                warnings.Add(new("gap_too_short", "info", "The uncovered range is below the minimum useful insert duration and was not proposed.", dto.Id));
                continue;
            }

            var anchor = ResolveAnchor(gap, shotById);
            if (anchor is null)
            {
                warnings.Add(new("gap_has_no_anchor", "blocking", "The uncovered range has no existing neighboring shot from which to derive a minimal insert.", dto.Id));
                continue;
            }

            if (input.ApprovedStory is null || input.LockedGuide is null)
                continue;
            if (!input.ApprovedStory.StorySceneIdsByMovieSceneId.TryGetValue(anchor.SceneId, out var storySceneId))
            {
                warnings.Add(new("approved_story_scene_link_missing", "blocking", "The neighboring production scene is not linked to an approved Story screenplay scene; no insert was invented.", dto.Id));
                continue;
            }

            var kit = ResolveKit(gap, kitsByShotId);
            if (kit is null)
            {
                warnings.Add(new("production_kit_missing_for_anchor", "blocking", "The neighboring shot has no Production Kit package to preserve its established references.", dto.Id));
                continue;
            }

            var insertType = SelectInsertType(anchor, kit);
            var duration = Math.Min(gap.DurationMilliseconds, MaximumProposalDurationMilliseconds);
            var proposalId = StableId($"{input.MovieProjectId:N}|{timeline?.Id:N}|{gap.Id:N}|{insertType}|{anchor.Id:N}");
            var storyScene = input.ApprovedStory.StorySceneIdsByMovieSceneId[anchor.SceneId];
            var sourceFields = new List<string> { "neighboring_shot_plan", "approved_story_scene_link", "locked_movie_guide", "production_kit" };
            if (!string.IsNullOrWhiteSpace(anchor.LocationSet)) sourceFields.Add("location_set");
            if (!string.IsNullOrWhiteSpace(anchor.Subjects)) sourceFields.Add("subjects");
            if (kit.PropNames.Count > 0) sourceFields.Add("production_kit_props");

            proposals.Add(new MovieMissingInsertProposalDto(
                proposalId,
                dto.Id,
                MovieMissingInsertApprovalStatuses.PendingApproval,
                RequiresApproval: true,
                ChangesStoryCanon: false,
                insertType,
                anchor.SceneId,
                storyScene,
                anchor.Id,
                duration,
                Description(anchor, insertType),
                "Bridge the existing uncovered edit range with the smallest insert that preserves the approved Story scene and current shot continuity; do not add a new story beat.",
                anchor.Subjects,
                anchor.LocationSet,
                anchor.ProductionRequirements,
                anchor.ContinuityReferences ?? anchor.VisualContinuityNotes,
                anchor.CameraAndFraming,
                anchor.CameraMotion,
                anchor.VisualContinuityNotes,
                new MovieMissingInsertProposalGroundingDto(storyScene, input.ApprovedStory.RevisionId, input.LockedGuide.RevisionId, anchor.Id, kit.PackageHash, sourceFields)));
        }

        var finalTime = assembledAtUtc ?? DateTime.UtcNow;
        return new MovieMissingInsertPlanDto(
            ContractVersion,
            input.MovieProjectId,
            timeline?.Id,
            timeline?.RevisionNumber,
            timeline?.Status ?? "Missing",
            proposals.Count > 0,
            CanonicalTimelineChanged: false,
            GenerationQueued: false,
            grounding,
            gaps.Select(gap => ToGapDto(gap, timeline, shotById)).ToArray(),
            continuityFindings,
            proposals,
            warnings,
            finalTime);
    }

    private static IReadOnlyList<GapCandidate> FindTimelineGaps(MovieMissingInsertTimelineTrackSnapshot track, int durationMilliseconds)
    {
        var ordered = track.Items
            .Where(item => item.TimelineOutMilliseconds > item.TimelineInMilliseconds)
            .OrderBy(item => item.TimelineInMilliseconds)
            .ThenBy(item => item.Id)
            .ToArray();
        var result = new List<GapCandidate>();
        var cursor = 0;
        MovieMissingInsertTimelineItemSnapshot? previous = null;
        foreach (var item in ordered)
        {
            var start = Math.Max(0, item.TimelineInMilliseconds);
            var end = Math.Max(start, item.TimelineOutMilliseconds);
            if (start > cursor)
                result.Add(new GapCandidate(track.Id, cursor, start, previous?.Id, item.Id, previous?.SourceShotId, item.SourceShotId, null));
            if (item.Kind.Equals(MovieTimelineItemKinds.Gap, StringComparison.OrdinalIgnoreCase))
                result.Add(new GapCandidate(track.Id, Math.Max(cursor, start), end, previous?.Id, item.Id, previous?.SourceShotId, item.SourceShotId, null));
            cursor = Math.Max(cursor, end);
            if (!item.Kind.Equals(MovieTimelineItemKinds.Gap, StringComparison.OrdinalIgnoreCase)) previous = item;
        }
        if (durationMilliseconds > cursor)
            result.Add(new GapCandidate(track.Id, cursor, durationMilliseconds, previous?.Id, null, previous?.SourceShotId, null, null));
        return MergeGaps(result);
    }

    private static IReadOnlyList<GapCandidate> MergeGaps(IReadOnlyList<GapCandidate> candidates)
    {
        var merged = new List<GapCandidate>();
        foreach (var candidate in candidates.Where(item => item.DurationMilliseconds > 0).OrderBy(item => item.TimelineInMilliseconds))
        {
            var previous = merged.LastOrDefault();
            if (previous is not null && previous.TrackId == candidate.TrackId && candidate.TimelineInMilliseconds <= previous.TimelineOutMilliseconds)
            {
                merged[^1] = previous with
                {
                    TimelineOutMilliseconds = Math.Max(previous.TimelineOutMilliseconds, candidate.TimelineOutMilliseconds),
                    AfterTimelineItemId = candidate.AfterTimelineItemId ?? previous.AfterTimelineItemId,
                    AfterShotId = candidate.AfterShotId ?? previous.AfterShotId,
                };
            }
            else merged.Add(candidate);
        }
        return merged;
    }

    private static void FindContinuityBoundaries(
        MovieMissingInsertTimelineTrackSnapshot track,
        IReadOnlyDictionary<Guid, MovieMissingInsertShotSnapshot> shots,
        ICollection<MovieMissingInsertContinuityFindingDto> findings)
    {
        var items = track.Items
            .Where(item => !item.Kind.Equals(MovieTimelineItemKinds.Gap, StringComparison.OrdinalIgnoreCase) && item.SourceShotId.HasValue)
            .OrderBy(item => item.TimelineInMilliseconds)
            .ToArray();
        for (var index = 1; index < items.Length; index++)
        {
            if (!shots.TryGetValue(items[index - 1].SourceShotId!.Value, out var before)
                || !shots.TryGetValue(items[index].SourceShotId!.Value, out var after)
                || before.SceneId != after.SceneId)
                continue;
            if (string.IsNullOrWhiteSpace(before.LocationSet) || string.IsNullOrWhiteSpace(after.LocationSet)
                || before.LocationSet.Equals(after.LocationSet, StringComparison.OrdinalIgnoreCase))
                continue;
            findings.Add(new MovieMissingInsertContinuityFindingDto(
                "continuity_location_boundary",
                "review",
                before.Id,
                after.Id,
                before.SceneId,
                $"Adjacent shots in scene '{before.SceneTitle}' name different locations/sets ('{before.LocationSet}' and '{after.LocationSet}').",
                "Confirm this existing coverage boundary against the approved Story scene and locked Guide before approving any insert; no story change was proposed."));
        }
    }

    private static MovieMissingInsertShotSnapshot? ResolveAnchor(GapCandidate gap, IReadOnlyDictionary<Guid, MovieMissingInsertShotSnapshot> shots)
    {
        if (gap.AfterShotId is Guid after && shots.TryGetValue(after, out var afterShot)) return afterShot;
        if (gap.BeforeShotId is Guid before && shots.TryGetValue(before, out var beforeShot)) return beforeShot;
        return null;
    }

    private static MovieMissingInsertProductionKitSnapshot? ResolveKit(GapCandidate gap, IReadOnlyDictionary<Guid, MovieMissingInsertProductionKitSnapshot> kits)
    {
        if (gap.AfterShotId is Guid after && kits.TryGetValue(after, out var afterKit)) return afterKit;
        if (gap.BeforeShotId is Guid before && kits.TryGetValue(before, out var beforeKit)) return beforeKit;
        return null;
    }

    private static string SelectInsertType(MovieMissingInsertShotSnapshot shot, MovieMissingInsertProductionKitSnapshot kit)
    {
        if (shot.SubjectCharacterIds.Count > 0 || !string.IsNullOrWhiteSpace(shot.Subjects) || !string.IsNullOrWhiteSpace(shot.Dialogue))
            return MovieMissingInsertProposalTypes.Reaction;
        if (kit.PropNames.Count > 0 || ContainsAny(shot.Description, "prop", "object", "hand", "detail", "close"))
            return MovieMissingInsertProposalTypes.Detail;
        return MovieMissingInsertProposalTypes.Environment;
    }

    private static string Description(MovieMissingInsertShotSnapshot shot, string insertType) =>
        $"{insertType} insert derived from existing Shot {shot.ShotSequence} in Scene {shot.SceneSequence} ({shot.SceneTitle}); preserve the shot's established subjects, location/set, camera language, and continuity references while covering the edit gap.";

    private static MovieMissingInsertGapDto ToGapDto(
        GapCandidate gap,
        MovieMissingInsertTimelineSnapshot? timeline,
        IReadOnlyDictionary<Guid, MovieMissingInsertShotSnapshot> shots)
    {
        Guid? sceneId = null;
        if (gap.AfterShotId is Guid after && shots.TryGetValue(after, out var afterShot))
            sceneId = afterShot.SceneId;
        else if (gap.BeforeShotId is Guid before && shots.TryGetValue(before, out var beforeShot))
            sceneId = beforeShot.SceneId;
        return new(
            gap.Id,
            MovieMissingInsertGapKinds.Timeline,
            gap.TrackId == Guid.Empty ? null : gap.TrackId,
            gap.TimelineInMilliseconds,
            gap.TimelineOutMilliseconds,
            gap.DurationMilliseconds,
            gap.BeforeTimelineItemId,
            gap.AfterTimelineItemId,
            gap.BeforeShotId,
            gap.AfterShotId,
            sceneId,
            timeline is null ? "Uncovered timeline range" : $"Uncovered range {gap.TimelineInMilliseconds}–{gap.TimelineOutMilliseconds} ms on the canonical video edit.");
    }

    private static bool ContainsAny(string? value, params string[] terms) =>
        terms.Any(term => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);

    private static Guid StableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record GapCandidate(
        Guid TrackId,
        int TimelineInMilliseconds,
        int TimelineOutMilliseconds,
        Guid? BeforeTimelineItemId,
        Guid? AfterTimelineItemId,
        Guid? BeforeShotId,
        Guid? AfterShotId,
        Guid? SceneId)
    {
        public Guid Id => StableId($"{TrackId:N}|{TimelineInMilliseconds}|{TimelineOutMilliseconds}|{BeforeTimelineItemId?.ToString("N")}|{AfterTimelineItemId?.ToString("N")}");
        public int DurationMilliseconds => Math.Max(0, TimelineOutMilliseconds - TimelineInMilliseconds);
    }
}

public sealed class MovieMissingInsertPlannerService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IMovieProductionReferencePackageService productionReferences) : IMovieMissingInsertPlannerService
{
    public async Task<MovieMissingInsertPlanDto?> GetAsync(Guid userId, Guid movieProjectId, Guid? timelineRevisionId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking()
            .Include(item => item.Guide).ThenInclude(item => item.Revisions)
            .SingleOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var timelineEntity = await db.MovieTimelines.AsNoTracking()
            .Include(item => item.Revisions).ThenInclude(item => item.Tracks).ThenInclude(item => item.Items)
            .SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        var revision = timelineEntity is null
            ? null
            : timelineRevisionId.HasValue
                ? timelineEntity.Revisions.FirstOrDefault(item => item.Id == timelineRevisionId.Value)
                : timelineEntity.Revisions.FirstOrDefault(item => item.Id == timelineEntity.CurrentRevisionId)
                    ?? timelineEntity.Revisions.OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
        if (timelineRevisionId.HasValue && revision is null)
            throw new MovieMissingInsertPlannerException("MOVIE_INSERT_PLANNER_REVISION_NOT_FOUND", "The selected timeline revision does not belong to this movie project.");

        var shotEntities = await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene)
            .Where(item => item.Scene.MovieProjectId == movieProjectId)
            .OrderBy(item => item.Scene.Sequence).ThenBy(item => item.Sequence).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var shotById = shotEntities.ToDictionary(item => item.Id);
        var takeIds = revision?.Tracks.SelectMany(item => item.Items).Where(item => item.SourceTakeId.HasValue).Select(item => item.SourceTakeId!.Value).Distinct().ToArray() ?? [];
        var takeShotIds = takeIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await db.MovieTakes.AsNoTracking().Where(item => takeIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.MovieShotId, cancellationToken);

        var story = await BuildApprovedStoryAsync(movieProjectId, cancellationToken);
        var guide = movie.Guide.LockedRevisionNumber is int lockedNumber
            ? movie.Guide.Revisions.FirstOrDefault(item => item.RevisionNumber == lockedNumber && item.Status == MovieGuideRevisionStatuses.Locked) is { } locked
                ? new MovieMissingInsertGuideSnapshot(locked.Id, locked.RevisionNumber)
                : null
            : null;
        var shots = shotEntities.Select(item => new MovieMissingInsertShotSnapshot(
            item.Id, item.MovieSceneId, item.Scene.Sequence, item.Scene.Title, item.Sequence, item.Description, item.Purpose, item.Subjects,
            MovieShotReadiness.ParseSubjectCharacterIds(item.SubjectCharacterIdsJson), item.LocationSet, item.ProductionRequirements, item.ContinuityReferences,
            item.CameraAndFraming, item.CameraMotion, item.Dialogue, item.VisualContinuityNotes,
            story?.StorySceneIdsByMovieSceneId.GetValueOrDefault(item.MovieSceneId))).ToArray();

        var timeline = revision is null
            ? null
            : new MovieMissingInsertTimelineSnapshot(
                revision.Id,
                revision.RevisionNumber,
                revision.Status,
                Math.Max(revision.DurationMilliseconds, revision.Tracks.SelectMany(item => item.Items).Select(item => item.TimelineOutMilliseconds).DefaultIfEmpty(0).Max()),
                revision.Tracks.Select(track => new MovieMissingInsertTimelineTrackSnapshot(
                    track.Id,
                    track.TrackNumber,
                    track.Kind,
                    track.IsMuted,
                    track.Items.Select(item => new MovieMissingInsertTimelineItemSnapshot(
                        item.Id,
                        item.Kind,
                        item.SourceTakeId.HasValue && takeShotIds.TryGetValue(item.SourceTakeId.Value, out var shotId) ? shotId : null,
                        item.SourceTakeId,
                        item.TimelineInMilliseconds,
                        item.TimelineOutMilliseconds)).ToArray())).ToArray());

        var anchorShotIds = timeline?.Tracks
            .Where(item => item.Kind.Equals(MovieTimelineTrackKinds.Video, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Items)
            .Where(item => item.SourceShotId.HasValue)
            .Select(item => item.SourceShotId!.Value)
            .Distinct()
            .Take(MovieProductionReferenceLimits.MaxPreviousShots * 4)
            .ToArray() ?? [];
        var kits = new List<MovieMissingInsertProductionKitSnapshot>();
        foreach (var shotId in anchorShotIds)
        {
            var package = await productionReferences.GetForShotAsync(userId, shotId, cancellationToken);
            if (package is null) continue;
            kits.Add(new MovieMissingInsertProductionKitSnapshot(
                shotId,
                package.PackageHash,
                package.SchemaVersion,
                package.Characters.Select(item => item.Name).Where(item => !string.IsNullOrWhiteSpace(item)).Take(32).ToArray(),
                package.Props.Select(item => item.Name).Where(item => !string.IsNullOrWhiteSpace(item)).Take(32).ToArray()));
        }

        return MovieMissingInsertPlanner.Build(new MovieMissingInsertPlannerInput(movieProjectId, timeline, story, guide, shots, kits));
    }

    private async Task<MovieMissingInsertStorySnapshot?> BuildApprovedStoryAsync(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var pointer = await db.MovieStories.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId)
            .Select(item => new { item.ApprovedRevisionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (pointer?.ApprovedRevisionId is not Guid revisionId) return null;
        var revision = await db.MovieStoryRevisions.AsNoTracking()
            .Include(item => item.Scenes)
            .SingleOrDefaultAsync(item => item.Id == revisionId && item.MovieStory.MovieProjectId == movieProjectId && item.Status == MovieStoryRevisionStatuses.Approved, cancellationToken);
        return revision is null
            ? null
            : new MovieMissingInsertStorySnapshot(
                revision.Id,
                revision.RevisionNumber,
                revision.Scenes.Where(item => item.MovieSceneId.HasValue).GroupBy(item => item.MovieSceneId!.Value).ToDictionary(item => item.Key, item => item.First().Id));
    }
}

public sealed class MovieMissingInsertPlannerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
