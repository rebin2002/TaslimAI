using System.Text.Json;

namespace Taslim.Api.Movies.DialogueTiming;

/// <summary>
/// Versioned, provider-neutral contract for planning dialogue alignment against a selected
/// visual take and an approved voice asset. This contract is intentionally persistence- and
/// execution-free: a future adapter may consume an eligible plan, but this planner never calls
/// a media provider, creates a generation job, or changes usage accounting.
/// </summary>
public static class MovieDialogueTimingContract
{
    public const int Version = 1;
    public const int MinimumDurationMilliseconds = 1;
    public const int MaximumDurationMilliseconds = 3_600_000;
    public const int MaximumCueCount = 256;
    public const int MaximumTextLength = 4_000;
    public const int MaximumCharacterNameLength = 160;
    public const int MaximumDriftThresholdMilliseconds = 5_000;

    public static readonly IReadOnlySet<string> SupportedVoiceAssetTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio",
    };

    public static readonly IReadOnlySet<string> SupportedVisualAssetTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "video",
    };

    public static readonly IReadOnlySet<string> SupportedVisualTakeStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        MovieTakeStatuses.Selected,
    };
}

public static class MovieLipSyncEligibilityStates
{
    public const string Eligible = "eligible";
    public const string NeedsReview = "needs_review";
    public const string Blocked = "blocked";
}

public static class MovieLipSyncReviewStates
{
    public const string NotRequired = "not_required";
    public const string Pending = "pending";
    public const string Blocked = "blocked";
}

public static class MovieVoiceAssetApprovalStates
{
    public const string Approved = "Approved";
}

public static class MovieDialogueAlignmentStates
{
    public const string Aligned = "aligned";
    public const string NeedsReview = "needs_review";
}

public static class MovieLipSyncReasonCodes
{
    public const string Eligible = "eligible";
    public const string VisualTakeNotSelected = "visual_take_not_selected";
    public const string VisualTakeNotReady = "visual_take_not_ready";
    public const string VisualMediaUnavailable = "visual_media_unavailable";
    public const string VoiceAssetNotApproved = "voice_asset_not_approved";
    public const string VoiceMediaUnavailable = "voice_media_unavailable";
    public const string TimingReviewRequired = "timing_review_required";
    public const string DurationReviewRequired = "duration_review_required";
    public const string LanguageReviewRequired = "language_review_required";
}

public static class MovieLipSyncEscalationTypes
{
    public const string Timing = "timing";
    public const string Duration = "duration";
    public const string Language = "language";
}

/// <summary>
/// Server-resolved identity and readiness facts for the visual take. The shot pointer is
/// carried separately from the take identity so a stale or fabricated selection cannot pass
/// planning merely because the take itself has a selected-looking status.
/// </summary>
public sealed record MovieSelectedVisualTakeReference(
    Guid MovieShotId,
    Guid MovieTakeId,
    Guid? ShotSelectedTakeId,
    string TakeStatus,
    Guid? VisualAssetId,
    string VisualAssetType,
    bool HasPublishedVideo,
    int DurationMilliseconds,
    bool IsFinal = false);

/// <summary>
/// Server-resolved facts for an audio Asset that has passed the product's voice approval
/// boundary. Approval is an input fact; this planner does not approve or mutate assets.
/// </summary>
public sealed record MovieApprovedVoiceAssetReference(
    Guid AssetId,
    string ApprovalState,
    string AssetType,
    bool HasPublishedAudio,
    int DurationMilliseconds,
    string? Language = null);

/// <summary>
/// Authored or externally measured timing windows. The planner compares the voice and visual
/// windows; it does not infer phonemes, inspect media bytes, or claim that a provider aligned
/// the speech.
/// </summary>
public sealed record MovieDialogueTimingCue(
    Guid CueId,
    int Sequence,
    string Text,
    int VoiceStartMilliseconds,
    int VoiceEndMilliseconds,
    int VisualStartMilliseconds,
    int VisualEndMilliseconds,
    string? CharacterName = null);

