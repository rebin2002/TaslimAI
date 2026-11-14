using Taslim.Api.Domain;
using Taslim.Api.Files;

namespace Taslim.Api.Movies;

/// <summary>
/// Provider-free source-metadata read model for AI Video Makeover (issue #698 slice).
/// Issue #811 recorded that the browser read models carry no source owner, visibility,
/// retention, frame dimensions, frame-rate or audio state, so safe attach could not be
/// completed from the client. This module computes the server-side source descriptor
/// from persisted authoritative columns only (<see cref="StoredFile"/>, <see cref="Asset"/>,
/// <see cref="MovieClip"/>), reports each fact as verified or unverified, and never
/// invents values that the schema does not carry. Frame rate and audio state are not
/// persisted anywhere on the media rows today, so they are always surfaced as explicit
/// pending facts, matching the #775 draft-admission vocabulary (#811's
/// "closing it needs a server-side source descriptor, not a client-side guess").
/// </summary>
public static class MovieVideoMakeoverSourceCodes
{
    /// <summary>Read-model marker sent on the response header.</summary>
    public const string ReadModel = "makeover-source-v1";

    // Structural blockers. These names follow the #811 source-catalogue blocker
    // vocabulary so both sides of the seam agree on one code per cause.
    public const string AssetMissing = "source_asset_missing";
    public const string AssetNotFound = "source_reference_invalid";
    public const string FileMissing = "source_file_missing";
    public const string AssetUnavailable = "source_asset_unavailable";
    public const string NotVideo = "source_asset_not_video";
    public const string ClipNotReady = "source_clip_not_ready";

    // Fact failures that map 1:1 onto #775 draft-admission block codes.
    public const string OwnershipUnverified = "source_ownership_unverified";
    public const string VisibilityNotPrivate = "source_visibility_not_private";
    public const string RetentionUnverified = "source_retention_unverified";
    public const string MetadataMissing = "source_metadata_missing";
    public const string MetadataOutOfBounds = "source_metadata_out_of_bounds";

    // Pending facts: never persisted by the schema, therefore never claimed.
    public const string PendingFrameRate = "frame_rate";
    public const string PendingAudioState = "audio_state";
}

/// <summary>
/// Bounds mirrored from #775's MOVIE_VIDEO_MAKEOVER_INPUT_LIMITS
/// (minDurationSeconds 1, maxDurationSeconds 3600, minDimension 1, maxDimension 16384,
/// maxSourceBytes 5368709120). Server and draft admission must not diverge; if #775
/// changes its limits this table changes with it in the same review.
/// </summary>
public static class MovieVideoMakeoverSourceLimits
{
    public const double MinDurationSeconds = 1;
    public const double MaxDurationSeconds = 3600;
    public const int MinDimension = 1;
    public const int MaxDimension = 16384;
    public const long MaxSourceBytes = 5_368_709_120;
}

public sealed record MovieVideoMakeoverSourceDto(
    Guid ClipId,
    Guid MovieProjectId,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    string ClipStatus,
    Guid? AssetId,
    string? AssetType,
    string? AssetStatus,
    Guid? StoredFileId,
    string? ContentType,
    long? SizeBytes,
    double? DurationSeconds,
    int? Width,
    int? Height,
    // The schema carries no persisted, measured frame rate or audio state for clip
    // media. These stay null/false on every descriptor: an absence, never a guess.
    double? FrameRate,
    bool FrameRateVerified,
    string? AudioState,
    bool AudioStateVerified,
    bool OwnershipVerified,
    bool OwnerIsRequester,
    string? VisibilityScope,
    bool VisibilityPrivate,
    DateTime? RetainUntil,
    bool RetentionVerified,
    bool StorageAvailable,
    bool AttachPermitted,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> PendingFacts);

/// <summary>
/// Response envelope. <c>WorkspaceId</c> is the caller's own authorized workspace,
/// resolved server-side from the project; no identifiers of other users or other
/// workspaces are ever emitted, and cross-workspace or requester-invisible links are
/// withheld as a whole row instead of being described.
/// </summary>
public sealed record MovieVideoMakeoverSourceResponse(
    string Module,
    Guid MovieProjectId,
    Guid WorkspaceId,
    DateTime GeneratedAtUtc,
    int TotalClipCount,
    int DisclosedCount,
    bool Truncated,
    IReadOnlyList<MovieVideoMakeoverSourceDto> Sources);

