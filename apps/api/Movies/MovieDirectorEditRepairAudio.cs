using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Taslim.Api.Movies;

public static class DirectorEditRepairAudioActionTypes
{
    public const string PlanAudioBridges = "plan_audio_bridges";

    public static bool IsSupported(string? value) => string.Equals(value?.Trim(), PlanAudioBridges, StringComparison.OrdinalIgnoreCase);
    public static string Normalize(string? value) => IsSupported(value) ? PlanAudioBridges : string.Empty;
}

public static class MovieDirectorAudioBridgeKinds
{
    public const string SoundEffect = MovieSoundKinds.SoundEffect;
    public const string Ambience = MovieSoundKinds.Ambience;
    public const string Music = "music";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SoundEffect, Ambience, Music,
    };
}

public static class MovieDirectorAudioBridgeSourceKinds
{
    public const string SoundTrack = "sound_track";
    public const string SoundtrackCue = "soundtrack_cue";
    public const string Library = "library";
    public const string ApprovedAsset = "approved_asset";
}

/// <summary>
/// A safe snapshot of an already available audio capability. It is deliberately not a provider
/// request: the Director can only choose from approved material that is already in the project.
/// </summary>
public sealed record MovieDirectorAudioCapabilitySnapshot(
    Guid SourceId,
    string Kind,
    string SourceKind,
    string Label,
    bool IsApproved,
    string? Layer = null,
    decimal? StartSeconds = null,
    decimal? EndSeconds = null);

/// <summary>
/// An advisory audio bridge. It is not a timeline item and cannot mutate the canonical timeline.
/// </summary>
public sealed record MovieDirectorAudioBridgeRecommendation(
    Guid RecommendationId,
    Guid TimelineId,
    int TimelineVersion,
    string Kind,
    string Layer,
    Guid? FromClipId,
    Guid? ToClipId,
    Guid? RelatedTransitionId,
    decimal StartSeconds,
    decimal DurationSeconds,
    Guid? SuggestedSourceId,
    string? SuggestedSourceKind,
    string Rationale,
    bool RequiresUserOverride = true);

public sealed record MovieDirectorEditRepairAudioPlan(
    Guid TimelineId,
    int TimelineVersion,
    string CanonicalTimelineFingerprint,
    IReadOnlyList<MovieDirectorAudioBridgeRecommendation> Recommendations,
    IReadOnlyList<string> UnresolvedNeeds,
    bool RequiresUserOverride = true);

public sealed record DirectorEditRepairAudioActionPayload(
    string Action,
    MovieDirectorEditRepairAudioPlan Plan);

public sealed record DirectorEditRepairAudioReviewDto(
    string Action,
    Guid TimelineId,
    int TimelineVersion,
    string CanonicalTimelineFingerprint,
    IReadOnlyList<MovieDirectorAudioBridgeRecommendation> Recommendations,
    IReadOnlyList<string> UnresolvedNeeds,
    bool RequiresUserOverride,
    bool ProviderCalled,
    bool CanonicalTimelineMutated);

public sealed class DirectorEditRepairAudioValidationException(string message) : Exception(message);

/// <summary>
/// Deterministic edit repair planning. It uses only a validated canonical timeline and snapshots
/// of existing SFX, ambience, music, or library material. It never calls a provider, creates a
/// generation job, changes a soundtrack track, or applies a user override.
/// </summary>
public static class MovieDirectorEditRepairAudioPlanner
{
    public const int MaxRecommendations = 64;
    public const int MaxCapabilities = 128;
    private const decimal DefaultBridgeSeconds = 0.8m;
    private const decimal SoundEffectBridgeSeconds = 0.4m;
    private const decimal GapThresholdSeconds = 0.05m;

    public static MovieDirectorEditRepairAudioPlan Plan(
        MovieCanonicalTimelineContract canonicalTimeline,
        IReadOnlyList<MovieDirectorAudioCapabilitySnapshot>? capabilities = null)
    {
        MovieTimelineValidator.ValidateOrThrow(canonicalTimeline);
        var boundedCapabilities = (capabilities ?? [])
            .Where(IsUsableCapability)
            .OrderBy(item => Priority(item.Kind))
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceId)
            .Take(MaxCapabilities)
            .ToArray();
        var recommendations = new List<MovieDirectorAudioBridgeRecommendation>();
        var unresolved = new List<string>();
        var clips = canonicalTimeline.Clips.OrderBy(item => item.Sequence).ThenBy(item => item.StartSeconds).ThenBy(item => item.ClipId).ToArray();
        var transitions = canonicalTimeline.Transitions.ToDictionary(item => (item.FromClipId, item.ToClipId), item => item);

