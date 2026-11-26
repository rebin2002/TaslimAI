using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taslim.Api.Contracts;

namespace Taslim.Api.Movies;

public static class MovieIntercutCoverageKinds
{
    public const string Primary = "primary";
    public const string Reaction = "reaction";
    public const string Insert = "insert";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Primary,
        Reaction,
        Insert,
    };

    public static bool IsSupported(string? value) => Supported.Contains(Normalize(value));
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

public static class MovieIntercutPlanCodes
{
    public const string ProjectRequired = "MOVIE_INTERCUT_PROJECT_REQUIRED";
    public const string TimelineRequired = "MOVIE_INTERCUT_TIMELINE_REQUIRED";
    public const string TimelineProjectMismatch = "MOVIE_INTERCUT_TIMELINE_PROJECT_MISMATCH";
    public const string TimelineVersionMismatch = "MOVIE_INTERCUT_TIMELINE_VERSION_MISMATCH";
    public const string TimelineVersionInvalid = "MOVIE_INTERCUT_TIMELINE_VERSION_INVALID";
    public const string ClipRequired = "MOVIE_INTERCUT_CLIP_REQUIRED";
    public const string ClipDuplicate = "MOVIE_INTERCUT_CLIP_DUPLICATE";
    public const string ClipOwnershipMismatch = "MOVIE_INTERCUT_CLIP_OWNERSHIP_MISMATCH";
    public const string ShotRequired = "MOVIE_INTERCUT_SHOT_REQUIRED";
    public const string SceneRequired = "MOVIE_INTERCUT_SCENE_REQUIRED";
    public const string StoryOrderInvalid = "MOVIE_INTERCUT_STORY_ORDER_INVALID";
    public const string CoverageKindInvalid = "MOVIE_INTERCUT_COVERAGE_KIND_INVALID";
    public const string DurationInvalid = "MOVIE_INTERCUT_DURATION_INVALID";
    public const string AnchorInvalid = "MOVIE_INTERCUT_ANCHOR_INVALID";
    public const string ApprovalRequired = "MOVIE_INTERCUT_APPROVAL_REQUIRED";
}

public sealed class MovieIntercutPlanningException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record MovieIntercutApproval(bool Approved, string Reason);

internal sealed record MovieIntercutCandidate(
    Guid ClipId,
    Guid MovieShotId,
    Guid MovieSceneId,
    int SceneSequence,
    int ShotSequence,
    decimal DurationSeconds,
    string CoverageKind,
    Guid? AnchorShotId,
    string? Label)
{
    public bool IsPrimary => CoverageKind == MovieIntercutCoverageKinds.Primary;

    public MovieIntercutCoverageDto ToDto() => new(
        ClipId,
        MovieShotId,
        MovieSceneId,
        SceneSequence,
        ShotSequence,
        DurationSeconds,
        CoverageKind,
        AnchorShotId,
        Label);
}

/// <summary>
/// Builds review-only, deterministic intercut proposals from canonical shot candidates. Primary
/// story order is authoritative; reaction and insert coverage may be intercut around it, but a
/// requested primary reorder is surfaced as an approval requirement rather than applied silently.
/// </summary>
public static class MovieIntercutPlanner
{
    private const int MaxClips = 10_000;
    private const decimal MaxClipDurationSeconds = 3_600m;
    // This is the assembled film timeline, NOT a single paid provider submission.
    // Keep MaxClipDurationSeconds independent from the two-hour film cap.
    private const decimal MaxTimelineDurationSeconds = 7_200m;