public sealed class MovieVideoMakeoverSourceEvaluationResult
{
    public bool Withhold { get; init; }
    public MovieVideoMakeoverSourceDto? Source { get; init; }
    public static MovieVideoMakeoverSourceEvaluationResult Withheld() => new() { Withhold = true };
}

/// <summary>
/// Pure evaluator over already-fetched rows. The controller owns the SQL and the
/// authorization checks; this owns the fact/boundary logic so both stay testable
/// without any provider.
/// </summary>
public static class MovieVideoMakeoverSourceEvaluator
{
    /// <summary>
    /// A row is withheld (never enumerated, never described) when its asset or stored
    /// file lives in a different workspace than the project, or when the stored file
    /// is not visible to the requester under the authoritative
    /// <see cref="StoredFileVisibility"/> boundary. This is the no-cross-account-
    /// disclosure rule: the caller cannot even learn that such a link exists.
    /// </summary>
    public static bool ShouldWithhold(
        Guid projectWorkspaceId,
        Asset? asset,
        StoredFile? storedFile,
        Func<StoredFile, bool> visibleToRequester)
        => (asset is not null && asset.WorkspaceId != projectWorkspaceId)
            || (storedFile is not null && storedFile.WorkspaceId != projectWorkspaceId)
            || (storedFile is not null && !visibleToRequester(storedFile));

