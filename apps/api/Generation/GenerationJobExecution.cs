using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Files;
using Taslim.Api.Images;
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
    Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null);
    Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default);
    Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
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
        // with a live usage record that no worker will ever claim. A caller may
        // already own a transaction when it is creating a compound provider-
        // neutral workflow (for example, a Movie take batch); join that
        // transaction instead of attempting an unsupported nested transaction.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await usage.BeginAsync(job, request.EstimatedProviderCostUsd, cancellationToken);
            await queue.EnqueueAsync(job.Id, cancellationToken);
            job.Status = GenerationJobStatus.Queued;
            job.QueuedAt = DateTime.UtcNow;
            if (ownsTransaction)
                await transaction!.CommitAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException) when (normalizedKey is not null && ownsTransaction)
        {
            await transaction!.RollbackAsync(CancellationToken.None);
            db.Entry(job).State = EntityState.Detached;
            var existing = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == normalizedKey, cancellationToken);
            if (existing is null) throw;
            if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                throw new GenerationJobValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different generation request.");
            return existing;
        }
        finally
        {
            if (ownsTransaction && transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null)
    {
        var source = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (source is null || !await access.IsMemberAsync(userId, source.WorkspaceId, cancellationToken) || IsPrivateResearchRecord(source, userId)) return null;
        if (source.Status is not (GenerationJobStatus.Failed or GenerationJobStatus.Cancelled))
            throw new GenerationJobValidationException("RETRY_SOURCE_NOT_TERMINAL", "Only failed or cancelled jobs can be retried.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new GenerationJobValidationException("RETRY_IDEMPOTENCY_REQUIRED", "A retry idempotency key is required.");

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

    private async Task ValidateMovieJobTargetAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken, Guid? retryOfJobId = null)
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
            || request.ProjectId != clip.MovieProject.ProjectId || clip.GenerationJobId is not null)
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
        if (job is null || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken) || IsPrivateResearchRecord(job, userId)) return null;
        return job;
    }

    public async Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, filter.WorkspaceId, cancellationToken)) return null;
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        // Research execution records can contain report text and source excerpts derived from
        // private uploads. Generated assets remain the intentional workspace sharing surface,
        // but the execution record itself is only visible to its creator.
        var query = db.GenerationJobs.AsNoTracking().Where(job => job.WorkspaceId == filter.WorkspaceId &&
            (job.JobType != GenerationJobTypes.ResearchGenerate || job.CreatedByUserId == userId));
        if (filter.Status.HasValue) query = query.Where(job => job.Status == filter.Status.Value);
        if (filter.ProjectId.HasValue) query = query.Where(job => job.ProjectId == filter.ProjectId.Value);
        if (!string.IsNullOrWhiteSpace(filter.JobType)) query = query.Where(job => job.JobType == filter.JobType);
        var totalCount = await query.CountAsync(cancellationToken);
        var jobs = await query.Include(job => job.Outputs)
            .OrderByDescending(job => job.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new GenerationJobListDto(jobs.Select(GenerationJobContractMapper.ToDto).ToArray(), page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    public async Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null) return GenerationJobCancelResult.NotFound;
        if (!await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken) || IsPrivateResearchRecord(job, userId)) return GenerationJobCancelResult.Forbidden;
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

    private static bool IsPrivateResearchRecord(GenerationJob job, Guid userId) =>
        string.Equals(job.JobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase)
        && job.CreatedByUserId != userId;

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
                    await RecoverExpiredClaimsAsync(db, usage, movieExecutions, notifications, stoppingToken);
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
                logger.LogInformation(
                    "Generation job claimed. JobId={JobId}; WorkspaceId={WorkspaceId}; JobType={JobType}; RequestId={RequestId}; RetryCount={RetryCount}; ClaimExpiresAt={ClaimExpiresAt}",
                    job.Id, job.WorkspaceId, job.JobType, job.RequestId, job.RetryCount, job.ClaimExpiresAt);
                await ExecuteJobAsync(job, workerId, workerIndex, stoppingToken);
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
                        .SetProperty(item => item.ErrorCode, CancellationCode(job))
                        .SetProperty(item => item.ErrorMessage, "The job was cancelled.")
                        .SetProperty(item => item.CancelledAt, now)
                        .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null)
                        .SetProperty(item => item.ConcurrencyToken, cancellationTerminalToken), cancellationToken);
                if (cancelled == 0)
                {
                    await cancellationFinalization.RollbackAsync(cancellationToken);
                    continue;
                }
                var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
                await usage.CancelAsync(transaction, CancellationCode(job), cancellationToken);
                await cancellationFinalization.CommitAsync(cancellationToken);
                logger.LogInformation("Expired cancelled generation lease finalized. JobId={JobId}; RetryReason={RetryReason}", job.Id, "cancellation_requested");
                continue;
            }

            var maxAutomaticRetries = Math.Max(0, settings.MaxAutomaticRetries);
            if (job.RetryCount < maxAutomaticRetries)
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
                        .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null)
                        .SetProperty(item => item.RetryCount, item => item.RetryCount + 1)
                        .SetProperty(item => item.ConcurrencyToken, recoveryToken), cancellationToken);
                if (recovered == 0) continue;
                logger.LogWarning("Expired generation lease recovered. JobId={JobId}; RetryReason={RetryReason}; RetryCount={RetryCount}; MaxAutomaticRetries={MaxAutomaticRetries}",
                    job.Id, "lease_expired", job.RetryCount + 1, maxAutomaticRetries);
                await TryNotifyAsync(() => notifications.CreateGenerationAttentionAsync(job.Id, cancellationToken), job.Id, cancellationToken);
                continue;
            }

            var poisonTerminalToken = Guid.NewGuid();
            await using var poisonFinalization = await db.Database.BeginTransactionAsync(cancellationToken);
            await movieExecutions.MarkFailedAsync(job.Id, expired.ConcurrencyToken, GenerationJobErrorCodes.Poisoned, cancellationToken);
            var poisoned = await db.GenerationJobs
                .Where(item => item.Id == job.Id
                    && item.Status == GenerationJobStatus.Running
                    && item.ConcurrencyToken == expired.ConcurrencyToken
                    && !item.CancellationRequested
                    && item.ClaimExpiresAt.HasValue
                    && item.ClaimExpiresAt < now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, GenerationJobStatus.Failed)
                    .SetProperty(item => item.ErrorCode, GenerationJobErrorCodes.Poisoned)
                    .SetProperty(item => item.ErrorMessage, PoisonJobMessage)
                    .SetProperty(item => item.FailedAt, now)
                    .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null)
                    .SetProperty(item => item.ConcurrencyToken, poisonTerminalToken), cancellationToken);
            if (poisoned == 0)
            {
                await poisonFinalization.RollbackAsync(cancellationToken);
                continue;
            }
            var poisonUsage = await usage.BeginAsync(job, cancellationToken: cancellationToken);
            await usage.FailAsync(poisonUsage, GenerationJobErrorCodes.Poisoned, cancellationToken: cancellationToken);
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
                await CancelRunningAsync(db, usage, claimedJob, claimedJob.ConcurrencyToken, stoppingToken);
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
                                await movieExecutions.MarkReadyAsync(current.Id, claimedJob.ConcurrencyToken, publication?.Asset?.Id, publication?.CreatedFile?.Id, null, publication?.Output.MetadataJson, stoppingToken);
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
                await CancelRunningAsync(db, usage, claimedJob, claimedJob.ConcurrencyToken, stoppingToken);
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
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
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
            // Host shutdown cancels stoppingToken before this handler exits. Durable
            // cancellation must still commit so the job does not remain Running until
            // lease recovery on the next process.
            await CancelRunningAsync(db, usage, claimedJob, claimedJob.ConcurrencyToken, CancellationToken.None);
            logger.LogInformation(
                "Generation job execution cancelled. JobId={JobId}; JobType={JobType}; RequestId={RequestId}; ElapsedMs={ElapsedMs}; UsageFinalized={UsageFinalized}",
                claimedJob.Id, claimedJob.JobType, claimedJob.RequestId, (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds, true);
        }
        catch (Exception exception)
        {
            if (!publicationCommitted)
                foreach (var publication in publications) await publisher.DiscardAsync(publication, CancellationToken.None);
            var failureCode = MapFailureCode(exception, claimedJob.JobType);
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
                logger.LogError("Document generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
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
                logger.LogError("Presentation generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
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
                logger.LogError("Research generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; SearchHttpStatus={SearchHttpStatus}; SearchErrorType={SearchErrorType}; SearchErrorCode={SearchErrorCode}; SearchErrorParam={SearchErrorParam}; ModelKey={ModelKey}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
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
                logger.LogError("Social generation failed. JobId={JobId}; RequestId={RequestId}; Stage={Stage}; ErrorCode={ErrorCode}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
                    claimedJob.RequestId,
                    stage,
                    failureCode,
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
                logger.LogError(exception, "Generation job execution failed. JobId={JobId}; WorkspaceId={WorkspaceId}; JobType={JobType}; RequestId={RequestId}; FailureCode={FailureCode}; ExceptionType={ExceptionType}; ElapsedMs={ElapsedMs}; UsageFinalized={UsageFinalized}",
                    claimedJob.Id, claimedJob.WorkspaceId, claimedJob.JobType, claimedJob.RequestId, failureCode, exception.GetType().Name,
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

    private static async Task CancelRunningAsync(TaslimDbContext db, IGenerationJobUsageService usage, GenerationJob job, Guid concurrencyToken, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var finalization = await db.Database.BeginTransactionAsync(cancellationToken);
        var cancelled = await db.GenerationJobs.Where(item => item.Id == job.Id && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Cancelled)
                .SetProperty(item => item.ErrorCode, CancellationCode(job))
                .SetProperty(item => item.ErrorMessage, "The job was cancelled.")
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
        await usage.CancelAsync(transaction, CancellationCode(job), cancellationToken);
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
