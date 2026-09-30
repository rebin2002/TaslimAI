using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieSoundtrackApprovalStates
{
    public const string Draft = "Draft";
    public const string InReview = "InReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Superseded = "Superseded";
    public static readonly IReadOnlySet<string> Reviewable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Draft, InReview };
}

public static class MovieSoundtrackDuckingTargets
{
    public const string Dialogue = "dialogue";
    public const string Narration = "narration";
    public const string SoundEffects = "sound_effects";
    public const string Ambience = "ambience";
    public const string Master = "master";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Dialogue, Narration, SoundEffects, Ambience, Master,
    };
}

public sealed class MovieSoundtrackCue
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid MovieActId { get; set; }
    public Guid MovieSceneId { get; set; }
    public int Sequence { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? NarrativeIntent { get; set; }
    public string Mood { get; set; } = "neutral";
    public int Intensity { get; set; }
    public decimal ActStartSeconds { get; set; }
    public decimal SceneStartSeconds { get; set; }
    public decimal TimelineStartSeconds { get; set; }
    public decimal DurationSeconds { get; set; }
    public string ApprovalState { get; set; } = MovieSoundtrackApprovalStates.Draft;
    public Guid? ApprovedVersionId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieAct MovieAct { get; set; } = null!;
    public MovieScene MovieScene { get; set; } = null!;
    public ICollection<MovieSoundtrackCueVersion> Versions { get; set; } = [];
    public ICollection<MovieSoundtrackDuckingIntent> DuckingIntents { get; set; } = [];
}