public sealed record MovieDialogueTimingPlanningRequest(
    MovieSelectedVisualTakeReference VisualTake,
    MovieApprovedVoiceAssetReference VoiceAsset,
    IReadOnlyList<MovieDialogueTimingCue> Cues,
    string? VisualLanguage = null,
    int MaximumStartDriftMilliseconds = 80,
    int MaximumEndDriftMilliseconds = 120,
    int MaximumDurationDeltaMilliseconds = 150);

public sealed record MovieDialogueTimingValidationError(string Code, string Field, string Message);

public sealed class MovieDialogueTimingValidationException(
    IReadOnlyList<MovieDialogueTimingValidationError> errors)
    : Exception("The dialogue timing and lip-sync planning request is invalid.")
{
    public IReadOnlyList<MovieDialogueTimingValidationError> Errors { get; } = errors;
}

public sealed record MovieDialogueAlignmentCue(
    Guid CueId,
    int Sequence,
    string Text,
    string? CharacterName,
    int VoiceStartMilliseconds,
    int VoiceEndMilliseconds,
    int VisualStartMilliseconds,
    int VisualEndMilliseconds,
    int StartDriftMilliseconds,
    int EndDriftMilliseconds,
    int DurationDeltaMilliseconds,
    string AlignmentState);

public sealed record MovieDialogueSynchronizationMetadata(
    int VoiceDurationMilliseconds,
    int VisualDurationMilliseconds,
    int DurationDeltaMilliseconds,
    int MaximumAbsoluteStartDriftMilliseconds,
    int MaximumAbsoluteEndDriftMilliseconds,
    int MaximumAbsoluteCueDurationDeltaMilliseconds,
    decimal MeanAbsoluteStartDriftMilliseconds,
    decimal MeanAbsoluteEndDriftMilliseconds,
    int CueCount);

public sealed record MovieLipSyncEscalation(
    string Type,
    string Code,
    string Message,
    IReadOnlyList<Guid> CueIds);

public sealed record MovieLipSyncPlan(
    int ContractVersion,
    Guid MovieShotId,
    Guid MovieTakeId,
    Guid VisualAssetId,
    Guid VoiceAssetId,
    string EligibilityState,
    string ReviewState,
    string DecisionCode,
    string DecisionMessage,
    MovieDialogueSynchronizationMetadata Synchronization,
    IReadOnlyList<MovieDialogueAlignmentCue> Cues,
    IReadOnlyList<MovieLipSyncEscalation> Escalations);

public static class MovieDialogueTimingRequestValidator
{
    public static IReadOnlyList<MovieDialogueTimingValidationError> Validate(MovieDialogueTimingPlanningRequest? request)
    {
        var errors = new List<MovieDialogueTimingValidationError>();
        if (request is null)
        {
            errors.Add(new("required", "request", "A planning request is required."));
            return errors;
        }

        ValidateVisualTake(errors, request.VisualTake);
        ValidateVoiceAsset(errors, request.VoiceAsset);
        ValidateThreshold(errors, nameof(request.MaximumStartDriftMilliseconds), request.MaximumStartDriftMilliseconds);
        ValidateThreshold(errors, nameof(request.MaximumEndDriftMilliseconds), request.MaximumEndDriftMilliseconds);
        ValidateThreshold(errors, nameof(request.MaximumDurationDeltaMilliseconds), request.MaximumDurationDeltaMilliseconds);
        ValidateCues(errors, request.Cues, request.VisualTake?.DurationMilliseconds ?? 0, request.VoiceAsset?.DurationMilliseconds ?? 0);
        return errors;
    }

