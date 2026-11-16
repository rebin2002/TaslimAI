using System.Collections.ObjectModel;

namespace Taslim.Api.Movies;

/// <summary>
/// Stable identifiers for the Movie timeline contract. The timeline is the only source of
/// truth for assembly. Director output is advisory until a user override is recorded and applied.
/// </summary>
public static class MovieTimelineContract
{
    public const string Version = "movie-timeline.v1";
    public const int MaxTimelineVersion = 1_000_000;
    public const int MaxClips = 10_000;
    public const int MaxTransitions = 20_000;
}

public static class MovieTimelineTransitionTypes
{
    public const string Cut = "cut";
    public const string Dissolve = "dissolve";
    public const string FadeIn = "fade_in";
    public const string FadeOut = "fade_out";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Cut,
        Dissolve,
        FadeIn,
        FadeOut,
    };

    public static bool IsSupported(string? type) => Supported.Contains(type?.Trim().ToLowerInvariant() ?? string.Empty);
}

public static class MovieTimelineEditActions
{
    public const string Add = "add";
    public const string Update = "update";
    public const string Remove = "remove";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Add,
        Update,
        Remove,
    };
}

/// <summary>
/// Provenance is deliberately represented by two different records below. A recommendation is
/// not an override and cannot mutate the canonical timeline by itself.
/// </summary>
public static class MovieTimelineDecisionSources
{
    public const string Director = "director";
    public const string User = "user";
}

/// <summary>
/// A clip entry in the authoritative movie timeline. Start and duration are timeline seconds,
/// not provider-specific frame counts.
/// </summary>
public sealed record MovieTimelineClipContract(
    Guid ClipId,
    Guid MovieShotId,
    int Sequence,
    decimal StartSeconds,
    decimal DurationSeconds);

/// <summary>
/// A transition is a decision about the canonical clip boundaries. A cut has zero duration,
/// a dissolve overlaps two adjacent clips, and fades attach to one clip edge.
/// </summary>
public sealed record MovieTimelineTransitionContract(
    Guid Id,
    string Type,
    Guid? FromClipId,
    Guid? ToClipId,
    decimal StartSeconds,
    decimal DurationSeconds);

/// <summary>
/// The current authoritative timeline snapshot. This contract is intentionally independent of
/// MovieAssembly and provider APIs so assembly can consume it without changing edit decisions.
/// </summary>
public sealed record MovieCanonicalTimelineContract(
    Guid TimelineId,
    int Version,
    IReadOnlyList<MovieTimelineClipContract> Clips,
    IReadOnlyList<MovieTimelineTransitionContract> Transitions,
    string ContractVersion = MovieTimelineContract.Version);

public sealed record MovieDirectorTransitionRecommendationContract(
    Guid RecommendationId,
    Guid TimelineId,
    int TimelineVersion,
    Guid? TransitionId,
    MovieTimelineTransitionContract? ProposedTransition,
    string Rationale);

public sealed record MovieUserTransitionOverrideContract(
    Guid OverrideId,
    Guid TimelineId,
    int TimelineVersion,
    Guid? TransitionId,
    MovieTimelineTransitionContract? ProposedTransition,
    string Reason,
    Guid? SupersedesRecommendationId = null);

/// <summary>
/// A reviewable edit decision is evaluated against the supplied canonical timeline version.
/// Applying it is a separate explicit operation; recommendation data is preserved as provenance.
/// </summary>
public sealed record MovieTimelineEditDecisionContract(
    Guid DecisionId,
    Guid TimelineId,
    int BaseTimelineVersion,
    string Action,
    Guid? TransitionId,
    MovieTimelineTransitionContract? ProposedTransition,
    MovieDirectorTransitionRecommendationContract? DirectorRecommendation,
    MovieUserTransitionOverrideContract? UserOverride);

public sealed record MovieTimelineValidationError(string Code, string Path, string Message);

public sealed record MovieTimelineValidationResult(IReadOnlyList<MovieTimelineValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static MovieTimelineValidationResult Valid { get; } = new([]);
}

