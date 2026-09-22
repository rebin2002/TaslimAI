using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Files;
using Taslim.Api.Images;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Taslim.Api.Usage;

namespace Taslim.Api.Generation;

public sealed class GenerationJobOptions
{
    public int WorkerConcurrency { get; set; } = 1;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int CancellationPollMilliseconds { get; set; } = 100;
    public int ClaimRecoveryIntervalMilliseconds { get; set; } = 30000;
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
    GeneratedAssetDescriptor? Asset = null);
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
    Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, CancellationToken cancellationToken = default);
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
                    : string.Equals(job.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase) ? UsageFeature.Presentation : UsageFeature.Generation,
            cancellationToken,
            job.Id,
            estimatedProviderCostUsd);

    public Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, CancellationToken cancellationToken = default) =>
        ledger.CompleteAsync(transaction, usage, cancellationToken);

    public Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default) =>
        ledger.FailAsync(transaction, failureCode, usage, cancellationToken);

    public Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default) =>
        ledger.CancelAsync(transaction, cancellationCode, cancellationToken);
}

public interface IGenerationJobService
{
    Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default);
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
    IGenerationJobUsageService usage) : IGenerationJobService
{
    public async Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default)
    {
        if (!GenerationJobTypes.Supported.Contains(request.JobType.Trim()))
            throw new GenerationJobValidationException(GenerationJobErrorCodes.TypeNotSupported, "This job type is not available.");
        if (!await access.IsMemberAsync(userId, request.WorkspaceId, cancellationToken))
            throw new GenerationJobForbiddenException();
        if (request.ProjectId.HasValue && !await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == request.WorkspaceId, cancellationToken))
            throw new GenerationJobValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");

        try
        {
            using var document = JsonDocument.Parse(request.InputJson);
        }
        catch (JsonException)
        {
            throw new GenerationJobValidationException("INVALID_INPUT_JSON", "The job input is not valid JSON.");
        }

        var now = DateTime.UtcNow;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.Supported.First(type => string.Equals(type, request.JobType.Trim(), StringComparison.OrdinalIgnoreCase)),
            Status = GenerationJobStatus.Pending,
            Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(),
            InputJson = request.InputJson,
            ProgressPercent = 0,
            CreatedAt = now,
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        await usage.BeginAsync(job, request.EstimatedProviderCostUsd, cancellationToken);
        await queue.EnqueueAsync(job.Id, cancellationToken);
        job.Status = GenerationJobStatus.Queued;
        job.QueuedAt = DateTime.UtcNow;
        return job;
    }

    public async Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.GenerationJobs.AsNoTracking().Include(item => item.Outputs).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken)) return null;
        return job;
    }

    public async Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, filter.WorkspaceId, cancellationToken)) return null;
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var query = db.GenerationJobs.AsNoTracking().Where(job => job.WorkspaceId == filter.WorkspaceId);
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
        if (!await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken)) return GenerationJobCancelResult.Forbidden;
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
            await CancelUsageAsync(job, CancellationCode(job), cancellationToken);
            return GenerationJobCancelResult.Cancelled;
        }

        var running = await db.GenerationJobs
            .Where(item => item.Id == jobId && item.Status == GenerationJobStatus.Running)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CancellationRequested, true), cancellationToken);
        return running > 0 ? GenerationJobCancelResult.CancellationRequested : GenerationJobCancelResult.Conflict;
    }

    private async Task CancelUsageAsync(GenerationJob job, string code, CancellationToken cancellationToken)
    {
        var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
        await usage.CancelAsync(transaction, code, cancellationToken);
    }

    private static string CancellationCode(GenerationJob job) =>
        string.Equals(job.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)
            ? GenerationJobErrorCodes.ImageCancelled
            : string.Equals(job.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.DocumentCancelled
                : string.Equals(job.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase)
                    ? GenerationJobErrorCodes.PresentationCancelled
                : GenerationJobErrorCodes.Cancelled;
}

public sealed class GenerationJobValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class GenerationJobForbiddenException : Exception;

