using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Taslim.Api.Movies.AdaptiveResolution;

namespace Taslim.Api.Movies;

public static class MovieProductionPreflightDecisionCodes
{
    public const string Ready = "ready";
    public const string ReferenceWorkRequired = "reference_work_required";
    public const string QualityReviewRequired = "quality_review_required";
    public const string CostConfirmationRequired = "cost_confirmation_required";
    public const string CostGuardrailBlocked = "cost_guardrail_blocked";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ClipDurationUnsupported = "clip_duration_unsupported";
    public const string ExecutionIntentUnsupported = "execution_intent_unsupported";
    public const string PromptCapacityUnsupported = "creative_text_capacity_unsupported";
}

public static class MovieProductionPreflightCheckKeys
{
    public const string ShotPlan = "shot_plan";
    public const string Storyboard = "approved_storyboard";
    public const string Keyframe = "approved_keyframe";
    public const string MotionPreview = "approved_motion_preview";
    public const string ReferencePackage = "reference_package";
    public const string AdaptiveQuality = "adaptive_quality";
    public const string CostGuardrails = "cost_guardrails";
    public const string ProviderAdmission = "provider_admission";
    public const string ClipDuration = "clip_duration";
    public const string PromptCapacity = "creative_text_capacity";
}

public sealed record MovieProductionPreflightRequest(
    Guid? SourceProductionVersionId = null,
    string? TargetResolution = null,
    string? QualityTier = null,
    bool ConfirmationAccepted = false);

public sealed record MovieProductionPreflightCheckDto(
    string Key,
    string Label,
    bool Satisfied,
    bool Required,
    string Detail);

public sealed record MovieProductionPreflightRecommendationDto(
    string Key,
    string Label,
    string Detail,
    string? SuggestedStage = null);

/// <summary>
/// Safe adaptive-resolution output for a production preflight. It contains no provider,
/// model, prompt, credential, or upstream execution identifiers.
/// </summary>
public sealed record MovieProductionAdaptiveResolutionDto(
    string SourceResolution,
    string TargetResolution,
    string ProcessingPath,
    string QualityTier,
    decimal QualityConfidence,
    decimal MinimumQualityConfidence,
    bool QcEscalationRequired,
    string? EscalateToSourceResolution,
    IReadOnlyList<string> ReasonCodes);

/// <summary>
/// User-safe, read-only production hand-off decision. This is a preview only: it never
/// creates a generation job, persists a production version, calls a provider, or charges a
/// customer. The same service is re-evaluated immediately before a render is queued.
/// </summary>
public sealed record MovieProductionPreflightDto(
    int SchemaVersion,
    Guid MovieShotId,
    Guid? SourceProductionVersionId,
    bool IsExpensiveProduction,
    bool CanProceed,
    string Decision,
    string Summary,
    IReadOnlyList<MovieProductionPreflightCheckDto> Checks,
    IReadOnlyList<MovieProductionPreflightRecommendationDto> Recommendations,
    MovieProductionAdaptiveResolutionDto AdaptiveResolution,
    GenerationCostPreviewDto CostGuardrails,
    string? RejectionCode = null,
    string? RejectionMessage = null);