    private static void ValidateVisualTake(ICollection<MovieDialogueTimingValidationError> errors, MovieSelectedVisualTakeReference? take)
    {
        if (take is null)
        {
            errors.Add(new("required", nameof(MovieDialogueTimingPlanningRequest.VisualTake), "A selected visual take reference is required."));
            return;
        }

        if (take.MovieShotId == Guid.Empty)
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VisualTake)}.{nameof(take.MovieShotId)}", "A movie shot is required."));
        if (take.MovieTakeId == Guid.Empty)
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VisualTake)}.{nameof(take.MovieTakeId)}", "A movie take is required."));
        if (take.ShotSelectedTakeId == Guid.Empty)
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VisualTake)}.{nameof(take.ShotSelectedTakeId)}", "The shot's selected take pointer is required."));
        if (take.VisualAssetId == Guid.Empty)
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VisualTake)}.{nameof(take.VisualAssetId)}", "A visual asset is required."));
        if (string.IsNullOrWhiteSpace(take.TakeStatus))
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VisualTake)}.{nameof(take.TakeStatus)}", "The visual take status is required."));
        if (string.IsNullOrWhiteSpace(take.VisualAssetType))
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VisualTake)}.{nameof(take.VisualAssetType)}", "The visual asset type is required."));
        ValidateDuration(errors, $"{nameof(MovieDialogueTimingPlanningRequest.VisualTake)}.{nameof(take.DurationMilliseconds)}", take.DurationMilliseconds);
    }

    private static void ValidateVoiceAsset(ICollection<MovieDialogueTimingValidationError> errors, MovieApprovedVoiceAssetReference? asset)
    {
        if (asset is null)
        {
            errors.Add(new("required", nameof(MovieDialogueTimingPlanningRequest.VoiceAsset), "An approved voice asset reference is required."));
            return;
        }

        if (asset.AssetId == Guid.Empty)
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VoiceAsset)}.{nameof(asset.AssetId)}", "A voice asset is required."));
        if (string.IsNullOrWhiteSpace(asset.ApprovalState))
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VoiceAsset)}.{nameof(asset.ApprovalState)}", "The voice approval state is required."));
        if (string.IsNullOrWhiteSpace(asset.AssetType))
            errors.Add(new("required", $"{nameof(MovieDialogueTimingPlanningRequest.VoiceAsset)}.{nameof(asset.AssetType)}", "The voice asset type is required."));
        ValidateDuration(errors, $"{nameof(MovieDialogueTimingPlanningRequest.VoiceAsset)}.{nameof(asset.DurationMilliseconds)}", asset.DurationMilliseconds);
        if (asset.Language?.Length > 16)
            errors.Add(new("too_long", $"{nameof(MovieDialogueTimingPlanningRequest.VoiceAsset)}.{nameof(asset.Language)}", "The voice language is too long."));
    }

    private static void ValidateThreshold(ICollection<MovieDialogueTimingValidationError> errors, string field, int value)
    {
        if (value is < 0 or > MovieDialogueTimingContract.MaximumDriftThresholdMilliseconds)
            errors.Add(new("out_of_range", field, $"{field} must be between 0 and {MovieDialogueTimingContract.MaximumDriftThresholdMilliseconds} milliseconds."));
    }

    private static void ValidateDuration(ICollection<MovieDialogueTimingValidationError> errors, string field, int value)
    {
        if (value is < MovieDialogueTimingContract.MinimumDurationMilliseconds or > MovieDialogueTimingContract.MaximumDurationMilliseconds)
            errors.Add(new("out_of_range", field, $"Duration must be between {MovieDialogueTimingContract.MinimumDurationMilliseconds} and {MovieDialogueTimingContract.MaximumDurationMilliseconds} milliseconds."));
    }

    private static void ValidateCues(
        ICollection<MovieDialogueTimingValidationError> errors,
        IReadOnlyList<MovieDialogueTimingCue>? cues,
        int visualDurationMilliseconds,
        int voiceDurationMilliseconds)
    {
        if (cues is null || cues.Count == 0)
        {
            errors.Add(new("required", nameof(MovieDialogueTimingPlanningRequest.Cues), "At least one dialogue timing cue is required."));
            return;
        }
        if (cues.Count > MovieDialogueTimingContract.MaximumCueCount)
            errors.Add(new("too_many_items", nameof(MovieDialogueTimingPlanningRequest.Cues), $"No more than {MovieDialogueTimingContract.MaximumCueCount} cues are allowed."));

        var cueIds = new HashSet<Guid>();
        var sequences = new HashSet<int>();
        var indexedCues = cues
            .Select((cue, index) => (Cue: cue, Index: index))
            .Where(item => item.Cue is not null)
            .OrderBy(item => item.Cue!.Sequence)
            .ThenBy(item => item.Cue!.CueId)
            .ToArray();
        MovieDialogueTimingCue? previous = null;
        foreach (var indexedCue in indexedCues)
        {
            var cue = indexedCue.Cue!;
            var field = $"{nameof(MovieDialogueTimingPlanningRequest.Cues)}[{indexedCue.Index}]";
            if (cue is null)
            {
                errors.Add(new("required", field, "A dialogue timing cue is required."));
                continue;
            }
            if (cue.CueId == Guid.Empty)
                errors.Add(new("required", $"{field}.{nameof(cue.CueId)}", "A cue identity is required."));
            else if (!cueIds.Add(cue.CueId))
                errors.Add(new("duplicate", $"{field}.{nameof(cue.CueId)}", "Cue identities must be unique."));
            if (cue.Sequence < 1)
                errors.Add(new("out_of_range", $"{field}.{nameof(cue.Sequence)}", "Cue sequence must be 1 or greater."));
            else if (!sequences.Add(cue.Sequence))
                errors.Add(new("duplicate", $"{field}.{nameof(cue.Sequence)}", "Cue sequences must be unique."));
            if (string.IsNullOrWhiteSpace(cue.Text))
                errors.Add(new("required", $"{field}.{nameof(cue.Text)}", "Cue text is required."));
            else if (cue.Text.Length > MovieDialogueTimingContract.MaximumTextLength)
                errors.Add(new("too_long", $"{field}.{nameof(cue.Text)}", $"Cue text must be {MovieDialogueTimingContract.MaximumTextLength} characters or fewer."));
            if (cue.CharacterName?.Length > MovieDialogueTimingContract.MaximumCharacterNameLength)
                errors.Add(new("too_long", $"{field}.{nameof(cue.CharacterName)}", $"Character name must be {MovieDialogueTimingContract.MaximumCharacterNameLength} characters or fewer."));

            ValidateWindow(errors, $"{field}.{nameof(cue.VoiceStartMilliseconds)}", $"{field}.{nameof(cue.VoiceEndMilliseconds)}", cue.VoiceStartMilliseconds, cue.VoiceEndMilliseconds, voiceDurationMilliseconds);
            ValidateWindow(errors, $"{field}.{nameof(cue.VisualStartMilliseconds)}", $"{field}.{nameof(cue.VisualEndMilliseconds)}", cue.VisualStartMilliseconds, cue.VisualEndMilliseconds, visualDurationMilliseconds);
            if (previous is not null)
            {
                if (cue.VoiceStartMilliseconds < previous.VoiceEndMilliseconds)
                    errors.Add(new("overlap", $"{field}.{nameof(cue.VoiceStartMilliseconds)}", "Voice cue windows must not overlap."));
                if (cue.VisualStartMilliseconds < previous.VisualEndMilliseconds)
                    errors.Add(new("overlap", $"{field}.{nameof(cue.VisualStartMilliseconds)}", "Visual cue windows must not overlap."));
            }
            previous = cue;
        }
    }

    private static void ValidateWindow(ICollection<MovieDialogueTimingValidationError> errors, string startField, string endField, int start, int end, int duration)
    {
        if (start < 0)
            errors.Add(new("out_of_range", startField, "Timing must not be negative."));
        if (end <= start)
            errors.Add(new("invalid_window", endField, "Timing end must be greater than timing start."));
        if (duration > 0 && end > duration)
            errors.Add(new("out_of_range", endField, "Timing must fit within the referenced media duration."));
    }
}

