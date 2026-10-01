using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies.AdaptiveResolution;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

/// <summary>
/// Durable orchestration states are intentionally separate from provider states. The
/// generation job remains the durable execution record; this state machine defines the
/// allowed movie-production hand-offs around that record.
/// </summary>
public enum MovieProductionOrchestrationState
{
    Requested,
    Validated,
    Queued,
    Running,
    QualityControlPending,
    Succeeded,
    Failed,
    CancellationRequested,
    Cancelled,
}

public static class MovieProductionOrchestrationStateMachine
{
    private static readonly IReadOnlyDictionary<MovieProductionOrchestrationState, IReadOnlySet<MovieProductionOrchestrationState>> Transitions =
        new Dictionary<MovieProductionOrchestrationState, IReadOnlySet<MovieProductionOrchestrationState>>
        {
            [MovieProductionOrchestrationState.Requested] = new HashSet<MovieProductionOrchestrationState> { MovieProductionOrchestrationState.Validated, MovieProductionOrchestrationState.Failed, MovieProductionOrchestrationState.Cancelled },
            [MovieProductionOrchestrationState.Validated] = new HashSet<MovieProductionOrchestrationState> { MovieProductionOrchestrationState.Queued, MovieProductionOrchestrationState.Failed, MovieProductionOrchestrationState.Cancelled },
            [MovieProductionOrchestrationState.Queued] = new HashSet<MovieProductionOrchestrationState> { MovieProductionOrchestrationState.Running, MovieProductionOrchestrationState.CancellationRequested, MovieProductionOrchestrationState.Failed, MovieProductionOrchestrationState.Cancelled },
            [MovieProductionOrchestrationState.Running] = new HashSet<MovieProductionOrchestrationState> { MovieProductionOrchestrationState.QualityControlPending, MovieProductionOrchestrationState.Succeeded, MovieProductionOrchestrationState.CancellationRequested, MovieProductionOrchestrationState.Failed, MovieProductionOrchestrationState.Cancelled },
            [MovieProductionOrchestrationState.QualityControlPending] = new HashSet<MovieProductionOrchestrationState> { MovieProductionOrchestrationState.Succeeded, MovieProductionOrchestrationState.Failed, MovieProductionOrchestrationState.Cancelled },
            [MovieProductionOrchestrationState.CancellationRequested] = new HashSet<MovieProductionOrchestrationState> { MovieProductionOrchestrationState.Cancelled, MovieProductionOrchestrationState.Failed },
            [MovieProductionOrchestrationState.Succeeded] = new HashSet<MovieProductionOrchestrationState>(),
            [MovieProductionOrchestrationState.Failed] = new HashSet<MovieProductionOrchestrationState>(),
            [MovieProductionOrchestrationState.Cancelled] = new HashSet<MovieProductionOrchestrationState>(),
        };

    public static bool CanTransition(MovieProductionOrchestrationState from, MovieProductionOrchestrationState to) =>
        from == to || Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static void EnsureTransition(MovieProductionOrchestrationState from, MovieProductionOrchestrationState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Movie production orchestration cannot transition from {from} to {to}.");
    }

    public static MovieProductionOrchestrationState From(GenerationJobStatus status, bool qualityControlPending = false) =>
        qualityControlPending ? MovieProductionOrchestrationState.QualityControlPending : status switch
        {
            GenerationJobStatus.Pending => MovieProductionOrchestrationState.Requested,
            GenerationJobStatus.Queued => MovieProductionOrchestrationState.Queued,
            GenerationJobStatus.Running => MovieProductionOrchestrationState.Running,
            GenerationJobStatus.Succeeded => MovieProductionOrchestrationState.Succeeded,
            GenerationJobStatus.Failed => MovieProductionOrchestrationState.Failed,
            GenerationJobStatus.Cancelled => MovieProductionOrchestrationState.Cancelled,
            _ => MovieProductionOrchestrationState.Failed,
        };
}

public sealed record MovieProductionGenerationRequest(
    Guid MovieShotId,
    Guid ApprovedSourceVersionId,
    string? Label,
    string? Title,
    string TargetResolution = MovieResolutionTiers.P1080,
    string QualityTier = DirectorQualityLevels.Standard,
    bool ConfirmationAccepted = false,
    string? IdempotencyKey = null,
    bool AllowReferenceReadinessOverride = false,
    string? ReferenceReadinessOverrideReason = null);

/// <summary>
/// This result is internal to the movie service boundary. It carries the canonical
/// entities needed to build the existing user-safe response without exposing routing,
/// model, prompt, or credential details.
/// </summary>
public sealed record MovieProductionOrchestrationResult(
    MovieProductionVersion Version,
    GenerationJob Job,
    MovieClip Clip,
    MovieProductionOrchestrationState State,
    string SourceResolution,
    string TargetResolution,
    string ProcessingPath,
    MovieGenerationCostEstimate CostEstimate);