        for (var index = 1; index < clips.Length && recommendations.Count < MaxRecommendations; index++)
        {
            var from = clips[index - 1];
            var to = clips[index];
            var outgoingEnd = from.StartSeconds + from.DurationSeconds;
            var boundary = to.StartSeconds;
            transitions.TryGetValue((from.ClipId, to.ClipId), out var transition);
            var gap = boundary - outgoingEnd;

            if (gap >= GapThresholdSeconds)
            {
                var gapCapability = SelectCapability(boundedCapabilities, MovieDirectorAudioBridgeKinds.Ambience, outgoingEnd, boundary)
                    ?? SelectCapability(boundedCapabilities, MovieDirectorAudioBridgeKinds.Music, outgoingEnd, boundary);
                if (gapCapability is null)
                {
                    unresolved.Add($"gap:{from.ClipId:N}:{to.ClipId:N}:no_approved_ambience_or_music");
                }
                else
                {
                    recommendations.Add(CreateRecommendation(
                        canonicalTimeline,
                        gapCapability,
                        from,
                        to,
                        transition?.Id,
                        outgoingEnd,
                        gap,
                        gapCapability.Kind.Equals(MovieDirectorAudioBridgeKinds.Music, StringComparison.OrdinalIgnoreCase)
                            ? "Fill the uncovered picture gap with existing approved music so no full scene regeneration is needed."
                            : "Fill the uncovered picture gap with existing approved ambience so no full scene regeneration is needed."));
                }
            }

            var hardBoundary = transition is null || string.Equals(transition.Type, MovieTimelineTransitionTypes.Cut, StringComparison.OrdinalIgnoreCase);
            if (hardBoundary && recommendations.Count < MaxRecommendations)
            {
                var smoothingDuration = Math.Min(DefaultBridgeSeconds, Math.Min(from.DurationSeconds, to.DurationSeconds));
                if (smoothingDuration > 0m)
                {
                    var smoothingStart = Math.Max(from.StartSeconds, boundary - smoothingDuration / 2m);
                    var ambience = SelectCapability(boundedCapabilities, MovieDirectorAudioBridgeKinds.Ambience, smoothingStart, smoothingStart + smoothingDuration)
                        ?? SelectCapability(boundedCapabilities, MovieDirectorAudioBridgeKinds.Music, smoothingStart, smoothingStart + smoothingDuration);
                    if (ambience is not null && !HasCoveredRecommendation(recommendations, ambience.Kind, smoothingStart, smoothingStart + smoothingDuration))
                    {
                        recommendations.Add(CreateRecommendation(
                            canonicalTimeline,
                            ambience,
                            from,
                            to,
                            transition?.Id,
                            smoothingStart,
                            smoothingDuration,
                            "Carry approved room tone or music across the hard visual boundary to smooth the edit."));
                    }

                    if (recommendations.Count < MaxRecommendations)
                    {
                        var sfxDuration = Math.Min(SoundEffectBridgeSeconds, Math.Min(from.DurationSeconds, to.DurationSeconds));
                        var sfxStart = Math.Max(from.StartSeconds, boundary - sfxDuration / 2m);
                        var sfx = SelectCapability(boundedCapabilities, MovieDirectorAudioBridgeKinds.SoundEffect, sfxStart, sfxStart + sfxDuration);
                        if (sfx is not null)
                        {
                            recommendations.Add(CreateRecommendation(
                                canonicalTimeline,
                                sfx,
                                from,
                                to,
                                transition?.Id,
                                sfxStart,
                                sfxDuration,
                                "Use an existing approved transition SFX as a small edit bridge; review its timing before acceptance."));
                        }
                    }
                }
            }
            else if (transition is not null && transition.DurationSeconds > 0m && recommendations.Count < MaxRecommendations)
            {
                var transitionCapability = SelectCapability(boundedCapabilities, MovieDirectorAudioBridgeKinds.Ambience, transition.StartSeconds, transition.StartSeconds + transition.DurationSeconds)
                    ?? SelectCapability(boundedCapabilities, MovieDirectorAudioBridgeKinds.Music, transition.StartSeconds, transition.StartSeconds + transition.DurationSeconds);
                if (transitionCapability is not null)
                {
                    recommendations.Add(CreateRecommendation(
                        canonicalTimeline,
                        transitionCapability,
                        from,
                        to,
                        transition.Id,
                        transition.StartSeconds,
                        transition.DurationSeconds,
                        "Support the existing visual transition with approved audio continuity; the visual transition remains canonical."));
                }
            }
        }