public static class MovieLipSyncPlanner
{
    public static MovieLipSyncPlan Plan(MovieDialogueTimingPlanningRequest request)
    {
        var errors = MovieDialogueTimingRequestValidator.Validate(request);
        if (errors.Count > 0) throw new MovieDialogueTimingValidationException(errors);

        var visualTake = request.VisualTake;
        var voiceAsset = request.VoiceAsset;
        var cues = request.Cues.OrderBy(item => item.Sequence).ThenBy(item => item.CueId).ToArray();
        var alignedCues = cues.Select(cue => ToAlignmentCue(
            cue,
            request.MaximumStartDriftMilliseconds,
            request.MaximumEndDriftMilliseconds,
            request.MaximumDurationDeltaMilliseconds)).ToArray();
        var synchronization = BuildSynchronization(visualTake.DurationMilliseconds, voiceAsset.DurationMilliseconds, alignedCues);
        var escalations = BuildEscalations(request, alignedCues, synchronization);
        var blockedDecision = ResolveBlockedDecision(visualTake, voiceAsset);

        if (blockedDecision is not null)
        {
            return new MovieLipSyncPlan(
                MovieDialogueTimingContract.Version,
                visualTake.MovieShotId,
                visualTake.MovieTakeId,
                visualTake.VisualAssetId!.Value,
                voiceAsset.AssetId,
                MovieLipSyncEligibilityStates.Blocked,
                MovieLipSyncReviewStates.Blocked,
                blockedDecision.Value.Code,
                blockedDecision.Value.Message,
                synchronization,
                alignedCues,
                escalations);
        }

        var needsReview = escalations.Count > 0;
        return new MovieLipSyncPlan(
            MovieDialogueTimingContract.Version,
            visualTake.MovieShotId,
            visualTake.MovieTakeId,
            visualTake.VisualAssetId!.Value,
            voiceAsset.AssetId,
            needsReview ? MovieLipSyncEligibilityStates.NeedsReview : MovieLipSyncEligibilityStates.Eligible,
            needsReview ? MovieLipSyncReviewStates.Pending : MovieLipSyncReviewStates.NotRequired,
            needsReview ? escalations[0].Code : MovieLipSyncReasonCodes.Eligible,
            needsReview ? "Dialogue timing requires review before lip-sync execution is considered eligible." : "The selected visual take and approved voice asset have a deterministic alignment plan.",
            synchronization,
            alignedCues,
            escalations);
    }