public sealed class MovieSoundtrackCueVersion
{
    public Guid Id { get; set; }
    public Guid MovieSoundtrackCueId { get; set; }
    public int VersionNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? ArrangementIntent { get; set; }
    public string Mood { get; set; } = "neutral";
    public int Intensity { get; set; }
    public Guid? AssetId { get; set; }
    public string ApprovalState { get; set; } = MovieSoundtrackApprovalStates.Draft;
    public string? ReviewNote { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieSoundtrackCue Cue { get; set; } = null!;
    public Asset? Asset { get; set; }
    public MovieSoundtrackAudioAssetProvenance? AudioAssetProvenance { get; set; }
    public ICollection<MovieSoundtrackCueVersionReview> Reviews { get; set; } = [];
}

public sealed class MovieSoundtrackCueVersionReview
{
    public Guid Id { get; set; }
    public Guid MovieSoundtrackCueVersionId { get; set; }
    public string Decision { get; set; } = MovieSoundtrackApprovalStates.Rejected;
    public string? Comment { get; set; }
    public Guid ReviewedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieSoundtrackCueVersion Version { get; set; } = null!;
    public ApplicationUser ReviewedByUser { get; set; } = null!;
}

public sealed class MovieSoundtrackAudioAssetProvenance
{
    public Guid Id { get; set; }
    public Guid MovieSoundtrackCueVersionId { get; set; }
    public Guid AssetId { get; set; }
    public Guid StoredFileId { get; set; }
    public Guid? SourceGenerationJobId { get; set; }
    public string AssetType { get; set; } = AssetTypes.Audio;
    public string MimeType { get; set; } = "audio/mpeg";
    public long SizeBytes { get; set; }
    public string? AssetMetadataJson { get; set; }
    public DateTime CapturedAt { get; set; }
    public MovieSoundtrackCueVersion CueVersion { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public StoredFile StoredFile { get; set; } = null!;
}

public sealed class MovieSoundtrackDuckingIntent
{
    public Guid Id { get; set; }
    public Guid MovieSoundtrackCueId { get; set; }
    public string TargetLane { get; set; } = MovieSoundtrackDuckingTargets.Dialogue;
    public decimal StartOffsetSeconds { get; set; }
    public decimal EndOffsetSeconds { get; set; }
    public decimal DuckDecibels { get; set; }
    public int AttackMilliseconds { get; set; }
    public int ReleaseMilliseconds { get; set; }
    public string? Rationale { get; set; }
    public MovieSoundtrackCue Cue { get; set; } = null!;
}

public sealed record MovieSoundtrackMediaRequest(
    Guid MovieProjectId,
    Guid MovieSoundtrackCueId,
    Guid MovieSoundtrackCueVersionId,
    string Title,
    string Mood,
    int Intensity,
    decimal DurationSeconds,
    string? ArrangementIntent);

public sealed record MovieSoundtrackMediaSubmission(string OperationId);

public interface IMovieSoundtrackMediaService
{
    bool IsAvailable { get; }
    IReadOnlyCollection<string> SupportedOperations { get; }
    Task<MovieSoundtrackMediaSubmission> SubmitAsync(MovieSoundtrackMediaRequest request, CancellationToken cancellationToken);
}

public sealed class UnavailableMovieSoundtrackMediaService : IMovieSoundtrackMediaService
{
    public bool IsAvailable => false;
    public IReadOnlyCollection<string> SupportedOperations { get; } = [];
    public Task<MovieSoundtrackMediaSubmission> SubmitAsync(MovieSoundtrackMediaRequest request, CancellationToken cancellationToken) =>
        Task.FromException<MovieSoundtrackMediaSubmission>(new MovieSoundtrackMediaUnavailableException());
}

public sealed class MovieSoundtrackMediaUnavailableException() : Exception("No movie soundtrack media service is configured.");

public sealed class MovieSoundtrackValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IMovieSoundtrackService
{
    Task<MovieSoundtrackDto?> GetProjectAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieSoundtrackCueDto?> GetCueAsync(Guid userId, Guid cueId, CancellationToken cancellationToken);
    Task<MovieSoundtrackCueDto?> CreateCueAsync(Guid userId, Guid movieProjectId, MovieSoundtrackCueRequest request, CancellationToken cancellationToken);
    Task<MovieSoundtrackCueDto?> UpdateCueAsync(Guid userId, Guid cueId, MovieSoundtrackCueUpdateRequest request, CancellationToken cancellationToken);
    Task<MovieSoundtrackCueDto?> CreateVersionAsync(Guid userId, Guid cueId, MovieSoundtrackCueVersionRequest request, CancellationToken cancellationToken);
    Task<MovieSoundtrackCueDto?> ReviewVersionAsync(Guid userId, Guid versionId, MovieSoundtrackCueVersionReviewRequest request, CancellationToken cancellationToken);
}

public sealed class MovieSoundtrackService(
    TaslimDbContext db,
    MovieAuthorizationService authorization,
    IMovieSoundtrackMediaService mediaService) : IMovieSoundtrackService
{
    public async Task<MovieSoundtrackDto?> GetProjectAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var project = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await authorization.CanPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var cues = await CueQuery()
            .Where(item => item.MovieProjectId == movieProjectId)
            .OrderBy(item => item.TimelineStartSeconds)
            .ThenBy(item => item.Sequence)
            .ToListAsync(cancellationToken);
        return new MovieSoundtrackDto(movieProjectId, mediaService.IsAvailable, cues.Select(ToDto).ToArray());
    }

    public async Task<MovieSoundtrackCueDto?> CreateCueAsync(Guid userId, Guid movieProjectId, MovieSoundtrackCueRequest request, CancellationToken cancellationToken)
    {
        await authorization.RequireAsync(userId, movieProjectId, MovieOperationalActions.SoundtrackEdit, cancellationToken);
        var project = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null) return null;
        var scene = await SceneQuery().FirstOrDefaultAsync(item => item.Id == request.MovieSceneId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (scene is null) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_SCENE_NOT_FOUND", "The soundtrack scene must belong to this movie project.");
        var actId = scene.MovieSequence?.MovieActId ?? request.MovieActId;
        if (!actId.HasValue || !await db.MovieActs.AnyAsync(item => item.Id == actId.Value && item.MovieProjectId == movieProjectId, cancellationToken))
            throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_ACT_REQUIRED", "A cue must reference an act in the movie hierarchy.");
        if (scene.MovieSequence?.MovieActId is Guid hierarchyActId && request.MovieActId.HasValue && request.MovieActId.Value != hierarchyActId)
            throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_HIERARCHY_MISMATCH", "The selected act does not own the selected scene.");

        ValidateCue(request.Title, request.Mood, request.Intensity, request.ActStartSeconds, request.SceneStartSeconds, request.TimelineStartSeconds, request.DurationSeconds, project.DurationSeconds, scene.DurationSeconds);
        var now = DateTime.UtcNow;
        var cue = new MovieSoundtrackCue
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, MovieActId = actId.Value, MovieSceneId = scene.Id,
            Sequence = (await db.MovieSoundtrackCues.Where(item => item.MovieProjectId == movieProjectId).MaxAsync(item => (int?)item.Sequence, cancellationToken) ?? 0) + 1,
            Title = request.Title.Trim(), NarrativeIntent = Clean(request.NarrativeIntent), Mood = request.Mood.Trim().ToLowerInvariant(), Intensity = request.Intensity,
            ActStartSeconds = request.ActStartSeconds, SceneStartSeconds = request.SceneStartSeconds, TimelineStartSeconds = request.TimelineStartSeconds,
            DurationSeconds = request.DurationSeconds, ApprovalState = MovieSoundtrackApprovalStates.Draft, CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        };
        cue.DuckingIntents = BuildDuckingIntents(cue.Id, request.DuckingIntents, cue.DurationSeconds);
        db.MovieSoundtrackCues.Add(cue);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(cue);
    }