        if (clips.Length < 2)
            unresolved.Add("timeline:needs_at_least_two_clips_for_bridge_planning");
        if (recommendations.Count == 0 && unresolved.Count == 0)
            unresolved.Add("timeline:no_missing_bridge_or_transition_smoothing_candidate");

        return new MovieDirectorEditRepairAudioPlan(
            canonicalTimeline.TimelineId,
            canonicalTimeline.Version,
            Fingerprint(canonicalTimeline),
            recommendations,
            unresolved.Distinct(StringComparer.Ordinal).Take(32).ToArray());
    }

    public static void ValidatePlan(MovieDirectorEditRepairAudioPlan plan)
    {
        if (plan is null || plan.TimelineId == Guid.Empty || plan.TimelineVersion <= 0 || string.IsNullOrWhiteSpace(plan.CanonicalTimelineFingerprint))
            throw new DirectorEditRepairAudioValidationException("The audio repair plan has no valid canonical timeline identity.");
        if (plan.Recommendations is null || plan.Recommendations.Count > MaxRecommendations)
            throw new DirectorEditRepairAudioValidationException("The audio repair plan exceeds the supported recommendation limit.");
        var recommendationIds = new HashSet<Guid>();
        foreach (var recommendation in plan.Recommendations)
        {
            if (recommendation.RecommendationId == Guid.Empty || !recommendationIds.Add(recommendation.RecommendationId))
                throw new DirectorEditRepairAudioValidationException("Audio bridge recommendation ids must be unique.");
            if (recommendation.TimelineId != plan.TimelineId || recommendation.TimelineVersion != plan.TimelineVersion)
                throw new DirectorEditRepairAudioValidationException("Audio bridge recommendations must target the same canonical timeline version.");
            if (!MovieDirectorAudioBridgeKinds.Supported.Contains(recommendation.Kind) || !MovieSoundLayers.Supported.Contains(recommendation.Layer))
                throw new DirectorEditRepairAudioValidationException("The audio bridge kind or layer is not supported.");
            if (recommendation.StartSeconds < 0m || recommendation.DurationSeconds <= 0m || recommendation.StartSeconds + recommendation.DurationSeconds > 86_400m)
                throw new DirectorEditRepairAudioValidationException("Audio bridge timing is outside the supported range.");
            if (string.IsNullOrWhiteSpace(recommendation.Rationale) || recommendation.Rationale.Length > 2_000)
                throw new DirectorEditRepairAudioValidationException("Audio bridge rationale is required and bounded.");
            if (!recommendation.RequiresUserOverride)
                throw new DirectorEditRepairAudioValidationException("Audio bridge recommendations must remain behind an explicit user override.");
        }
    }

    public static string Fingerprint(MovieCanonicalTimelineContract timeline) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(timeline, DirectorJson.Options)))).ToLowerInvariant();

    private static MovieDirectorAudioBridgeRecommendation CreateRecommendation(
        MovieCanonicalTimelineContract timeline,
        MovieDirectorAudioCapabilitySnapshot capability,
        MovieTimelineClipContract from,
        MovieTimelineClipContract to,
        Guid? transitionId,
        decimal start,
        decimal duration,
        string rationale)
    {
        var layer = capability.Layer?.Trim().ToLowerInvariant();
        if (!MovieSoundLayers.Supported.Contains(layer ?? string.Empty))
            layer = capability.Kind.Equals(MovieDirectorAudioBridgeKinds.Music, StringComparison.OrdinalIgnoreCase)
                ? MovieSoundLayers.Background
                : capability.Kind.Equals(MovieDirectorAudioBridgeKinds.Ambience, StringComparison.OrdinalIgnoreCase)
                    ? MovieSoundLayers.Environment
                    : MovieSoundLayers.Foreground;
        return new MovieDirectorAudioBridgeRecommendation(
            Guid.NewGuid(), timeline.TimelineId, timeline.Version, capability.Kind.Trim().ToLowerInvariant(), layer!,
            from.ClipId, to.ClipId, transitionId, decimal.Round(start, 3), decimal.Round(duration, 3), capability.SourceId,
            capability.SourceKind, rationale);
    }

    private static MovieDirectorAudioCapabilitySnapshot? SelectCapability(
        IEnumerable<MovieDirectorAudioCapabilitySnapshot> capabilities,
        string kind,
        decimal start,
        decimal end) => capabilities.FirstOrDefault(item =>
            string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && (!item.StartSeconds.HasValue || !item.EndSeconds.HasValue || (item.StartSeconds.Value <= start && item.EndSeconds.Value >= end)));

    private static bool HasCoveredRecommendation(IEnumerable<MovieDirectorAudioBridgeRecommendation> recommendations, string kind, decimal start, decimal end) =>
        recommendations.Any(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && item.StartSeconds <= start && item.StartSeconds + item.DurationSeconds >= end);

    private static bool IsUsableCapability(MovieDirectorAudioCapabilitySnapshot item) =>
        item.SourceId != Guid.Empty && item.IsApproved && MovieDirectorAudioBridgeKinds.Supported.Contains(item.Kind)
        && !string.IsNullOrWhiteSpace(item.Label) && item.Label.Length <= 160
        && (string.IsNullOrWhiteSpace(item.SourceKind) || item.SourceKind.Length <= 40)
        && (!item.StartSeconds.HasValue || item.StartSeconds >= 0m)
        && (!item.EndSeconds.HasValue || item.EndSeconds > item.StartSeconds);

    private static int Priority(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        MovieDirectorAudioBridgeKinds.Ambience => 0,
        MovieDirectorAudioBridgeKinds.Music => 1,
        MovieDirectorAudioBridgeKinds.SoundEffect => 2,
        _ => 3,
    };
}

