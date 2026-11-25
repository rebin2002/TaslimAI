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
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject)
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
            new(MovieProductionPreflightCheckKeys.ReferencePackage, "Reference package", blockingPackageWarnings.Length == 0, true,
                blockingPackageWarnings.Length == 0
                    ? package is null ? "The reference package is unavailable." : "No blocking reference conflicts were found."
                    : $"Resolve {blockingPackageWarnings.Length} blocking reference conflict{(blockingPackageWarnings.Length == 1 ? "" : "s")} before production."),
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

        var durationSeconds = Math.Clamp(shot.DurationSeconds ?? shot.Scene.DurationSeconds ?? Math.Min(shot.Scene.MovieProject.DurationSeconds, 60), 1, 3_600);
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
        var requiredChecksPassed = checks.Where(item => item.Required).All(item => item.Satisfied);
        var decision = requiredChecksPassed
            ? MovieProductionPreflightDecisionCodes.Ready
            : checks.Any(item => !item.Satisfied && item.Key is MovieProductionPreflightCheckKeys.Storyboard or MovieProductionPreflightCheckKeys.Keyframe or MovieProductionPreflightCheckKeys.MotionPreview or MovieProductionPreflightCheckKeys.ReferencePackage)
                ? MovieProductionPreflightDecisionCodes.ReferenceWorkRequired
                : !adaptiveSatisfied
                    ? MovieProductionPreflightDecisionCodes.QualityReviewRequired
                    : !guardrail.CanProceed && guardrail.ConfirmationRequired
                        ? MovieProductionPreflightDecisionCodes.CostConfirmationRequired
                        : MovieProductionPreflightDecisionCodes.CostGuardrailBlocked;
        var canProceed = requiredChecksPassed;
        var summary = canProceed
            ? "Cheap reference work, adaptive quality, and server-side cost safety checks are ready for production."
            : decision == MovieProductionPreflightDecisionCodes.ReferenceWorkRequired
                ? "Complete the recommended reference work before starting this expensive production pass."
                : decision == MovieProductionPreflightDecisionCodes.QualityReviewRequired
                    ? "Review the adaptive quality escalation before starting production."
                    : guardrail.RejectionMessage ?? "Review the production preflight before continuing.";
        var rejectionCode = canProceed ? null : decision == MovieProductionPreflightDecisionCodes.CostConfirmationRequired
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