    public static MovieIntercutPlanDto Build(Guid movieProjectId, MovieIntercutPlanRequest request)
    {
        if (movieProjectId == Guid.Empty)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ProjectRequired, "A movie project is required for intercut planning.");
        if (request is null)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ClipRequired, "Intercut coverage candidates are required.");
        if (request.BaseTimelineVersion <= 0 || request.BaseTimelineVersion >= MovieTimelineContract.MaxTimelineVersion)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.TimelineVersionInvalid, "The base timeline version is outside the supported range.");
        if (request.Clips is null || request.Clips.Count == 0)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ClipRequired, "At least one shot coverage candidate is required.");
        if (request.Clips.Count > MaxClips)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ClipRequired, "The intercut proposal exceeds the supported coverage limit.");

        var candidates = request.Clips.Select(ToCandidate).ToArray();
        ValidateCandidates(candidates);
        ValidateRequestedOrder(candidates, request.RequestedClipOrder);

        var timelineId = request.TimelineId.GetValueOrDefault();
        if (timelineId == Guid.Empty)
            timelineId = StableGuid("movie-intercut-timeline", movieProjectId.ToString("N"));

        var canonicalOrder = OrderForStory(candidates);
        var requestedOrder = request.RequestedClipOrder?.ToArray() ?? [];
        var storyOrderChangeRequested = IsStoryOrderChangeRequested(candidates, canonicalOrder, requestedOrder);
        // A requested change to reaction/insert placement must not be silently discarded
        // merely because the primary story beats remain in canonical order.
        var coverageOrderChangeRequested = requestedOrder.Length > 0
            && !requestedOrder.SequenceEqual(canonicalOrder.Select(item => item.ClipId));
        var findings = new List<string>
        {
            "primary_coverage_follows_scene_and_shot_story_order",
            "reaction_and_insert_coverage_is_intercut_without_reordering_primary_beats",
            "canonical_clip_and_shot_ownership_not_verified_in_review_only_proposal",
            "no_media_generation_started",
        };
        if (storyOrderChangeRequested)
            findings.Add("requested_primary_order_change_requires_explicit_user_approval");
        else
            findings.Add("story_order_change_not_requested");
        if (coverageOrderChangeRequested && !storyOrderChangeRequested)
            findings.Add("requested_coverage_order_change_requires_explicit_user_approval");

        var timeline = BuildTimeline(timelineId, request.BaseTimelineVersion + 1, canonicalOrder);
        var proposalId = StableGuid("movie-intercut-proposal", movieProjectId.ToString("N"), timelineId.ToString("N"), request.BaseTimelineVersion.ToString(CultureInfo.InvariantCulture), ComputeCandidateHash(candidates, requestedOrder));
        var provenanceHash = ComputeProposalHash(movieProjectId, proposalId, request.BaseTimelineVersion, timeline, candidates, requestedOrder);
        return new MovieIntercutPlanDto(
            proposalId,
            movieProjectId,
            timelineId,
            request.BaseTimelineVersion,
            timeline.Version,
            timeline,
            canonicalOrder.Select(item => item.ToDto()).ToArray(),
            requestedOrder,
            !storyOrderChangeRequested,
            coverageOrderChangeRequested,
            findings,
            provenanceHash);
    }

    /// <summary>
    /// Applies a changed requested coverage order only with an explicit approval reason. A
    /// rejected approval returns the review proposal unchanged and never mutates persisted data.
    /// </summary>
    public static MovieCanonicalTimelineContract ApplyUserApproval(MovieIntercutPlanDto proposal, MovieIntercutApproval approval)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        if (approval is null) throw new ArgumentNullException(nameof(approval));
        if (!proposal.RequiresUserApproval)
            return proposal.ProposedTimeline;
        if (!approval.Approved)
            return proposal.ProposedTimeline;
        if (string.IsNullOrWhiteSpace(approval.Reason) || approval.Reason.Trim().Length > 2_000)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ApprovalRequired, "An approved story-order change requires a reason of 1 to 2,000 characters.");

        var canonicalIds = proposal.ProposedTimeline.Clips
            .OrderBy(item => item.Sequence)
            .Select(item => item.ClipId)
            .ToArray();
        var approvedOrder = proposal.RequestedClipOrder.ToArray();
        if (approvedOrder.Length != canonicalIds.Length
            || approvedOrder.Distinct().Count() != approvedOrder.Length
            || !approvedOrder.ToHashSet().SetEquals(canonicalIds))
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.StoryOrderInvalid, "The approved order must contain each canonical proposal clip exactly once.");
        if (approvedOrder.SequenceEqual(canonicalIds))
            return proposal.ProposedTimeline;

        // The request order is retained in the proposal's coverage ordering only through the
        // deterministic requested order. Rebuild from the explicit approval using the
        // canonical clip durations and stable coverage metadata.
        var clipsById = proposal.ProposedTimeline.Clips.ToDictionary(item => item.ClipId);
        // One lookup per clip keeps approval linear for long-form films. Repeated
        // First() scans made large editorial reorders quadratic in clip count.
        var coverageByClipId = proposal.Coverage.ToDictionary(item => item.ClipId);
        var reordered = approvedOrder.Select(id =>
        {
            var clip = clipsById[id];
            var coverage = coverageByClipId[id];
            return new MovieIntercutCandidate(
                clip.ClipId,
                clip.MovieShotId,
                coverage.MovieSceneId,
                coverage.SceneSequence,
                coverage.ShotSequence,
                clip.DurationSeconds,
                coverage.CoverageKind,
                coverage.AnchorShotId,
                coverage.Label);
        }).ToArray();
        var nextTimeline = BuildTimeline(proposal.TimelineId, checked(proposal.ProposedTimeline.Version + 1), reordered);
        return nextTimeline;
    }

    private static MovieIntercutCandidate ToCandidate(MovieIntercutCoverageRequest item)
    {
        if (item is null) throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ClipRequired, "A coverage candidate cannot be null.");
        return new(
            item.ClipId,
            item.MovieShotId,
            item.MovieSceneId,
            item.SceneSequence,
            item.ShotSequence,
            item.DurationSeconds,
            MovieIntercutCoverageKinds.Normalize(item.CoverageKind),
            item.AnchorShotId,
            Clean(item.Label));
    }

    private static void ValidateCandidates(IReadOnlyList<MovieIntercutCandidate> candidates)
    {
        var ids = new HashSet<Guid>();
        var shotIds = new HashSet<Guid>();
        // Two different primary clips cannot both claim one canonical story slot.
        // An ID-based tie breaker would produce a reproducible but incorrect story order.
        var primaryStoryPositions = new HashSet<(int Scene, int Shot)>();
        foreach (var item in candidates)
        {
            if (item.ClipId == Guid.Empty || !ids.Add(item.ClipId))
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ClipDuplicate, "Coverage clip ids must be present and unique.");
            if (item.MovieShotId == Guid.Empty)
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ShotRequired, "Every coverage candidate must reference a canonical shot.");
            if (item.MovieSceneId == Guid.Empty)
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.SceneRequired, "Every coverage candidate must reference a canonical scene.");
            if (item.SceneSequence <= 0 || item.ShotSequence <= 0)
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.StoryOrderInvalid, "Scene and shot story order values must be positive.");
            if (!MovieIntercutCoverageKinds.IsSupported(item.CoverageKind))
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.CoverageKindInvalid, "Coverage kind must be primary, reaction, or insert.");
            if (item.DurationSeconds <= 0m || item.DurationSeconds > MaxClipDurationSeconds)
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.DurationInvalid, "Coverage duration must be greater than zero and no more than 3,600 seconds.");
            if (item.AnchorShotId == item.MovieShotId)
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.AnchorInvalid, "Coverage cannot anchor to its own shot.");
            if (item.IsPrimary && item.AnchorShotId.HasValue)
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.AnchorInvalid, "Primary coverage cannot use an intercut anchor.");
            if (item.IsPrimary && !shotIds.Add(item.MovieShotId))
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.StoryOrderInvalid, "Only one primary coverage clip may represent a canonical shot.");
            if (item.IsPrimary && !primaryStoryPositions.Add((item.SceneSequence, item.ShotSequence)))
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.StoryOrderInvalid, "Primary coverage must have distinct canonical scene and shot positions.");
        }

        var totalDuration = candidates.Sum(item => item.DurationSeconds);
        if (totalDuration > MaxTimelineDurationSeconds)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.DurationInvalid, "The proposed timeline exceeds the supported 7,200-second duration.");
        // An intercut is editorial coverage *around a primary story beat*. An anchor
        // pointing to another reaction/insert is never placed at the anchored shot by
        // OrderForStory, so fail closed rather than silently proposing the wrong cut.
        var primaryShots = candidates.Where(item => item.IsPrimary).Select(item => item.MovieShotId).ToHashSet();
        if (primaryShots.Count == 0)
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.StoryOrderInvalid, "An intercut proposal requires at least one primary story shot.");
        if (candidates.Any(item => item.AnchorShotId is Guid anchor && !primaryShots.Contains(anchor)))
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.AnchorInvalid, "Every intercut anchor must reference a primary story shot in the same proposal.");
    }

    private static void ValidateRequestedOrder(IReadOnlyList<MovieIntercutCandidate> candidates, IReadOnlyList<Guid>? requestedOrder)
    {
        if (requestedOrder is null || requestedOrder.Count == 0) return;
        var candidateIds = candidates.Select(item => item.ClipId).ToHashSet();
        // Bound membership verification to O(n), even for the maximum 10,000 clips.
        if (requestedOrder.Count != candidates.Count || requestedOrder.Distinct().Count() != requestedOrder.Count || requestedOrder.Any(id => !candidateIds.Contains(id)))
            throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.StoryOrderInvalid, "A requested clip order must contain every proposal clip exactly once.");
    }

    private static IReadOnlyList<MovieIntercutCandidate> OrderForStory(IReadOnlyList<MovieIntercutCandidate> candidates)
    {
        var primaries = candidates.Where(item => item.IsPrimary).OrderBy(StoryKey).ThenBy(item => item.ClipId).ToArray();
        var primaryByShot = primaries.ToDictionary(item => item.MovieShotId);
        return candidates
            .OrderBy(item => EffectiveSceneSequence(item, primaryByShot))
            .ThenBy(item => EffectiveShotSequence(item, primaryByShot))
            .ThenBy(item => item.IsPrimary ? 0 : 1)
            .ThenBy(item => CoverageRank(item.CoverageKind))
            .ThenBy(item => item.AnchorShotId.HasValue ? 1 : 0)
            .ThenBy(item => item.ClipId)
            .ToArray();
    }

    private static bool IsStoryOrderChangeRequested(IReadOnlyList<MovieIntercutCandidate> candidates, IReadOnlyList<MovieIntercutCandidate> canonicalOrder, IReadOnlyList<Guid> requestedOrder)
    {
        if (requestedOrder.Count == 0) return false;
        var canonicalPrimaries = canonicalOrder.Where(item => item.IsPrimary).Select(item => item.ClipId).ToArray();
        var primaryClipIds = candidates.Where(item => item.IsPrimary).Select(item => item.ClipId).ToHashSet();
        var requestedPrimaries = requestedOrder.Where(primaryClipIds.Contains).ToArray();
        return !canonicalPrimaries.SequenceEqual(requestedPrimaries);
    }

    private static MovieCanonicalTimelineContract BuildTimeline(Guid timelineId, int version, IReadOnlyList<MovieIntercutCandidate> ordered)
    {
        var start = 0m;
        var clips = new List<MovieTimelineClipContract>(ordered.Count);
        foreach (var item in ordered)
        {
            clips.Add(new MovieTimelineClipContract(item.ClipId, item.MovieShotId, clips.Count + 1, start, item.DurationSeconds));
            start += item.DurationSeconds;
        }
        var transitions = clips.Skip(1).Select((clip, index) => new MovieTimelineTransitionContract(
            StableGuid("movie-intercut-cut", timelineId.ToString("N"), version.ToString(CultureInfo.InvariantCulture), clips[index].ClipId.ToString("N"), clip.ClipId.ToString("N")),
            MovieTimelineTransitionTypes.Cut,
            clips[index].ClipId,
            clip.ClipId,
            clip.StartSeconds,
            0m)).ToArray();
        var result = new MovieCanonicalTimelineContract(timelineId, version, clips, transitions);
        MovieTimelineValidator.ValidateOrThrow(result);
        return result;
    }

    private static int EffectiveSceneSequence(MovieIntercutCandidate item, IReadOnlyDictionary<Guid, MovieIntercutCandidate> primaryByShot) =>
        item.AnchorShotId is Guid anchor && primaryByShot.TryGetValue(anchor, out var anchored) ? anchored.SceneSequence : item.SceneSequence;

    private static int EffectiveShotSequence(MovieIntercutCandidate item, IReadOnlyDictionary<Guid, MovieIntercutCandidate> primaryByShot) =>
        item.AnchorShotId is Guid anchor && primaryByShot.TryGetValue(anchor, out var anchored) ? anchored.ShotSequence : item.ShotSequence;

    private static (int Scene, int Shot) StoryKey(MovieIntercutCandidate item) => (item.SceneSequence, item.ShotSequence);
    private static int CoverageRank(string kind) => kind switch { MovieIntercutCoverageKinds.Primary => 0, MovieIntercutCoverageKinds.Reaction => 1, MovieIntercutCoverageKinds.Insert => 2, _ => 3 };

    private static string ComputeCandidateHash(IEnumerable<MovieIntercutCandidate> candidates, IEnumerable<Guid> requestedOrder)
    {
        // Human-readable labels can contain ':' and '|'. Delimiter-joining the
        // candidates allowed a single label to masquerade as an extra candidate
        // and assign two different review proposals the same deterministic ID.
        // JSON preserves field and array boundaries, including nullable values.
        var canonical = new
        {
            Candidates = candidates.OrderBy(item => item.ClipId).Select(item => new
            {
                item.ClipId,
                item.MovieShotId,
                item.MovieSceneId,
                item.SceneSequence,
                item.ShotSequence,
                item.DurationSeconds,
                item.CoverageKind,
                item.AnchorShotId,
                item.Label,
            }).ToArray(),
            RequestedClipOrder = requestedOrder.ToArray(),
        };
        return Hash(JsonSerializer.Serialize(canonical));
    }

    private static string ComputeProposalHash(Guid projectId, Guid proposalId, int baseVersion, MovieCanonicalTimelineContract timeline, IEnumerable<MovieIntercutCandidate> candidates, IEnumerable<Guid> requestedOrder) =>
        Hash(string.Join("|", projectId.ToString("N"), proposalId.ToString("N"), baseVersion, timeline.TimelineId.ToString("N"), timeline.Version, string.Join(";", timeline.Clips.Select(item => string.Join(":", item.ClipId.ToString("N"), item.MovieShotId.ToString("N"), item.Sequence, item.StartSeconds.ToString(CultureInfo.InvariantCulture), item.DurationSeconds.ToString(CultureInfo.InvariantCulture)))), ComputeCandidateHash(candidates, requestedOrder)));

    private static Guid StableGuid(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