public sealed class MovieDirectorEditRepairAudioActionExecutor : IDirectorActionExecutor
{
    public string ActionType => DirectorActionTypes.EditRepairAudio;

    public Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, Guid executingUserId, CancellationToken cancellationToken = default)
    {
        DirectorEditRepairAudioActionPayload? payload;
        try { payload = JsonSerializer.Deserialize<DirectorEditRepairAudioActionPayload>(action.PayloadJson, DirectorJson.Options); }
        catch (JsonException) { payload = null; }
        if (payload is null || !DirectorEditRepairAudioActionTypes.IsSupported(payload.Action))
            return Task.FromResult(new DirectorActionExecution(false, "DIRECTOR_EDIT_REPAIR_AUDIO_INVALID", "The audio edit-repair plan is invalid.", null));
        try
        {
            MovieDirectorEditRepairAudioPlanner.ValidatePlan(payload.Plan);
        }
        catch (DirectorEditRepairAudioValidationException exception)
        {
            return Task.FromResult(new DirectorActionExecution(false, "DIRECTOR_EDIT_REPAIR_AUDIO_INVALID", exception.Message, null));
        }
        var result = JsonSerializer.Serialize(new
        {
            action = payload.Action,
            plan = payload.Plan,
            providerCalled = false,
            canonicalTimelineMutated = false,
            userOverrideRequired = true,
        }, DirectorJson.Options);
        return Task.FromResult(new DirectorActionExecution(
            true,
            null,
            "The audio bridge plan was recorded for review; no provider was called and the canonical timeline was not changed.",
            result));
    }
}

public static class MovieDirectorAudioCapabilityMapper
{
    public static MovieDirectorAudioCapabilitySnapshot FromSoundTrack(MovieSoundTrack track) => new(
        track.AssetId ?? track.Id,
        track.Kind,
        track.SourceKind,
        track.Name,
        string.Equals(track.Status, MovieSoundStatuses.Approved, StringComparison.OrdinalIgnoreCase),
        track.Layer,
        track.StartMilliseconds / 1_000m,
        track.EndMilliseconds / 1_000m);

    public static MovieDirectorAudioCapabilitySnapshot FromSoundtrackCue(MovieSoundtrackCue cue) => new(
        cue.ApprovedVersionId ?? cue.Id,
        MovieDirectorAudioBridgeKinds.Music,
        MovieDirectorAudioBridgeSourceKinds.SoundtrackCue,
        cue.Title,
        string.Equals(cue.ApprovalState, MovieSoundtrackApprovalStates.Approved, StringComparison.OrdinalIgnoreCase) && cue.ApprovedVersionId.HasValue,
        MovieSoundLayers.Background,
        cue.TimelineStartSeconds,
        cue.TimelineStartSeconds + cue.DurationSeconds);
}