public static class MovieTimelineValidationCodes
{
    public const string ContractVersionInvalid = "TIMELINE_CONTRACT_VERSION_INVALID";
    public const string TimelineIdRequired = "TIMELINE_ID_REQUIRED";
    public const string TimelineVersionInvalid = "TIMELINE_VERSION_INVALID";
    public const string TimelineVersionConflict = "TIMELINE_VERSION_CONFLICT";
    public const string TimelineLimitExceeded = "TIMELINE_LIMIT_EXCEEDED";
    public const string ClipIdRequired = "TIMELINE_CLIP_ID_REQUIRED";
    public const string ClipShotIdRequired = "TIMELINE_CLIP_SHOT_ID_REQUIRED";
    public const string ClipSequenceInvalid = "TIMELINE_CLIP_SEQUENCE_INVALID";
    public const string ClipTimingInvalid = "TIMELINE_CLIP_TIMING_INVALID";
    public const string ClipSequenceDuplicate = "TIMELINE_CLIP_SEQUENCE_DUPLICATE";
    public const string ClipIdDuplicate = "TIMELINE_CLIP_ID_DUPLICATE";
    public const string TransitionIdRequired = "TIMELINE_TRANSITION_ID_REQUIRED";
    public const string TransitionTypeInvalid = "TIMELINE_TRANSITION_TYPE_INVALID";
    public const string TransitionTimingInvalid = "TIMELINE_TRANSITION_TIMING_INVALID";
    public const string TransitionEndpointInvalid = "TIMELINE_TRANSITION_ENDPOINT_INVALID";
    public const string TransitionBoundaryInvalid = "TIMELINE_TRANSITION_BOUNDARY_INVALID";
    public const string TransitionDuplicate = "TIMELINE_TRANSITION_DUPLICATE";
    public const string TransitionOverlap = "TIMELINE_TRANSITION_OVERLAP";
    public const string EditActionInvalid = "TIMELINE_EDIT_ACTION_INVALID";
    public const string EditTargetInvalid = "TIMELINE_EDIT_TARGET_INVALID";
    public const string RecommendationInvalid = "TIMELINE_RECOMMENDATION_INVALID";
    public const string OverrideRequired = "TIMELINE_USER_OVERRIDE_REQUIRED";
    public const string OverrideInvalid = "TIMELINE_USER_OVERRIDE_INVALID";
}

