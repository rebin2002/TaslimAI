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
    IMovieProductionPreflightService productionPreflight,
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

        // Validate the user-supplied label before any speculative clip, job or usage
        // reservation is created. The label used to be validated only when the
        // production version was constructed, after the generation job was queued.
        var normalizedLabel = CleanBounded(request.Label, 160);
        // Match the API's existing 160-character title contract before a
        // continuity snapshot, speculative clip, job, or cost reservation exists.
        // A rejected title must not leave paid-generation state behind.
        if (!string.IsNullOrWhiteSpace(request.Title) && request.Title.Trim().Length > 160)
            throw new MovieProductionValidationException(
                "PRODUCTION_TITLE_TOO_LARGE", "The production title must be 160 characters or fewer.");

        var existing = await FindExistingAsync(userId, shot, request, normalizedKey, cancellationToken);
        if (existing is not null)
            return existing;

        var source = await db.MovieProductionVersions.FirstOrDefaultAsync(
            item => item.Id == request.ApprovedSourceVersionId && item.MovieShotId == request.MovieShotId,
            cancellationToken);
        var workflowError = MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ProductionRender, source);
        if (workflowError is not null)
            throw new MovieProductionValidationException("PRODUCTION_RENDER_INVALID", workflowError);

        // Admission must use the selected adapter's declared single-clip capability,
        // not the total movie duration. Fail before cost preflight, continuity
        // persistence, clip/job creation or any usage reservation.
        var plan = BuildPlan(shot, request);
        var durationSeconds = ResolveSingleClipDuration(
            shot.DurationSeconds, shot.Scene.DurationSeconds, shot.Scene.MovieProject.DurationSeconds);
        ValidateProductionClipAdmission(provider, durationSeconds, plan.SourceResolution,
            plan.TargetResolution, plan.QualityTier, plan.ProcessingPath,
            shot.Scene.MovieProject.AspectRatio);
        // Share the conservative prompt floor with the read-only preflight.
        // Queue admission still validates an optional custom render title.
        var renderDescription = string.IsNullOrWhiteSpace(request.Title)
            ? shot.Scene.MovieProject.Title : request.Title.Trim();
        var promptPayload = BuildProductionPromptPayloads(shot, renderDescription, provider);
        var minimumCreativePromptLength = promptPayload.MinimumCharacters;
        if (provider.SceneClipPromptMaxCharacters is { } promptCapacity
            && minimumCreativePromptLength > promptCapacity)
            throw new MovieProductionValidationException(
                "MOVIE_CLIP_PROMPT_UNSUPPORTED",
                "The shot description exceeds this provider's prompt limit. Shorten it before rendering.");

        // Approved production sends the character-continuity snapshot as well
        // as the scene/shot direction. Preview without writing the snapshot so
        // an oversized character card cannot reach pricing or usage admission.
        var continuityPreview = await continuity.BuildSnapshotForTargetAsync(
            shot.Scene.MovieProjectId, shot.Scene.Id, shot.Id,
            persist: false, cancellationToken);
        if (provider.SceneClipPromptMaxCharacters is { } continuityPromptCapacity
            && minimumCreativePromptLength + continuityPreview.SnapshotJson.Trim().Length > continuityPromptCapacity)
            throw new MovieProductionValidationException(
                "MOVIE_CLIP_PROMPT_UNSUPPORTED",
                "The character-continuity context exceeds this provider's prompt limit. Shorten it before rendering.");

        var preflightResult = await productionPreflight.EvaluateAsync(
            userId,
            request.MovieShotId,
            new MovieProductionPreflightRequest(
                request.ApprovedSourceVersionId,
                request.TargetResolution,
                request.QualityTier,
                request.ConfirmationAccepted),
            cancellationToken);
        if (preflightResult is null)
            return null;
        if (!preflightResult.CanProceed)
            throw new MovieProductionValidationException(
                preflightResult.RejectionCode ?? "PRODUCTION_PREFLIGHT_REQUIRED",
                preflightResult.RejectionMessage ?? preflightResult.Summary);

        await referenceReadiness.EnsureCanGenerateAsync(
            userId,
            shot.Id,
            request.AllowReferenceReadinessOverride,
            request.ReferenceReadinessOverrideReason,
            MovieReferenceReadinessOverrideSources.ProductionRender,
            cancellationToken);

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
        // Character cards can change between the read-only preview and the
        // snapshot committed for this clip. Recheck the exact persisted content
        // so a raced edit cannot introduce unsupported paid prompt text.
        if (provider.SceneClipPromptMaxCharacters is { } persistedPromptCapacity
            && minimumCreativePromptLength + snapshot.SnapshotJson.Trim().Length > persistedPromptCapacity)
            throw new MovieProductionValidationException(
                "MOVIE_CLIP_PROMPT_UNSUPPORTED",
                "The character-continuity context exceeds this provider's prompt limit. Shorten it before rendering.");
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
            promptPayload.GuideJson,
            promptPayload.SceneJson,
            promptPayload.ShotJson,
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
            snapshot.SnapshotHash,
            SelectedProviderKey: provider.Key,
            CharacterContinuityJson: snapshot.SnapshotJson);

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

        // CreateAsync is idempotent at the GenerationJob row, but the clip and
        // production version are created before that row. Two identical requests
        // can therefore both create provisional clips and then receive the same
        // canonical job. Do not attach the losing request's clip/version to that
        // job; replay the winner after its relationship rows become visible.
        if (normalizedKey is not null && !JobTargetsClip(job, clip.Id))
        {
            db.MovieClips.Remove(clip);
            await db.SaveChangesAsync(CancellationToken.None);
            return await FindExistingAfterConcurrentCreateAsync(userId, shot, request, normalizedKey, cancellationToken);
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
            Label = normalizedLabel,
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
        MovieShot shot,
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
        // An idempotent replay must not silently adopt a paid job from a
        // different adapter after routing or deployment configuration changes.
        // Historical jobs without a bound provider retain their legacy behavior.
        if (input is null || !MatchesProductionReplay(input, shot, request)
            || (!string.IsNullOrWhiteSpace(input.SelectedProviderKey)
                && !string.Equals(input.SelectedProviderKey, provider.Key, StringComparison.OrdinalIgnoreCase)))
            throw new MovieProductionValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different production request.");
        // For newly admitted production jobs, character continuity forms part
        // of the canonical creative request. The same replay key must not adopt
        // a previously priced render after the character bible changes. Older
        // serialized jobs lacking this optional field preserve replay behavior.
        if (input.CharacterContinuityJson is not null)
        {
            var currentContinuity = await continuity.BuildSnapshotForTargetAsync(
                shot.Scene.MovieProjectId, shot.Scene.Id, shot.Id, persist: false, cancellationToken);
            if (!string.Equals(input.CharacterContinuityJson, currentContinuity.SnapshotJson, StringComparison.Ordinal))
                throw new MovieProductionValidationException("IDEMPOTENCY_KEY_REUSED",
                    "This idempotency key was already used for a different production request.");
        }
        var version = await db.MovieProductionVersions
            .Include(item => item.AssetReferences)
            .Include(item => item.GenerationJob).ThenInclude(item => item!.ProviderAttempts)
            .Include(item => item.GenerationJob).ThenInclude(item => item!.Assets)
            .FirstOrDefaultAsync(item => item.MovieShotId == request.MovieShotId && item.GenerationJobId == existingJob.Id, cancellationToken);
        var clip = await db.MovieClips.FirstOrDefaultAsync(item => item.MovieShotId == request.MovieShotId && item.GenerationJobId == existingJob.Id, cancellationToken);
        if (version is null || clip is null)
            throw new MovieProductionValidationException("IDEMPOTENCY_INCOMPLETE", "The previous production request is still being finalized; retry with the same idempotency key.");
        if (!string.Equals(version.Label, CleanBounded(request.Label, 160), StringComparison.Ordinal))
            throw new MovieProductionValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different production request.");
        var target = input.TargetResolution ?? MovieResolutionTiers.P1080;
        var source = input.SourceResolution ?? target;
        return new(version, existingJob, clip, MovieProductionOrchestrationStateMachine.From(existingJob.Status), source, target, input.ProcessingPath ?? MovieResolutionPathKinds.Native, MovieGenerationCostEstimate.Unevaluated("idempotent_replay"));
    }

    private async Task<MovieProductionOrchestrationResult?> FindExistingAfterConcurrentCreateAsync(
        Guid userId,
        MovieShot shot,
        MovieProductionGenerationRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 20;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await FindExistingAsync(userId, shot, request, idempotencyKey, cancellationToken);
            }
            catch (MovieProductionValidationException exception)
                when (exception.Code == "IDEMPOTENCY_INCOMPLETE" && attempt + 1 < maxAttempts)
            {
                // The winning request commits the job before its version/clip
                // relationship update. Give that short, durable write window a
                // bounded replay path instead of surfacing a false incomplete error.
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }

        return await FindExistingAsync(userId, shot, request, idempotencyKey, cancellationToken);
    }

    // A production render's idempotency key binds its actual priced and creative
    // intent, not just the source version and output tier. A changed shot, scene,
    // guide, or movie must not silently adopt the previous billable job.
    private static bool MatchesProductionReplay(
        MovieGenerationInput input,
        MovieShot shot,
        MovieProductionGenerationRequest request)
    {
        var movie = shot.Scene.MovieProject;
        var plan = BuildPlan(shot, request);
        // A replay key binds the original priced clip duration. If a later
        // edit makes the selected shot invalid as a single provider clip,
        // treat this as changed replay intent, never as a successful replay.
        int duration;
        try
        {
            duration = ResolveSingleClipDuration(
                shot.DurationSeconds, shot.Scene.DurationSeconds, movie.DurationSeconds);
        }
        catch (MovieProductionValidationException error)
            when (error.Code == "MOVIE_CLIP_DURATION_UNSUPPORTED")
        {
            return false;
        }
        var title = string.IsNullOrWhiteSpace(request.Title) ? movie.Title : request.Title.Trim();
        var guide = movie.Guide is null ? null : JsonSerializer.Serialize(new
        {
            movie.Guide.VisualLanguage,
            movie.Guide.CameraLanguage,
            movie.Guide.ColorAndLighting,
            movie.Guide.SoundAndNarration,
            movie.Guide.ContinuityRules,
        });
        var scene = JsonSerializer.Serialize(new { shot.Scene.Title, shot.Scene.Summary, shot.Scene.DurationSeconds });
        var shotSnapshot = JsonSerializer.Serialize(new
        {
            shot.Description, shot.Purpose, shot.Subjects,
            shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds,
        });

        return string.Equals(input.Operation, MovieStudioOperations.SceneClip, StringComparison.OrdinalIgnoreCase)
            && input.MovieProjectId == movie.Id
            && input.MovieSceneId == shot.Scene.Id
            && input.MovieShotId == shot.Id
            && input.SourceProductionVersionId == request.ApprovedSourceVersionId
            && input.DurationSeconds == duration
            && string.Equals(input.Description, title, StringComparison.Ordinal)
            && string.Equals(input.AspectRatio, movie.AspectRatio, StringComparison.Ordinal)
            && string.Equals(input.Style, movie.Style, StringComparison.Ordinal)
            && string.Equals(input.Language, movie.Language, StringComparison.Ordinal)
            && string.Equals(input.AdditionalInstructions, movie.AdditionalInstructions, StringComparison.Ordinal)
            && string.Equals(input.ContinuityGuideJson, guide, StringComparison.Ordinal)
            && string.Equals(input.SceneJson, scene, StringComparison.Ordinal)
            && string.Equals(input.ShotJson, shotSnapshot, StringComparison.Ordinal)
            && string.Equals(input.SourceResolution, plan.SourceResolution, StringComparison.OrdinalIgnoreCase)
            && string.Equals(input.TargetResolution, plan.TargetResolution, StringComparison.OrdinalIgnoreCase)
            && string.Equals(input.ProcessingPath, plan.ProcessingPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(input.QualityTier, plan.QualityTier, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool JobTargetsClip(GenerationJob job, Guid clipId)
    {
        try
        {
            var input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson);
            return clipId != Guid.Empty && input?.MovieClipId == clipId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Movie/scene duration is timeline planning metadata, never permission to
    // truncate one billable render to an arbitrary shorter clip. Preserve the
    // selected shot's actual duration and reject invalid single-clip lengths
    // before cost estimation or job persistence. The adapter's stricter range
    // is checked separately by ValidateProductionClipAdmission.
    internal static int ResolveSingleClipDuration(int? shotSeconds, int? sceneSeconds, int movieSeconds)
    {
        var durationSeconds = shotSeconds ?? sceneSeconds ?? Math.Min(movieSeconds, 60);
        if (durationSeconds is < 1 or > 3600)
            throw new MovieProductionValidationException(
                "MOVIE_CLIP_DURATION_UNSUPPORTED",
                "The selected shot or scene duration cannot be sent as one production clip. Use shorter shots or a validated segmentation workflow.");
        return durationSeconds;
    }

    // Shared provider-neutral admission predicate for approved production renders.
    // The read-only preflight can inspect the same decision without throwing or
    // creating a clip/job; queue admission throws the exact same rejection.
    // Unknown adapters, unverified durations and unpriced profiles fail closed.
    internal static MovieProductionValidationException? EvaluateProductionClipAdmission(
        IMovieVideoProvider videoProvider,
        int durationSeconds,
        string sourceResolution,
        string targetResolution,
        string qualityTier,
        string processingPath,
        string aspectRatio)
    {
        if (!videoProvider.IsAvailable
            || !videoProvider.SupportedOperations.Contains(MovieStudioOperations.SceneClip, StringComparer.OrdinalIgnoreCase))
            return new MovieProductionValidationException(
                "MOVIE_PROVIDER_UNAVAILABLE",
                "The selected video provider is unavailable for production clips.");

        if (videoProvider.SceneClipDurationRange is not { } durationRange
            || !durationRange.Supports(durationSeconds))
            return new MovieProductionValidationException(
                "MOVIE_CLIP_DURATION_UNSUPPORTED",
                "The selected video provider cannot generate a single clip of this duration. Use shorter shots or a validated segmentation workflow.");

        if (!videoProvider.SupportsSceneClipCostIntent(
                sourceResolution, targetResolution, qualityTier, processingPath, aspectRatio))
            return new MovieProductionValidationException(
                "MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED",
                "The selected video provider cannot deliver the requested resolution, quality and processing profile.");

        return null;
    }

    internal static void ValidateProductionClipAdmission(
        IMovieVideoProvider videoProvider,
        int durationSeconds,
        string sourceResolution,
        string targetResolution,
        string qualityTier,
        string processingPath,
        string aspectRatio)
    {
        var rejection = EvaluateProductionClipAdmission(videoProvider, durationSeconds,
            sourceResolution, targetResolution, qualityTier, processingPath, aspectRatio);
        if (rejection is not null)
            throw rejection;
    }

    // Excludes character continuity, which both preflight and queue check
    // separately. The JSON properties and order match the video job payload.
    internal static (long MinimumCharacters, string? GuideJson, string SceneJson, string ShotJson)
        BuildProductionPromptPayloads(
            MovieShot shot, string renderDescription, IMovieVideoProvider videoProvider)
    {
        var movie = shot.Scene.MovieProject;
        var guide = movie.Guide;
        var guidePayload = guide is null ? null : JsonSerializer.Serialize(new
        {
            guide.VisualLanguage,
            guide.CameraLanguage,
            guide.ColorAndLighting,
            guide.SoundAndNarration,
            guide.ContinuityRules,
        });
        var scenePayload = JsonSerializer.Serialize(new
        {
            shot.Scene.Title, shot.Scene.Summary, shot.Scene.DurationSeconds,
        });
        var shotPayload = JsonSerializer.Serialize(new
        {
            shot.Description, shot.Purpose, shot.Subjects,
            shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds,
        });
        var minimumCharacters = (long)renderDescription.Trim().Length
            + (movie.Style?.Trim().Length ?? 0)
            + (movie.AdditionalInstructions?.Trim().Length ?? 0)
            + (guidePayload?.Length ?? 0)
            + scenePayload.Length
            + shotPayload.Length
            + Math.Max(0, videoProvider.SceneClipPromptFormattingOverheadCharacters);
        return (minimumCharacters, guidePayload, scenePayload, shotPayload);
    }

    private static ProductionPlan BuildPlan(MovieShot shot, MovieProductionGenerationRequest request)
    {
        var plan = MovieProductionAdaptivePlanBuilder.Build(shot, request.TargetResolution, request.QualityTier);
        return new(plan.SourceResolution, plan.TargetResolution, plan.ProcessingPath, plan.QualityTier);
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

    internal static string? NormalizeIdempotencyKey(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrWhiteSpace(key)) return null;
        // GenerationJobService accepts at most 80 characters. Reject before
        // continuity snapshots or speculative clip/version persistence.
        if (key.Length > 80) throw new MovieProductionValidationException("IDEMPOTENCY_KEY_INVALID", "The idempotency key is too long.");
        return key;
    }

    private static string? CleanBounded(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new MovieProductionValidationException("PRODUCTION_LABEL_TOO_LARGE", "The production label is too long.");

    private sealed record ProductionPlan(string SourceResolution, string TargetResolution, string ProcessingPath, string QualityTier);
}