    public static MovieVideoMakeoverSourceDto Evaluate(
        MovieClip clip,
        Guid movieProjectId,
        Asset? asset,
        StoredFile? storedFile,
        Guid requesterUserId,
        DateTime nowUtc)
    {
        var blockers = new SortedSet<string>(StringComparer.Ordinal);

        // --- asset / file resolution -------------------------------------------
        if (clip.AssetId is null) blockers.Add(MovieVideoMakeoverSourceCodes.AssetMissing);
        else if (asset is null) blockers.Add(MovieVideoMakeoverSourceCodes.AssetNotFound);

        // The stored file is resolved from the clip first, then the asset. When the
        // clip carries no direct link we fall back to the asset's own file link; a
        // missing row behind a non-null id is a broken reference, not an absence.
        Guid? referencedFileId = clip.StoredFileId ?? asset?.StoredFileId;
        if (referencedFileId is null) blockers.Add(MovieVideoMakeoverSourceCodes.FileMissing);
        else if (storedFile is null) blockers.Add(MovieVideoMakeoverSourceCodes.FileMissing);

        if (asset is not null && asset.Status != AssetStatus.Active)
            blockers.Add(MovieVideoMakeoverSourceCodes.AssetUnavailable);

        if (storedFile is not null && storedFile.Status != StoredFileStatus.Ready)
            blockers.Add(MovieVideoMakeoverSourceCodes.AssetUnavailable);

        if (asset is not null && !string.Equals(asset.AssetType, AssetTypes.Video, StringComparison.OrdinalIgnoreCase))
            blockers.Add(MovieVideoMakeoverSourceCodes.NotVideo);

        if (!string.Equals(clip.Status, MovieClipStatuses.Ready, StringComparison.OrdinalIgnoreCase))
            blockers.Add(MovieVideoMakeoverSourceCodes.ClipNotReady);

        // --- ownership ----------------------------------------------------------
        // Verified only when both rows exist, sit in the caller's workspace (the
        // caller's workspace equality is guaranteed for disclosed rows by
        // ShouldWithhold), and carry non-empty owner columns. The raw owner id of
        // another workspace member is never emitted; only the boolean relationship.
        var ownershipVerified = asset is not null && storedFile is not null
            && asset.CreatedByUserId != Guid.Empty
            && storedFile.UserId != Guid.Empty;
        if (!ownershipVerified) blockers.Add(MovieVideoMakeoverSourceCodes.OwnershipUnverified);

        // --- visibility ----------------------------------------------------------
        // The only boundaries that exist for a disclosed file are project-workspace
        // sharing and uploader-private (StoredFileVisibility); neither is public.
        // The requester-invisible case never reaches here - the row is withheld.
        string? visibilityScope = storedFile is null ? null
            : storedFile.ProjectId.HasValue && !storedFile.ConversationId.HasValue
                ? "project_workspace"
                : "owner_private";
        var visibilityPrivate = storedFile is not null;
        if (!visibilityPrivate) blockers.Add(MovieVideoMakeoverSourceCodes.VisibilityNotPrivate);

        // --- retention -------------------------------------------------------------
        var retentionVerified = storedFile is not null
            && storedFile.DeletedAt is null
            && storedFile.Status == StoredFileStatus.Ready
            && storedFile.RetainUntil is not null
            && storedFile.RetainUntil.Value > nowUtc;
        if (!retentionVerified) blockers.Add(MovieVideoMakeoverSourceCodes.RetentionUnverified);

        // --- technical metadata ------------------------------------------------------
        double? duration = storedFile?.DurationSeconds
            ?? (clip.DurationSeconds.HasValue ? (double?)clip.DurationSeconds.Value : null);
        var sizeBytes = storedFile?.SizeBytes;
        int? width = storedFile?.Width;
        int? height = storedFile?.Height;

        var storageAvailable = storedFile is not null
            && storedFile.Status == StoredFileStatus.Ready
            && storedFile.DeletedAt is null;

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var outOfBounds = new SortedSet<string>(StringComparer.Ordinal);
        if (width is null) missing.Add("width");
        else if (width.Value < MovieVideoMakeoverSourceLimits.MinDimension || width.Value > MovieVideoMakeoverSourceLimits.MaxDimension)
            outOfBounds.Add("width");
        if (height is null) missing.Add("height");
        else if (height.Value < MovieVideoMakeoverSourceLimits.MinDimension || height.Value > MovieVideoMakeoverSourceLimits.MaxDimension)
            outOfBounds.Add("height");
        if (duration is null) missing.Add("duration");
        else if (duration.Value < MovieVideoMakeoverSourceLimits.MinDurationSeconds || duration.Value > MovieVideoMakeoverSourceLimits.MaxDurationSeconds)
            outOfBounds.Add("duration");
        if (sizeBytes is null || sizeBytes.Value <= 0) missing.Add("size");
        else if (sizeBytes.Value > MovieVideoMakeoverSourceLimits.MaxSourceBytes) outOfBounds.Add("size");

        if (missing.Count > 0) blockers.Add(MovieVideoMakeoverSourceCodes.MetadataMissing);
        if (outOfBounds.Count > 0) blockers.Add(MovieVideoMakeoverSourceCodes.MetadataOutOfBounds);

        // --- pending facts -----------------------------------------------------------
        // Frame rate and audio state have no persisted column on StoredFile/Asset and
        // no verified probe result is attached to clips. They are reported as pending
        // facts (null values) so the #775 draft admission keeps gating the features
        // that need them; this endpoint never claims them.
        var pendingFacts = new[]
        {
            MovieVideoMakeoverSourceCodes.PendingFrameRate,
            MovieVideoMakeoverSourceCodes.PendingAudioState,
        };

        return new MovieVideoMakeoverSourceDto(
            clip.Id,
            movieProjectId,
            clip.MovieSceneId,
            clip.MovieShotId,
            clip.Status,
            clip.AssetId,
            asset?.AssetType,
            asset is null ? null : asset.Status.ToString().ToLowerInvariant(),
            storedFile?.Id ?? referencedFileId,
            storedFile?.ContentType ?? asset?.MimeType,
            sizeBytes,
            duration,
            width,
            height,
            FrameRate: null,
            FrameRateVerified: false,
            AudioState: null,
            AudioStateVerified: false,
            ownershipVerified,
            storedFile is not null && storedFile.UserId == requesterUserId,
            visibilityScope,
            visibilityPrivate,
            storedFile?.RetainUntil,
            retentionVerified,
            storageAvailable,
            AttachPermitted: blockers.Count == 0,
            blockers.ToArray(),
            pendingFacts);
    }
}