public interface IMovieProductionPreflightService
{
    Task<MovieProductionPreflightDto?> EvaluateAsync(
        Guid userId,
        Guid shotId,
        MovieProductionPreflightRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record MovieProductionAdaptivePlan(
    string SourceResolution,
    string TargetResolution,
    string ProcessingPath,
    string QualityTier,
    AdaptiveResolution.AdaptiveResolutionDirectorRecommendation Recommendation);

/// <summary>
/// Builds the exact adaptive plan used by both preflight and the render orchestrator.
/// Keeping this calculation shared prevents the preview and the server-trusted queue path
/// from silently choosing different source resolutions or mastering paths.
/// </summary>
public static class MovieProductionAdaptivePlanBuilder
{
    public static MovieProductionAdaptivePlan Build(MovieShot shot, string? targetResolution, string? qualityTier)
    {
        var target = string.IsNullOrWhiteSpace(targetResolution) ? MovieResolutionTiers.P1080 : targetResolution.Trim();
        if (!MovieResolutionTiers.TryGet(target, out var targetTier))
            throw new MovieProductionValidationException("PRODUCTION_TARGET_RESOLUTION_INVALID", "Choose a supported production resolution.");
        var quality = DirectorQualityLevels.QualityTiers.FirstOrDefault(item =>
            string.Equals(item, string.IsNullOrWhiteSpace(qualityTier) ? DirectorQualityLevels.Standard : qualityTier.Trim(), StringComparison.OrdinalIgnoreCase));
        if (quality is null)
            throw new MovieProductionValidationException("PRODUCTION_QUALITY_INVALID", "Choose a supported production quality level.");

        var director = new AdaptiveResolution.AdaptiveResolutionDirector();
        var recommendation = director.Recommend(new AdaptiveResolution.AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = targetTier.Code,
            QualityTier = quality,
            DurationSeconds = Math.Clamp(shot.DurationSeconds ?? 1, 1, 3_600),
            Importance = string.IsNullOrWhiteSpace(shot.Purpose) ? 50 : 70,
            MotionComplexity = string.IsNullOrWhiteSpace(shot.CameraMotion) ? 35 : 65,
            CameraComplexity = string.IsNullOrWhiteSpace(shot.CameraAndFraming) ? 35 : 60,
            ContinuitySensitivity = string.IsNullOrWhiteSpace(shot.ContinuityReferences) ? 40 : 75,
            RequiredQualityDimensions = [],
            Constraints = new AdaptiveResolutionWave3Constraints(false, null, true),
        });
        return new(recommendation.SourceResolution, recommendation.MasterTargetResolution, recommendation.PipelinePath, quality, recommendation);
    }
}

public sealed class MovieProductionPreflightService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IMovieProductionReferencePackageService references,
    IMovieCharacterContinuityService continuity,
    IMovieVideoProvider provider,
    IMovieGenerationCostEstimator costEstimator,
    IGenerationCostGuardrailService costGuardrails) : IMovieProductionPreflightService
{
    public async Task<MovieProductionPreflightDto?> EvaluateAsync(
        Guid userId,
        Guid shotId,
        MovieProductionPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        var shot = await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide)
            .Include(item => item.ProductionVersions)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken))
            return null;

        var plan = MovieProductionAdaptivePlanBuilder.Build(shot, request.TargetResolution, request.QualityTier);
        var package = await references.GetForShotAsync(userId, shotId, cancellationToken);
        var readiness = MovieShotReadiness.Evaluate(shot);
        var versions = shot.ProductionVersions;
        var approvedStoryboard = versions.Any(item =>
            item.Stage == MovieProductionStages.ApprovedStoryboard && item.Status == MovieProductionVersionStatuses.Approved);
        var approvedKeyframe = versions.Any(item =>
            item.Stage == MovieProductionStages.ApprovedKeyframe && item.Status == MovieProductionVersionStatuses.Approved);
        var source = request.SourceProductionVersionId.HasValue
            ? versions.FirstOrDefault(item => item.Id == request.SourceProductionVersionId.Value)
            : null;
        var approvedMotionPreview = source is not null
            && source.Stage == MovieProductionStages.MotionPreview
            && source.Status == MovieProductionVersionStatuses.Approved;

        var packageWarnings = package?.Warnings ?? [];
        var blockingPackageWarnings = packageWarnings.Where(IsBlockingReferenceWarning).ToArray();
        var checks = new List<MovieProductionPreflightCheckDto>
        {
            new(MovieProductionPreflightCheckKeys.ShotPlan, "Shot plan", readiness.Ready, false,
                readiness.Ready ? "The shot plan is complete." : readiness.Summary),
            new(MovieProductionPreflightCheckKeys.Storyboard, "Approved storyboard", approvedStoryboard, true,
                approvedStoryboard ? "An approved storyboard is available." : "Create and approve a storyboard before motion work."),
            new(MovieProductionPreflightCheckKeys.Keyframe, "Approved source keyframe", approvedKeyframe, true,
                approvedKeyframe ? "An approved keyframe is available for continuity." : "Generate and approve a keyframe before production video."),
            new(MovieProductionPreflightCheckKeys.MotionPreview, "Approved motion preview", approvedMotionPreview, true,
                approvedMotionPreview ? "The selected motion preview is approved." : "Create and approve a motion preview before the production render."),
            BuildReferencePackageCheck(package, blockingPackageWarnings),
        };

        var adaptiveSatisfied = !plan.Recommendation.QcEscalationRequired;
        checks.Add(new MovieProductionPreflightCheckDto(
            MovieProductionPreflightCheckKeys.AdaptiveQuality,
            "Adaptive quality confidence",
            adaptiveSatisfied,
            true,
            adaptiveSatisfied
                ? $"The {plan.SourceResolution} source plan meets the {plan.Recommendation.QualityRequirement.MinimumConfidence:0.##} quality floor."
                : $"Quality confidence {plan.Recommendation.QualityConfidence:0.##} is below the {plan.Recommendation.QualityRequirement.MinimumConfidence:0.##} floor; review the higher-source escalation."));

        // The queue checks the real selected adapter before creating a clip, job or
        // usage reservation. Use its exact provider-owned decision in this read-only
        // preview: a film timeline is not one provider-sized video clip.
        var selectedDuration = shot.DurationSeconds ?? shot.Scene.DurationSeconds
            ?? Math.Min(shot.Scene.MovieProject.DurationSeconds, 60);
        var durationInPlatformRange = selectedDuration is >= 1 and <= 3_600;
        var admissionError = durationInPlatformRange
            ? MovieProductionGenerationOrchestrator.EvaluateProductionClipAdmission(
                provider, selectedDuration, plan.SourceResolution, plan.TargetResolution,
                plan.QualityTier, plan.ProcessingPath, shot.Scene.MovieProject.AspectRatio)
            : new MovieProductionValidationException(
                "MOVIE_CLIP_DURATION_UNSUPPORTED",
                "The selected shot or scene duration cannot be rendered as one clip. Create shorter shots or use a validated segmentation workflow.");
        // Duration support is independent of provider availability. An unavailable
        // adapter with no declared duration range must not be advertised as
        // supporting this clip merely because availability was checked first.
        var clipDurationAccepted = durationInPlatformRange
            && provider.SceneClipDurationRange is { } clipDurationRange
            && clipDurationRange.Supports(selectedDuration);
        checks.Add(new MovieProductionPreflightCheckDto(
            MovieProductionPreflightCheckKeys.ClipDuration,
            "Single production clip duration", clipDurationAccepted, true,
            clipDurationAccepted
                ? "The selected adapter accepts this single-clip duration."
                : provider.SceneClipDurationRange is null
                    ? "The selected adapter has not declared a verified single-clip duration range."
                    : "Create shorter shots or use a validated segmentation workflow; the selected clip is outside the supported duration."));
        checks.Add(new MovieProductionPreflightCheckDto(
            MovieProductionPreflightCheckKeys.ProviderAdmission,
            "Video provider output capability", admissionError is null, true,
            admissionError?.Message ?? "The selected adapter supports this output profile and scene-clip operation."));

        // A read-only, non-persisted continuity preview keeps the provider
        // prompt floor consistent with actual queue admission. Any custom title
        // supplied later remains subject to the queue's own validation.
        var minimumPromptCharacters = MovieProductionGenerationOrchestrator.MinimumProductionPromptCharacters(
            shot, shot.Scene.MovieProject.Title, provider);
        var continuityPreview = await continuity.BuildSnapshotForTargetAsync(
            shot.Scene.MovieProjectId, shot.Scene.Id, shot.Id,
            persist: false, cancellationToken);
        var totalPromptCharacters = minimumPromptCharacters + continuityPreview.SnapshotJson.Trim().Length;
        var promptFits = provider.SceneClipPromptMaxCharacters is not { } promptLimit
            || totalPromptCharacters <= promptLimit;
        checks.Add(new MovieProductionPreflightCheckDto(
            MovieProductionPreflightCheckKeys.PromptCapacity,
            "Creative text capacity", promptFits, true,
            promptFits
                ? "The current creative directions and continuity context fit the selected adapter's declared limit."
                : "Shorten the project, shot or character-continuity directions before rendering."));

        // Preserve the source duration when valid; any invalid value is a
        // read-only estimate placeholder and cannot authorize an actual render.
        var durationSeconds = Math.Clamp(selectedDuration, 1, 3_600);
        var estimate = await costEstimator.EstimateAsync(
            new MovieGenerationCostRequest(durationSeconds, plan.SourceResolution, plan.TargetResolution, plan.QualityTier, plan.ProcessingPath),
            selectedProviderKey: provider.Key,
            cancellationToken: cancellationToken);
        var guardrail = await costGuardrails.EvaluateAsync(
            userId,
            shot.Scene.MovieProject.WorkspaceId,
            shot.Scene.MovieProject.ProjectId,
            estimate.ToGenerationCostEstimate(),
            request.ConfirmationAccepted,
            cancellationToken);
        checks.Add(new MovieProductionPreflightCheckDto(
            MovieProductionPreflightCheckKeys.CostGuardrails,
            "Cost safety review",
            guardrail.CanProceed,
            true,
            guardrail.CanProceed
                ? estimate.IsEstimated && estimate.MaximumAmountUsd.HasValue
                    ? "The server-side estimate is within the configured safety envelope."
                    : "No priced route is available; the request remains provider-neutral and non-chargeable."
                : guardrail.RejectionMessage ?? "Review the server-side cost safety decision before continuing."));

        var recommendations = BuildRecommendations(readiness, approvedStoryboard, approvedKeyframe, approvedMotionPreview, source, package, blockingPackageWarnings, plan, guardrail, request.ConfirmationAccepted);
        if (!clipDurationAccepted && admissionError?.Code == "MOVIE_CLIP_DURATION_UNSUPPORTED")
            recommendations = recommendations.Concat([
                new MovieProductionPreflightRecommendationDto("shorten_production_shot",
                    "Create shorter shots", "Split the selected shot into adapter-supported clips before production.")]).ToArray();
        else if (admissionError?.Code == "MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED")
            recommendations = recommendations.Concat([
                new MovieProductionPreflightRecommendationDto("choose_supported_video_profile",
                    "Choose a supported output profile", "The selected output quality or resolution cannot be executed as priced.")]).ToArray();
        if (!promptFits)
            recommendations = recommendations.Concat([
                new MovieProductionPreflightRecommendationDto("shorten_production_prompt",
                    "Shorten production directions", "The video adapter cannot fit the current creative and continuity context.")]).ToArray();
        var requiredChecksPassed = checks.Where(item => item.Required).All(item => item.Satisfied);
        // An unavailable or incompatible video adapter is a hard production
        // blocker even when reference work is incomplete. Report the actionable
        // provider problem first rather than suggesting that completing reference
        // assets alone will make the render executable.
        var decision = requiredChecksPassed
            ? MovieProductionPreflightDecisionCodes.Ready
            : admissionError?.Code == "MOVIE_PROVIDER_UNAVAILABLE"
                ? MovieProductionPreflightDecisionCodes.ProviderUnavailable
                : !clipDurationAccepted
                    ? MovieProductionPreflightDecisionCodes.ClipDurationUnsupported
                    : admissionError?.Code == "MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED"
                        ? MovieProductionPreflightDecisionCodes.ExecutionIntentUnsupported
                        : !promptFits
                            ? MovieProductionPreflightDecisionCodes.PromptCapacityUnsupported
                            : checks.Any(item => !item.Satisfied && item.Key is MovieProductionPreflightCheckKeys.Storyboard or MovieProductionPreflightCheckKeys.Keyframe or MovieProductionPreflightCheckKeys.MotionPreview or MovieProductionPreflightCheckKeys.ReferencePackage)
                            ? MovieProductionPreflightDecisionCodes.ReferenceWorkRequired
                            : !adaptiveSatisfied
                                ? MovieProductionPreflightDecisionCodes.QualityReviewRequired
                                : !guardrail.CanProceed && guardrail.ConfirmationRequired
                                    ? MovieProductionPreflightDecisionCodes.CostConfirmationRequired
                                    : MovieProductionPreflightDecisionCodes.CostGuardrailBlocked;
        var canProceed = requiredChecksPassed;
        var summary = canProceed
            ? "Cheap reference work, adaptive quality, provider capability, and server-side cost safety checks are ready for production."
            : decision == MovieProductionPreflightDecisionCodes.ClipDurationUnsupported
                ? "Create shorter shots or use validated segmentation before production."
                : decision == MovieProductionPreflightDecisionCodes.PromptCapacityUnsupported
                    ? "Shorten creative directions or character continuity to fit the video adapter."
                : decision == MovieProductionPreflightDecisionCodes.ReferenceWorkRequired
                    ? "Complete the recommended reference work before starting this expensive production pass."
                    : decision is MovieProductionPreflightDecisionCodes.ProviderUnavailable or MovieProductionPreflightDecisionCodes.ExecutionIntentUnsupported
                        ? admissionError!.Message
                        : decision == MovieProductionPreflightDecisionCodes.QualityReviewRequired
                            ? "Review the adaptive quality escalation before starting production."
                            : guardrail.RejectionMessage ?? "Review the production preflight before continuing.";
        var rejectionCode = canProceed ? null
            : decision == MovieProductionPreflightDecisionCodes.ClipDurationUnsupported
                ? "MOVIE_CLIP_DURATION_UNSUPPORTED"
                : decision is MovieProductionPreflightDecisionCodes.ProviderUnavailable or MovieProductionPreflightDecisionCodes.ExecutionIntentUnsupported
                    ? admissionError!.Code
                : decision == MovieProductionPreflightDecisionCodes.PromptCapacityUnsupported
                    ? "MOVIE_CLIP_PROMPT_UNSUPPORTED"
                    : decision == MovieProductionPreflightDecisionCodes.CostConfirmationRequired
                        ? "GENERATION_CONFIRMATION_REQUIRED"
                        : guardrail.RejectionCode ?? "PRODUCTION_PREFLIGHT_REQUIRED";
        var rejectionMessage = canProceed ? null : summary;

        return new MovieProductionPreflightDto(
            1,
            shot.Id,
            request.SourceProductionVersionId,
            true,
            canProceed,
            decision,
            summary,
            checks,
            recommendations,
            new MovieProductionAdaptiveResolutionDto(
                plan.SourceResolution,
                plan.TargetResolution,
                plan.ProcessingPath,
                plan.QualityTier,
                plan.Recommendation.QualityConfidence,
                plan.Recommendation.QualityRequirement.MinimumConfidence,
                plan.Recommendation.QcEscalationRequired,
                plan.Recommendation.Escalation.EscalateToSourceResolution,
                plan.Recommendation.ReasonCodes),
            GenerationCostPreviewMapper.ToDto(guardrail),
            rejectionCode,
            rejectionMessage);
    }

    // A missing canonical reference package is a blocking condition even if the
    // warning list is empty: unavailable references cannot be treated as approved.
    internal static MovieProductionPreflightCheckDto BuildReferencePackageCheck(
        MovieProductionReferencePackageDto? package,
        IReadOnlyCollection<MovieProductionReferenceWarningDto> blockingWarnings)
    {
        var satisfied = package is not null && blockingWarnings.Count == 0;
        var detail = package is null
            ? "The reference package is unavailable; resolve the reference context before production."
            : blockingWarnings.Count == 0
                ? "No blocking reference conflicts were found."
                : $"Resolve {blockingWarnings.Count} blocking reference conflict{(blockingWarnings.Count == 1 ? "" : "s")} before production.";
        return new(MovieProductionPreflightCheckKeys.ReferencePackage, "Reference package", satisfied, true, detail);
    }

    private static IReadOnlyList<MovieProductionPreflightRecommendationDto> BuildRecommendations(
        MovieShotReadinessDto readiness,
        bool approvedStoryboard,
        bool approvedKeyframe,
        bool approvedMotionPreview,
        MovieProductionVersion? source,
        MovieProductionReferencePackageDto? package,
        IReadOnlyList<MovieProductionReferenceWarningDto> blockingWarnings,
        MovieProductionAdaptivePlan plan,
        GenerationCostPreflightResult guardrail,
        bool confirmationAccepted)
    {
        var recommendations = new List<MovieProductionPreflightRecommendationDto>();
        if (!readiness.Ready)
            recommendations.Add(new("complete_shot_plan", "Complete the shot plan", "Add the missing planning fields so the reference hand-off is explicit before production.", MovieProductionStages.ShotPlan));
        if (!approvedStoryboard)
            recommendations.Add(new("approve_storyboard", "Approve a storyboard", "Create a low-cost visual plan and approve it before generating motion.", MovieProductionStages.ApprovedStoryboard));
        if (!approvedKeyframe)
            recommendations.Add(new("approve_keyframe", "Approve a source keyframe", "Generate a single source frame, review continuity, and approve it before any expensive video pass.", MovieProductionStages.ApprovedKeyframe));
        if (!approvedMotionPreview || source is null)
            recommendations.Add(new("approve_motion_preview", "Approve a motion preview", "Use a short motion check to salvage framing and continuity before committing to a production render.", MovieProductionStages.MotionPreview));
        if (package is null)
            recommendations.Add(new("rebuild_reference_package", "Review the reference hand-off", "The canonical reference package could not be assembled; resolve the reference context before production."));
        else
        {
            foreach (var warning in package.Warnings.Where(item => !blockingWarnings.Contains(item)))
                recommendations.Add(new($"reference_warning:{warning.Code}", "Review a continuity reference", warning.Message));
            foreach (var warning in blockingWarnings)
                recommendations.Add(new($"resolve_reference:{warning.Code}", "Resolve a blocking reference conflict", warning.Message));
        }
        if (plan.Recommendation.QcEscalationRequired)
            recommendations.Add(new("review_adaptive_escalation", "Review the higher-source option", plan.Recommendation.Escalation.EscalateToSourceResolution is null
                ? "The selected source does not meet the adaptive quality floor; review QC before production."
                : $"Review {plan.Recommendation.Escalation.EscalateToSourceResolution} as the higher-source option before production."));
        if (!guardrail.CanProceed)
            recommendations.Add(new("review_cost_safety", confirmationAccepted ? "Resolve the cost safety block" : "Review the server-side cost safety decision", guardrail.RejectionMessage ?? "The request cannot proceed under the current cost safety settings."));
        else if (guardrail.ConfirmationRequired && !confirmationAccepted)
            recommendations.Add(new("confirm_cost_safety", "Confirm the production estimate", "The server-side cost safety review requires explicit confirmation before queueing."));
        return recommendations;
    }

    private static bool IsBlockingReferenceWarning(MovieProductionReferenceWarningDto warning) =>
        warning.Code.Contains("hard_continuity_conflict", StringComparison.OrdinalIgnoreCase)
        || warning.Code.Contains("blocking", StringComparison.OrdinalIgnoreCase)
        || warning.Code.Contains("critical", StringComparison.OrdinalIgnoreCase)
        || warning.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)
        || warning.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
        || warning.Severity.Equals("blocking", StringComparison.OrdinalIgnoreCase);
}