    public static string SerializeProductSafe(MovieLipSyncPlan plan) => JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static MovieDialogueAlignmentCue ToAlignmentCue(
        MovieDialogueTimingCue cue,
        int maximumStartDriftMilliseconds,
        int maximumEndDriftMilliseconds,
        int maximumDurationDeltaMilliseconds)
    {
        var startDrift = cue.VisualStartMilliseconds - cue.VoiceStartMilliseconds;
        var endDrift = cue.VisualEndMilliseconds - cue.VoiceEndMilliseconds;
        var voiceDuration = cue.VoiceEndMilliseconds - cue.VoiceStartMilliseconds;
        var visualDuration = cue.VisualEndMilliseconds - cue.VisualStartMilliseconds;
        var durationDelta = visualDuration - voiceDuration;
        return new MovieDialogueAlignmentCue(
            cue.CueId,
            cue.Sequence,
            cue.Text,
            cue.CharacterName,
            cue.VoiceStartMilliseconds,
            cue.VoiceEndMilliseconds,
            cue.VisualStartMilliseconds,
            cue.VisualEndMilliseconds,
            startDrift,
            endDrift,
            durationDelta,
            Math.Abs(startDrift) <= maximumStartDriftMilliseconds
                && Math.Abs(endDrift) <= maximumEndDriftMilliseconds
                && Math.Abs(durationDelta) <= maximumDurationDeltaMilliseconds
                ? MovieDialogueAlignmentStates.Aligned
                : MovieDialogueAlignmentStates.NeedsReview);
    }

    private static MovieDialogueSynchronizationMetadata BuildSynchronization(
        int visualDurationMilliseconds,
        int voiceDurationMilliseconds,
        IReadOnlyList<MovieDialogueAlignmentCue> cues)
    {
        var absoluteStart = cues.Select(item => Math.Abs(item.StartDriftMilliseconds)).ToArray();
        var absoluteEnd = cues.Select(item => Math.Abs(item.EndDriftMilliseconds)).ToArray();
        var absoluteDuration = cues.Select(item => Math.Abs(item.DurationDeltaMilliseconds)).ToArray();
        return new MovieDialogueSynchronizationMetadata(
            voiceDurationMilliseconds,
            visualDurationMilliseconds,
            visualDurationMilliseconds - voiceDurationMilliseconds,
            absoluteStart.Max(),
            absoluteEnd.Max(),
            absoluteDuration.Max(),
            decimal.Round(absoluteStart.Select(value => (decimal)value).Average(), 3),
            decimal.Round(absoluteEnd.Select(value => (decimal)value).Average(), 3),
            cues.Count);
    }