public sealed class GenerationJobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<GenerationJobOptions> options,
    ILogger<GenerationJobWorker> logger) : BackgroundService
{
    private readonly GenerationJobOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, Math.Max(1, settings.WorkerConcurrency)).Select(_ => RunWorkerAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        var schedule = new GenerationJobPollingSchedule(settings);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var now = DateTime.UtcNow;
                if (schedule.RecoveryDue(now))
                {
                    await RecoverExpiredClaimsAsync(db, stoppingToken);
                    schedule.ScheduleNextRecovery(now);
                }
                var job = await ClaimAsync(db, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(schedule.IdleDelay, stoppingToken);
                    continue;
                }
                await ExecuteJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Generation job worker iteration failed safely.");
                await Task.Delay(schedule.IdleDelay, stoppingToken);
            }
        }
    }

    private static Task<int> RecoverExpiredClaimsAsync(TaslimDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return db.GenerationJobs
            .Where(job => job.Status == GenerationJobStatus.Running && job.ClaimExpiresAt.HasValue && job.ClaimExpiresAt < now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, GenerationJobStatus.Queued)
                .SetProperty(job => job.QueuedAt, now)
                .SetProperty(job => job.StartedAt, (DateTime?)null)
                .SetProperty(job => job.ClaimExpiresAt, (DateTime?)null)
                .SetProperty(job => job.CancellationRequested, false), cancellationToken);
    }

    private static async Task<GenerationJob?> ClaimAsync(TaslimDbContext db, CancellationToken cancellationToken)
    {
        var isSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (isSqlite)
        {
            var candidate = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Status == GenerationJobStatus.Queued, cancellationToken);
            if (candidate is null) return null;
            var startedAt = DateTime.UtcNow;
            var claimExpiresAt = startedAt.AddMinutes(15);
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
        job.ClaimExpiresAt = DateTime.UtcNow.AddMinutes(15);
        job.ConcurrencyToken = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    private async Task ExecuteJobAsync(GenerationJob claimedJob, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = scope.ServiceProvider.GetRequiredService<IGenerationJobUsageService>();
        var publisher = scope.ServiceProvider.GetRequiredService<IGeneratedAssetPublisher>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorCancellationAsync(db, claimedJob.Id, cancellation, stoppingToken);
        var publications = new List<PreparedGenerationOutput>();
        var publicationCommitted = false;
        AiUsageMetadata? providerUsage = null;
        var executionStarted = Stopwatch.GetTimestamp();
        try
        {
            var handlers = scope.ServiceProvider.GetServices<IGenerationJobHandler>();
            var handler = handlers.FirstOrDefault(item => item.CanHandle(claimedJob.JobType));
            if (handler is null)
            {
                await FailAsync(db, usage, claimedJob, GenerationJobErrorCodes.TypeNotSupported, "This job type is not available.", null, stoppingToken);
                return;
            }
            var progress = new SerializedProgress(value => UpdateProgressSafelyAsync(claimedJob.Id, value, stoppingToken));
            var result = await handler.ExecuteAsync(claimedJob, progress, cancellation.Token);
            await progress.DrainAsync();
            providerUsage = result.Usage;
            var current = await db.GenerationJobs.FirstOrDefaultAsync(item => item.Id == claimedJob.Id, stoppingToken);
            if (current is null || current.Status != GenerationJobStatus.Running || current.CancellationRequested || cancellation.IsCancellationRequested)
            {
                await CancelRunningAsync(db, usage, claimedJob, stoppingToken);
                return;
            }
            foreach (var output in result.Outputs)
            {
                try
                {
                    publications.Add(await publisher.PrepareAsync(current, output, stoppingToken));
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
            }
            AttachAssetRepresentations(publications);
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
                        .Where(item => item.Id == current.Id && item.Status == GenerationJobStatus.Running && !item.CancellationRequested)
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
                        }
                        await usage.CompleteAsync(await usage.BeginAsync(current, cancellationToken: stoppingToken), result.Usage ?? new AiUsageMetadata("system", "unknown", null, null, null, 0m, 0m, 0, "completed", true), stoppingToken);
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
            if (completed == 0)
            {
                foreach (var publication in publications) await publisher.DiscardAsync(publication, stoppingToken);
                await CancelRunningAsync(db, usage, claimedJob, stoppingToken);
                return;
            }
            publicationCommitted = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!publicationCommitted)
                foreach (var publication in publications) await publisher.DiscardAsync(publication, CancellationToken.None);
            await CancelRunningAsync(db, usage, claimedJob, stoppingToken);
        }
        catch (Exception exception)
        {
            if (!publicationCommitted)
                foreach (var publication in publications) await publisher.DiscardAsync(publication, CancellationToken.None);
            var failureCode = MapFailureCode(exception, claimedJob.JobType);
            var failureUsage = providerUsage ?? (exception as DocumentGenerationStageException)?.Usage;
            failureUsage ??= (exception as PresentationGenerationStageException)?.Usage;
            if (string.Equals(claimedJob.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase))
            {
                var stage = (exception as DocumentGenerationStageException)?.Stage ?? DocumentGenerationStages.Execution;
                var providerException = exception as AiProviderException ?? exception.InnerException as AiProviderException;
                logger.LogError("Document generation failed. JobId={JobId}; Stage={Stage}; ErrorCode={ErrorCode}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
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
                logger.LogError("Presentation generation failed. JobId={JobId}; Stage={Stage}; ErrorCode={ErrorCode}; ExceptionType={ExceptionType}; ProviderFailureCategory={ProviderFailureCategory}; ProviderHttpStatus={ProviderHttpStatus}; ProviderErrorCode={ProviderErrorCode}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}; ElapsedMs={ElapsedMs}",
                    claimedJob.Id,
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
            else
            {
                logger.LogError(exception, "Generation job execution failed. JobId={JobId}; JobType={JobType}; FailureCode={FailureCode}", claimedJob.Id, claimedJob.JobType, failureCode);
            }
            await FailAsync(db, usage, claimedJob, failureCode, FailureMessage(failureCode), failureUsage, stoppingToken);
        }
        finally
        {
            cancellation.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
    }

    private async Task MonitorCancellationAsync(TaslimDbContext _, Guid jobId, CancellationTokenSource cancellation, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            if (await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.Id == jobId && job.CancellationRequested, stoppingToken))
            {
                cancellation.Cancel();
                return;
            }
            await Task.Delay(settings.CancellationPollMilliseconds, stoppingToken);
        }
    }

    private async Task<int> UpdateProgressAsync(Guid jobId, int progress, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.GenerationJobs.Where(job => job.Id == jobId && job.Status == GenerationJobStatus.Running)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.ProgressPercent, Math.Clamp(progress, 0, 100)), cancellationToken);
    }

    private async Task UpdateProgressSafelyAsync(Guid jobId, int progress, CancellationToken cancellationToken)
    {
        try
        {
            await UpdateProgressAsync(jobId, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Generation job progress update was skipped. JobId={JobId}; ExceptionType={ExceptionType}", jobId, exception.GetType().Name);
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

    private static async Task CancelRunningAsync(TaslimDbContext db, IGenerationJobUsageService usage, GenerationJob job, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.GenerationJobs.Where(item => item.Id == job.Id && item.Status == GenerationJobStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Cancelled)
                .SetProperty(item => item.ErrorCode, CancellationCode(job))
                .SetProperty(item => item.ErrorMessage, "The job was cancelled.")
                .SetProperty(item => item.CancelledAt, now)
                .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null), cancellationToken);
        var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
        await usage.CancelAsync(transaction, CancellationCode(job), cancellationToken);
    }

    private static async Task FailAsync(TaslimDbContext db, IGenerationJobUsageService usage, GenerationJob job, string code, string message, AiUsageMetadata? providerUsage, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.GenerationJobs.Where(item => item.Id == job.Id && item.Status == GenerationJobStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Failed)
                .SetProperty(item => item.ErrorCode, code)
                .SetProperty(item => item.ErrorMessage, message)
                .SetProperty(item => item.FailedAt, now)
                .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null), cancellationToken);
        var transaction = await usage.BeginAsync(job, cancellationToken: cancellationToken);
        await usage.FailAsync(transaction, code, providerUsage, cancellationToken);
    }

    private static string CancellationCode(GenerationJob job) =>
        string.Equals(job.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)
            ? GenerationJobErrorCodes.ImageCancelled
            : string.Equals(job.JobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase)
                ? GenerationJobErrorCodes.DocumentCancelled
                : string.Equals(job.JobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase)
                    ? GenerationJobErrorCodes.PresentationCancelled
                : GenerationJobErrorCodes.Cancelled;

    private static string MapFailureCode(Exception exception, string jobType)
    {
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
        if (!string.Equals(jobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase)) return GenerationJobErrorCodes.ExecutionFailed;
        return exception switch
        {
            ImageRequestValidationException validation => validation.Code,
            ImageProviderUnavailableException => GenerationJobErrorCodes.ImageProviderUnavailable,
            ImageProviderTimeoutException => GenerationJobErrorCodes.ImageProviderUnavailable,
            ImageProviderSafetyException => GenerationJobErrorCodes.ImageSafetyRefusal,
            ImageOutputInvalidException => GenerationJobErrorCodes.ImageOutputInvalid,
            ImageProviderFailureException failure => failure.SafeCode,
            FileStorageUnavailableException => GenerationJobErrorCodes.ImageOutputStorageFailed,
            FileStorageOperationException => GenerationJobErrorCodes.ImageOutputStorageFailed,
            FileUploadValidationException => GenerationJobErrorCodes.ImageOutputStorageFailed,
            _ => GenerationJobErrorCodes.ImageGenerationFailed,
        };
    }

    private static string FailureMessage(string code) => code switch
    {
        GenerationJobErrorCodes.ImageProviderUnavailable => "Image generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.ImageSafetyRefusal => "This request could not be completed by the image safety system. Try a different description.",
        GenerationJobErrorCodes.ImageOutputInvalid => "The image result was invalid. Please try again.",
        GenerationJobErrorCodes.ImageOutputStorageFailed => "The image was generated but could not be saved. Please try again.",
        GenerationJobErrorCodes.ImageRequestInvalid => "Please check the image request and try again.",
        GenerationJobErrorCodes.ImageCancelled => "The image generation was cancelled.",
        GenerationJobErrorCodes.DocumentProviderUnavailable => "Document generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.DocumentProviderConfiguration => "Document generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.DocumentProviderUnsupportedRequest => "Document generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.DocumentProviderRateLimited => "Document generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.DocumentProviderTransientFailure => "Document generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.DocumentContextTooLarge => "The selected source material is too large. Choose fewer or shorter documents.",
        GenerationJobErrorCodes.DocumentOutputInvalid => "The generated document was invalid. Please try again.",
        GenerationJobErrorCodes.DocumentStorageFailed => "The document was generated but could not be saved. Please try again.",
        GenerationJobErrorCodes.DocumentRenderFailed => "The document could not be rendered. Please try again.",
        GenerationJobErrorCodes.DocumentCancelled => "The document generation was cancelled.",
        GenerationJobErrorCodes.PresentationProviderUnavailable => "Presentation generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.PresentationProviderConfiguration => "Presentation generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.PresentationProviderUnsupportedRequest => "Presentation generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.PresentationProviderRateLimited => "Presentation generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.PresentationProviderTransientFailure => "Presentation generation is temporarily unavailable. Please try again later.",
        GenerationJobErrorCodes.PresentationContextTooLarge => "The selected source material is too large. Choose fewer or shorter documents.",
        GenerationJobErrorCodes.PresentationOutputInvalid => "The generated presentation was invalid. Please try again.",
        GenerationJobErrorCodes.PresentationStorageFailed => "The presentation was generated but could not be saved. Please try again.",
        GenerationJobErrorCodes.PresentationRenderFailed => "The presentation could not be rendered. Please try again.",
        GenerationJobErrorCodes.PresentationCancelled => "The presentation generation was cancelled.",
        _ when code.StartsWith("IMAGE_", StringComparison.Ordinal) => "The image could not be generated. Please try again.",
        _ when code.StartsWith("DOCUMENT_", StringComparison.Ordinal) => "The document could not be generated. Please try again.",
        _ when code.StartsWith("PRESENTATION_", StringComparison.Ordinal) => "The presentation could not be generated. Please try again.",
        _ => "The job could not be completed.",
    };
}
