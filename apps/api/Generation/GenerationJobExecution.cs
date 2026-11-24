using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Images;
using Taslim.Api.Infrastructure;
using Taslim.Api.Music;
using Taslim.Api.Movies;
using Taslim.Api.Notifications;
using Taslim.Api.Operations;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Taslim.Api.Research;
using Taslim.Api.Social;
using Taslim.Api.Usage;
using Taslim.Api.Voice;
using Taslim.Api.Video;

namespace Taslim.Api.Generation;

public sealed class GenerationJobOptions
{
    public int WorkerConcurrency { get; set; } = 1;
    public bool WorkerEnabled { get; set; } = true;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int CancellationPollMilliseconds { get; set; } = 100;
    public int ClaimRecoveryIntervalMilliseconds { get; set; } = 30000;
    public int ClaimLeaseMinutes { get; set; } = 30;
    public int LeaseRenewalIntervalMilliseconds { get; set; } = 60000;
    public int MaxAutomaticRetries { get; set; } = 3;
    public int HeartbeatStaleAfterSeconds { get; set; } = 120;
}

public sealed class GenerationJobPollingSchedule(GenerationJobOptions options)
{
    public DateTime NextRecoveryAt { get; private set; } = DateTime.MinValue;
    public TimeSpan IdleDelay => TimeSpan.FromMilliseconds(Math.Max(1, options.PollIntervalMilliseconds));

    public bool RecoveryDue(DateTime now) => now >= NextRecoveryAt;

    public void ScheduleNextRecovery(DateTime now) =>
        NextRecoveryAt = now.AddMilliseconds(Math.Max(1000, options.ClaimRecoveryIntervalMilliseconds));
}

public interface IGenerationJobQueue
{
    Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public sealed class DatabaseGenerationJobQueue(TaslimDbContext db) : IGenerationJobQueue
{
    public async Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await db.GenerationJobs
            .Where(job => job.Id == jobId && job.Status == GenerationJobStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, GenerationJobStatus.Queued)
                .SetProperty(job => job.QueuedAt, now), cancellationToken);
    }
}

public sealed record GenerationHandlerOutput(
    string OutputType,
    Guid? StoredFileId,
    string? MetadataJson,
    GeneratedFileArtifact? FileArtifact = null,
    GeneratedAssetDescriptor? Asset = null,
    GeneratedStreamFileArtifact? StreamArtifact = null);
public sealed record GenerationHandlerResult(string ResultJson, IReadOnlyList<GenerationHandlerOutput> Outputs, AiUsageMetadata? Usage = null);

public interface IGenerationJobHandler
{
    bool CanHandle(string jobType);
    Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken);
}

public sealed class SystemTestGenerationJobHandler : IGenerationJobHandler
{
    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        foreach (var stage in new[] { 20, 40, 60, 80, 100 })
        {
            await Task.Delay(45, cancellationToken);
            progress.Report(stage);
        }

        var result = JsonSerializer.Serialize(new
        {
            jobType = GenerationJobTypes.SystemTest,
            message = "Generation job completed.",
            version = 1,
        });
        var metadata = JsonSerializer.Serialize(new { deterministic = true, stageCount = 5 });
        var artifact = new GeneratedFileArtifact("generation-result.json", "application/json", System.Text.Encoding.UTF8.GetBytes(result));
        var asset = new GeneratedAssetDescriptor(job.Title ?? "Generation result", "Deterministic system test output.", AssetTypes.File, metadata);
        return new GenerationHandlerResult(result, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadata, artifact, asset)], new AiUsageMetadata("system", "system.test", null, null, null, 0m, 0m, 0, "completed", true));
    }
}

public interface IGenerationJobUsageService
{
    Task<UsageTransaction> BeginAsync(GenerationJob job, decimal? estimatedProviderCostUsd = null, CancellationToken cancellationToken = default);
    Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, bool hasBillableAsset = true, CancellationToken cancellationToken = default);
    Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default);
    Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default);
}

public sealed class GenerationJobUsageService(IUsageLedgerService ledger) : IGenerationJobUsageService
{
    public Task<UsageTransaction> BeginAsync(GenerationJob job, decimal? estimatedProviderCostUsd = null, CancellationToken cancellationToken = default) =>
        ledger.GetOrCreatePendingAsync(
            job.WorkspaceId,
            job.CreatedByUserId,
            job.ProjectId,
            null,
            $"generation:{job.Id:N}",
            string.Equals(job.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)
                ? UsageFeature.Image
                : string.Equals(job.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase)
                    ? UsageFeature.Document
                    : string.Equals(job.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase)
                        ? UsageFeature.Presentation
                        : string.Equals(job.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase)
                            ? UsageFeature.Research
                            : (string.Equals(job.JobType, GenerationJobTypes.VoiceGenerate, StringComparison.OrdinalIgnoreCase)
                                || GenerationJobTypes.MovieDialogueVoiceTypes.Contains(job.JobType))
                                    ? UsageFeature.Voice
                                    : string.Equals(job.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase)
                                    ? UsageFeature.Music
                                    : string.Equals(job.JobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase)
                                        ? UsageFeature.Social
                                        : GenerationJobTypes.MovieSoundTypes.Contains(job.JobType)
                                            ? UsageFeature.Movie
                                        : GenerationJobTypes.MovieTypes.Contains(job.JobType)
                                            ? UsageFeature.Movie
                                            : UsageFeature.Generation,
            cancellationToken,
            job.Id,
            estimatedProviderCostUsd,
            job.CostEstimateJson);

    public Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, bool hasBillableAsset = true, CancellationToken cancellationToken = default) =>
        hasBillableAsset
            ? ledger.CompleteAsync(transaction, usage, cancellationToken)
            : ledger.FailAsync(transaction, GenerationJobErrorCodes.NoBillableAsset, usage, cancellationToken);

    public Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default) =>
        ledger.FailAsync(transaction, failureCode, usage, cancellationToken);

    public Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default) =>
        ledger.CancelAsync(transaction, cancellationCode, cancellationToken);
}

public interface IGenerationJobService
{
    Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null, Guid? retryOfJobId = null);
    Task<GenerationJob> CreatePendingMovieProductionAsync(Guid userId, CreateGenerationJobRequest request, string idempotencyKey, CancellationToken cancellationToken = default, string? requestId = null);
    Task<GenerationJob?> PublishPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> AbortPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null);
    Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    /// <summary>
    /// <see cref="GetAsync"/> plus the live downloadability of exactly the renditions this job's
    /// published <c>ResultJson</c> snapshot advertises, so the studio read path can answer each
    /// advertised format from the current stored-file state instead of from the immutable
    /// publication-time record (issue #1049).
    ///
    /// The production service resolves the live facts. The default keeps the historical payload
    /// byte-for-byte intact for implementations that carry no availability data of their own: it
    /// performs the same authorized read and pairs it with <c>null</c>, which the contract mapper
    /// renders as the contract exactly as it is served today.
    /// </summary>
    Task<GenerationJobPublishedReadModel?> GetPublishedAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        ToReadModelWithoutAvailabilityAsync(GetAsync(userId, jobId, cancellationToken));

    private static async Task<GenerationJobPublishedReadModel?> ToReadModelWithoutAvailabilityAsync(Task<GenerationJob?> read)
    {
        var job = await read;
        return job is null ? null : new GenerationJobPublishedReadModel(job, null);
    }
    Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default);
    Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An authorized generation-job read paired with the live availability of the renditions its
/// published snapshot advertises.
///
/// The dictionary maps representation identity to "the download route would serve this rendition
/// right now", the same fact <c>AssetService.GetDownloadAsync</c> enforces with
/// <c>representation.StoredFile is { Status: StoredFileStatus.Ready }</c>. It is <c>null</c> when the
/// snapshot advertises no renditions at all, which keeps the served contract byte-for-byte what it
/// was before this seam existed, and is populated - failing closed per identity - as soon as the
/// snapshot promises at least one download. The job's <c>ResultJson</c> is never rewritten here.
/// </summary>
public sealed record GenerationJobPublishedReadModel(GenerationJob Job, IReadOnlyDictionary<Guid, bool>? RepresentationAvailability);


internal static class GenerationJobPublicationContract
{
    public const string MarkerProperty = "_taslimPublicationContract";
    public const string ApprovedMovieProductionV1 = "approved-movie-production/v1";
    public const string LinkageIncompleteErrorCode = "MOVIE_PRODUCTION_LINK_INCOMPLETE";

    public static string AddApprovedMovieProductionMarker(string inputJson, bool normalizeMovieClipId)
    {
        JsonObject? input;
        try { input = JsonNode.Parse(inputJson) as JsonObject; }
        catch (JsonException) { input = null; }
        if (input is null)
            throw new GenerationJobValidationException("MOVIE_PRODUCTION_TARGET_INVALID", "The approved movie production input is invalid.");

        foreach (var key in input.Select(property => property.Key)
                     .Where(key => string.Equals(key, MarkerProperty, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            input.Remove(key);

        if (normalizeMovieClipId)
        {
            var clipIdProperty = input.Select(property => property.Key)
                .FirstOrDefault(key => string.Equals(key, nameof(MovieGenerationInput.MovieClipId), StringComparison.OrdinalIgnoreCase));
            if (clipIdProperty is null)
                throw new GenerationJobValidationException("MOVIE_PRODUCTION_TARGET_INVALID", "The approved movie production clip is missing.");
            input[clipIdProperty] = Guid.Empty;
        }

        input[MarkerProperty] = ApprovedMovieProductionV1;
        return input.ToJsonString();
    }

    public static (bool HasMarker, bool IsApprovedMovieProduction) ReadMarker(string inputJson)
    {
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (false, false);
            var found = false;
            var isApproved = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, MarkerProperty, StringComparison.OrdinalIgnoreCase)) continue;
                if (found) return (true, false);
                found = true;
                isApproved = property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(property.Value.GetString(), ApprovedMovieProductionV1, StringComparison.Ordinal);
            }
            return (found, isApproved);
        }
        catch (JsonException) { return (false, false); }
    }

    public static async Task<bool> HasDurableApprovedMovieLinksAsync(
        TaslimDbContext db,
        GenerationJob job,
        CancellationToken cancellationToken)
    {
        var marker = ReadMarker(job.InputJson);
        if (!marker.HasMarker || !marker.IsApprovedMovieProduction
            || !string.Equals(job.JobType, GenerationJobTypes.MovieClipGenerate, StringComparison.OrdinalIgnoreCase))
            return false;

        MovieGenerationInput? input;
        try { input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson); }
        catch (JsonException) { input = null; }
        if (input is null
            || !string.Equals(input.Operation, MovieStudioOperations.SceneClip, StringComparison.OrdinalIgnoreCase)
            || input.MovieProjectId == Guid.Empty
            || input.MovieClipId == Guid.Empty
            || !input.MovieSceneId.HasValue || input.MovieSceneId.Value == Guid.Empty
            || !input.MovieShotId.HasValue || input.MovieShotId.Value == Guid.Empty
            || !input.SourceProductionVersionId.HasValue || input.SourceProductionVersionId.Value == Guid.Empty)
            return false;

        var clipExists = await db.MovieClips.AsNoTracking().AnyAsync(clip =>
            clip.Id == input.MovieClipId
            && clip.GenerationJobId == job.Id
            && clip.MovieProjectId == input.MovieProjectId
            && clip.MovieSceneId == input.MovieSceneId
            && clip.MovieShotId == input.MovieShotId
            && clip.MovieProject.WorkspaceId == job.WorkspaceId
            && clip.MovieProject.ProjectId == job.ProjectId,
            cancellationToken);
        if (!clipExists) return false;

        var sourceExists = await db.MovieProductionVersions.AsNoTracking().AnyAsync(source =>
            source.Id == input.SourceProductionVersionId.Value
            && source.MovieShotId == input.MovieShotId.Value
            && source.Status == MovieProductionVersionStatuses.Approved
            && source.MovieShot.Scene.Id == input.MovieSceneId.Value
            && source.MovieShot.Scene.MovieProjectId == input.MovieProjectId
            && source.MovieShot.Scene.MovieProject.WorkspaceId == job.WorkspaceId
            && source.MovieShot.Scene.MovieProject.ProjectId == job.ProjectId,
            cancellationToken);
        if (!sourceExists) return false;

        var linkedVersions = await db.MovieProductionVersions.AsNoTracking()
            .Where(version => version.GenerationJobId == job.Id
                && version.MovieShotId == input.MovieShotId.Value
                && version.SourceVersionId == input.SourceProductionVersionId.Value
                && version.Stage == MovieProductionStages.ProductionRender
                && version.Status == MovieProductionVersionStatuses.PendingApproval
                && version.CreatedByUserId == job.CreatedByUserId)
            .Select(version => version.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (linkedVersions.Count != 1) return false;

        var expectedFromStage = await db.MovieShots.AsNoTracking()
            .Where(shot => shot.Id == input.MovieShotId.Value
                && shot.MovieSceneId == input.MovieSceneId.Value
                && shot.Scene.MovieProjectId == input.MovieProjectId
                && shot.Scene.MovieProject.WorkspaceId == job.WorkspaceId
                && shot.Scene.MovieProject.ProjectId == job.ProjectId)
            .Select(shot => shot.ProductionStage)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(expectedFromStage)) return false;

        return await db.MovieProductionStageTransitions.AsNoTracking().AnyAsync(transition =>
            transition.GenerationJobId == job.Id
            && transition.MovieShotId == input.MovieShotId.Value
            && transition.MovieProductionVersionId == linkedVersions[0]
            && transition.SourceVersionId == input.SourceProductionVersionId.Value
            && transition.FromStage == expectedFromStage
            && transition.ToStage == MovieProductionStages.ProductionRender
            && transition.EventType == "created"
            && transition.ActorUserId == job.CreatedByUserId,
            cancellationToken);
    }
}

public enum GenerationJobCancelResult
{
    Cancelled,
    CancellationRequested,
    NotFound,
    Forbidden,
    Conflict,
}