    public async Task<MovieSoundtrackCueDto?> UpdateCueAsync(Guid userId, Guid cueId, MovieSoundtrackCueUpdateRequest request, CancellationToken cancellationToken)
    {
        var cue = await CueQuery(tracking: true).FirstOrDefaultAsync(item => item.Id == cueId, cancellationToken);
        if (cue is null) return null;
        await authorization.RequireAsync(userId, cue.MovieProjectId, MovieOperationalActions.SoundtrackEdit, cancellationToken);
        var project = cue.MovieProject;
        var scene = cue.MovieScene;
        var title = request.Title?.Trim() ?? cue.Title;
        var mood = request.Mood?.Trim().ToLowerInvariant() ?? cue.Mood;
        var intensity = request.Intensity ?? cue.Intensity;
        var actStart = request.ActStartSeconds ?? cue.ActStartSeconds;
        var sceneStart = request.SceneStartSeconds ?? cue.SceneStartSeconds;
        var timelineStart = request.TimelineStartSeconds ?? cue.TimelineStartSeconds;
        var duration = request.DurationSeconds ?? cue.DurationSeconds;
        ValidateCue(title, mood, intensity, actStart, sceneStart, timelineStart, duration, project.DurationSeconds, scene.DurationSeconds);
        cue.Title = title; cue.NarrativeIntent = request.NarrativeIntent is null ? cue.NarrativeIntent : Clean(request.NarrativeIntent);
        cue.Mood = mood; cue.Intensity = intensity; cue.ActStartSeconds = actStart; cue.SceneStartSeconds = sceneStart; cue.TimelineStartSeconds = timelineStart; cue.DurationSeconds = duration;
        if (request.DuckingIntents is not null)
        {
            db.MovieSoundtrackDuckingIntents.RemoveRange(cue.DuckingIntents);
            cue.DuckingIntents = BuildDuckingIntents(cue.Id, request.DuckingIntents, duration);
        }
        cue.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(cue);
    }