/// <summary>
/// Pure validation for canonical timeline snapshots and edit decisions. It does not consult a
/// provider, infer missing timing, or reorder user data. Errors are emitted in deterministic path
/// and code order so API callers and tests receive stable results.
/// </summary>
public static class MovieTimelineValidator
{
    public static MovieTimelineValidationResult Validate(MovieCanonicalTimelineContract timeline)
    {
        var errors = new List<MovieTimelineValidationError>();
        ValidateTimelineShape(timeline, errors);
        if (timeline.Clips is null || timeline.Transitions is null)
            return Result(errors);

        var clipsById = new Dictionary<Guid, MovieTimelineClipContract>();
        var sequences = new HashSet<int>();
        for (var index = 0; index < timeline.Clips.Count; index++)
        {
            var clip = timeline.Clips[index];
            var path = $"clips[{index}]";
            if (clip.ClipId == Guid.Empty)
                Add(errors, MovieTimelineValidationCodes.ClipIdRequired, $"{path}.clipId", "A timeline clip id is required.");
            else if (!clipsById.TryAdd(clip.ClipId, clip))
                Add(errors, MovieTimelineValidationCodes.ClipIdDuplicate, $"{path}.clipId", "Timeline clip ids must be unique.");
            if (clip.MovieShotId == Guid.Empty)
                Add(errors, MovieTimelineValidationCodes.ClipShotIdRequired, $"{path}.movieShotId", "A timeline clip must reference a canonical MovieShot.");
            if (clip.Sequence <= 0 || !sequences.Add(clip.Sequence))
                Add(errors, clip.Sequence <= 0 ? MovieTimelineValidationCodes.ClipSequenceInvalid : MovieTimelineValidationCodes.ClipSequenceDuplicate, $"{path}.sequence", "Clip sequence numbers must be positive and unique.");
            if (clip.StartSeconds < 0m || clip.DurationSeconds <= 0m)
                Add(errors, MovieTimelineValidationCodes.ClipTimingInvalid, $"{path}.timing", "Clip start must be non-negative and duration must be greater than zero.");
            if (index > 0 && clip.StartSeconds < timeline.Clips[index - 1].StartSeconds)
                Add(errors, MovieTimelineValidationCodes.ClipTimingInvalid, $"{path}.startSeconds", "Clips must be ordered by non-decreasing timeline start.");
        }

        var transitionIds = new HashSet<Guid>();
        var boundaryKeys = new HashSet<string>(StringComparer.Ordinal);
        var intervals = new List<(decimal Start, decimal End, string Path)>();
        for (var index = 0; index < timeline.Transitions.Count; index++)
        {
            var transition = timeline.Transitions[index];
            var path = $"transitions[{index}]";
            ValidateTransition(transition, clipsById, path, errors);
            if (transition.Id == Guid.Empty)
                Add(errors, MovieTimelineValidationCodes.TransitionIdRequired, $"{path}.id", "A timeline transition id is required.");
            else if (!transitionIds.Add(transition.Id))
                Add(errors, MovieTimelineValidationCodes.TransitionDuplicate, $"{path}.id", "Timeline transition ids must be unique.");

            var boundaryKey = BoundaryKey(transition);
            if (boundaryKey is not null && !boundaryKeys.Add(boundaryKey))
                Add(errors, MovieTimelineValidationCodes.TransitionDuplicate, path, "Only one transition may control a clip boundary.");
            if (transition.DurationSeconds > 0m)
                intervals.Add((transition.StartSeconds, transition.StartSeconds + transition.DurationSeconds, path));
        }

        foreach (var pair in intervals.OrderBy(item => item.Start).ThenBy(item => item.End).ThenBy(item => item.Path).Zip(intervals.OrderBy(item => item.Start).ThenBy(item => item.End).ThenBy(item => item.Path).Skip(1)))
        {
            if (pair.First.End > pair.Second.Start)
                Add(errors, MovieTimelineValidationCodes.TransitionOverlap, pair.Second.Path, "Positive-duration transitions must not overlap.");
        }

        return Result(errors);
    }

    public static MovieTimelineValidationResult ValidateEditDecision(
        MovieCanonicalTimelineContract timeline,
        MovieTimelineEditDecisionContract decision)
    {
        var result = Validate(timeline);
        var errors = result.Errors.ToList();
        if (decision is null)
        {
            Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "decision", "An edit decision is required.");
            return Result(errors);
        }