public sealed class GenerationJobService(
    TaslimDbContext db,
    WorkspaceAccessService access,
    IGenerationJobQueue queue,
    IGenerationJobUsageService usage,
    IGenerationCostGuardrailService costGuardrails,
    IHttpContextAccessor httpContextAccessor,
    IOptions<ImageGenerationOptions> imageOptions,
    MovieCollaborationAccess? movieCollaboration = null) : IGenerationJobService
{
    public async Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null, Guid? retryOfJobId = null)
    {
        if (!GenerationJobTypes.Supported.Contains(request.JobType.Trim()))
            throw new GenerationJobValidationException(GenerationJobErrorCodes.TypeNotSupported, "This job type is not available.");
        if (!await access.IsMemberAsync(userId, request.WorkspaceId, cancellationToken))
            throw new GenerationJobForbiddenException();
        if (request.ProjectId.HasValue && !await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == request.WorkspaceId, cancellationToken))
            throw new GenerationJobValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");
        if (GenerationJobTypes.MovieTypes.Contains(request.JobType.Trim()))
            await ValidateMovieJobTargetAsync(userId, request, cancellationToken, retryOfJobId);
        else if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(request.JobType.Trim()))
            await ValidateMovieDialogueVoiceJobTargetAsync(userId, request, cancellationToken);
        else if (GenerationJobTypes.MovieSoundTypes.Contains(request.JobType.Trim()))
            await ValidateMovieSoundJobTargetAsync(userId, request, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(request.InputJson);
        }
        catch (JsonException)
        {
            throw new GenerationJobValidationException("INVALID_INPUT_JSON", "The job input is not valid JSON.");
        }

        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedJobType = GenerationJobTypes.Supported.First(type => string.Equals(type, request.JobType.Trim(), StringComparison.OrdinalIgnoreCase));
        var normalizedTitle = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        GenerationJob? retryOf = null;
        if (retryOfJobId.HasValue)
        {
            retryOf = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == retryOfJobId.Value, cancellationToken);
            if (retryOf is null || retryOf.WorkspaceId != request.WorkspaceId || retryOf.JobType != normalizedJobType)
                throw new GenerationJobValidationException("RETRY_SOURCE_NOT_FOUND", "The retry source is not available.");
            if (retryOf.Status is not (GenerationJobStatus.Failed or GenerationJobStatus.Cancelled))
                throw new GenerationJobValidationException("RETRY_SOURCE_NOT_TERMINAL", "Only failed or cancelled jobs can be retried.");
        }
        var requestFingerprint = ComputeRequestFingerprint(request.WorkspaceId, request.ProjectId, normalizedJobType, normalizedTitle, request.InputJson, request.EstimatedProviderCostUsd, retryOfJobId);
        if (normalizedKey is not null)
        {
            var existing = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == normalizedKey, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                    throw new GenerationJobValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different generation request.");
                return existing;
            }
        }

        var estimate = request.InternalCostEstimate
            ?? (request.EstimatedProviderCostUsd.HasValue
                ? new GenerationCostEstimate(true, Math.Max(0m, request.EstimatedProviderCostUsd.Value), UsageCurrencies.Usd, null, null, null, [])
                : GenerationCostEstimate.Unknown("job_estimate_missing"));
        var preflight = await costGuardrails.EvaluateAsync(
            userId,
            request.WorkspaceId,
            request.ProjectId,
            estimate,
            request.ConfirmationAccepted,
            cancellationToken);
        if (!preflight.Allowed)
            throw new GenerationJobValidationException(preflight.RejectionCode ?? "GENERATION_COST_CAP_EXCEEDED", preflight.RejectionMessage ?? "This generation exceeds a configured safety limit.");
        if (preflight.ConfirmationRequired && !request.ConfirmationAccepted)
            throw new GenerationJobValidationException("GENERATION_CONFIRMATION_REQUIRED", "Explicit confirmation is required before queueing this generation.");

        var now = DateTime.UtcNow;
        requestId ??= httpContextAccessor.HttpContext?.TraceIdentifier;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            CreatedByUserId = userId,
            JobType = normalizedJobType,
            Status = GenerationJobStatus.Pending,
            Title = normalizedTitle,
            InputJson = request.InputJson,
            IdempotencyKey = normalizedKey,
            RequestFingerprint = requestFingerprint,
            RequestId = requestId,
            RetryOfJobId = retryOfJobId,
            RetryCount = retryOf is null ? 0 : retryOf.RetryCount + 1,
            EstimatedProviderCostUsd = request.EstimatedProviderCostUsd,
            EstimatedProviderCostKnown = request.EstimatedProviderCostUsd.HasValue,
            CostEstimateJson = request.InternalCostEstimateJson ?? request.InternalCostEstimate?.ToJson(),
            ProgressPercent = 0,
            CreatedAt = now,
        };
        db.GenerationJobs.Add(job);
        // Keep the job row, usage reservation, and queue transition in one
        // transaction even when the caller did not provide an idempotency key.
        // Otherwise a transient queue/database failure can strand a Pending job
        // with a live usage record that no worker will ever claim.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await usage.BeginAsync(job, request.EstimatedProviderCostUsd, cancellationToken);
            await queue.EnqueueAsync(job.Id, cancellationToken);
            job.Status = GenerationJobStatus.Queued;
            job.QueuedAt = DateTime.UtcNow;
            await transaction.CommitAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException) when (normalizedKey is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.Entry(job).State = EntityState.Detached;
            var existing = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == normalizedKey, cancellationToken);
            if (existing is null) throw;
            if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                throw new GenerationJobValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different generation request.");
            return existing;
        }
    }

    public async Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null)
    {
        var source = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (source is null || !await access.IsMemberAsync(userId, source.WorkspaceId, cancellationToken) || IsPrivateExecutionRecord(source, userId)) return null;
        if (source.Status is not (GenerationJobStatus.Failed or GenerationJobStatus.Cancelled))
            throw new GenerationJobValidationException("RETRY_SOURCE_NOT_TERMINAL", "Only failed or cancelled jobs can be retried.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new GenerationJobValidationException("RETRY_IDEMPOTENCY_REQUIRED", "A retry idempotency key is required.");
        // Duplicate-paid-submission isolation. A music job that ended in MUSIC_OUTPUT_RETRIEVAL_FAILED
        // already created (and was billed by the provider for) its generation; only delivery of the
        // finished result failed. RetryAsync re-runs CreateAsync with the source input, which for
        // music means a fresh POST to the provider, so this refusal is enforced here rather than
        // left to the retry copy. Two music codes are refused and they cover the two unknown states:
        // the result is known to exist but could not be delivered, and the submission was accepted but
        // its outcome was never observed. Each family also covers the cancellation that proves the very
        // same state (F-36): cancelling after the provider reported completion leaves an undelivered
        // finished track, exactly like a delivery timeout, and cancelling an accepted generation that
        // was never observed leaves an unknown outcome, exactly like a polling timeout. Every other
        // terminal state - the provider-outage classes, a provider-declared rejection, and a
        // cancellation before anything was accepted - keeps its existing retry behaviour, because those
        // states are evidence that nothing is in flight.
        if (string.Equals(source.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase)
            && source.ErrorCode is GenerationJobErrorCodes.MusicOutputRetrievalFailed or GenerationJobErrorCodes.MusicCancelledUndeliveredOutput)
        {
            throw new GenerationJobValidationException(
                GenerationJobErrorCodes.MusicRetrievalRetryBlocked,
                "This retry was blocked because the music was already generated and only delivery failed. Generating again would create a duplicate.");
        }
        // Accepted-but-unconfirmed: the provider took the submission and we never saw its outcome -
        // either because polling lost contact with it (MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED) or because
        // the member cancelled it (MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION, F-36). A retry would be a
        // SECOND paid submission in both cases, so the endpoint refuses it until the first attempt is
        // reconciled (explicit user policy 15:59Z: avoid duplicate charges over immediate retry). This
        // supersedes the earlier informed-customer-retry posture: "you may retry once" was truthful
        // about the risk but still let the duplicate happen, and there is no verified safe-recovery path
        // for an accepted generation today - RecoverExpiredClaimsAsync already refuses to re-dispatch
        // music, so nothing on this branch can reattach to the first attempt.
        if (string.Equals(source.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase)
            && source.ErrorCode is GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed or GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission)
        {
            throw new GenerationJobValidationException(
                GenerationJobErrorCodes.MusicSubmissionRetryBlocked,
                "This retry was blocked because the first attempt may already have reached the provider and its outcome has not been reconciled yet. Starting another would risk a duplicate paid generation.");
        }
        if (string.Equals(source.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase) && !imageOptions.Value.Enabled)
            throw new GenerationJobValidationException(GenerationJobErrorCodes.ImageStudioUnavailable, "Image generation is not available right now.");

        var retryInputJson = source.InputJson;
        MovieDialogueTake? pendingDialogueTake = null;
        if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(source.JobType))
        {
            MovieDialogueVoiceInput? dialogueInput;
            try { dialogueInput = JsonSerializer.Deserialize<MovieDialogueVoiceInput>(source.InputJson); }
            catch (JsonException) { dialogueInput = null; }
            var previousTake = dialogueInput is null
                ? null
                : await db.MovieDialogueTakes.SingleOrDefaultAsync(item => item.Id == dialogueInput.MovieDialogueTakeId && item.GenerationJobId == source.Id, cancellationToken);
            if (dialogueInput is null || previousTake is null)
                throw new GenerationJobValidationException(GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid, "The dialogue retry source is no longer available.");
            var now = DateTime.UtcNow;
            pendingDialogueTake = new MovieDialogueTake
            {
                Id = Guid.NewGuid(), MovieDialogueLineId = previousTake.MovieDialogueLineId, MovieClipId = previousTake.MovieClipId,
                VersionNumber = (await db.MovieDialogueTakes.Where(item => item.MovieDialogueLineId == previousTake.MovieDialogueLineId).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1,
                Label = $"Retry {previousTake.VersionNumber + 1}", Status = MovieDialogueTakeStatuses.Planned, CreatedAt = now, UpdatedAt = now,
            };
            db.MovieDialogueTakes.Add(pendingDialogueTake);
            await db.SaveChangesAsync(cancellationToken);
            retryInputJson = JsonSerializer.Serialize(dialogueInput with { MovieDialogueTakeId = pendingDialogueTake.Id });
        }
        GenerationJob retry;
        try
        {
            retry = await CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = source.WorkspaceId,
                ProjectId = source.ProjectId,
                JobType = source.JobType,
                Title = source.Title,
                InputJson = retryInputJson,
                EstimatedProviderCostUsd = source.EstimatedProviderCostUsd,
                InternalCostEstimate = ParseCostEstimate(source.CostEstimateJson),
            }, cancellationToken, idempotencyKey, requestId, jobId);
        }
        catch
        {
            if (pendingDialogueTake is not null)
            {
                db.MovieDialogueTakes.Remove(pendingDialogueTake);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            throw;
        }
        if (pendingDialogueTake is not null)
        {
            pendingDialogueTake.GenerationJobId = retry.Id;
            pendingDialogueTake.Status = MovieDialogueTakeStatuses.Queued;
            pendingDialogueTake.UpdatedAt = DateTime.UtcNow;
            await db.MovieDialogueLines.Where(item => item.Id == pendingDialogueTake.MovieDialogueLineId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieDialogueLineStatuses.Queued).SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (GenerationJobTypes.MovieTypes.Contains(source.JobType))
        {
            if (string.Equals(source.JobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase))
            {
                MovieFinalAssemblyInput? assemblyInput;
                try { assemblyInput = JsonSerializer.Deserialize<MovieFinalAssemblyInput>(source.InputJson); }
                catch (JsonException) { assemblyInput = null; }
                if (assemblyInput is not null)
                {
                    var assembly = await db.MovieAssemblies.FirstOrDefaultAsync(item => item.Id == assemblyInput.AssemblyId && item.MovieProjectId == assemblyInput.MovieProjectId, cancellationToken);
                    if (assembly is not null)
                    {
                        assembly.GenerationJobId = retry.Id;
                        assembly.AssetId = null;
                        assembly.Status = MovieAssemblyStatuses.Queued;
                        assembly.QcStatus = MovieFinalAssemblyQcStatuses.NotRun;
                        assembly.QcResultJson = null;
                        assembly.LastErrorCode = null;
                        assembly.ProgressPercent = 0;
                        assembly.CheckpointJson = JsonSerializer.Serialize(new { phase = "retry_resume", progressPercent = 0 });
                        assembly.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }
                return retry;
            }
            MovieGenerationInput? input;
            try { input = JsonSerializer.Deserialize<MovieGenerationInput>(source.InputJson); }
            catch (JsonException) { input = null; }
            if (input is not null)
            {
                var clip = await db.MovieClips.FirstOrDefaultAsync(item => item.Id == input.MovieClipId && item.MovieProjectId == input.MovieProjectId, cancellationToken);
                if (clip is not null && !await db.MovieTakes.AnyAsync(item => item.GenerationJobId == retry.Id, cancellationToken))
                {
                    var previousTake = await db.MovieTakes.FirstOrDefaultAsync(item => item.GenerationJobId == source.Id && item.MovieShotId == input.MovieShotId, cancellationToken);
                    var now = DateTime.UtcNow;
                    clip.GenerationJobId = retry.Id;
                    clip.AssetId = null;
                    clip.StoredFileId = null;
                    clip.ProviderKey = null;
                    clip.ProviderClipId = null;
                    clip.Status = MovieClipStatuses.Queued;
                    clip.UpdatedAt = now;
                    if (input.MovieShotId.HasValue && previousTake is not null)
                    {
                        var versionNumber = (await db.MovieTakes.Where(item => item.MovieShotId == input.MovieShotId.Value).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1;
                        db.MovieTakes.Add(new MovieTake
                        {
                            Id = Guid.NewGuid(),
                            MovieShotId = input.MovieShotId.Value,
                            VersionNumber = versionNumber,
                            Label = $"Retry {versionNumber}",
                            Status = MovieTakeStatuses.Queued,
                            QualityLevel = previousTake?.QualityLevel ?? MovieQualityLevels.Standard,
                            AutoDirectorEnabled = previousTake?.AutoDirectorEnabled ?? false,
                            MovieClipId = clip.Id,
                            GenerationJobId = retry.Id,
                            RetryOfTakeId = previousTake?.Id,
                            CreatedAt = now,
                            UpdatedAt = now,
                        });
                    }
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }
        return retry;
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        var normalized = idempotencyKey?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > 80)
            throw new GenerationJobValidationException("IDEMPOTENCY_KEY_INVALID", "The idempotency key is too long.");
        return normalized;
    }

    private static string ComputeRequestFingerprint(Guid workspaceId, Guid? projectId, string jobType, string? title, string inputJson, decimal? estimatedProviderCostUsd, Guid? retryOfJobId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            workspaceId,
            projectId,
            jobType,
            title,
            inputJson,
            estimatedProviderCostUsd,
            retryOfJobId,
        }))));

    private static GenerationCostEstimate? ParseCostEstimate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<GenerationCostEstimate>(json); }
        catch (JsonException) { return null; }
    }

    private async Task ValidateMovieJobTargetAsync(
        Guid userId,
        CreateGenerationJobRequest request,
        CancellationToken cancellationToken,
        Guid? retryOfJobId = null,
        Guid? allowedLinkedGenerationJobId = null)
    {
        var isAssembly = string.Equals(request.JobType.Trim(), GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase);
        var isQuickMovie = string.Equals(request.JobType.Trim(), GenerationJobTypes.MovieQuickGenerate, StringComparison.OrdinalIgnoreCase);
        if (isAssembly)
        {
            MovieFinalAssemblyInput? assemblyInput;
            try { assemblyInput = JsonSerializer.Deserialize<MovieFinalAssemblyInput>(request.InputJson); }
            catch (JsonException) { assemblyInput = null; }
            var assembly = assemblyInput is null
                ? null
                : await db.MovieAssemblies.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == assemblyInput.AssemblyId, cancellationToken);
            if (assemblyInput is null || assemblyInput.MovieProjectId == Guid.Empty || assemblyInput.AssemblyId == Guid.Empty || assembly is null
                || assembly.MovieProjectId != assemblyInput.MovieProjectId
                || assembly.MovieProject.WorkspaceId != request.WorkspaceId
                || request.ProjectId != assembly.MovieProject.ProjectId
                || (assembly.GenerationJobId is not null && !retryOfJobId.HasValue)
                || !string.Equals(assembly.IdempotencyKey, assemblyInput.IdempotencyKey, StringComparison.Ordinal))
                throw new GenerationJobValidationException("MOVIE_ASSEMBLY_TARGET_INVALID", "The final assembly target is invalid or already claimed.");
            if (assembly.MovieProject.CreatedByUserId != userId && (movieCollaboration is null || !await movieCollaboration.HasPermissionAsync(userId, assembly.MovieProjectId, MoviePermissions.FinalApproval, cancellationToken)))
                throw new GenerationJobForbiddenException();
            return;
        }
        if (!isQuickMovie && !string.Equals(request.JobType.Trim(), GenerationJobTypes.MovieClipGenerate, StringComparison.OrdinalIgnoreCase))
            throw new GenerationJobValidationException("MOVIE_JOB_TARGET_INVALID", "Movie jobs must be created through a validated movie clip target.");

        MovieGenerationInput? input;
        try { input = JsonSerializer.Deserialize<MovieGenerationInput>(request.InputJson); }
        catch (JsonException) { input = null; }
        if (input is null || input.MovieProjectId == Guid.Empty || input.MovieClipId == Guid.Empty || (!isQuickMovie && input.MovieSceneId == Guid.Empty))
            throw new GenerationJobValidationException("MOVIE_JOB_TARGET_INVALID", "The movie job target is invalid.");

        var clip = await db.MovieClips
            .Include(item => item.MovieProject)
            .FirstOrDefaultAsync(item => item.Id == input.MovieClipId, cancellationToken);
        if (clip is null || clip.MovieProjectId != input.MovieProjectId || clip.MovieProject.WorkspaceId != request.WorkspaceId
            || (isQuickMovie ? input.Operation != MovieStudioOperations.QuickMovie || input.MovieSceneId.HasValue || input.MovieShotId.HasValue
                : input.Operation != MovieStudioOperations.SceneClip || clip.MovieSceneId != input.MovieSceneId || clip.MovieShotId != input.MovieShotId)
            || request.ProjectId != clip.MovieProject.ProjectId
            || (clip.GenerationJobId is not null && clip.GenerationJobId != allowedLinkedGenerationJobId))
            throw new GenerationJobValidationException("MOVIE_JOB_TARGET_INVALID", "The movie job target does not belong to the selected project or is no longer available.");
        if (clip.MovieProject.CreatedByUserId != userId && (movieCollaboration is null || !await movieCollaboration.HasPermissionAsync(userId, clip.MovieProjectId, MoviePermissions.Generate, cancellationToken)))
            throw new GenerationJobForbiddenException();
    }

    private async Task ValidateMovieDialogueVoiceJobTargetAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken)
    {
        MovieDialogueVoiceInput? input;
        try { input = JsonSerializer.Deserialize<MovieDialogueVoiceInput>(request.InputJson); }
        catch (JsonException) { input = null; }
        if (input is null || input.MovieProjectId == Guid.Empty || input.MovieClipId == Guid.Empty || input.MovieDialogueLineId == Guid.Empty || input.MovieDialogueTakeId == Guid.Empty)
            throw new GenerationJobValidationException(GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid, "The dialogue voice target is invalid.");
        var clip = await db.MovieClips.Include(item => item.MovieProject).SingleOrDefaultAsync(item => item.Id == input.MovieClipId, cancellationToken);
        var line = await db.MovieDialogueLines.SingleOrDefaultAsync(item => item.Id == input.MovieDialogueLineId && item.MovieClipId == input.MovieClipId, cancellationToken);
        var take = await db.MovieDialogueTakes.SingleOrDefaultAsync(item => item.Id == input.MovieDialogueTakeId && item.MovieDialogueLineId == input.MovieDialogueLineId, cancellationToken);
        if (clip is null || line is null || take is null || clip.MovieProjectId != input.MovieProjectId || clip.MovieProject.WorkspaceId != request.WorkspaceId
            || request.ProjectId != clip.MovieProject.ProjectId || take.GenerationJobId is not null || take.Status != MovieDialogueTakeStatuses.Planned)
            throw new GenerationJobValidationException(GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid, "The dialogue voice target is no longer available.");
        if (clip.MovieProject.CreatedByUserId != userId && (movieCollaboration is null || !await movieCollaboration.HasPermissionAsync(userId, clip.MovieProjectId, MoviePermissions.Generate, cancellationToken)))
            throw new GenerationJobForbiddenException();
    }

    private async Task ValidateMovieSoundJobTargetAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken)
    {
        MovieSoundGenerationInput? input;
        try { input = JsonSerializer.Deserialize<MovieSoundGenerationInput>(request.InputJson); }
        catch (JsonException) { input = null; }
        if (input is null || input.MovieProjectId == Guid.Empty || input.MovieSoundTrackId == Guid.Empty)
            throw new GenerationJobValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The movie sound target is invalid.");

        var track = await db.MovieSoundTracks
            .Include(item => item.MovieProject)
            .FirstOrDefaultAsync(item => item.Id == input.MovieSoundTrackId, cancellationToken);
        if (track is null || track.MovieProjectId != input.MovieProjectId || track.MovieProject.WorkspaceId != request.WorkspaceId
            || track.MovieSceneId != input.MovieSceneId || track.MovieShotId != input.MovieShotId
            || track.GenerationJobId is not null || request.ProjectId != track.MovieProject.ProjectId)
            throw new GenerationJobValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The movie sound target is no longer available.");
        if (track.CreatedByUserId != userId && (movieCollaboration is null || !await movieCollaboration.HasPermissionAsync(userId, track.MovieProjectId, MoviePermissions.Generate, cancellationToken)))
            throw new GenerationJobForbiddenException();
    }

    public async Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.GenerationJobs.AsNoTracking().Include(item => item.Outputs).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken) || IsPrivateExecutionRecord(job, userId)) return null;
        return job;
    }

    public async Task<GenerationJobPublishedReadModel?> GetPublishedAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        // Authorization stays exactly where it was: the availability half below only ever runs on a
        // job this caller has already been allowed to read.
        var job = await GetAsync(userId, jobId, cancellationToken);
        if (job is null) return null;
        return new GenerationJobPublishedReadModel(job, await ReadPublishedRepresentationAvailabilityAsync(userId, job, cancellationToken));
    }

    /// <summary>
    /// Answers every rendition the published <c>ResultJson</c> snapshot advertises with the answer the
    /// download route would give <em>this</em> viewer right now, in ONE projected query regardless of
    /// how many renditions the snapshot names (issue #1049: the snapshot is baked at completion and is
    /// never rewritten, so after a retention prune it still promises a download the route refuses with
    /// <c>409 ASSET_FILE_UNAVAILABLE</c>).
    ///
    /// The answer comes from <see cref="AssetService.CanDownloadRepresentation"/>, the same predicate
    /// <c>AssetService.GetDownloadAsync</c> enforces and <c>AssetContractMapper.ToDto</c> reports, so
    /// the studio half of the contract cannot advertise a rendition the route refuses. File status is
    /// only one of its three branches: a Ready file the viewer may not read (conversation-scoped or
    /// non-shared) and a Ready document rendition whose stored metadata disagrees with the file are
    /// both refusals, and the studio views have to withdraw for them exactly as the library lane does
    /// (#1041 / #1092 invariant, applied to the studio path it did not cover).
    ///
    /// The set is bounded by construction: the identities come from this authorized job's snapshot and
    /// nothing else is asked for, only the columns the shared predicate reads are projected (no bytes,
    /// storage keys, hashes or neighbouring rows), and each identity is additionally pinned to the
    /// authorized job's own workspace so a stale or foreign identity inside a snapshot fails closed
    /// instead of reporting another workspace's file state.
    ///
    /// Returns <c>null</c> when the snapshot advertises no rendition at all, which leaves the served
    /// contract byte-for-byte unchanged; a job that promises downloads gets a complete, fail-closed
    /// answer for each of them.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, bool>?> ReadPublishedRepresentationAvailabilityAsync(Guid userId, GenerationJob job, CancellationToken cancellationToken)
    {
        var advertised = GenerationJobContractMapper.AdvertisedRepresentationIds(job.ResultJson);
        if (advertised.Count == 0) return null;
        var advertisedIds = advertised.ToList();
        var workspaceId = job.WorkspaceId;
        var rows = await db.AssetRepresentations.AsNoTracking()
            .Where(representation => advertisedIds.Contains(representation.Id)
                && representation.Asset.WorkspaceId == workspaceId)
            .Select(representation => new
            {
                representation.Id,
                representation.AssetId,
                representation.StoredFileId,
                representation.RepresentationType,
                representation.FileName,
                RepresentationContentType = representation.ContentType,
                representation.SizeBytes,
                AssetWorkspaceId = representation.Asset.WorkspaceId,
                AssetType = representation.Asset.AssetType,
                // StoredFile is a required relationship (inner join), so these are always present in
                // a returned row; Project is optional, hence the explicit null branch that EF can
                // translate to a CASE instead of dereferencing a missing join partner.
                FileId = representation.StoredFile.Id,
                FileStatus = representation.StoredFile.Status,
                FileWorkspaceId = representation.StoredFile.WorkspaceId,
                FileUserId = representation.StoredFile.UserId,
                FileProjectId = representation.StoredFile.ProjectId,
                FileConversationId = representation.StoredFile.ConversationId,
                FileProjectWorkspaceId = representation.StoredFile.Project == null
                    ? (Guid?)null
                    : representation.StoredFile.Project.WorkspaceId,
                FileContentType = representation.StoredFile.ContentType,
                FileExtension = representation.StoredFile.Extension,
                FileSizeBytes = representation.StoredFile.SizeBytes,
            })
            .ToListAsync(cancellationToken);

        // Seeded unavailable, then confirmed from the loaded state: an advertised rendition whose row
        // is gone entirely, and one whose stored file row is gone, is a rendition the route cannot
        // serve either, so both stay false.
        var availability = new Dictionary<Guid, bool>(advertisedIds.Count);
        foreach (var id in advertisedIds) availability[id] = false;
        foreach (var row in rows)
        {
            // Rebuilt from the projection rather than loaded as tracked graph entities: the predicate
            // reads exactly these members, and only its own answer leaves this method.
            var asset = new Asset { Id = row.AssetId, WorkspaceId = row.AssetWorkspaceId, AssetType = row.AssetType };
            var representation = new AssetRepresentation
            {
                Id = row.Id,
                AssetId = row.AssetId,
                StoredFileId = row.StoredFileId,
                RepresentationType = row.RepresentationType,
                FileName = row.FileName,
                ContentType = row.RepresentationContentType,
                SizeBytes = row.SizeBytes,
            };
            var file = new StoredFile
            {
                Id = row.FileId,
                WorkspaceId = row.FileWorkspaceId,
                UserId = row.FileUserId,
                ProjectId = row.FileProjectId,
                ConversationId = row.FileConversationId,
                Project = row.FileProjectWorkspaceId is null
                    ? null
                    : new Project { Id = row.FileProjectId ?? Guid.Empty, WorkspaceId = row.FileProjectWorkspaceId.Value },
                Status = row.FileStatus,
                ContentType = row.FileContentType,
                Extension = row.FileExtension,
                SizeBytes = row.FileSizeBytes,
            };
            availability[row.Id] = AssetService.CanDownloadRepresentation(asset, representation, file, userId);
        }
        return availability;
    }

    public async Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, filter.WorkspaceId, cancellationToken)) return null;
        var page = ApiPagination.NormalizePage(filter.Page);
        var pageSize = ApiPagination.NormalizePageSize(filter.PageSize);
        // Mirrors IsPrivateExecutionRecord: research records stay with their creator because the
        // published report asset is creator-private (#936), and an unscoped presentation export
        // publishes an asset that AssetVisibility already keeps private to its creator, so in both
        // cases the result JSON references assets other workspace members cannot read. Expressed
        // as a translatable predicate so paging and the total count stay consistent with the
        // hidden records.
        var query = db.GenerationJobs.AsNoTracking().Where(job => job.WorkspaceId == filter.WorkspaceId &&
            (job.CreatedByUserId == userId
                || (job.JobType != GenerationJobTypes.ResearchGenerate
                    && (job.JobType != GenerationJobTypes.PresentationGenerate || job.ProjectId != null))));
        if (filter.Status.HasValue) query = query.Where(job => job.Status == filter.Status.Value);
        if (filter.ProjectId.HasValue) query = query.Where(job => job.ProjectId == filter.ProjectId.Value);
        if (!string.IsNullOrWhiteSpace(filter.JobType)) query = query.Where(job => job.JobType == filter.JobType);
        var totalCount = await query.CountAsync(cancellationToken);
        var jobs = await query.Include(job => job.Outputs)
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Skip(ApiPagination.GetOffset(page, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new GenerationJobListDto(jobs.Select(GenerationJobContractMapper.ToDto).ToArray(), page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    public async Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null) return GenerationJobCancelResult.NotFound;
        if (!await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken) || IsPrivateExecutionRecord(job, userId)) return GenerationJobCancelResult.Forbidden;
        var now = DateTime.UtcNow;
        var immediate = await db.GenerationJobs
            .Where(item => item.Id == jobId && (item.Status == GenerationJobStatus.Pending || item.Status == GenerationJobStatus.Queued))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Cancelled)
                .SetProperty(item => item.ErrorCode, CancellationCode(job))
                .SetProperty(item => item.ErrorMessage, "The job was cancelled.")
                .SetProperty(item => item.ProgressPercent, 0)
                .SetProperty(item => item.CancelledAt, now), cancellationToken);
        if (immediate > 0)
        {
            if (GenerationJobTypes.MovieTypes.Contains(job.JobType))
            {
                if (string.Equals(job.JobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase))
                    await db.MovieAssemblies.Where(item => item.GenerationJobId == job.Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieAssemblyStatuses.Cancelled).SetProperty(item => item.LastErrorCode, GenerationJobErrorCodes.MovieAssemblyCancelled).SetProperty(item => item.UpdatedAt, now), cancellationToken);
                else
                    await db.MovieClips.Where(clip => clip.GenerationJobId == job.Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(clip => clip.Status, MovieClipStatuses.Cancelled).SetProperty(clip => clip.UpdatedAt, now), cancellationToken);
            }
            if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(job.JobType))
                await db.MovieDialogueTakes.Where(take => take.GenerationJobId == job.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(take => take.Status, MovieDialogueTakeStatuses.Cancelled).SetProperty(take => take.UpdatedAt, now), cancellationToken);
            if (GenerationJobTypes.MovieSoundTypes.Contains(job.JobType))
                await db.MovieSoundTracks.Where(track => track.GenerationJobId == job.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(track => track.Status, MovieSoundStatuses.Cancelled).SetProperty(track => track.UpdatedAt, now), cancellationToken);
            await CancelUsageAsync(job, CancellationCode(job), cancellationToken);
            return GenerationJobCancelResult.Cancelled;
        }

        var running = await db.GenerationJobs
            .Where(item => item.Id == jobId && item.Status == GenerationJobStatus.Running)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CancellationRequested, true), cancellationToken);
        return running > 0 ? GenerationJobCancelResult.CancellationRequested : GenerationJobCancelResult.Conflict;
    }

    // Execution records that describe creator-private output must not disclose the output's
    // identifiers. Research records embed report text derived from private uploads, and since
    // #936 the research report asset is creator-private for the same reason, so the record and
    // the asset hide from co-members together. A presentation job without a project publishes
    // an asset that AssetVisibility keeps private to its creator, so the asset id,
    // representation ids and exported file name carried in the presentation result JSON are
    // private to that creator as well. Project-scoped presentation and other deliverable jobs
    // keep the intentional workspace sharing surface for both the record and the asset.
    private static bool IsPrivateExecutionRecord(GenerationJob job, Guid userId) =>
        job.CreatedByUserId != userId
        && (string.Equals(job.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase)
            || IsUnscopedPresentationExportRecord(job));

    private static bool IsUnscopedPresentationExportRecord(GenerationJob job) =>
        string.Equals(job.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase)
        && job.ProjectId is null;

    private async Task CancelUsageAsync(GenerationJob job, string code, CancellationToken cancellationToken)
    {
        var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
        await usage.CancelAsync(transaction, code, cancellationToken);
    }

    private static string CancellationCode(GenerationJob job) =>
        GenerationJobTypes.MovieDialogueVoiceTypes.Contains(job.JobType)
            ? GenerationJobErrorCodes.MovieDialogueVoiceCancelled
            : GenerationJobTypes.MovieTypes.Contains(job.JobType)
            ? GenerationJobErrorCodes.MovieCancelled
            : GenerationJobTypes.MovieSoundTypes.Contains(job.JobType)
                ? GenerationJobErrorCodes.MovieSoundCancelled
            : string.Equals(job.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)
            ? GenerationJobErrorCodes.ImageCancelled
            : string.Equals(job.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.DocumentCancelled
                : string.Equals(job.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase)
                    ? GenerationJobErrorCodes.PresentationCancelled
                    : string.Equals(job.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase)
                        ? GenerationJobErrorCodes.ResearchCancelled
                        : string.Equals(job.JobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase)
                            ? GenerationJobErrorCodes.SocialCancelled
                            : string.Equals(job.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase)
            ? GenerationJobErrorCodes.MusicCancelled
            : string.Equals(job.JobType, GenerationJobTypes.VoiceGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.VoiceCancelled
                : GenerationJobErrorCodes.Cancelled;

    public async Task<GenerationJob> CreatePendingMovieProductionAsync(
        Guid userId,
        CreateGenerationJobRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default,
        string? requestId = null)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        if (normalizedKey is null)
            throw new GenerationJobValidationException("IDEMPOTENCY_KEY_REQUIRED", "An idempotency key is required for approved movie production.");

        var requestedJobType = request.JobType?.Trim();
        if (string.IsNullOrWhiteSpace(requestedJobType) || !GenerationJobTypes.Supported.Contains(requestedJobType))
            throw new GenerationJobValidationException(GenerationJobErrorCodes.TypeNotSupported, "This job type is not available.");
        if (!string.Equals(requestedJobType, GenerationJobTypes.MovieClipGenerate, StringComparison.OrdinalIgnoreCase))
            throw new GenerationJobValidationException("MOVIE_PRODUCTION_TARGET_INVALID", "Deferred publication is only available for approved movie production clips.");
        if (!await access.IsMemberAsync(userId, request.WorkspaceId, cancellationToken))
            throw new GenerationJobForbiddenException();
        if (request.ProjectId.HasValue && !await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == request.WorkspaceId, cancellationToken))
            throw new GenerationJobValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");

        var estimate = ValidatePendingMovieProductionEstimate(request);

        MovieGenerationInput? input;
        try { input = JsonSerializer.Deserialize<MovieGenerationInput>(request.InputJson); }
        catch (JsonException) { input = null; }
        if (input is null
            || !string.Equals(input.Operation, MovieStudioOperations.SceneClip, StringComparison.OrdinalIgnoreCase)
            || !input.MovieSceneId.HasValue || input.MovieSceneId.Value == Guid.Empty
            || !input.MovieShotId.HasValue || input.MovieShotId.Value == Guid.Empty
            || !input.SourceProductionVersionId.HasValue || input.SourceProductionVersionId.Value == Guid.Empty)
            throw new GenerationJobValidationException("MOVIE_PRODUCTION_TARGET_INVALID", "The approved movie production target is invalid.");

        var sourceIsApprovedForShot = await db.MovieProductionVersions.AsNoTracking().AnyAsync(source =>
            source.Id == input.SourceProductionVersionId.Value
            && source.MovieShotId == input.MovieShotId.Value
            && source.Status == MovieProductionVersionStatuses.Approved
            && source.MovieShot.Scene.Id == input.MovieSceneId.Value
            && source.MovieShot.Scene.MovieProjectId == input.MovieProjectId
            && source.MovieShot.Scene.MovieProject.WorkspaceId == request.WorkspaceId
            && source.MovieShot.Scene.MovieProject.ProjectId == request.ProjectId,
            cancellationToken);
        if (!sourceIsApprovedForShot)
            throw new GenerationJobValidationException("MOVIE_PRODUCTION_TARGET_INVALID", "The approved movie production source is no longer available.");

        var normalizedJobType = GenerationJobTypes.Supported.First(type => string.Equals(type, requestedJobType, StringComparison.OrdinalIgnoreCase));
        var normalizedTitle = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        var inputJson = GenerationJobPublicationContract.AddApprovedMovieProductionMarker(request.InputJson, normalizeMovieClipId: false);
        var fingerprintInputJson = GenerationJobPublicationContract.AddApprovedMovieProductionMarker(request.InputJson, normalizeMovieClipId: true);
        var requestFingerprint = ComputePendingMovieProductionFingerprint(
            request, normalizedJobType, normalizedTitle, fingerprintInputJson);

        var existing = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(
            item => item.CreatedByUserId == userId && item.IdempotencyKey == normalizedKey,
            cancellationToken);
        await ValidateMovieJobTargetAsync(
            userId,
            request,
            cancellationToken,
            allowedLinkedGenerationJobId: existing?.Id);
        if (existing is not null)
        {
            var existingMarker = GenerationJobPublicationContract.ReadMarker(existing.InputJson);
            if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                || !existingMarker.HasMarker || !existingMarker.IsApprovedMovieProduction)
                throw new GenerationJobValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different generation request.");
            return existing;
        }

        var preflight = await costGuardrails.EvaluateAsync(
            userId,
            request.WorkspaceId,
            request.ProjectId,
            estimate,
            request.ConfirmationAccepted,
            cancellationToken);
        if (!preflight.Allowed)
            throw new GenerationJobValidationException(preflight.RejectionCode ?? "GENERATION_COST_CAP_EXCEEDED", preflight.RejectionMessage ?? "This generation exceeds a configured safety limit.");
        if (preflight.ConfirmationRequired && !request.ConfirmationAccepted)
            throw new GenerationJobValidationException("GENERATION_CONFIRMATION_REQUIRED", "Explicit confirmation is required before queueing this generation.");

        requestId ??= httpContextAccessor.HttpContext?.TraceIdentifier;
        var now = DateTime.UtcNow;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            CreatedByUserId = userId,
            JobType = normalizedJobType,
            Status = GenerationJobStatus.Pending,
            Title = normalizedTitle,
            InputJson = inputJson,
            IdempotencyKey = normalizedKey,
            RequestFingerprint = requestFingerprint,
            RequestId = requestId,
            EstimatedProviderCostUsd = request.EstimatedProviderCostUsd,
            EstimatedProviderCostKnown = request.EstimatedProviderCostUsd.HasValue,
            CostEstimateJson = request.InternalCostEstimateJson ?? request.InternalCostEstimate?.ToJson(),
            ProgressPercent = 0,
            CreatedAt = now,
        };
        db.GenerationJobs.Add(job);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await usage.BeginAsync(job, request.EstimatedProviderCostUsd, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.Entry(job).State = EntityState.Detached;
            var collisionExisting = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(
                item => item.CreatedByUserId == userId && item.IdempotencyKey == normalizedKey,
                cancellationToken);
            if (collisionExisting is null) throw;
            var existingMarker = GenerationJobPublicationContract.ReadMarker(collisionExisting.InputJson);
            if (!string.Equals(collisionExisting.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                || !existingMarker.HasMarker || !existingMarker.IsApprovedMovieProduction)
                throw new GenerationJobValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different generation request.");
            return collisionExisting;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.Entry(job).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<GenerationJob?> PublishPendingMovieProductionAsync(
        Guid userId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.CreatedByUserId != userId
            || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        var marker = GenerationJobPublicationContract.ReadMarker(job.InputJson);
        if (!marker.HasMarker || !marker.IsApprovedMovieProduction
            || !string.Equals(job.JobType, GenerationJobTypes.MovieClipGenerate, StringComparison.OrdinalIgnoreCase))
            throw new GenerationJobValidationException("MOVIE_PRODUCTION_TARGET_INVALID", "The approved movie production job is invalid.");

        if (job.Status is GenerationJobStatus.Cancelled or GenerationJobStatus.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return job;
        }

        if (!await GenerationJobPublicationContract.HasDurableApprovedMovieLinksAsync(db, job, cancellationToken))
            throw new GenerationJobValidationException(GenerationJobPublicationContract.LinkageIncompleteErrorCode, "The approved movie production links are not durably complete.");

        if (job.Status == GenerationJobStatus.Pending)
            await queue.EnqueueAsync(job.Id, cancellationToken);
        else if (job.Status is not (GenerationJobStatus.Queued or GenerationJobStatus.Running or GenerationJobStatus.Succeeded))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        var published = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == job.Id, cancellationToken);
        if (published is null)
            throw new GenerationJobValidationException("MOVIE_PRODUCTION_TARGET_INVALID", "The approved movie production job is no longer available.");

        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    public async Task<bool> AbortPendingMovieProductionAsync(
        Guid userId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.CreatedByUserId != userId
            || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var marker = GenerationJobPublicationContract.ReadMarker(job.InputJson);
        if (!marker.HasMarker || !marker.IsApprovedMovieProduction
            || !string.Equals(job.JobType, GenerationJobTypes.MovieClipGenerate, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var now = DateTime.UtcNow;
        var cancelled = await db.GenerationJobs
            .Where(item => item.Id == jobId && item.CreatedByUserId == userId && item.Status == GenerationJobStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Cancelled)
                .SetProperty(item => item.ErrorCode, GenerationJobErrorCodes.MovieCancelled)
                .SetProperty(item => item.ErrorMessage, "Approved movie production was aborted before publication.")
                .SetProperty(item => item.ProgressPercent, 0)
                .SetProperty(item => item.CancelledAt, now), cancellationToken);
        if (cancelled == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var reservation = await usage.BeginAsync(job, cancellationToken: cancellationToken);
        await usage.CancelAsync(reservation, GenerationJobErrorCodes.MovieCancelled, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string ComputePendingMovieProductionFingerprint(
        CreateGenerationJobRequest request,
        string jobType,
        string? title,
        string normalizedInputJson)
    {
        var baseFingerprint = ComputeRequestFingerprint(
            request.WorkspaceId,
            request.ProjectId,
            jobType,
            title,
            normalizedInputJson,
            request.EstimatedProviderCostUsd,
            retryOfJobId: null);
        var trustedEstimateJson = request.InternalCostEstimateJson ?? request.InternalCostEstimate?.ToJson();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            baseFingerprint,
            trustedEstimateJson,
            request.ConfirmationAccepted,
        }))));
    }

    private static GenerationCostEstimate ValidatePendingMovieProductionEstimate(CreateGenerationJobRequest request)
    {
        var estimate = request.InternalCostEstimate;
        if (estimate is null
            || !estimate.IsKnown
            || !estimate.AmountUsd.HasValue
            || !request.EstimatedProviderCostUsd.HasValue
            || !string.Equals(estimate.Currency, UsageCurrencies.Usd, StringComparison.OrdinalIgnoreCase))
            throw new GenerationJobValidationException(
                "COST_ESTIMATE_UNKNOWN",
                "Approved movie production requires a known USD cost estimate.");

        var maximumUsd = request.EstimatedProviderCostUsd.Value;
        if (estimate.AmountUsd.Value < 0m || maximumUsd < 0m || estimate.AmountUsd.Value != maximumUsd)
            throw new GenerationJobValidationException(
                "COST_ESTIMATE_UNKNOWN",
                "The approved movie production cost estimate does not match its persisted USD maximum.");

        if (request.InternalCostEstimateJson is not null
            && !PersistedMovieCostEstimateMatches(request.InternalCostEstimateJson, maximumUsd))
            throw new GenerationJobValidationException(
                "COST_ESTIMATE_UNKNOWN",
                "The persisted approved movie cost estimate does not match its USD maximum.");

        return estimate;
    }

    private static bool PersistedMovieCostEstimateMatches(string json, decimal maximumUsd)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(nameof(MovieGenerationCostEstimate.State), out var state)
                && state.ValueKind == JsonValueKind.String
                && string.Equals(state.GetString(), MovieGenerationCostEstimateStates.Estimated, StringComparison.Ordinal)
                && root.TryGetProperty(nameof(MovieGenerationCostEstimate.MaximumAmountUsd), out var maximum)
                && maximum.ValueKind == JsonValueKind.Number
                && maximum.TryGetDecimal(out var persistedMaximumUsd)
                && persistedMaximumUsd == maximumUsd
                && root.TryGetProperty(nameof(MovieGenerationCostEstimate.Currency), out var currency)
                && currency.ValueKind == JsonValueKind.String
                && string.Equals(currency.GetString(), UsageCurrencies.Usd, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class GenerationJobValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class GenerationJobForbiddenException : Exception;
public sealed class GenerationNoBillableAssetException(AiUsageMetadata? usage) : Exception
{
    public AiUsageMetadata? Usage { get; } = usage;
}

public sealed class GenerationJobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<GenerationJobOptions> options,
    ILogger<GenerationJobWorker> logger) : BackgroundService
{
    private const string PoisonJobMessage = "The generation could not be recovered safely. Please try again.";
    private readonly GenerationJobOptions settings = options.Value;
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, Math.Max(1, settings.WorkerConcurrency)).Select(index => RunWorkerAsync(index, stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerIndex, CancellationToken stoppingToken)
    {
        var schedule = new GenerationJobPollingSchedule(settings);
        var workerId = $"{instanceId}:{workerIndex}";
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var notifications = scope.ServiceProvider.GetRequiredService<INotificationEventWriter>();
                var usage = scope.ServiceProvider.GetRequiredService<IGenerationJobUsageService>();
                var movieExecutions = scope.ServiceProvider.GetRequiredService<MovieVideoExecutionStore>();
                var now = DateTime.UtcNow;
                await TouchWorkerHeartbeatSafelyAsync(db, workerId, workerIndex, null, null, consecutiveFailures, now, stoppingToken);
                if (schedule.RecoveryDue(now))
                {
await RecoverExpiredClaimsAsync(db, usage, movieExecutions, notifications, scope.ServiceProvider, stoppingToken);
                    schedule.ScheduleNextRecovery(now);
                }
                var job = await ClaimAsync(db, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(schedule.IdleDelay, stoppingToken);
                    continue;
                }
                consecutiveFailures = 0;
                await TouchWorkerHeartbeatSafelyAsync(db, workerId, workerIndex, job.Id, now, consecutiveFailures, now, stoppingToken);
                using (GenerationJobOperationalScope.Begin(logger, job))
                {
                    logger.LogInformation(
                        "Generation job claimed. JobId={JobId}; WorkspaceId={WorkspaceId}; JobType={JobType}; RequestId={RequestId}; RetryCount={RetryCount}; ClaimExpiresAt={ClaimExpiresAt}",
                        job.Id, job.WorkspaceId, job.JobType, job.RequestId, job.RetryCount, job.ClaimExpiresAt);
                    await ExecuteJobAsync(job, workerId, workerIndex, stoppingToken);
                }
                await TouchWorkerHeartbeatSafelyAsync(db, workerId, workerIndex, null, now, consecutiveFailures, DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                consecutiveFailures = Math.Min(consecutiveFailures + 1, 1000);
                logger.LogError(exception, "Generation job worker iteration failed safely.");
                await Task.Delay(schedule.IdleDelay, stoppingToken);
            }
        }
    }

    private async Task RecoverExpiredClaimsAsync(
        TaslimDbContext db,
        IGenerationJobUsageService usage,
        MovieVideoExecutionStore movieExecutions,
        INotificationEventWriter notifications,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expiredClaims = await db.GenerationJobs.AsNoTracking()
            .Where(job => job.Status == GenerationJobStatus.Running && job.ClaimExpiresAt.HasValue && job.ClaimExpiresAt < now)
            .OrderBy(job => job.ClaimExpiresAt)
            .ThenBy(job => job.CreatedAt)
            .Select(job => new { job.Id, job.ConcurrencyToken })
            .ToListAsync(cancellationToken);

        foreach (var expired in expiredClaims)
        {
            var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == expired.Id, cancellationToken);
            if (job is null || job.Status != GenerationJobStatus.Running || job.ConcurrencyToken != expired.ConcurrencyToken
                || job.ClaimExpiresAt is null || job.ClaimExpiresAt >= now)
                continue;

            if (job.CancellationRequested)
            {
                // F-36.1: this is the branch the F-36 in-process fix could not reach, because nothing
                // survives here but the row - the dead worker's stage flags went with it. The
                // provider-attempt record written at acceptance time is the durable answer. With it, a
                // music job cancelled after the provider accepted the paid generation terminalises on
                // the unconfirmed code that RetryAsync already refuses; without it, a cancellation that
                // never reached a provider keeps its ordinary code and stays retryable.
                var evidence = await ReadMusicSubmissionEvidenceAsync(db, job, expired.ConcurrencyToken, cancellationToken);
                string cancellationCode;
                if (evidence.DispatchMarkerOnly)
                {
                    // F-36.1b: the paid request was handed to the transport but no acceptance was ever
                    // recorded, so a generation may exist at the provider with nothing on our side to
                    // reconcile against. That is still a closed outcome: a retry could double-charge.
                    cancellationCode = GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission;
                }
                else if (evidence.OperationId is not null && evidence.OperationId.Length > 0)
                {
                    var reconciliation = await ReconcileMusicSubmissionAsync(
                        services, db, job, expired.ConcurrencyToken, evidence.OperationId, evidence.Provider, cancellationToken);
                    cancellationCode = reconciliation.Outcome switch
                    {
                        // The paid generation exists at the provider and only delivery was lost.
                        MusicSubmissionOutcome.Completed => GenerationJobErrorCodes.MusicCancelledUndeliveredOutput,
                        // Only a vendor that documents "not charged on failure" may release the retry.
                        MusicSubmissionOutcome.ProviderDeclaredFailure when reconciliation.ProviderDeclaresNoCharge => CancellationCode(job),
                        _ => GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission,
                    };
                }
                else
                {
                    cancellationCode = CancellationCode(job);
                }
                var cancellationMessage = cancellationCode == CancellationCode(job)
                    ? "The job was cancelled."
                    : GenerationJobErrorMessages.For(cancellationCode) ?? "The job was cancelled.";
                var cancellationTerminalToken = Guid.NewGuid();
                await using var cancellationFinalization = await db.Database.BeginTransactionAsync(cancellationToken);
                await movieExecutions.MarkCancelledAsync(job.Id, expired.ConcurrencyToken, cancellationToken);
                var cancelled = await db.GenerationJobs
                    .Where(item => item.Id == job.Id
                        && item.Status == GenerationJobStatus.Running
                        && item.ConcurrencyToken == expired.ConcurrencyToken
                        && item.CancellationRequested
                        && item.ClaimExpiresAt.HasValue
                        && item.ClaimExpiresAt < now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, GenerationJobStatus.Cancelled)
                        .SetProperty(item => item.ErrorCode, cancellationCode)
                        .SetProperty(item => item.ErrorMessage, cancellationMessage)
                        .SetProperty(item => item.CancelledAt, now)
                        .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null)
                        .SetProperty(item => item.ConcurrencyToken, cancellationTerminalToken), cancellationToken);
                if (cancelled == 0)
                {
                    await cancellationFinalization.RollbackAsync(cancellationToken);
                    continue;
                }
                var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
                await usage.CancelAsync(transaction, cancellationCode, cancellationToken);
                await cancellationFinalization.CommitAsync(cancellationToken);
                logger.LogInformation("Expired cancelled generation lease finalized. JobId={JobId}; RetryReason={RetryReason}", job.Id, "cancellation_requested");
                continue;
            }

            // Post-billing duplicate-submission policy for music. The bounded re-queue below
            // "executes the handler from the beginning" (its own comment), which for music.generate is a
            // fresh billable POST to Stable Audio or Mubert: neither provider accepts an idempotency key
            // on creation and the provider operation id only exists after the call returns, so recovery
            // can neither replay nor re-fetch it. A worker that dies after submitting - crash loop, OOM
            // kill, or a rollout mid-generation - would therefore pay for up to MaxAutomaticRetries extra
            // generations behind a single customer request and orphan the first artifact, with nobody
            // visible in the loop. Music therefore takes the existing token-fenced terminal instead of
            // the re-queue, carrying a dedicated code that records the unknown submission outcome; the
            // terminal is deliberately shared rather than duplicated so that the claim-token fencing
            // contract (GenerationJobFinalizationSourceContractTests) keeps exactly one Running ->
            // Failed write here. Every other job type keeps the current bounded recovery unchanged.
            var automaticRedispatchAllowed = !string.Equals(job.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase);
            var maxAutomaticRetries = Math.Max(0, settings.MaxAutomaticRetries);
            if (automaticRedispatchAllowed && job.RetryCount < maxAutomaticRetries)
            {
                var recoveryToken = Guid.NewGuid();
                var recovered = await db.GenerationJobs
                    .Where(item => item.Id == job.Id
                        && item.Status == GenerationJobStatus.Running
                        && item.ConcurrencyToken == expired.ConcurrencyToken
                        && !item.CancellationRequested
                        && item.ClaimExpiresAt.HasValue
                        && item.ClaimExpiresAt < now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, GenerationJobStatus.Queued)
                        .SetProperty(item => item.QueuedAt, now)
                        .SetProperty(item => item.StartedAt, (DateTime?)null)
                        // The requeued attempt executes the handler from the
                        // beginning, so the dead attempt's progress must not
                        // survive the transition; otherwise the status surface
                        // keeps reporting a queued job at a stale, higher
                        // progress until the next worker's first report.
                        .SetProperty(item => item.ProgressPercent, 0)
                        .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null)
                        .SetProperty(item => item.RetryCount, item => item.RetryCount + 1)
                        .SetProperty(item => item.ConcurrencyToken, recoveryToken), cancellationToken);
                if (recovered == 0) continue;
                logger.LogWarning("Expired generation lease recovered. JobId={JobId}; RetryReason={RetryReason}; RetryCount={RetryCount}; MaxAutomaticRetries={MaxAutomaticRetries}",
                    job.Id, "lease_expired", job.RetryCount + 1, maxAutomaticRetries);
                await TryNotifyAsync(() => notifications.CreateGenerationAttentionAsync(job.Id, cancellationToken), job.Id, cancellationToken);
                continue;
            }

            if (!automaticRedispatchAllowed)
                logger.LogWarning("Music generation lease expired with the provider submission outcome unknown; automatic re-dispatch suppressed so no duplicate paid generation was created. JobId={JobId}; RetryCount={RetryCount}; MaxAutomaticRetries={MaxAutomaticRetries}",
                    job.Id, job.RetryCount, maxAutomaticRetries);

            // Music arrives here on its first expired lease because rebilling automatically is the
            // hazard; every other lane arrives only after exhausting its bounded retries.
            var terminalCode = automaticRedispatchAllowed
                ? GenerationJobErrorCodes.Poisoned
                : GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed;
            var terminalMessage = terminalCode == GenerationJobErrorCodes.Poisoned
                ? PoisonJobMessage
                : GenerationJobErrorMessages.For(terminalCode) ?? PoisonJobMessage;
            var poisonTerminalToken = Guid.NewGuid();
            await using var poisonFinalization = await db.Database.BeginTransactionAsync(cancellationToken);
            await movieExecutions.MarkFailedAsync(job.Id, expired.ConcurrencyToken, terminalCode, cancellationToken);
            var poisoned = await db.GenerationJobs
                .Where(item => item.Id == job.Id
                    && item.Status == GenerationJobStatus.Running
                    && item.ConcurrencyToken == expired.ConcurrencyToken
                    && !item.CancellationRequested
                    && item.ClaimExpiresAt.HasValue
                    && item.ClaimExpiresAt < now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, GenerationJobStatus.Failed)
                    .SetProperty(item => item.ErrorCode, terminalCode)
                    .SetProperty(item => item.ErrorMessage, terminalMessage)
                    .SetProperty(item => item.FailedAt, now)
                    .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null)
                    .SetProperty(item => item.ConcurrencyToken, poisonTerminalToken), cancellationToken);
            if (poisoned == 0)
            {
                await poisonFinalization.RollbackAsync(cancellationToken);
                continue;
            }
            var poisonUsage = await usage.BeginAsync(job, cancellationToken: cancellationToken);
            await usage.FailAsync(poisonUsage, terminalCode, cancellationToken: cancellationToken);
            await poisonFinalization.CommitAsync(cancellationToken);
            logger.LogError("Generation job moved to poison terminal state after bounded lease recovery. JobId={JobId}; RetryCount={RetryCount}; MaxAutomaticRetries={MaxAutomaticRetries}",
                job.Id, job.RetryCount, maxAutomaticRetries);
            await TryNotifyAsync(() => notifications.CreateGenerationFailedAsync(job.Id, cancellationToken), job.Id, cancellationToken);
        }
    }

    private async Task<GenerationJob?> ClaimAsync(TaslimDbContext db, CancellationToken cancellationToken)
    {
        var isSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (isSqlite)
        {
            var candidate = await db.GenerationJobs.AsNoTracking()
                .Where(item => item.Status == GenerationJobStatus.Queued)
                .OrderBy(item => item.QueuedAt)
                .ThenBy(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null) return null;
            var startedAt = DateTime.UtcNow;
            var claimExpiresAt = startedAt.AddMinutes(Math.Max(2, settings.ClaimLeaseMinutes));
            var concurrencyToken = Guid.NewGuid();
            var claimed = await db.GenerationJobs
                .Where(item => item.Id == candidate.Id && item.Status == GenerationJobStatus.Queued)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, GenerationJobStatus.Running)
                    .SetProperty(item => item.StartedAt, startedAt)
                    .SetProperty(item => item.ClaimExpiresAt, claimExpiresAt)
                    .SetProperty(item => item.ConcurrencyToken, concurrencyToken), cancellationToken);
            return claimed == 0 ? null : await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == candidate.Id, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.GenerationJobs.FromSqlInterpolated($"SELECT * FROM \"GenerationJobs\" WHERE \"Status\" = {GenerationJobStatus.Queued.ToString()} ORDER BY \"QueuedAt\", \"CreatedAt\" FOR UPDATE SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        job.Status = GenerationJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.ClaimExpiresAt = DateTime.UtcNow.AddMinutes(Math.Max(2, settings.ClaimLeaseMinutes));
        job.ConcurrencyToken = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    private static int? ReadDurationMilliseconds(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("durationMilliseconds", out var value) && value.TryGetInt32(out var duration) && duration > 0 ? duration : null;
        }
        catch (JsonException) { return null; }
    }

    private static int? ReadDurationSeconds(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("durationSeconds", out var value)
                && value.TryGetInt32(out var duration)
                && duration is > 0 and <= 3_600
                ? duration
                : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task ExecuteJobAsync(GenerationJob claimedJob, string workerId, int workerIndex, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationEventWriter>();
        var usage = scope.ServiceProvider.GetRequiredService<IGenerationJobUsageService>();
        var budget = scope.ServiceProvider.GetRequiredService<IGenerationBudgetService>();
        var publisher = scope.ServiceProvider.GetRequiredService<IGeneratedAssetPublisher>();
        var retentionHook = scope.ServiceProvider.GetRequiredService<IGeneratedMediaRetentionHook>();
        var movieExecutions = scope.ServiceProvider.GetRequiredService<MovieVideoExecutionStore>();
        var dialogueExecutions = scope.ServiceProvider.GetRequiredService<MovieDialogueVoiceExecutionStore>();
        var assemblyExecutions = scope.ServiceProvider.GetRequiredService<MovieFinalAssemblyExecutionStore>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorCancellationAndLeaseAsync(claimedJob.Id, claimedJob.ConcurrencyToken, workerId, workerIndex, cancellation, stoppingToken);
        var publications = new List<PreparedGenerationOutput>();
        var publicationCommitted = false;
        AiUsageMetadata? providerUsage = null;
        GenerationProviderAttempt? providerAttempt = null;
        var executionStarted = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "Generation job execution started. JobId={JobId}; WorkspaceId={WorkspaceId}; JobType={JobType}; RequestId={RequestId}; RetryCount={RetryCount}",
            claimedJob.Id, claimedJob.WorkspaceId, claimedJob.JobType, claimedJob.RequestId, claimedJob.RetryCount);
        try
        {
            // Defense-in-depth after queue claim: approved Movie jobs reach Queued only through
            // PublishPendingMovieProductionAsync after the durable-link check succeeds.
            var publicationMarker = GenerationJobPublicationContract.ReadMarker(claimedJob.InputJson);
            if (publicationMarker.HasMarker
                && (!publicationMarker.IsApprovedMovieProduction
                    || !string.Equals(claimedJob.JobType, GenerationJobTypes.MovieClipGenerate, StringComparison.OrdinalIgnoreCase)
                    || !await GenerationJobPublicationContract.HasDurableApprovedMovieLinksAsync(db, claimedJob, stoppingToken)))
            {
                await FailAsync(
                    db,
                    usage,
                    claimedJob,
                    GenerationJobPublicationContract.LinkageIncompleteErrorCode,
                    "The approved movie production links are not durably complete.",
                    null,
                    claimedJob.ConcurrencyToken,
                    stoppingToken);
                await TryNotifyAsync(() => notifications.CreateGenerationFailedAsync(claimedJob.Id, stoppingToken), claimedJob.Id, stoppingToken);
                return;
            }
            var handlers = scope.ServiceProvider.GetServices<IGenerationJobHandler>();
            var handler = handlers.FirstOrDefault(item => item.CanHandle(claimedJob.JobType));
            if (handler is null)
            {
                await FailAsync(db, usage, claimedJob, GenerationJobErrorCodes.TypeNotSupported, "This job type is not available.", null, claimedJob.ConcurrencyToken, stoppingToken);
                await TryNotifyAsync(() => notifications.CreateGenerationFailedAsync(claimedJob.Id, stoppingToken), claimedJob.Id, stoppingToken);
                return;
            }
            if (string.Equals(claimedJob.JobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase))
                await assemblyExecutions.MarkRunningAsync(claimedJob.Id, claimedJob.ConcurrencyToken, stoppingToken);
            else if (GenerationJobTypes.MovieTypes.Contains(claimedJob.JobType))
                await movieExecutions.MarkRunningAsync(claimedJob.Id, claimedJob.ConcurrencyToken, stoppingToken);
            if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(claimedJob.JobType))
                await dialogueExecutions.MarkRunningAsync(claimedJob.Id, claimedJob.ConcurrencyToken, stoppingToken);
            var estimate = claimedJob.EstimatedProviderCostKnown == true && claimedJob.EstimatedProviderCostUsd.HasValue
                ? new GenerationCostEstimate(true, claimedJob.EstimatedProviderCostUsd, UsageCurrencies.Usd, null, null, null, [])
                : GenerationCostEstimate.Unknown("job_estimate_missing");
            providerAttempt = await budget.BeginAttemptAsync(claimedJob, claimedJob.Provider ?? "selected", claimedJob.ProviderModel, estimate, stoppingToken);

            // F-36.1: the evidence sink belongs to the worker, not to the provider: the provider is a
            // singleton with no database, and the attempt row created above is the durable home for the
            // accepted provider operation id. Lease recovery reads that record to tell "cancelled before
            // anything was submitted" (safe to retry) apart from "cancelled while a paid generation is
            // live" (must refuse, or the customer pays twice).
            if (providerAttempt is not null
                && handler is IAcceptsMusicSubmissionEvidence evidenceReporter
                && string.Equals(claimedJob.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase))
            {
                var evidenceAttemptId = providerAttempt.Id;
                var evidenceClaimToken = providerAttempt.JobConcurrencyToken;
                evidenceReporter.AcceptedSubmissionSink = providerOperationId =>
                    StampAcceptedSubmissionAsync(db, evidenceAttemptId, evidenceClaimToken, providerOperationId, stoppingToken);
                // F-36.1b: the pre-dispatch marker shares the same durable home and the same claim
                // scope, so recovery can tell "never reached a provider" from "paid request sent".
                evidenceReporter.DispatchingSink = () =>
                    StampDispatchingSubmissionAsync(db, evidenceAttemptId, evidenceClaimToken, stoppingToken);
            }
            var progress = new SerializedProgress(value => UpdateProgressSafelyAsync(claimedJob.Id, claimedJob.ConcurrencyToken, value, stoppingToken));
            var result = await handler.ExecuteAsync(claimedJob, progress, cancellation.Token);
            await progress.DrainAsync();
            if ((GenerationJobTypes.MovieTypes.Contains(claimedJob.JobType) || GenerationJobTypes.MovieDialogueVoiceTypes.Contains(claimedJob.JobType))
                && !result.Outputs.Any(output => output.Asset is not null
                    && (output.StoredFileId.HasValue || output.FileArtifact is not null || output.StreamArtifact is not null)))
                throw new MovieVideoProviderOutputException();
            providerUsage = result.Usage;
            var current = await db.GenerationJobs.FirstOrDefaultAsync(item => item.Id == claimedJob.Id, stoppingToken);
            if (current is null || current.Status != GenerationJobStatus.Running || current.ConcurrencyToken != claimedJob.ConcurrencyToken || current.CancellationRequested || cancellation.IsCancellationRequested)
            {
                // A cancellation observed at this fence means the handler finished late while
                // representation publication was still pending. The provider attempt must
                // reach the same terminal state as the OperationCanceledException path, so
                // telemetry never keeps a Started attempt for an already-Cancelled job.
                if (providerAttempt is not null)
                    await budget.CompleteAttemptAsync(providerAttempt, null, false, GenerationProviderAttemptStatus.Cancelled, GenerationJobErrorCodes.Cancelled, CancellationToken.None);
                // F-36.1: for music this is the accepted-and-finished state, not an ordinary
                // cancellation: the paid generation exists at the provider and only its delivery was
                // lost, so the retry refusal must follow the outcome rather than the cancel verb.
                var (acceptedFenceCode, acceptedFenceMessage) = AcceptedOutputCancellationContract(claimedJob);
                await CancelRunningAsync(db, usage, claimedJob, claimedJob.ConcurrencyToken, stoppingToken, acceptedFenceCode, acceptedFenceMessage);
                return;
            }
            foreach (var output in result.Outputs)
            {
                try
                {
                    publications.Add(await publisher.PrepareAsync(current, output, stoppingToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ImageOutputStorageException(exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase))
                {
                    var stage = output.FileArtifact?.RepresentationType?.Equals(AssetRepresentationTypes.Pdf, StringComparison.OrdinalIgnoreCase) == true
                        ? DocumentGenerationStages.StoragePdf
                        : DocumentGenerationStages.StorageDocx;
                    throw new DocumentGenerationStageException(stage, DocumentGenerationFailureCodes.ForStage(stage), "The generated document could not be stored.", providerUsage, exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PresentationGenerationStageException(PresentationGenerationStages.StoragePptx, GenerationJobErrorCodes.PresentationStorageFailed, "The generated presentation could not be stored.", providerUsage, exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ResearchGenerationStageException(ResearchGenerationStages.StorageDocx, GenerationJobErrorCodes.ResearchStorageFailed, "The generated research report could not be stored.", providerUsage, exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SocialGenerationStageException(SocialGenerationStages.Storage, GenerationJobErrorCodes.SocialStorageFailed, "The generated social content could not be stored.", providerUsage, exception);
                }
            }
            AttachAssetRepresentations(publications);
            if (!publications.Any(item => item.Asset is not null))
                throw new GenerationNoBillableAssetException(providerUsage);
            var resultJson = AddPublishedAssetReference(result.ResultJson, publications);
            if (result.Usage is not null)
            {
                current.Provider = result.Usage.ProviderKey;
                current.ProviderModel = result.Usage.ModelKey;
            }

            int completed;
            try
            {
                await using (var completionTransaction = await db.Database.BeginTransactionAsync(stoppingToken))
                {
                    var completedAt = DateTime.UtcNow;
                    completed = await db.GenerationJobs
                        .Where(item => item.Id == current.Id && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == claimedJob.ConcurrencyToken && !item.CancellationRequested)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(item => item.Status, GenerationJobStatus.Succeeded)
                            .SetProperty(item => item.ProgressPercent, 100)
                            .SetProperty(item => item.ResultJson, resultJson)
                            .SetProperty(item => item.Provider, result.Usage == null ? null : result.Usage.ProviderKey)
                            .SetProperty(item => item.ProviderModel, result.Usage == null ? null : result.Usage.ModelKey)
                        .SetProperty(item => item.CompletedAt, completedAt)
                        .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null), stoppingToken);
                    if (completed == 0)
                    {
                        await completionTransaction.RollbackAsync(stoppingToken);
                    }
                    else
                    {
                        db.Entry(current).State = EntityState.Detached;
                        foreach (var publication in publications)
                        {
                            db.GenerationJobOutputs.Add(publication.Output);
                            if (publication.Asset is not null) db.Assets.Add(publication.Asset);
                            if (publication.Provenance is not null) db.GeneratedMediaProvenance.Add(publication.Provenance);
                        }
                        // Movie clip and final-assembly projections must not lag the
                        // canonical job state. Persist the generated asset first, then
                        // update the movie sidecar in this same transaction so readers
                        // can never observe Succeeded with a missing private asset link.
                        if (GenerationJobTypes.MovieTypes.Contains(claimedJob.JobType))
                        {
                            await db.SaveChangesAsync(stoppingToken);
                            var publication = publications.FirstOrDefault(item => item.Asset is not null);
                            if (string.Equals(claimedJob.JobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase))
                            {
                                if (publication?.Asset is not null)
                                    await assemblyExecutions.MarkReadyAsync(current.Id, claimedJob.ConcurrencyToken, publication.Asset.Id, publication.Output.MetadataJson, JsonSerializer.Serialize(new { contract = MovieFinalAssemblyQualityControl.ContractVersion, status = MovieFinalAssemblyQcStatuses.Passed }), stoppingToken);
                            }
                            else
                            {
                                await movieExecutions.MarkReadyAsync(current.Id, claimedJob.ConcurrencyToken, publication?.Asset?.Id, publication?.CreatedFile?.Id, ReadDurationSeconds(publication?.Output.MetadataJson), publication?.Output.MetadataJson, stoppingToken);
                            }
                        }
                        await usage.CompleteAsync(await usage.BeginAsync(current, cancellationToken: stoppingToken), result.Usage ?? new AiUsageMetadata("system", "unknown", null, null, null, 0m, 0m, 0, "completed", true), hasBillableAsset: true, cancellationToken: stoppingToken);
                        await completionTransaction.CommitAsync(stoppingToken);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase))
            {
                throw new DocumentGenerationStageException(DocumentGenerationStages.AssetPublish, GenerationJobErrorCodes.DocumentStorageFailed, "The generated document could not be published.", providerUsage, exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase))
            {
                throw new PresentationGenerationStageException(PresentationGenerationStages.AssetPublish, GenerationJobErrorCodes.PresentationStorageFailed, "The generated presentation could not be published.", providerUsage, exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase))
            {
                throw new ResearchGenerationStageException(ResearchGenerationStages.AssetPublish, GenerationJobErrorCodes.ResearchStorageFailed, "The generated research report could not be published.", providerUsage, exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && string.Equals(claimedJob.JobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase))
            {
                throw new SocialGenerationStageException(SocialGenerationStages.AssetPublish, GenerationJobErrorCodes.SocialStorageFailed, "The generated social content could not be published.", providerUsage, exception);
            }
            if (completed == 0)
            {
                foreach (var publication in publications) await publisher.DiscardAsync(publication, stoppingToken);
                // The fenced completion lost to a cancellation committed during publication.
                // Prepared representations are discarded above and never become readable
                // assets; the provider attempt is finalized with the same guard as the
                // OperationCanceledException path so its terminal state matches the job.
                if (providerAttempt is not null)
                    await budget.CompleteAttemptAsync(providerAttempt, null, false, GenerationProviderAttemptStatus.Cancelled, GenerationJobErrorCodes.Cancelled, CancellationToken.None);
                // F-36.1: for music this is the accepted-and-finished state, not an ordinary
                // cancellation: the paid generation exists at the provider and only its delivery was
                // lost, so the retry refusal must follow the outcome rather than the cancel verb.
                var (acceptedFenceCode, acceptedFenceMessage) = AcceptedOutputCancellationContract(claimedJob);
                await CancelRunningAsync(db, usage, claimedJob, claimedJob.ConcurrencyToken, stoppingToken, acceptedFenceCode, acceptedFenceMessage);
                return;
            }
            publicationCommitted = true;
            foreach (var publication in publications.Where(item => item.Provenance is not null))
            {
                try
                {
                    var provenance = publication.Provenance!;
                    await retentionHook.OnStoredAsync(new GeneratedMediaRetentionContext(
                        provenance.WorkspaceId,
                        provenance.StoredFileId,
                        provenance.GenerationJobId,
                        provenance.AssetId,
                        provenance.MovieTakeId,
                        provenance.RetainUntil,
                        "generation_succeeded"), stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Generated media retention registration was skipped safely. JobId={JobId}", claimedJob.Id);
                }
            }
            if (providerAttempt is not null)
                await budget.CompleteAttemptAsync(providerAttempt, result.Usage?.ActualCost, result.Usage?.ActualCost.HasValue == true, GenerationProviderAttemptStatus.Succeeded, cancellationToken: stoppingToken);
            logger.LogInformation(
                "Generation job execution completed. JobId={JobId}; JobType={JobType}; RequestId={RequestId}; ProviderKey={ProviderKey}; ProviderModel={ProviderModel}; ElapsedMs={ElapsedMs}; UsageFinalized={UsageFinalized}",
                claimedJob.Id, claimedJob.JobType, claimedJob.RequestId, result.Usage?.ProviderKey, result.Usage?.ModelKey,
                (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds, true);
            await TryNotifyAsync(() => notifications.CreateGenerationCompletedAsync(claimedJob.Id, stoppingToken), claimedJob.Id, stoppingToken);
            if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(claimedJob.JobType))
            {
                var publication = publications.FirstOrDefault(item => item.Asset is not null);
                await dialogueExecutions.MarkReadyAsync(current.Id, claimedJob.ConcurrencyToken, publication?.Asset?.Id, publication?.CreatedFile?.Id, ReadDurationMilliseconds(publication?.Output.MetadataJson), publication?.Output.MetadataJson, result.Usage?.SafeMetadataJson, stoppingToken);
            }
            if (GenerationJobTypes.MovieSoundTypes.Contains(claimedJob.JobType))
            {
                var publication = publications.FirstOrDefault(item => item.Asset is not null);
                await db.MovieSoundTracks.Where(track => track.GenerationJobId == claimedJob.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(track => track.Status, MovieSoundStatuses.ReadyForReview)
                        .SetProperty(track => track.AssetId, publication == null ? (Guid?)null : publication.Asset!.Id)
                        .SetProperty(track => track.UpdatedAt, DateTime.UtcNow), stoppingToken);
            }
        }
        catch (MovieVideoStaleWorkerException)
        {
            if (!publicationCommitted)
                foreach (var publication in publications) await publisher.DiscardAsync(publication, CancellationToken.None);
            logger.LogWarning("Stale movie worker stopped without mutating the recovered execution. JobId={JobId}; RequestId={RequestId}", claimedJob.Id, claimedJob.RequestId);
        }
        catch (MovieVideoProviderCancelledException)
        {
            if (!publicationCommitted)
                foreach (var publication in publications) await publisher.DiscardAsync(publication, CancellationToken.None);
            if (GenerationJobTypes.MovieTypes.Contains(claimedJob.JobType))
            {
                if (string.Equals(claimedJob.JobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase))
                    await assemblyExecutions.MarkCancelledAsync(claimedJob.Id, claimedJob.ConcurrencyToken, CancellationToken.None);
                else
                    await movieExecutions.MarkCancelledAsync(claimedJob.Id, claimedJob.ConcurrencyToken, CancellationToken.None);
            }
            if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(claimedJob.JobType))
                await dialogueExecutions.MarkCancelledAsync(claimedJob.Id, CancellationToken.None);
            await CancelRunningAsync(db, usage, claimedJob, claimedJob.ConcurrencyToken, stoppingToken);
        }
        catch (OperationCanceledException cancellationException) when (cancellation.IsCancellationRequested)
        {
            if (!publicationCommitted)
                foreach (var publication in publications) await publisher.DiscardAsync(publication, CancellationToken.None);
            if (GenerationJobTypes.MovieTypes.Contains(claimedJob.JobType))
            {
                if (string.Equals(claimedJob.JobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase))
                    await assemblyExecutions.MarkCancelledAsync(claimedJob.Id, claimedJob.ConcurrencyToken, CancellationToken.None);
                else
                    await movieExecutions.MarkCancelledAsync(claimedJob.Id, claimedJob.ConcurrencyToken, CancellationToken.None);
            }
            if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(claimedJob.JobType))
                await dialogueExecutions.MarkCancelledAsync(claimedJob.Id, CancellationToken.None);
            if (providerAttempt is not null)
                await budget.CompleteAttemptAsync(providerAttempt, null, false, GenerationProviderAttemptStatus.Cancelled, GenerationJobErrorCodes.Cancelled, CancellationToken.None);
            // F-36: WHICH cancellation was it? A music cancellation raised after the provider accepted
            // the paid submission carries a distinct terminal code, because nothing cancels the accepted
            // operation at the provider: the attempt may still finish and may still bill, so the retry
            // endpoint must refuse it instead of treating it like an ordinary cancellation. Every other
            // cancellation - including a music cancellation before acceptance - keeps its existing
            // per-type code and stays retryable, which MusicRetryPostureIsolationTests pins both ways.
            // The provider-attempt telemetry above deliberately keeps the generic Cancelled status: it
            // records how the attempt ended for operators, not the customer-facing outcome.
            var (cancelCode, cancelMessage) = cancellationException switch
            {
                MusicProviderCancellationWithAcceptedSubmissionException { OutputConfirmedComplete: true } => (GenerationJobErrorCodes.MusicCancelledUndeliveredOutput, "The music generation was cancelled before the finished track could be delivered."),
                MusicProviderCancellationWithAcceptedSubmissionException { OutputConfirmedComplete: false } => (GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission, "The music generation was cancelled after the provider accepted it, so its outcome has not been reconciled."),
                _ => (CancellationCode(claimedJob), "The job was cancelled."),
            };
            // Host shutdown cancels stoppingToken before this handler exits. Durable
            // cancellation must still commit so the job does not remain Running until
            // lease recovery on the next process.
            await CancelRunningAsync(db, usage, claimedJob, claimedJob.ConcurrencyToken, CancellationToken.None, cancelCode, cancelMessage);
            logger.LogInformation(
                "Generation job execution cancelled. JobId={JobId}; JobType={JobType}; RequestId={RequestId}; ElapsedMs={ElapsedMs}; UsageFinalized={UsageFinalized}",
                claimedJob.Id, claimedJob.JobType, claimedJob.RequestId, (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds, true);
        }
        catch (Exception exception)
        {
            if (!publicationCommitted)
                foreach (var publication in publications) await publisher.DiscardAsync(publication, CancellationToken.None);
            var failureCode = MapFailureCode(exception, claimedJob.JobType);
            var failureClassification = GenerationFailureClassifier.Classify(failureCode);
            if (GenerationJobTypes.MovieTypes.Contains(claimedJob.JobType))
            {
                if (string.Equals(claimedJob.JobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase))
                    await assemblyExecutions.MarkFailedAsync(claimedJob.Id, claimedJob.ConcurrencyToken, failureCode, CancellationToken.None);
                else
                    await movieExecutions.MarkFailedAsync(claimedJob.Id, claimedJob.ConcurrencyToken, failureCode, CancellationToken.None);
            }
            if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(claimedJob.JobType))
                await dialogueExecutions.MarkFailedAsync(claimedJob.Id, failureCode, CancellationToken.None);
            var failureUsage = providerUsage ?? (exception as DocumentGenerationStageException)?.Usage;
            failureUsage ??= (exception as PresentationGenerationStageException)?.Usage;
            failureUsage ??= (exception as ResearchGenerationStageException)?.Usage;
            failureUsage ??= (exception as SocialGenerationStageException)?.Usage;
var qualityFailure = exception as GenerationQualityControlException ?? exception.InnerException as GenerationQualityControlException;
            if (qualityFailure is not null)
            {
                var qualityMetadata = JsonSerializer.Serialize(new
                {
                    qualityControl = new
                    {
                        outcome = qualityFailure.Classification.ToString(),
                        reasonCode = qualityFailure.ReasonCode,
                        category = qualityFailure.Result.Findings.FirstOrDefault()?.Category,
                    },
                });
                failureUsage = (failureUsage ?? new AiUsageMetadata("system", "unknown", null, null, null, 0m, 0m, 0, "quality_failed", true)) with
                {
                    SafeMetadataJson = qualityMetadata,
                };
            }
            if (providerAttempt is not null)
                await budget.CompleteAttemptAsync(providerAttempt, failureUsage?.ActualCost, failureUsage?.ActualCost.HasValue == true, GenerationProviderAttemptStatus.Failed, failureCode, CancellationToken.None);
            if (string.Equals(claimedJob.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase))
            {
                var stage = (exception as DocumentGenerationStageException)?.Stage ?? DocumentGenerationStages.Execution;
                var providerException = exception as AiProviderException ?? exception.InnerException as AiProviderException;
                logger.LogError("Document generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; FailureCategory={FailureCategory}; Retryable={Retryable}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
                    failureClassification.Category,
                    failureClassification.Retryable,
                    exception.GetType().Name,
                    providerException?.FailureCategory,
                    providerException?.HttpStatusCode,
                    providerException?.ProviderErrorCode,
                    providerException?.ModelKey,
                    providerException?.StructuredOutputRequested,
                    providerException?.StreamingRequested,
                    (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds);
            }
            else if (string.Equals(claimedJob.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase))
            {
                var stage = (exception as PresentationGenerationStageException)?.Stage ?? PresentationGenerationStages.Execution;
                var providerException = exception as AiProviderException ?? exception.InnerException as AiProviderException;
                logger.LogError("Presentation generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; FailureCategory={FailureCategory}; Retryable={Retryable}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
                    failureClassification.Category,
                    failureClassification.Retryable,
                    exception.GetType().Name,
                    providerException?.FailureCategory,
                    providerException?.HttpStatusCode,
                    providerException?.ProviderErrorCode,
                    providerException?.ModelKey,
                    providerException?.StructuredOutputRequested,
                    providerException?.StreamingRequested,
                    (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds);
            }
            else if (string.Equals(claimedJob.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase))
            {
                var stage = (exception as ResearchGenerationStageException)?.Stage ?? ResearchGenerationStages.Report;
                var providerException = exception as AiProviderException ?? exception.InnerException as AiProviderException;
                var searchDetails = exception.InnerException switch
                {
                    ResearchSearchFailedException failed => failed.Details,
                    ResearchSearchUnavailableException unavailable => unavailable.Details,
                    _ => null,
                };
                logger.LogError("Research generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; FailureCategory={FailureCategory}; Retryable={Retryable}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; SearchHttpStatus={SearchHttpStatus}; SearchErrorType={SearchErrorType}; SearchErrorCode={SearchErrorCode}; SearchErrorParam={SearchErrorParam}; ModelKey={ModelKey}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
                    failureClassification.Category,
                    failureClassification.Retryable,
                    exception.GetType().Name,
                    providerException?.FailureCategory,
                    providerException?.HttpStatusCode,
                    providerException?.ProviderErrorCode,
                    searchDetails?.HttpStatusCode,
                    searchDetails?.ErrorType,
                    searchDetails?.ErrorCode,
                    searchDetails?.ErrorParam,
                    providerException?.ModelKey,
                    (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds);
            }
            else if (string.Equals(claimedJob.JobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase))
            {
                var stage = (exception as SocialGenerationStageException)?.Stage ?? SocialGenerationStages.Execution;
                var providerException = exception as AiProviderException ?? exception.InnerException as AiProviderException;
                logger.LogError("Social generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; FailureCategory={FailureCategory}; Retryable={Retryable}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
                    failureClassification.Category,
                    failureClassification.Retryable,
                    exception.GetType().Name,
                    providerException?.FailureCategory,
                    providerException?.HttpStatusCode,
                    providerException?.ProviderErrorCode,
                    providerException?.ModelKey,
                    providerException?.StructuredOutputRequested,
                    (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds);
            }
            else
            {
                logger.LogError("Generation job execution failed. JobId={JobId}; WorkspaceId={WorkspaceId}; JobType={JobType}; RequestId={RequestId}; FailureCode={FailureCode}; FailureCategory={FailureCategory}; Retryable={Retryable}; ExceptionType={ExceptionType}; ElapsedMs={ElapsedMs}; UsageFinalized={UsageFinalized}",
                    claimedJob.Id, claimedJob.WorkspaceId, claimedJob.JobType, claimedJob.RequestId, failureCode, failureClassification.Category, failureClassification.Retryable, exception.GetType().Name,
                    (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds, true);
            }
            await FailAsync(db, usage, claimedJob, failureCode, FailureMessage(failureCode), failureUsage, claimedJob.ConcurrencyToken, stoppingToken);
            logger.LogInformation(
                "Generation job failure finalized. JobId={JobId}; JobType={JobType}; RequestId={RequestId}; FailureCode={FailureCode}; UsageFinalized={UsageFinalized}",
                claimedJob.Id, claimedJob.JobType, claimedJob.RequestId, failureCode, true);
            await TryNotifyAsync(() => notifications.CreateGenerationFailedAsync(claimedJob.Id, stoppingToken), claimedJob.Id, stoppingToken);
        }
        finally
        {
            cancellation.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
    }

    private async Task MonitorCancellationAndLeaseAsync(Guid jobId, Guid concurrencyToken, string workerId, int workerIndex, CancellationTokenSource cancellation, CancellationToken stoppingToken)
    {
        var nextRenewalAt = DateTime.UtcNow;
        var nextHeartbeatAt = DateTime.UtcNow;
        try
        {
            while (!stoppingToken.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var now = DateTime.UtcNow;
                var current = await db.GenerationJobs.AsNoTracking()
                    .Where(job => job.Id == jobId)
                    .Select(job => new { job.Status, job.ConcurrencyToken, job.CancellationRequested, job.RequestId })
                    .FirstOrDefaultAsync(stoppingToken);
                if (current is null || current.Status != GenerationJobStatus.Running || current.ConcurrencyToken != concurrencyToken || current.CancellationRequested)
                {
                    logger.LogWarning(
                        "Generation job lease or cancellation state changed during execution. JobId={JobId}; RequestId={RequestId}; Status={Status}; CancellationRequested={CancellationRequested}",
                        jobId, current?.RequestId, current?.Status, current?.CancellationRequested);
                    cancellation.Cancel();
                    return;
                }

                if (now >= nextHeartbeatAt)
                {
                    await TouchWorkerHeartbeatSafelyAsync(db, workerId, workerIndex, jobId, null, 0, now, stoppingToken);
                    nextHeartbeatAt = now.AddSeconds(Math.Clamp(settings.HeartbeatStaleAfterSeconds / 2, 15, 60));
                }

                if (now >= nextRenewalAt)
                {
                    var claimExpiresAt = now.AddMinutes(Math.Max(2, settings.ClaimLeaseMinutes));
                    var renewed = await db.GenerationJobs
                        .Where(job => job.Id == jobId && job.Status == GenerationJobStatus.Running && job.ConcurrencyToken == concurrencyToken)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.ClaimExpiresAt, claimExpiresAt), stoppingToken);
                    if (renewed == 0)
                    {
                        logger.LogWarning("Generation job lease renewal lost. JobId={JobId}; RequestId={RequestId}", jobId, current.RequestId);
                        cancellation.Cancel();
                        return;
                    }
                    logger.LogDebug("Generation job heartbeat renewed. JobId={JobId}; RequestId={RequestId}; ClaimExpiresAt={ClaimExpiresAt}", jobId, current.RequestId, claimExpiresAt);
                    nextRenewalAt = now.AddMilliseconds(Math.Max(1000, settings.LeaseRenewalIntervalMilliseconds));
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(10, settings.CancellationPollMilliseconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Generation job heartbeat monitor stopped safely. JobId={JobId}", jobId);
            cancellation.Cancel();
        }
    }

    private async Task TouchWorkerHeartbeatSafelyAsync(
        TaslimDbContext db,
        string workerId,
        int workerIndex,
        Guid? activeJobId,
        DateTime? claimedAt,
        int consecutiveFailures,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            await TouchWorkerHeartbeatAsync(db, workerId, workerIndex, activeJobId, claimedAt, consecutiveFailures, now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Generation worker heartbeat was skipped safely. WorkerId={WorkerId}", workerId);
        }
    }

    private async Task TouchWorkerHeartbeatAsync(
        TaslimDbContext db,
        string workerId,
        int workerIndex,
        Guid? activeJobId,
        DateTime? claimedAt,
        int consecutiveFailures,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var heartbeat = await db.GenerationWorkerHeartbeats.SingleOrDefaultAsync(item => item.WorkerId == workerId, cancellationToken);
        if (heartbeat is null)
        {
            heartbeat = new GenerationWorkerHeartbeat
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                InstanceId = instanceId,
                WorkerIndex = workerIndex,
                WorkerConcurrency = Math.Max(1, settings.WorkerConcurrency),
                Status = GenerationWorkerStatuses.Running,
                StartedAt = now,
            };
            db.GenerationWorkerHeartbeats.Add(heartbeat);
        }
        heartbeat.Status = GenerationWorkerStatuses.Running;
        heartbeat.LastSeenAt = now;
        heartbeat.ActiveJobId = activeJobId;
        heartbeat.ConsecutiveIterationFailures = consecutiveFailures;
        if (claimedAt.HasValue) heartbeat.LastClaimedAt = claimedAt;
        if (!activeJobId.HasValue) heartbeat.LastCompletedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> UpdateProgressAsync(Guid jobId, Guid concurrencyToken, int progress, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.GenerationJobs.Where(job => job.Id == jobId && job.Status == GenerationJobStatus.Running && job.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.ProgressPercent, Math.Clamp(progress, 0, 100)), cancellationToken);
    }

    private async Task UpdateProgressSafelyAsync(Guid jobId, Guid concurrencyToken, int progress, CancellationToken cancellationToken)
    {
        try
        {
            await UpdateProgressAsync(jobId, concurrencyToken, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Generation job progress update was skipped. JobId={JobId}; ExceptionType={ExceptionType}", jobId, exception.GetType().Name);
        }
    }

    private async Task TryNotifyAsync(Func<Task> action, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            if (await NotificationDeliveryRetry.TryExecuteAsync(action, cancellationToken: cancellationToken)) return;
            logger.LogWarning("Generation notification delivery failed after bounded retries. JobId={JobId}; MaxAttempts={MaxAttempts}",
                jobId, NotificationDeliveryRetry.DefaultMaxAttempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Generation notification delivery was skipped safely after retry handling. JobId={JobId}", jobId);
        }
    }

    private sealed class SerializedProgress(Func<int, Task> writer) : IProgress<int>
    {
        private readonly object gate = new();
        private Task pending = Task.CompletedTask;

        public void Report(int value)
        {
            lock (gate)
            {
                pending = pending.ContinueWith(_ => writer(value), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            }
        }

        public Task DrainAsync()
        {
            lock (gate) return pending;
        }
    }

    private static string AddPublishedAssetReference(string resultJson, IReadOnlyList<PreparedGenerationOutput> publications)
    {
        var asset = publications.Select(item => item.Asset).FirstOrDefault(item => item is not null);
        if (asset is null) return resultJson;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return resultJson;
            var values = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
            values["assetId"] = asset.Id;
            values["representations"] = asset.Representations.OrderBy(item => item.RepresentationType).Select(item => new
            {
                id = item.Id,
                type = item.RepresentationType,
                fileName = item.FileName,
                contentType = item.ContentType,
            }).ToArray();
            return JsonSerializer.Serialize(values);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { assetId = asset.Id });
        }
    }

    private static void AttachAssetRepresentations(IReadOnlyList<PreparedGenerationOutput> publications)
    {
        var asset = publications.Select(item => item.Asset).FirstOrDefault(item => item is not null);
        if (asset is null) return;
        foreach (var publication in publications)
        {
            var file = publication.CreatedFile;
            if (file is null) continue;
            var representationType = file.Extension.TrimStart('.').ToLowerInvariant();
            if (representationType is not (AssetRepresentationTypes.Docx or AssetRepresentationTypes.Pdf or AssetRepresentationTypes.Pptx)) continue;
            asset.Representations.Add(new AssetRepresentation
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                StoredFileId = file.Id,
                RepresentationType = representationType,
                FileName = file.OriginalFileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
                CreatedAt = DateTime.UtcNow,
            });
        }
    }

    private static async Task CancelRunningAsync(TaslimDbContext db, IGenerationJobUsageService usage, GenerationJob job, Guid concurrencyToken, CancellationToken cancellationToken, string? terminalCode = null, string? terminalMessage = null)
    {
        var now = DateTime.UtcNow;
        await using var finalization = await db.Database.BeginTransactionAsync(cancellationToken);
        // terminalCode is only supplied by the caller that KNOWS more than "the member cancelled" -
        // today that is the accepted-but-unresolved music cancellation (F-36). Everything else keeps the
        // existing per-type cancellation code and its retryability.
        var cancelled = await db.GenerationJobs.Where(item => item.Id == job.Id && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Cancelled)
                .SetProperty(item => item.ErrorCode, terminalCode ?? CancellationCode(job))
                .SetProperty(item => item.ErrorMessage, terminalMessage ?? "The job was cancelled.")
                .SetProperty(item => item.CancelledAt, now)
                .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null), cancellationToken);
        if (cancelled == 0)
        {
            await finalization.RollbackAsync(cancellationToken);
            return;
        }
        if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(job.JobType))
        {
            await db.MovieDialogueTakes.Where(take => take.GenerationJobId == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(take => take.Status, MovieDialogueTakeStatuses.Cancelled).SetProperty(take => take.UpdatedAt, now), cancellationToken);
            await db.MovieDialogueLines.Where(line => line.Takes.Any(take => take.GenerationJobId == job.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(line => line.Status, MovieDialogueLineStatuses.Draft).SetProperty(line => line.UpdatedAt, now), cancellationToken);
        }
        if (GenerationJobTypes.MovieSoundTypes.Contains(job.JobType))
            await db.MovieSoundTracks.Where(track => track.GenerationJobId == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(track => track.Status, MovieSoundStatuses.Cancelled).SetProperty(track => track.UpdatedAt, now), cancellationToken);
        var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
        // The usage ledger records the same code the job row carries, so a reconciliation pass sees the
        // unknown submission outcome in both places rather than only in the job.
        await usage.CancelAsync(transaction, terminalCode ?? CancellationCode(job), cancellationToken);
        await finalization.CommitAsync(cancellationToken);
    }

    private static async Task FailAsync(TaslimDbContext db, IGenerationJobUsageService usage, GenerationJob job, string code, string message, AiUsageMetadata? providerUsage, Guid concurrencyToken, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var finalization = await db.Database.BeginTransactionAsync(cancellationToken);
        var failed = await db.GenerationJobs.Where(item => item.Id == job.Id && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Failed)
                .SetProperty(item => item.ErrorCode, code)
                .SetProperty(item => item.ErrorMessage, message)
                .SetProperty(item => item.FailedAt, now)
                .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null), cancellationToken);
        if (failed == 0)
        {
            await finalization.RollbackAsync(cancellationToken);
            return;
        }
        if (GenerationJobTypes.MovieSoundTypes.Contains(job.JobType))
            await db.MovieSoundTracks.Where(track => track.GenerationJobId == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(track => track.Status, MovieSoundStatuses.Failed).SetProperty(track => track.UpdatedAt, now), cancellationToken);
        var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
        await usage.FailAsync(transaction, code, providerUsage, cancellationToken);
        await finalization.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// F-36.1: the cancellation terminal an accepted-and-completed music generation must carry when
    /// the worker lost the delivery race rather than the generation itself. Every other job type keeps
    /// its existing per-type code and its existing retry behaviour, because for them a cancellation is
    /// evidence that nothing billable is in flight.
    /// <summary>
    /// F-36.1b: sentinel recorded on the attempt row between "the paid request is about to leave this
    /// process" and "the provider answered with an operation id". Prefixed so it can never collide
    /// with a vendor id, and refused by both providers' reconciliation paths.
    /// </summary>
    private const string MusicDispatchMarker = "taslim:dispatch-pending";

    /// </summary>
    private static (string Code, string Message) AcceptedOutputCancellationContract(GenerationJob job) =>
        string.Equals(job.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase)
            ? (GenerationJobErrorCodes.MusicCancelledUndeliveredOutput,
               GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicCancelledUndeliveredOutput) ?? "The job was cancelled.")
            : (CancellationCode(job), "The job was cancelled.");

    /// <summary>
    /// F-36.1: durable acceptance evidence for the claim generation that died. Keyed on the job's
    /// concurrency token, which the worker rotates at every claim, so evidence from a superseded
    /// attempt can never hold this recovery's cancellation closed.
    /// <summary>
    /// F-36.1b: the durable submission state of the claim generation that died, read from the
    /// provider-attempt row. Three states, not two: nothing stamped (a cancellation that never
    /// reached a provider, still retryable), a dispatch marker with no operation id (a paid request
    /// may have left this process and cannot be reconciled, closed), and an operation id (closed,
    /// but reconcilable against the provider's own status endpoint). Keyed on the job's concurrency
    /// token, which the worker rotates at every claim, so evidence from a superseded attempt can
    /// never hold this recovery's cancellation closed.
    /// </summary>
    private static async Task<(bool DispatchMarkerOnly, string? OperationId, string? Provider)> ReadMusicSubmissionEvidenceAsync(
        TaslimDbContext db,
        GenerationJob job,
        Guid claimConcurrencyToken,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(job.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase))
            return (false, null, null);
        var stamped = await db.GenerationProviderAttempts.AsNoTracking()
            .Where(item => item.GenerationJobId == job.Id
                && item.JobConcurrencyToken == claimConcurrencyToken
                && item.ProviderExecutionId != null)
            .Select(item => new { item.ProviderExecutionId, item.Provider })
            .FirstOrDefaultAsync(cancellationToken);
        if (stamped is null || string.IsNullOrWhiteSpace(stamped.ProviderExecutionId))
            return (false, null, null);
        return string.Equals(stamped.ProviderExecutionId, MusicDispatchMarker, StringComparison.Ordinal)
            ? (true, null, stamped.Provider)
            : (false, stamped.ProviderExecutionId, stamped.Provider);
    }

    private static IMusicGenerationProvider? MatchMusicProvider(
        IReadOnlyList<IMusicGenerationProvider> candidates,
        string? providerKey)
        => string.IsNullOrWhiteSpace(providerKey)
            ? null
            : candidates.FirstOrDefault(item => string.Equals(item.Key, providerKey.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// F-36.1b: asks the music provider what happened to an accepted submission and records the
    /// classification on the attempt row. Reconciliation may only ever make the terminal state MORE
    /// accurate, never less safe: an unmatched provider, an unreachable endpoint, an unreadable
    /// answer or a thrown fault all return Unknown, which the caller maps to the closed terminal.
    /// The lookup is a status GET, so it cannot itself create or bill a generation, and no provider
    /// payload, credential or customer content is stored.
    /// </summary>
    private async Task<MusicSubmissionReconciliation> ReconcileMusicSubmissionAsync(
        IServiceProvider services,
        TaslimDbContext db,
        GenerationJob job,
        Guid claimConcurrencyToken,
        string providerOperationId,
        string? providerKey,
        CancellationToken cancellationToken)
    {
        try
        {
            // The durable record names the provider the attempt was opened against, which is the only
            // answer that can be trusted after the process that made the submission is gone. A job created
            // without a provider column carries the guardrail placeholder instead, so the configured key is
            // the second candidate: it is the same key the handler selected when the request went out.
            // Still no match, still no answer: Unknown, which the ladder keeps closed.
            var candidates = services.GetServices<IMusicGenerationProvider>().ToList();
            var provider = MatchMusicProvider(candidates, providerKey)
                ?? MatchMusicProvider(candidates, services.GetService<IOptions<MusicGenerationOptions>>()?.Value?.ProviderKey);
            if (provider is null)
                return MusicSubmissionReconciliation.Unknown;
            var result = await provider.ReconcileAcceptedSubmissionAsync(providerOperationId, cancellationToken);
            await RecordMusicReconciliationAsync(db, job, claimConcurrencyToken, result, cancellationToken);
            logger.LogInformation(
                "Music accepted-submission reconciliation completed. JobId={JobId}; Outcome={Outcome}; ProviderDeclaresNoCharge={ProviderDeclaresNoCharge}",
                job.Id, result.Outcome, result.ProviderDeclaresNoCharge);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Music accepted-submission reconciliation was inconclusive; keeping the closed terminal. JobId={JobId}", job.Id);
            return MusicSubmissionReconciliation.Unknown;
        }
    }

    /// <summary>
    /// F-36.1b: durable audit trail for the decision, in the attempt row's existing safe-metadata
    /// column (no migration). Classification and timestamp only.
    /// </summary>
    private static async Task RecordMusicReconciliationAsync(
        TaslimDbContext db,
        GenerationJob job,
        Guid claimConcurrencyToken,
        MusicSubmissionReconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        var payload = $"{{\"musicSubmissionReconcile\":{{\"outcome\":\"{reconciliation.Outcome}\",\"providerDeclaresNoCharge\":{(reconciliation.ProviderDeclaresNoCharge ? "true" : "false")},\"checkedAtUtc\":\"{DateTime.UtcNow:O}\"}}}}";
        var bounded = payload.Length <= 8000 ? payload : payload.Substring(0, 8000);
        await db.GenerationProviderAttempts
            .Where(item => item.GenerationJobId == job.Id
                && item.JobConcurrencyToken == claimConcurrencyToken
                && item.ProviderExecutionId != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.SafeMetadataJson, bounded),
                cancellationToken);
    }

    /// <summary>
    /// F-36.1b: writes the pre-dispatch marker onto the attempt row before the paid request is handed
    /// to the transport, and refuses to dispatch when it cannot. Never overwrites an accepted operation
    /// id, so the two durable facts stay distinguishable and a late marker cannot mask a known id.
    /// The marker is what separates "nothing was sent" from "a paid generation may exist at the provider
    /// with no operation id to reconcile", so a persistence failure is not a log line: recovery would
    /// then have to choose between releasing a cancellation that might have a paid sibling behind it and
    /// refusing one that had nothing behind it, and the customer could be charged twice for one request.
    /// Aborting before the transport is the branch where nothing is unknown. No request has left this
    /// process when this throws, so the pre-dispatch posture is exactly preserved: the failure maps to
    /// the retryable outage class, the reservation keeps its cancellation/refund handling with zero
    /// charge, and a retry from here creates the FIRST paid submission rather than a second one - unlike
    /// the submission-unconfirmed class, whose copy asserts the request may already have reached the
    /// provider and whose retry is blocked. The same token predicate that fences the write is what makes
    /// a superseded claim unable to spend money at all, so a zero-row marker is treated as a lost claim.
    /// </summary>
    private async Task StampDispatchingSubmissionAsync(
        TaslimDbContext db,
        Guid attemptId,
        Guid jobConcurrencyToken,
        CancellationToken cancellationToken)
    {
        int stamped;
        try
        {
            stamped = await db.GenerationProviderAttempts
                .Where(item => item.Id == attemptId
                    && item.JobConcurrencyToken == jobConcurrencyToken
                    && (item.ProviderExecutionId == null || item.ProviderExecutionId == ""))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProviderExecutionId, MusicDispatchMarker),
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The write did not land. Clearing keeps a faulted context usable for the failure path that
            // follows; the billable request is never issued behind an unrecorded marker.
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Music dispatch marker could not be persisted; the billable request was not dispatched. AttemptId={AttemptId}", attemptId);
            throw new MusicProviderUnavailableException();
        }

        if (stamped == 0)
        {
            // No attempt row matched this claim: the concurrency token moved (a superseded claim may not
            // spend money) or the row already carries submission evidence, which the marker predicate
            // deliberately refuses to overwrite. Both mean the marker cannot be relied on, so the paid
            // request stops here. A caller-initiated cancellation is unaffected: a token-filtered UPDATE
            // observes cancellation by throwing OperationCanceledException rather than returning zero
            // rows, so the cancellation path above keeps propagating cancellation instead of this.
            logger.LogWarning("Music dispatch marker matched no current attempt row; the billable request was not dispatched. AttemptId={AttemptId}; JobConcurrencyToken={JobConcurrencyToken}", attemptId, jobConcurrencyToken);
            throw new MusicProviderUnavailableException();
        }
    }


    /// <summary>
    /// F-36.1: writes the accepted provider operation id onto the already-persisted attempt row and
    /// commits it immediately, outside any transaction the dying process might never have reached. The
    /// record is what lease recovery depends on, so a write that cannot land fails closed: it raises the
    /// submission-unconfirmed signal instead of letting acceptance be read as "nothing was submitted",
    /// and that class is already terminal with automatic re-dispatch suppressed.
    /// </summary>
    private async Task StampAcceptedSubmissionAsync(
        TaslimDbContext db,
        Guid attemptId,
        Guid jobConcurrencyToken,
        string providerOperationId,
        CancellationToken cancellationToken)
    {
        var operationId = (providerOperationId ?? string.Empty).Trim();
        if (operationId.Length == 0)
        {
            logger.LogWarning("Music provider reported acceptance without a usable operation id; treating the submission as unconfirmed. AttemptId={AttemptId}", attemptId);
            throw new MusicProviderSubmissionUnconfirmedException();
        }

        const int ProviderExecutionIdLimit = 240;
        if (operationId.Length > ProviderExecutionIdLimit)
        {
            // The value is the provider's own opaque identifier, bounded at 240 characters by the
            // model. Keeping a prefix still records the fact recovery needs - a paid submission was
            // accepted - without inventing an identifier; a later provider-status reconciliation simply
            // will not match a truncated value and stays on the same fail-closed path.
            operationId = operationId[..ProviderExecutionIdLimit];
        }

        try
        {
            var stamped = await db.GenerationProviderAttempts
                .Where(item => item.Id == attemptId && item.JobConcurrencyToken == jobConcurrencyToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProviderExecutionId, operationId),
                    cancellationToken);
            if (stamped == 0)
            {
                logger.LogWarning("Music accepted-submission evidence found no current attempt row; treating the submission as unconfirmed. AttemptId={AttemptId}; JobConcurrencyToken={JobConcurrencyToken}", attemptId, jobConcurrencyToken);
                throw new MusicProviderSubmissionUnconfirmedException();
            }
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Music accepted-submission evidence could not be persisted; treating the submission as unconfirmed. AttemptId={AttemptId}", attemptId);
            throw new MusicProviderSubmissionUnconfirmedException();
        }
    }

    private static string CancellationCode(GenerationJob job) =>
        GenerationJobTypes.MovieDialogueVoiceTypes.Contains(job.JobType)
            ? GenerationJobErrorCodes.MovieDialogueVoiceCancelled
            : GenerationJobTypes.MovieTypes.Contains(job.JobType)
            ? GenerationJobErrorCodes.MovieCancelled
            : GenerationJobTypes.MovieSoundTypes.Contains(job.JobType)
                ? GenerationJobErrorCodes.MovieSoundCancelled
            : string.Equals(job.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)
            ? GenerationJobErrorCodes.ImageCancelled
            : string.Equals(job.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.DocumentCancelled
            : string.Equals(job.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.PresentationCancelled
            : string.Equals(job.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.ResearchCancelled
            : string.Equals(job.JobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.SocialCancelled
            : string.Equals(job.JobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.MusicCancelled
            : string.Equals(job.JobType, GenerationJobTypes.VoiceGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.VoiceCancelled
            : GenerationJobErrorCodes.Cancelled;

    private static string MapFailureCode(Exception exception, string jobType)
    {
        if (exception is GenerationNoBillableAssetException) return GenerationJobErrorCodes.NoBillableAsset;
        if (exception is GenerationBudgetRejectedException budgetRejected) return budgetRejected.Code;
        var qualityFailure = exception as GenerationQualityControlException ?? exception.InnerException as GenerationQualityControlException;
        if (qualityFailure is not null)
        {
            if (string.Equals(jobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.ImageOutputInvalid;
            if (string.Equals(jobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.DocumentOutputInvalid;
            if (string.Equals(jobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.PresentationOutputInvalid;
            if (string.Equals(jobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.ResearchOutputInvalid;
            if (string.Equals(jobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.SocialOutputInvalid;
            if (GenerationJobTypes.MovieSoundTypes.Contains(jobType)) return GenerationJobErrorCodes.MovieSoundOutputInvalid;
            if (GenerationJobTypes.MovieTypes.Contains(jobType)) return GenerationJobErrorCodes.MovieOutputInvalid;
            if (string.Equals(jobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.MusicOutputInvalid;
            if (string.Equals(jobType, GenerationJobTypes.VoiceGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.VoiceOutputInvalid;
        }
        if (string.Equals(jobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase))
        {
            return exception switch
            {
                DocumentGenerationStageException staged => staged.Code,
                DocumentRequestValidationException validation => validation.Code,
                DocumentContextLimitException => GenerationJobErrorCodes.DocumentContextTooLarge,
                DocumentOutputValidationException => GenerationJobErrorCodes.DocumentOutputInvalid,
                AiProviderException providerException => DocumentGenerationFailureCodes.ForProvider(providerException),
                FileStorageUnavailableException => GenerationJobErrorCodes.DocumentStorageFailed,
                FileStorageOperationException => GenerationJobErrorCodes.DocumentStorageFailed,
                FileUploadValidationException => GenerationJobErrorCodes.DocumentStorageFailed,
                AiProviderUnavailableException => GenerationJobErrorCodes.DocumentProviderUnavailable,
                AiProviderTimeoutException => GenerationJobErrorCodes.DocumentProviderUnavailable,
                _ => GenerationJobErrorCodes.DocumentGenerationFailed,
            };
        }
        if (string.Equals(jobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase))
        {
            return exception switch
            {
                PresentationGenerationStageException staged => staged.Code,
                PresentationRequestValidationException validation => validation.Code,
                PresentationContextLimitException => GenerationJobErrorCodes.PresentationContextTooLarge,
                PresentationOutputValidationException => GenerationJobErrorCodes.PresentationOutputInvalid,
                AiProviderException providerException => PresentationGenerationFailureCodes.ForProvider(providerException),
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.PresentationStorageFailed,
                AiProviderUnavailableException or AiProviderTimeoutException => GenerationJobErrorCodes.PresentationProviderUnavailable,
                _ => GenerationJobErrorCodes.PresentationGenerationFailed,
            };
        }
        if (string.Equals(jobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase))
        {
            return exception switch
            {
                ResearchGenerationStageException staged => staged.Code,
                ResearchRequestValidationException validation => validation.Code,
                ResearchContextLimitException => GenerationJobErrorCodes.ResearchContextTooLarge,
                ResearchCitationValidationException => GenerationJobErrorCodes.ResearchCitationValidationFailed,
                ResearchOutputValidationException or ResearchOutputInvalidException => GenerationJobErrorCodes.ResearchOutputInvalid,
                ResearchSearchUnavailableException or ResearchSearchTimeoutException => GenerationJobErrorCodes.ResearchSearchUnavailable,
                ResearchSearchFailedException => GenerationJobErrorCodes.ResearchSearchFailed,
                AiProviderException or AiProviderUnavailableException or AiProviderTimeoutException => GenerationJobErrorCodes.ResearchProviderUnavailable,
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.ResearchStorageFailed,
                _ => GenerationJobErrorCodes.ResearchGenerationFailed,
            };
        }
        if (string.Equals(jobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase))
        {
            return exception switch
            {
                SocialGenerationStageException staged => staged.Code,
                SocialRequestValidationException validation => validation.Code,
                SocialContextLimitException => GenerationJobErrorCodes.SocialContextTooLarge,
                SocialOutputValidationException => GenerationJobErrorCodes.SocialOutputInvalid,
                AiProviderException providerException => SocialGenerationFailureCodes.ForProvider(providerException),
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.SocialStorageFailed,
                AiProviderUnavailableException or AiProviderTimeoutException => GenerationJobErrorCodes.SocialProviderUnavailable,
                _ => GenerationJobErrorCodes.SocialGenerationFailed,
            };
        }
        if (GenerationJobTypes.MovieDialogueVoiceTypes.Contains(jobType))
        {
            return exception switch
            {
                MovieDialogueVoiceRequestValidationException validation => validation.Code,
                MovieDialogueVoiceProviderUnavailableException => GenerationJobErrorCodes.MovieDialogueVoiceProviderUnavailable,
                MovieDialogueVoiceOutputInvalidException => GenerationJobErrorCodes.MovieDialogueVoiceOutputInvalid,
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.VoiceOutputStorageFailed,
                _ => GenerationJobErrorCodes.MovieDialogueVoiceGenerationFailed,
            };
        }
        if (GenerationJobTypes.MovieSoundTypes.Contains(jobType))
        {
            return exception switch
            {
                MovieSoundRequestValidationException validation => validation.Code,
                MovieSoundProviderUnavailableException => GenerationJobErrorCodes.MovieSoundProviderUnavailable,
                MovieSoundOutputInvalidException => GenerationJobErrorCodes.MovieSoundOutputInvalid,
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.MovieSoundOutputStorageFailed,
                _ => GenerationJobErrorCodes.MovieSoundGenerationFailed,
            };
        }
        if (GenerationJobTypes.MovieTypes.Contains(jobType))
        {
            return exception switch
            {
                MovieFinalAssemblyUnavailableException => GenerationJobErrorCodes.MovieAssemblyProviderUnavailable,
                MovieFinalAssemblyQualityControlException => GenerationJobErrorCodes.MovieAssemblyQcFailed,
                MovieFinalAssemblyExecutionException assemblyException => assemblyException.Code,
                VideoGenerationAdapterExecutionException adapterException => adapterException.Category switch
                {
                    VideoGenerationFailureCategory.Unavailable or VideoGenerationFailureCategory.Authentication => GenerationJobErrorCodes.MovieProviderUnavailable,
                    VideoGenerationFailureCategory.TimedOut => GenerationJobErrorCodes.MovieProviderTimeout,
                    VideoGenerationFailureCategory.UnsupportedCapability or VideoGenerationFailureCategory.InvalidRequest => GenerationJobErrorCodes.MovieProviderUnsupportedRequest,
                    VideoGenerationFailureCategory.Cancelled or VideoGenerationFailureCategory.StaleEvent => GenerationJobErrorCodes.MovieCancelled,
                    VideoGenerationFailureCategory.ArtifactUnavailable or VideoGenerationFailureCategory.MalformedResponse => GenerationJobErrorCodes.MovieOutputInvalid,
                    _ => GenerationJobErrorCodes.MovieGenerationFailed,
                },
                MovieProviderUnavailableException => GenerationJobErrorCodes.MovieProviderUnavailable,
                MovieVideoProviderTimeoutException => GenerationJobErrorCodes.MovieProviderTimeout,
                MovieVideoStaleWorkerException => GenerationJobErrorCodes.MovieCancelled,
                MovieVideoProviderCancelledException => GenerationJobErrorCodes.MovieCancelled,
                MovieVideoProviderException providerException when providerException.Code == GenerationJobErrorCodes.MovieProviderUnavailable => GenerationJobErrorCodes.MovieProviderUnavailable,
                MovieVideoProviderException providerException when providerException.Code == GenerationJobErrorCodes.MovieProviderUnsupportedRequest => GenerationJobErrorCodes.MovieProviderUnsupportedRequest,
                MovieVideoProviderException => GenerationJobErrorCodes.MovieGenerationFailed,
                MovieVideoProviderOutputException => GenerationJobErrorCodes.MovieOutputInvalid,
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.MovieOutputStorageFailed,
                _ => GenerationJobErrorCodes.MovieGenerationFailed,
            };
        }
        if (string.Equals(jobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase))
        {
            return exception switch
            {
                MusicRequestValidationException validation => validation.Code,
                // Must precede the MusicProviderUnavailableException arm: the retrieval failure is
                // deliberately a subclass so that existing unavailable guards still catch it, while
                // the code mapping keeps it out of the "please try again" class.
                MusicOutputRetrievalFailedException => GenerationJobErrorCodes.MusicOutputRetrievalFailed,
                // Also before the generic unavailable arm. The provider accepted a billable
                // generation and its outcome was never observed, so it must not read as the retryable
                // outage class - a replay would post a second paid track. This reuses the code the
                // expired-lease terminal already reports for that identical unknown, whose copy
                // states the outcome instead of asserting that a finished track exists.
                MusicProviderSubmissionUnconfirmedException => GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed,
                MusicProviderUnavailableException => GenerationJobErrorCodes.MusicProviderUnavailable,
                MusicProviderTimeoutException => GenerationJobErrorCodes.MusicProviderTimeout,
                MusicProviderRateLimitException => GenerationJobErrorCodes.MusicProviderRateLimited,
                MusicProviderRejectedException => GenerationJobErrorCodes.MusicPromptRejected,
                MusicProviderInvalidRequestException => GenerationJobErrorCodes.MusicProviderInvalidRequest,
                MusicOutputInvalidException => GenerationJobErrorCodes.MusicOutputInvalid,
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.MusicOutputStorageFailed,
                _ => GenerationJobErrorCodes.MusicGenerationFailed,
            };
        }
        if (string.Equals(jobType, GenerationJobTypes.VoiceGenerate, StringComparison.OrdinalIgnoreCase))
        {
            return exception switch
            {
                VoiceRequestValidationException validation => validation.Code,
                VoiceProviderUnsupportedRequestException => GenerationJobErrorCodes.VoiceProviderUnsupportedRequest,
                VoiceProviderAuthenticationException => GenerationJobErrorCodes.VoiceProviderAuthentication,
                VoiceProviderRateLimitException => GenerationJobErrorCodes.VoiceProviderRateLimited,
                VoiceProviderTimeoutException => GenerationJobErrorCodes.VoiceProviderTimeout,
                VoiceProviderInvalidInputException => GenerationJobErrorCodes.VoiceProviderInvalidInput,
                VoiceProviderUnavailableException or VoiceProviderConfigurationException => GenerationJobErrorCodes.VoiceProviderUnavailable,
                VoiceLanguageUnsupportedException => GenerationJobErrorCodes.VoiceLanguageUnsupported,
                VoiceProviderFailureException => GenerationJobErrorCodes.VoiceProviderFailed,
                VoiceOutputInvalidException => GenerationJobErrorCodes.VoiceOutputInvalid,
                FileStorageUnavailableException or FileStorageOperationException or FileUploadValidationException => GenerationJobErrorCodes.VoiceOutputStorageFailed,
                _ => GenerationJobErrorCodes.VoiceProviderFailed,
            };
        }
        if (!string.Equals(jobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.ExecutionFailed;
        return exception switch
        {
            ImageRequestValidationException validation => validation.Code,
            ImageProviderUnavailableException => GenerationJobErrorCodes.ImageProviderUnavailable,
            ImageProviderTimeoutException => GenerationJobErrorCodes.ImageProviderUnavailable,
            ImageProviderRateLimitException => GenerationJobErrorCodes.ImageProviderUnavailable,
            ImageProviderUnsupportedRequestException => GenerationJobErrorCodes.ImageRequestInvalid,
            ImageProviderSafetyException => GenerationJobErrorCodes.ImageSafetyRefusal,
            ImageOutputInvalidException => GenerationJobErrorCodes.ImageOutputInvalid,
            ImageProviderFailureException failure => failure.SafeCode,
            ImageOutputStorageException => GenerationJobErrorCodes.ImageOutputStorageFailed,
            FileStorageUnavailableException => GenerationJobErrorCodes.ImageOutputStorageFailed,
            FileStorageOperationException => GenerationJobErrorCodes.ImageOutputStorageFailed,
            FileUploadValidationException => GenerationJobErrorCodes.ImageOutputStorageFailed,
            _ => GenerationJobErrorCodes.ImageGenerationFailed,
        };
    }

    private static string FailureMessage(string code) => GenerationJobErrorMessages.For(code) ?? "The job could not be completed.";
}