public interface IMovieProductionGenerationOrchestrator
{
    Task<MovieProductionOrchestrationResult?> QueueApprovedAsync(
        Guid userId,
        MovieProductionGenerationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Cancellation and retry policy stay with the canonical generation-job lifecycle.</summary>
    Task<GenerationJobCancelResult> RequestCancellationAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, string idempotencyKey, CancellationToken cancellationToken = default);
}

public sealed class MovieProductionGenerationOrchestrator(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IGenerationJobService jobs,
    IMovieCharacterContinuityService continuity,
    IMovieVideoProvider provider,
    IMovieGenerationCostEstimator costEstimator,
    IGenerationCostGuardrailService costGuardrails,
    IMovieReferenceReadinessService referenceReadiness) : IMovieProductionGenerationOrchestrator
{
    public async Task<MovieProductionOrchestrationResult?> QueueApprovedAsync(
        Guid userId,
        MovieProductionGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide)
            .Include(item => item.ProductionVersions)
            .FirstOrDefaultAsync(item => item.Id == request.MovieShotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken))
            return null;

        var existing = await FindExistingAsync(userId, request, normalizedKey, cancellationToken);
        if (existing is not null)
            return existing;

        var source = await db.MovieProductionVersions.FirstOrDefaultAsync(
            item => item.Id == request.ApprovedSourceVersionId && item.MovieShotId == request.MovieShotId,
            cancellationToken);
        var workflowError = MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ProductionRender, source);
        if (workflowError is not null)
            throw new MovieProductionValidationException("PRODUCTION_RENDER_INVALID", workflowError);

        await referenceReadiness.EnsureCanGenerateAsync(
            userId,
            shot.Id,
            request.AllowReferenceReadinessOverride,
            request.ReferenceReadinessOverrideReason,
            MovieReferenceReadinessOverrideSources.ProductionRender,
            cancellationToken);

        var plan = BuildPlan(shot, request);
        var durationSeconds = Math.Clamp(shot.DurationSeconds ?? shot.Scene.DurationSeconds ?? Math.Min(shot.Scene.MovieProject.DurationSeconds, 60), 1, 3600);
        var estimate = await costEstimator.EstimateAsync(
            new MovieGenerationCostRequest(
                durationSeconds,
                plan.SourceResolution,
                plan.TargetResolution,
                plan.QualityTier,
                plan.ProcessingPath),
            selectedProviderKey: provider.Key,
            cancellationToken: cancellationToken);
        var preflight = await costGuardrails.EvaluateAsync(
            userId,
            shot.Scene.MovieProject.WorkspaceId,
            shot.Scene.MovieProject.ProjectId,
            estimate.ToGenerationCostEstimate(),
            request.ConfirmationAccepted,
            cancellationToken);
        if (!preflight.CanProceed)
            throw new MovieProductionValidationException(
                preflight.RejectionCode ?? "GENERATION_CONFIRMATION_REQUIRED",
                preflight.RejectionMessage ?? "Confirm the generation cost before queueing this production request.");

        var snapshot = await continuity.BuildSnapshotForTargetAsync(
            shot.Scene.MovieProjectId,
            shot.Scene.Id,
            shot.Id,
            persist: true,
            cancellationToken);
        var now = DateTime.UtcNow;
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(),
            MovieProjectId = shot.Scene.MovieProjectId,
            MovieSceneId = shot.Scene.Id,
            MovieShotId = shot.Id,
            Status = MovieClipStatuses.Queued,
            DurationSeconds = durationSeconds,
            ContinuitySnapshotJson = snapshot.SnapshotJson,
            ContinuitySnapshotId = snapshot.SnapshotId,
            ContinuitySnapshotVersion = snapshot.Version,
            ContinuitySnapshotHash = snapshot.SnapshotHash,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieClips.Add(clip);
        await db.SaveChangesAsync(cancellationToken);

        var input = new MovieGenerationInput(
            MovieStudioOperations.SceneClip,
            shot.Scene.MovieProjectId,
            clip.Id,
            shot.Scene.Id,
            shot.Id,
            string.IsNullOrWhiteSpace(request.Title) ? shot.Scene.MovieProject.Title : request.Title.Trim(),
            durationSeconds,
            shot.Scene.MovieProject.AspectRatio,
            shot.Scene.MovieProject.Style,
            shot.Scene.MovieProject.Language,
            shot.Scene.MovieProject.AdditionalInstructions,
            shot.Scene.MovieProject.Guide is null ? null : JsonSerializer.Serialize(new
            {
                shot.Scene.MovieProject.Guide.VisualLanguage,
                shot.Scene.MovieProject.Guide.CameraLanguage,
                shot.Scene.MovieProject.Guide.ColorAndLighting,
                shot.Scene.MovieProject.Guide.SoundAndNarration,
                shot.Scene.MovieProject.Guide.ContinuityRules,
            }),
            JsonSerializer.Serialize(new { shot.Scene.Title, shot.Scene.Summary, shot.Scene.DurationSeconds }),
            JsonSerializer.Serialize(new { shot.Description, shot.Purpose, shot.Subjects, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds }),
            null,
            null,
            null,
            null,
            source!.Id,
            null,
            null,
            null,
            plan.SourceResolution,
            plan.TargetResolution,
            plan.ProcessingPath,
            plan.QualityTier,
            snapshot.SnapshotHash);

        GenerationJob job;
        try
        {
            job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = shot.Scene.MovieProject.WorkspaceId,
                ProjectId = shot.Scene.MovieProject.ProjectId,
                JobType = GenerationJobTypes.MovieClipGenerate,
                Title = string.IsNullOrWhiteSpace(request.Title) ? shot.Scene.MovieProject.Title : request.Title.Trim(),
                InputJson = JsonSerializer.Serialize(input),
                EstimatedProviderCostUsd = estimate.MaximumAmountUsd,
                InternalCostEstimate = estimate.ToGenerationCostEstimate(),
                InternalCostEstimateJson = estimate.ToJson(),
                ConfirmationAccepted = request.ConfirmationAccepted,
            }, cancellationToken: cancellationToken, idempotencyKey: normalizedKey);
        }
        catch
        {
            db.MovieClips.Remove(clip);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        clip.GenerationJobId = job.Id;
        clip.GenerationJob = job;
        clip.Status = MovieClipStatuses.Queued;
        clip.UpdatedAt = DateTime.UtcNow;

        var version = new MovieProductionVersion
        {
            Id = Guid.NewGuid(),
            MovieShotId = shot.Id,
            VersionNumber = shot.ProductionVersions.Count == 0 ? 1 : shot.ProductionVersions.Max(item => item.VersionNumber) + 1,
            Stage = MovieProductionStages.ProductionRender,
            Status = MovieProductionVersionStatuses.PendingApproval,
            Label = CleanBounded(request.Label, 160),
            CompositionJson = source.CompositionJson,
            StageProvenanceJson = JsonSerializer.Serialize(new
            {
                sourceVersionId = source.Id,
                action = "production_render",
                orchestrationContract = "wave4.production-generation.v1",
                qualityControlContract = MovieProductionQualityControlService.ContractVersion,
                targetResolution = plan.TargetResolution,
                processingPath = plan.ProcessingPath,
            }),
            SourceVersionId = source.Id,
            GenerationJobId = job.Id,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            ContinuitySnapshotId = snapshot.SnapshotId,
            ContinuitySnapshotVersion = snapshot.Version,
            ContinuitySnapshotHash = snapshot.SnapshotHash,
            ContinuitySnapshotReferenceJson = JsonSerializer.Serialize(new { snapshotId = snapshot.SnapshotId, snapshot.Version, snapshot.SnapshotHash }),
        };
        db.MovieProductionVersions.Add(version);
        db.MovieProductionStageTransitions.Add(new MovieProductionStageTransition
        {
            Id = Guid.NewGuid(),
            MovieShotId = shot.Id,
            MovieProductionVersionId = version.Id,
            FromStage = shot.ProductionStage,
            ToStage = MovieProductionStages.ProductionRender,
            EventType = "created",
            SourceVersionId = source.Id,
            GenerationJobId = job.Id,
            ActorUserId = userId,
            CreatedAt = now,
            MetadataJson = version.StageProvenanceJson,
        });
        shot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        // A worker may complete between CreateAsync and the relationship update. Re-read
        // the canonical job state and reconcile the clip so a fast disabled/fake adapter
        // cannot leave a queued clip after a terminal job.
        await ReconcileFastTerminalAsync(clip, job.Id, cancellationToken);
        var state = MovieProductionOrchestrationStateMachine.From(job.Status);
        return new(version, job, clip, state, plan.SourceResolution, plan.TargetResolution, plan.ProcessingPath, estimate);
    }

    public async Task<GenerationJobCancelResult> RequestCancellationAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        return await jobs.CancelAsync(userId, jobId, cancellationToken);
    }

    public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        jobs.RetryAsync(userId, jobId, cancellationToken, idempotencyKey);

    private async Task<MovieProductionOrchestrationResult?> FindExistingAsync(
        Guid userId,
        MovieProductionGenerationRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null) return null;
        var existingJob = await db.GenerationJobs
            .Include(item => item.Outputs)
            .FirstOrDefaultAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existingJob is null) return null;
        MovieGenerationInput? input = null;
        try { input = JsonSerializer.Deserialize<MovieGenerationInput>(existingJob.InputJson); } catch (JsonException) { }
        var requestedTarget = string.IsNullOrWhiteSpace(request.TargetResolution) ? MovieResolutionTiers.P1080 : request.TargetResolution.Trim();
        var requestedQuality = string.IsNullOrWhiteSpace(request.QualityTier) ? DirectorQualityLevels.Standard : request.QualityTier.Trim();
        var requestedTitle = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        if (input is null
            || input.MovieShotId != request.MovieShotId
            || input.SourceProductionVersionId != request.ApprovedSourceVersionId
            || !string.Equals(input.TargetResolution, requestedTarget, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(input.QualityTier, requestedQuality, StringComparison.OrdinalIgnoreCase)
            || (requestedTitle is not null && !string.Equals(input.Description, requestedTitle, StringComparison.Ordinal)))
            throw new MovieProductionValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different production request.");
        var version = await db.MovieProductionVersions
            .Include(item => item.AssetReferences)
            .Include(item => item.GenerationJob).ThenInclude(item => item!.ProviderAttempts)
            .Include(item => item.GenerationJob).ThenInclude(item => item!.Assets)
            .FirstOrDefaultAsync(item => item.MovieShotId == request.MovieShotId && item.GenerationJobId == existingJob.Id, cancellationToken);
        var clip = await db.MovieClips.FirstOrDefaultAsync(item => item.MovieShotId == request.MovieShotId && item.GenerationJobId == existingJob.Id, cancellationToken);
        if (version is null || clip is null)
            throw new MovieProductionValidationException("IDEMPOTENCY_INCOMPLETE", "The previous production request is still being finalized; retry with the same idempotency key.");
        var target = input.TargetResolution ?? MovieResolutionTiers.P1080;
        var source = input.SourceResolution ?? target;
        return new(version, existingJob, clip, MovieProductionOrchestrationStateMachine.From(existingJob.Status), source, target, input.ProcessingPath ?? MovieResolutionPathKinds.Native, MovieGenerationCostEstimate.Unevaluated("idempotent_replay"));
    }

    private static ProductionPlan BuildPlan(MovieShot shot, MovieProductionGenerationRequest request)
    {
        var target = request.TargetResolution?.Trim();
        if (!MovieResolutionTiers.TryGet(target, out var targetTier))
            throw new MovieProductionValidationException("PRODUCTION_TARGET_RESOLUTION_INVALID", "Choose a supported production resolution.");
        var quality = DirectorQualityLevels.QualityTiers.FirstOrDefault(item => string.Equals(item, request.QualityTier?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (quality is null)
            throw new MovieProductionValidationException("PRODUCTION_QUALITY_INVALID", "Choose a supported production quality level.");
        var director = new AdaptiveResolution.AdaptiveResolutionDirector();
        var recommendation = director.Recommend(new AdaptiveResolutionDirectorRequest
        {
            MasterTargetResolution = targetTier.Code,
            QualityTier = quality,
            DurationSeconds = Math.Clamp(shot.DurationSeconds ?? 1, 1, 3600),
            Importance = string.IsNullOrWhiteSpace(shot.Purpose) ? 50 : 70,
            MotionComplexity = string.IsNullOrWhiteSpace(shot.CameraMotion) ? 35 : 65,
            CameraComplexity = string.IsNullOrWhiteSpace(shot.CameraAndFraming) ? 35 : 60,
            ContinuitySensitivity = string.IsNullOrWhiteSpace(shot.ContinuityReferences) ? 40 : 75,
            RequiredQualityDimensions = [],
            Constraints = new AdaptiveResolutionWave3Constraints(false, null, true),
        });
        return new(recommendation.SourceResolution, recommendation.MasterTargetResolution, recommendation.PipelinePath, quality);
    }

    private async Task ReconcileFastTerminalAsync(MovieClip clip, Guid jobId, CancellationToken cancellationToken)
    {
        var status = await db.GenerationJobs.AsNoTracking().Where(item => item.Id == jobId).Select(item => item.Status).FirstOrDefaultAsync(cancellationToken);
        if (status is GenerationJobStatus.Failed)
        {
            clip.Status = MovieClipStatuses.Failed;
            clip.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (status is GenerationJobStatus.Cancelled)
        {
            clip.Status = MovieClipStatuses.Cancelled;
            clip.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key.Length > 128) throw new MovieProductionValidationException("IDEMPOTENCY_KEY_INVALID", "The idempotency key is too long.");
        return key;
    }

    private static string? CleanBounded(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new MovieProductionValidationException("PRODUCTION_LABEL_TOO_LARGE", "The production label is too long.");

    private sealed record ProductionPlan(string SourceResolution, string TargetResolution, string ProcessingPath, string QualityTier);
}