        if (decision.DecisionId == Guid.Empty)
            Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "decisionId", "An edit decision id is required.");
        if (decision.TimelineId != timeline.TimelineId)
            Add(errors, MovieTimelineValidationCodes.TimelineVersionConflict, "timelineId", "The decision must target the supplied canonical timeline.");
        if (decision.BaseTimelineVersion != timeline.Version)
            Add(errors, MovieTimelineValidationCodes.TimelineVersionConflict, "baseTimelineVersion", "The decision was created against a stale canonical timeline version.");
        var action = Normalize(decision.Action);
        if (!MovieTimelineEditActions.Supported.Contains(action))
            Add(errors, MovieTimelineValidationCodes.EditActionInvalid, "action", "Choose add, update, or remove.");

        ValidateRecommendation(timeline, decision.DirectorRecommendation, errors);
        ValidateOverride(timeline, decision.UserOverride, errors);

        if (decision.UserOverride is null)
            Add(errors, MovieTimelineValidationCodes.OverrideRequired, "userOverride", "A Director recommendation or edit decision must be explicitly accepted by a user override before it can affect the timeline.");
        if (decision.DirectorRecommendation is not null && decision.UserOverride is not null
            && decision.UserOverride.SupersedesRecommendationId.HasValue
            && decision.UserOverride.SupersedesRecommendationId != decision.DirectorRecommendation.RecommendationId)
            Add(errors, MovieTimelineValidationCodes.OverrideInvalid, "userOverride.supersedesRecommendationId", "The user override must reference the recommendation it supersedes.");
        if (decision.UserOverride is not null)
        {
            if (decision.UserOverride.TransitionId != decision.TransitionId)
                Add(errors, MovieTimelineValidationCodes.OverrideInvalid, "userOverride.transitionId", "The user override target must match the edit decision target.");
            if (!TransitionsEqual(decision.UserOverride.ProposedTransition, decision.ProposedTransition))
                Add(errors, MovieTimelineValidationCodes.OverrideInvalid, "userOverride.proposedTransition", "The user override must carry the transition that the edit decision will apply.");
        }

        if (action == MovieTimelineEditActions.Add)
        {
            if (decision.TransitionId.HasValue)
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "transitionId", "An add decision cannot target an existing transition.");
            if (decision.ProposedTransition is null)
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "proposedTransition", "An add decision requires a transition.");
        }
        else if (action == MovieTimelineEditActions.Update)
        {
            if (!decision.TransitionId.HasValue || decision.TransitionId == Guid.Empty)
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "transitionId", "An update decision must target an existing transition.");
            else if (!timeline.Transitions.Any(item => item.Id == decision.TransitionId))
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "transitionId", "The update target must exist in the canonical timeline.");
            if (decision.ProposedTransition is null)
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "proposedTransition", "An update decision requires a transition.");
            else if (decision.ProposedTransition.Id != decision.TransitionId)
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "proposedTransition.id", "An update must preserve the targeted transition id.");
        }
        else if (action == MovieTimelineEditActions.Remove)
        {
            if (!decision.TransitionId.HasValue || decision.TransitionId == Guid.Empty || !timeline.Transitions.Any(item => item.Id == decision.TransitionId))
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "transitionId", "A remove decision must target an existing transition.");
            if (decision.ProposedTransition is not null)
                Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "proposedTransition", "A remove decision cannot include a replacement transition.");
        }

        if (decision.ProposedTransition is not null)
            ValidateTransition(decision.ProposedTransition, timeline.Clips.ToDictionary(item => item.ClipId), "proposedTransition", errors);

        return Result(errors);
    }

    public static void ValidateOrThrow(MovieCanonicalTimelineContract timeline)
    {
        ThrowIfInvalid(Validate(timeline));
    }

    public static void ValidateEditDecisionOrThrow(MovieCanonicalTimelineContract timeline, MovieTimelineEditDecisionContract decision)
    {
        ThrowIfInvalid(ValidateEditDecision(timeline, decision));
    }

    private static void ValidateTimelineShape(MovieCanonicalTimelineContract timeline, ICollection<MovieTimelineValidationError> errors)
    {
        if (timeline is null)
        {
            Add(errors, MovieTimelineValidationCodes.EditTargetInvalid, "timeline", "A canonical timeline is required.");
            return;
        }
        if (!string.Equals(timeline.ContractVersion?.Trim(), MovieTimelineContract.Version, StringComparison.Ordinal))
            Add(errors, MovieTimelineValidationCodes.ContractVersionInvalid, "contractVersion", $"The timeline contract must be {MovieTimelineContract.Version}.");
        if (timeline.TimelineId == Guid.Empty)
            Add(errors, MovieTimelineValidationCodes.TimelineIdRequired, "timelineId", "A timeline id is required.");
        if (timeline.Version <= 0 || timeline.Version > MovieTimelineContract.MaxTimelineVersion)
            Add(errors, MovieTimelineValidationCodes.TimelineVersionInvalid, "version", "Timeline version must be within the supported positive range.");
        if (timeline.Clips is null || timeline.Transitions is null)
            return;
        if (timeline.Clips.Count > MovieTimelineContract.MaxClips || timeline.Transitions.Count > MovieTimelineContract.MaxTransitions)
            Add(errors, MovieTimelineValidationCodes.TimelineLimitExceeded, "timeline", "The canonical timeline exceeds the supported clip or transition limit.");
    }

    private static void ValidateRecommendation(MovieCanonicalTimelineContract timeline, MovieDirectorTransitionRecommendationContract? recommendation, ICollection<MovieTimelineValidationError> errors)
    {
        if (recommendation is null) return;
        if (recommendation.RecommendationId == Guid.Empty)
            Add(errors, MovieTimelineValidationCodes.RecommendationInvalid, "directorRecommendation.recommendationId", "A Director recommendation id is required.");
        if (recommendation.TimelineId != timeline.TimelineId || recommendation.TimelineVersion != timeline.Version)
            Add(errors, MovieTimelineValidationCodes.TimelineVersionConflict, "directorRecommendation.timelineVersion", "The Director recommendation must target the current canonical timeline version.");
        if (string.IsNullOrWhiteSpace(recommendation.Rationale) || recommendation.Rationale.Length > 2_000)
            Add(errors, MovieTimelineValidationCodes.RecommendationInvalid, "directorRecommendation.rationale", "Director rationale must be between 1 and 2,000 characters.");
        if (recommendation.TransitionId.HasValue && !timeline.Transitions.Any(item => item.Id == recommendation.TransitionId))
            Add(errors, MovieTimelineValidationCodes.RecommendationInvalid, "directorRecommendation.transitionId", "The recommended update target must exist in the canonical timeline.");
        if (recommendation.ProposedTransition is not null)
            ValidateTransition(recommendation.ProposedTransition, timeline.Clips.ToDictionary(item => item.ClipId), "directorRecommendation.proposedTransition", errors);
    }

    private static void ValidateOverride(MovieCanonicalTimelineContract timeline, MovieUserTransitionOverrideContract? userOverride, ICollection<MovieTimelineValidationError> errors)
    {
        if (userOverride is null) return;
        if (userOverride.OverrideId == Guid.Empty)
            Add(errors, MovieTimelineValidationCodes.OverrideInvalid, "userOverride.overrideId", "A user override id is required.");
        if (userOverride.TimelineId != timeline.TimelineId || userOverride.TimelineVersion != timeline.Version)
            Add(errors, MovieTimelineValidationCodes.TimelineVersionConflict, "userOverride.timelineVersion", "The user override must target the current canonical timeline version.");
        if (string.IsNullOrWhiteSpace(userOverride.Reason) || userOverride.Reason.Length > 2_000)
            Add(errors, MovieTimelineValidationCodes.OverrideInvalid, "userOverride.reason", "A user override reason must be between 1 and 2,000 characters.");
        if (userOverride.TransitionId.HasValue && !timeline.Transitions.Any(item => item.Id == userOverride.TransitionId))
            Add(errors, MovieTimelineValidationCodes.OverrideInvalid, "userOverride.transitionId", "The user override target must exist in the canonical timeline.");
    }

    private static void ValidateTransition(
        MovieTimelineTransitionContract transition,
        IReadOnlyDictionary<Guid, MovieTimelineClipContract> clips,
        string path,
        ICollection<MovieTimelineValidationError> errors)
    {
        if (transition is null)
        {
            Add(errors, MovieTimelineValidationCodes.TransitionEndpointInvalid, path, "A transition is required.");
            return;
        }
        var type = Normalize(transition.Type);
        if (!MovieTimelineTransitionTypes.Supported.Contains(type))
            Add(errors, MovieTimelineValidationCodes.TransitionTypeInvalid, $"{path}.type", "Choose cut, dissolve, fade_in, or fade_out.");
        if (transition.Id == Guid.Empty)
            Add(errors, MovieTimelineValidationCodes.TransitionIdRequired, $"{path}.id", "A transition id is required.");
        if (transition.StartSeconds < 0m || transition.DurationSeconds < 0m)
            Add(errors, MovieTimelineValidationCodes.TransitionTimingInvalid, $"{path}.timing", "Transition start and duration must be non-negative.");

        var from = transition.FromClipId is Guid fromId && clips.TryGetValue(fromId, out var fromClip) ? fromClip : null;
        var to = transition.ToClipId is Guid toId && clips.TryGetValue(toId, out var toClip) ? toClip : null;
        if (transition.FromClipId.HasValue && from is null || transition.ToClipId.HasValue && to is null)
            Add(errors, MovieTimelineValidationCodes.TransitionEndpointInvalid, $"{path}.endpoints", "Transition endpoints must reference clips in the canonical timeline.");

        switch (type)
        {
            case MovieTimelineTransitionTypes.Cut:
                RequireEndpoints(transition, from, to, path, errors, fromRequired: true, toRequired: true);
                if (transition.DurationSeconds != 0m)
                    Add(errors, MovieTimelineValidationCodes.TransitionTimingInvalid, $"{path}.durationSeconds", "A cut must have zero duration.");
                if (from is not null && to is not null)
                {
                    if (to.Sequence != from.Sequence + 1 || to.StartSeconds != from.StartSeconds + from.DurationSeconds)
                        Add(errors, MovieTimelineValidationCodes.TransitionBoundaryInvalid, path, "A cut must join adjacent clips at the exact boundary.");
                    if (transition.StartSeconds != to.StartSeconds)
                        Add(errors, MovieTimelineValidationCodes.TransitionBoundaryInvalid, $"{path}.startSeconds", "A cut starts at the incoming clip boundary.");
                }
                break;
            case MovieTimelineTransitionTypes.Dissolve:
                RequireEndpoints(transition, from, to, path, errors, fromRequired: true, toRequired: true);
                if (transition.DurationSeconds <= 0m)
                    Add(errors, MovieTimelineValidationCodes.TransitionTimingInvalid, $"{path}.durationSeconds", "A dissolve must have positive duration.");
                if (from is not null && to is not null)
                {
                    if (to.Sequence != from.Sequence + 1 || transition.DurationSeconds > from.DurationSeconds || transition.DurationSeconds > to.DurationSeconds)
                        Add(errors, MovieTimelineValidationCodes.TransitionBoundaryInvalid, path, "A dissolve must join adjacent clips and fit inside both clips.");
                    if (to.StartSeconds != from.StartSeconds + from.DurationSeconds - transition.DurationSeconds || transition.StartSeconds != to.StartSeconds)
                        Add(errors, MovieTimelineValidationCodes.TransitionBoundaryInvalid, path, "A dissolve must use the exact overlap between adjacent clips.");
                }
                break;
            case MovieTimelineTransitionTypes.FadeIn:
                RequireEndpoints(transition, from, to, path, errors, fromRequired: false, toRequired: true);
                if (transition.DurationSeconds <= 0m || to is not null && transition.DurationSeconds > to.DurationSeconds)
                    Add(errors, MovieTimelineValidationCodes.TransitionTimingInvalid, $"{path}.durationSeconds", "A fade-in must be positive and fit inside the incoming clip.");
                if (from is not null || to is not null && transition.StartSeconds != to.StartSeconds)
                    Add(errors, MovieTimelineValidationCodes.TransitionBoundaryInvalid, path, "A fade-in must start at the incoming clip boundary and have no outgoing clip.");
                break;
            case MovieTimelineTransitionTypes.FadeOut:
                RequireEndpoints(transition, from, to, path, errors, fromRequired: true, toRequired: false);
                if (transition.DurationSeconds <= 0m || from is not null && transition.DurationSeconds > from.DurationSeconds)
                    Add(errors, MovieTimelineValidationCodes.TransitionTimingInvalid, $"{path}.durationSeconds", "A fade-out must be positive and fit inside the outgoing clip.");
                if (to is not null || from is not null && transition.StartSeconds != from.StartSeconds + from.DurationSeconds - transition.DurationSeconds)
                    Add(errors, MovieTimelineValidationCodes.TransitionBoundaryInvalid, path, "A fade-out must end at the outgoing clip boundary and have no incoming clip.");
                break;
        }
    }

    private static void RequireEndpoints(
        MovieTimelineTransitionContract transition,
        MovieTimelineClipContract? from,
        MovieTimelineClipContract? to,
        string path,
        ICollection<MovieTimelineValidationError> errors,
        bool fromRequired,
        bool toRequired)
    {
        if (fromRequired && (transition.FromClipId is null || from is null))
            Add(errors, MovieTimelineValidationCodes.TransitionEndpointInvalid, $"{path}.fromClipId", "An outgoing clip is required for this transition.");
        if (toRequired && (transition.ToClipId is null || to is null))
            Add(errors, MovieTimelineValidationCodes.TransitionEndpointInvalid, $"{path}.toClipId", "An incoming clip is required for this transition.");
        if (!fromRequired && transition.FromClipId is not null)
            Add(errors, MovieTimelineValidationCodes.TransitionEndpointInvalid, $"{path}.fromClipId", "This transition cannot have an outgoing clip.");
        if (!toRequired && transition.ToClipId is not null)
            Add(errors, MovieTimelineValidationCodes.TransitionEndpointInvalid, $"{path}.toClipId", "This transition cannot have an incoming clip.");
        if (from is not null && to is not null && from.ClipId == to.ClipId)
            Add(errors, MovieTimelineValidationCodes.TransitionEndpointInvalid, path, "A transition cannot use the same clip on both endpoints.");
    }

    private static string? BoundaryKey(MovieTimelineTransitionContract transition)
    {
        var type = Normalize(transition.Type);
        return type switch
        {
            MovieTimelineTransitionTypes.Cut or MovieTimelineTransitionTypes.Dissolve
                when transition.FromClipId.HasValue && transition.ToClipId.HasValue
                => $"pair:{transition.FromClipId:N}:{transition.ToClipId:N}",
            MovieTimelineTransitionTypes.FadeIn when transition.ToClipId.HasValue => $"in:{transition.ToClipId:N}",
            MovieTimelineTransitionTypes.FadeOut when transition.FromClipId.HasValue => $"out:{transition.FromClipId:N}",
            _ => null,
        };
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool TransitionsEqual(MovieTimelineTransitionContract? left, MovieTimelineTransitionContract? right) =>
        left is null && right is null
        || left is not null && right is not null
            && left.Id == right.Id
            && string.Equals(Normalize(left.Type), Normalize(right.Type), StringComparison.Ordinal)
            && left.FromClipId == right.FromClipId
            && left.ToClipId == right.ToClipId
            && left.StartSeconds == right.StartSeconds
            && left.DurationSeconds == right.DurationSeconds;

    private static void ThrowIfInvalid(MovieTimelineValidationResult result)
    {
        if (result.IsValid) return;
        var first = result.Errors[0];
        throw new MovieTimelineValidationException(first.Code, first.Message, result.Errors);
    }

    private static MovieTimelineValidationResult Result(IEnumerable<MovieTimelineValidationError> errors) =>
        new(new ReadOnlyCollection<MovieTimelineValidationError>(errors
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ToList()));

    private static void Add(ICollection<MovieTimelineValidationError> errors, string code, string path, string message) =>
        errors.Add(new MovieTimelineValidationError(code, path, message));
}