    public async Task<MovieSoundtrackCueDto?> CreateVersionAsync(Guid userId, Guid cueId, MovieSoundtrackCueVersionRequest request, CancellationToken cancellationToken)
    {
        var cue = await CueQuery(tracking: true).FirstOrDefaultAsync(item => item.Id == cueId, cancellationToken);
        if (cue is null) return null;
        await authorization.RequireAsync(userId, cue.MovieProjectId, MovieOperationalActions.SoundtrackEdit, cancellationToken);
        ValidateVersion(request.Label, request.Mood ?? cue.Mood, request.Intensity ?? cue.Intensity, request.ArrangementIntent);
        var asset = request.AssetId.HasValue ? await LoadAudioAssetAsync(request.AssetId.Value, cue.MovieProject.WorkspaceId, cancellationToken) : null;
        var now = DateTime.UtcNow;
        var version = new MovieSoundtrackCueVersion
        {
            Id = Guid.NewGuid(), MovieSoundtrackCueId = cue.Id,
            VersionNumber = (await db.MovieSoundtrackCueVersions.Where(item => item.MovieSoundtrackCueId == cue.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1,
            Label = request.Label.Trim(), ArrangementIntent = Clean(request.ArrangementIntent), Mood = (request.Mood ?? cue.Mood).Trim().ToLowerInvariant(),
            Intensity = request.Intensity ?? cue.Intensity, AssetId = asset?.Id, ApprovalState = MovieSoundtrackApprovalStates.Draft,
            CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        };
        if (asset is not null) version.AudioAssetProvenance = BuildProvenance(version.Id, asset, now);
        cue.ApprovalState = MovieSoundtrackApprovalStates.InReview;
        cue.UpdatedAt = now;
        db.MovieSoundtrackCueVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadCueDtoAsync(cue.Id, cancellationToken);
    }

    public async Task<MovieSoundtrackCueDto?> ReviewVersionAsync(Guid userId, Guid versionId, MovieSoundtrackCueVersionReviewRequest request, CancellationToken cancellationToken)
    {
        var version = await db.MovieSoundtrackCueVersions
            .Include(item => item.Cue).ThenInclude(item => item.MovieProject)
            .Include(item => item.AudioAssetProvenance)
            .FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null) return null;
        await authorization.RequireAsync(userId, version.Cue.MovieProjectId, MovieOperationalActions.SoundtrackApproval, cancellationToken);
        var decision = request.Decision.Trim();
        if (!string.Equals(decision, MovieSoundtrackApprovalStates.Approved, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(decision, MovieSoundtrackApprovalStates.Rejected, StringComparison.OrdinalIgnoreCase))
            throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_REVIEW_INVALID", "Decision must be Approved or Rejected.");
        if (!MovieSoundtrackApprovalStates.Reviewable.Contains(version.ApprovalState))
            throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_VERSION_NOT_REVIEWABLE", "Only draft or in-review cue versions can be reviewed.");
        if (request.Comment?.Length > 4_000) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_REVIEW_INVALID", "Review comments must be 4,000 characters or fewer.");
        if (string.Equals(decision, MovieSoundtrackApprovalStates.Approved, StringComparison.OrdinalIgnoreCase) && (!version.AssetId.HasValue || version.AudioAssetProvenance is null))
            throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_AUDIO_ASSET_REQUIRED", "An approved cue version must reference a ready audio asset with provenance.");

        var now = DateTime.UtcNow;
        if (string.Equals(decision, MovieSoundtrackApprovalStates.Approved, StringComparison.OrdinalIgnoreCase))
        {
            var previous = await db.MovieSoundtrackCueVersions.Where(item => item.MovieSoundtrackCueId == version.MovieSoundtrackCueId && item.Id != version.Id && item.ApprovalState == MovieSoundtrackApprovalStates.Approved).ToListAsync(cancellationToken);
            foreach (var item in previous) { item.ApprovalState = MovieSoundtrackApprovalStates.Superseded; item.UpdatedAt = now; }
            version.Cue.ApprovedVersionId = version.Id;
            version.Cue.ApprovalState = MovieSoundtrackApprovalStates.Approved;
        }
        else
        {
            version.Cue.ApprovalState = version.Cue.ApprovedVersionId.HasValue ? MovieSoundtrackApprovalStates.Approved : MovieSoundtrackApprovalStates.Rejected;
        }
        version.ApprovalState = decision.Equals(MovieSoundtrackApprovalStates.Approved, StringComparison.OrdinalIgnoreCase) ? MovieSoundtrackApprovalStates.Approved : MovieSoundtrackApprovalStates.Rejected;
        version.ReviewNote = Clean(request.Comment); version.ReviewedByUserId = userId; version.ReviewedAt = now; version.UpdatedAt = now; version.Cue.UpdatedAt = now;
        db.MovieSoundtrackCueVersionReviews.Add(new MovieSoundtrackCueVersionReview { Id = Guid.NewGuid(), MovieSoundtrackCueVersionId = version.Id, Decision = version.ApprovalState, Comment = version.ReviewNote, ReviewedByUserId = userId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return await LoadCueDtoAsync(version.MovieSoundtrackCueId, cancellationToken);
    }

    public async Task<MovieSoundtrackCueDto?> GetCueAsync(Guid userId, Guid cueId, CancellationToken cancellationToken)
    {
        var cue = await db.MovieSoundtrackCues.AsNoTracking().FirstOrDefaultAsync(item => item.Id == cueId, cancellationToken);
        if (cue is null || !await authorization.CanPermissionAsync(userId, cue.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return await LoadCueDtoAsync(cueId, cancellationToken);
    }

    private async Task<MovieSoundtrackCueDto?> LoadCueDtoAsync(Guid cueId, CancellationToken cancellationToken)
    {
        var cue = await CueQuery().FirstOrDefaultAsync(item => item.Id == cueId, cancellationToken);
        return cue is null ? null : ToDto(cue);
    }

    private IQueryable<MovieSoundtrackCue> CueQuery(bool tracking = false)
    {
        var query = db.MovieSoundtrackCues
            .Include(item => item.MovieProject)
            .Include(item => item.MovieScene).ThenInclude(item => item.MovieSequence).ThenInclude(item => item!.MovieAct)
            .Include(item => item.DuckingIntents)
            .Include(item => item.Versions).ThenInclude(item => item.Asset)
            .Include(item => item.Versions).ThenInclude(item => item.AudioAssetProvenance)
            .Include(item => item.Versions).ThenInclude(item => item.Reviews)
            .AsQueryable();
        return tracking ? query : query.AsNoTracking();
    }

    private IQueryable<MovieScene> SceneQuery() => db.MovieScenes.Include(item => item.MovieProject).Include(item => item.MovieSequence).ThenInclude(item => item!.MovieAct);

    private async Task<Asset> LoadAudioAssetAsync(Guid assetId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.Include(item => item.StoredFile).FirstOrDefaultAsync(item => item.Id == assetId && item.WorkspaceId == workspaceId, cancellationToken);
        if (asset is null) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_ASSET_NOT_FOUND", "The audio asset was not found in this workspace.");
        if (asset.Status != AssetStatus.Active || (!string.Equals(asset.AssetType, AssetTypes.Audio, StringComparison.OrdinalIgnoreCase) && !string.Equals(asset.AssetType, AssetTypes.Music, StringComparison.OrdinalIgnoreCase)))
            throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_ASSET_INVALID", "The cue asset must be an active audio or music asset.");
        if (!asset.StoredFileId.HasValue || asset.StoredFile is null || asset.StoredFile.Status != StoredFileStatus.Ready || !asset.StoredFile.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_ASSET_NOT_READY", "The cue asset must have a ready private audio file.");
        return asset;
    }

    private static MovieSoundtrackAudioAssetProvenance BuildProvenance(Guid versionId, Asset asset, DateTime now) => new()
    {
        Id = Guid.NewGuid(), MovieSoundtrackCueVersionId = versionId, AssetId = asset.Id, StoredFileId = asset.StoredFileId!.Value,
        SourceGenerationJobId = asset.SourceGenerationJobId, AssetType = asset.AssetType, MimeType = asset.MimeType ?? asset.StoredFile!.ContentType,
        SizeBytes = asset.StoredFile!.SizeBytes, AssetMetadataJson = Bound(asset.MetadataJson, 4_000), CapturedAt = now,
    };

    private static List<MovieSoundtrackDuckingIntent> BuildDuckingIntents(Guid cueId, IReadOnlyList<MovieSoundtrackDuckingIntentRequest>? requests, decimal duration)
    {
        if (requests is null) return [];
        if (requests.Count > 32) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_DUCKING_INVALID", "A cue can contain at most 32 ducking intents.");
        return requests.Select(request =>
        {
            var target = request.TargetLane.Trim().ToLowerInvariant();
            if (!MovieSoundtrackDuckingTargets.Supported.Contains(target)) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_DUCKING_INVALID", "The ducking target lane is not supported.");
            if (request.StartOffsetSeconds < 0 || request.EndOffsetSeconds <= request.StartOffsetSeconds || request.EndOffsetSeconds > duration)
                throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_DUCKING_INVALID", "Ducking offsets must be within the cue duration.");
            if (request.DuckDecibels <= 0 || request.DuckDecibels > 24 || request.AttackMilliseconds < 0 || request.AttackMilliseconds > 10_000 || request.ReleaseMilliseconds < 0 || request.ReleaseMilliseconds > 10_000)
                throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_DUCKING_INVALID", "Ducking levels and envelope values are outside the supported bounds.");
            return new MovieSoundtrackDuckingIntent
            {
                Id = Guid.NewGuid(), MovieSoundtrackCueId = cueId, TargetLane = target, StartOffsetSeconds = request.StartOffsetSeconds,
                EndOffsetSeconds = request.EndOffsetSeconds, DuckDecibels = request.DuckDecibels, AttackMilliseconds = request.AttackMilliseconds,
                ReleaseMilliseconds = request.ReleaseMilliseconds, Rationale = Clean(request.Rationale),
            };
        }).ToList();
    }

    private static void ValidateCue(string title, string mood, int intensity, decimal actStart, decimal sceneStart, decimal timelineStart, decimal duration, int projectDuration, int? sceneDuration)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 160) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_CUE_INVALID", "Cue title is required and must be 160 characters or fewer.");
        if (string.IsNullOrWhiteSpace(mood) || mood.Trim().Length > 80) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_CUE_INVALID", "Cue mood is required and must be 80 characters or fewer.");
        if (intensity is < 0 or > 100) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_CUE_INVALID", "Cue intensity must be between 0 and 100.");
        if (actStart < 0 || sceneStart < 0 || timelineStart < 0 || duration <= 0 || duration > 3_600) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_TIMING_INVALID", "Cue timing must use non-negative bounded values and a positive duration.");
        if (projectDuration > 0 && timelineStart + duration > projectDuration) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_TIMING_INVALID", "The cue must fit inside the movie timeline.");
        if (sceneDuration.HasValue && sceneStart + duration > sceneDuration.Value) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_TIMING_INVALID", "The cue must fit inside the selected scene.");
    }

    private static void ValidateVersion(string? label, string mood, int intensity, string? arrangementIntent)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 160) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_VERSION_INVALID", "Version label is required and must be 160 characters or fewer.");
        if (string.IsNullOrWhiteSpace(mood) || mood.Trim().Length > 80 || intensity is < 0 or > 100) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_VERSION_INVALID", "Version mood and intensity are invalid.");
        if (arrangementIntent?.Length > 4_000) throw new MovieSoundtrackValidationException("MOVIE_SOUNDTRACK_VERSION_INVALID", "Arrangement intent must be 4,000 characters or fewer.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Bound(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    private static MovieSoundtrackCueDto ToDto(MovieSoundtrackCue cue) => new(
        cue.Id, cue.MovieProjectId, cue.MovieActId, cue.MovieSceneId, cue.Sequence, cue.Title, cue.NarrativeIntent, cue.Mood, cue.Intensity,
        cue.ActStartSeconds, cue.SceneStartSeconds, cue.TimelineStartSeconds, cue.DurationSeconds, cue.ApprovalState, cue.ApprovedVersionId,
        cue.CreatedAt, cue.UpdatedAt, cue.DuckingIntents.OrderBy(item => item.StartOffsetSeconds).Select(item => new MovieSoundtrackDuckingIntentDto(item.Id, item.TargetLane, item.StartOffsetSeconds, item.EndOffsetSeconds, item.DuckDecibels, item.AttackMilliseconds, item.ReleaseMilliseconds, item.Rationale)).ToArray(),
        cue.Versions.OrderByDescending(item => item.VersionNumber).Select(item => new MovieSoundtrackCueVersionDto(item.Id, item.VersionNumber, item.Label, item.ArrangementIntent, item.Mood, item.Intensity, item.AssetId, item.ApprovalState, item.ReviewNote, item.CreatedByUserId, item.ReviewedByUserId, item.ReviewedAt, item.CreatedAt, item.UpdatedAt,
            item.AudioAssetProvenance is null ? null : new MovieSoundtrackAudioAssetProvenanceDto(item.AudioAssetProvenance.AssetId, item.AudioAssetProvenance.StoredFileId, item.AudioAssetProvenance.AssetType, item.AudioAssetProvenance.MimeType, item.AudioAssetProvenance.SizeBytes, item.AudioAssetProvenance.SourceGenerationJobId, item.AudioAssetProvenance.CapturedAt),
            item.Reviews.OrderByDescending(review => review.CreatedAt).Select(review => new MovieSoundtrackCueVersionReviewDto(review.Id, review.Decision, review.Comment, review.ReviewedByUserId, review.CreatedAt)).ToArray())).ToArray());
}