    private static IReadOnlyList<MovieLipSyncEscalation> BuildEscalations(
        MovieDialogueTimingPlanningRequest request,
        IReadOnlyList<MovieDialogueAlignmentCue> cues,
        MovieDialogueSynchronizationMetadata synchronization)
    {
        var escalations = new List<MovieLipSyncEscalation>();
        var timingCues = cues.Where(item => Math.Abs(item.StartDriftMilliseconds) > request.MaximumStartDriftMilliseconds || Math.Abs(item.EndDriftMilliseconds) > request.MaximumEndDriftMilliseconds).Select(item => item.CueId).ToArray();
        if (timingCues.Length > 0)
        {
            escalations.Add(new(
                MovieLipSyncEscalationTypes.Timing,
                MovieLipSyncReasonCodes.TimingReviewRequired,
                "One or more dialogue windows exceed the configured start or end drift tolerance.",
                timingCues));
        }

        if (Math.Abs(synchronization.DurationDeltaMilliseconds) > request.MaximumDurationDeltaMilliseconds || synchronization.MaximumAbsoluteCueDurationDeltaMilliseconds > request.MaximumDurationDeltaMilliseconds)
        {
            escalations.Add(new(
                MovieLipSyncEscalationTypes.Duration,
                MovieLipSyncReasonCodes.DurationReviewRequired,
                "Voice and visual durations differ beyond the configured synchronization tolerance.",
                cues.Where(item => Math.Abs(item.DurationDeltaMilliseconds) > request.MaximumDurationDeltaMilliseconds).Select(item => item.CueId).ToArray()));
        }

        if (!string.IsNullOrWhiteSpace(request.VisualLanguage) && !string.IsNullOrWhiteSpace(request.VoiceAsset.Language) && !string.Equals(request.VisualLanguage.Trim(), request.VoiceAsset.Language.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            escalations.Add(new(
                MovieLipSyncEscalationTypes.Language,
                MovieLipSyncReasonCodes.LanguageReviewRequired,
                "The visual and approved voice languages differ and require editorial review.",
                cues.Select(item => item.CueId).ToArray()));
        }
        return escalations;
    }

    private static (string Code, string Message)? ResolveBlockedDecision(
        MovieSelectedVisualTakeReference take,
        MovieApprovedVoiceAssetReference voiceAsset)
    {
        if (take.ShotSelectedTakeId != take.MovieTakeId)
            return (MovieLipSyncReasonCodes.VisualTakeNotSelected, "Only the shot's explicitly selected visual take may be planned for lip-sync.");
        if (!MovieDialogueTimingContract.SupportedVisualTakeStatuses.Contains(take.TakeStatus))
            return (MovieLipSyncReasonCodes.VisualTakeNotReady, "The selected visual take is not in a lip-sync-ready state.");
        if (!MovieDialogueTimingContract.SupportedVisualAssetTypes.Contains(take.VisualAssetType))
            return (MovieLipSyncReasonCodes.VisualMediaUnavailable, "The selected visual take does not reference a supported video asset.");
        if (!take.HasPublishedVideo)
            return (MovieLipSyncReasonCodes.VisualMediaUnavailable, "The selected visual take has no published video media.");
        if (!string.Equals(voiceAsset.ApprovalState, MovieVoiceAssetApprovalStates.Approved, StringComparison.OrdinalIgnoreCase))
            return (MovieLipSyncReasonCodes.VoiceAssetNotApproved, "An approved voice asset is required before lip-sync planning can proceed.");
        if (!MovieDialogueTimingContract.SupportedVoiceAssetTypes.Contains(voiceAsset.AssetType) || !voiceAsset.HasPublishedAudio)
            return (MovieLipSyncReasonCodes.VoiceMediaUnavailable, "The approved voice asset has no published audio media.");
        return null;
    }
}