/// <summary>
/// The only mutating operation in this contract layer. It requires an explicit user override,
/// revalidates the base version, and returns a new canonical timeline version. Director-only
/// recommendations are intentionally not applicable.
/// </summary>
public static class MovieTimelineAuthority
{
    public static MovieCanonicalTimelineContract ApplyUserOverride(
        MovieCanonicalTimelineContract canonicalTimeline,
        MovieTimelineEditDecisionContract decision)
    {
        MovieTimelineValidator.ValidateEditDecisionOrThrow(canonicalTimeline, decision);
        if (decision.UserOverride is null)
            throw new MovieTimelineValidationException(
                MovieTimelineValidationCodes.OverrideRequired,
                "A Director recommendation cannot mutate the canonical timeline without an explicit user override.",
                []);

        var transitions = canonicalTimeline.Transitions.ToList();
        var action = decision.Action.Trim().ToLowerInvariant();
        switch (action)
        {
            case MovieTimelineEditActions.Add:
                transitions.Add(decision.ProposedTransition!);
                break;
            case MovieTimelineEditActions.Update:
                var index = transitions.FindIndex(item => item.Id == decision.TransitionId);
                transitions[index] = decision.ProposedTransition!;
                break;
            case MovieTimelineEditActions.Remove:
                transitions.RemoveAll(item => item.Id == decision.TransitionId);
                break;
        }

        var next = canonicalTimeline with
        {
            Version = checked(canonicalTimeline.Version + 1),
            Transitions = transitions
                .OrderBy(item => item.StartSeconds)
                .ThenBy(item => item.DurationSeconds)
                .ThenBy(item => item.Id)
                .ToArray(),
        };
        MovieTimelineValidator.ValidateOrThrow(next);
        return next;
    }
}
