using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using FileSettings = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Operations;

public interface IAdminOperationsService
{
    Task<AdminOperationsDashboardDto> GetDashboardAsync(AdminOperationsFilter filter, CancellationToken cancellationToken = default);
    Task<AdminJobRecoveryResult?> RecoverExpiredJobAsync(Guid actorUserId, Guid jobId, string reason, string? idempotencyKey, CancellationToken cancellationToken = default);
}

public sealed class AdminOperationConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AdminOperationsService(
    TaslimDbContext db,
    IOptions<BillingOptions> billingOptions,
    IOptions<FileSettings> fileOptions,
    IOptions<GenerationJobOptions> generationOptions,
    ProviderHealthService providerHealth) : IAdminOperationsService
{
    private const int RecentItemLimit = 20;
    private static readonly TimeSpan LongRunningThreshold = TimeSpan.FromMinutes(15);
    private static readonly string[] MovieJobTypes = GenerationJobTypes.MovieTypes.ToArray();

    public async Task<AdminOperationsDashboardDto> GetDashboardAsync(AdminOperationsFilter filter, CancellationToken cancellationToken = default)
    {
        var range = NormalizeRange(filter.FromUtc, filter.ToUtc);
        var generation = await BuildGenerationAsync(range, cancellationToken);
        var usage = await BuildUsageAsync(range, cancellationToken);
        var usersAndWorkspaces = await BuildUsersAndWorkspacesAsync(cancellationToken);
        var assetsAndStorage = await BuildAssetsAndStorageAsync(range, cancellationToken);
        var billing = await BuildBillingAsync(range, cancellationToken);
        var signals = await BuildSignalsAsync(range, generation, cancellationToken);
        var providers = await providerHealth.GetAsync(range, cancellationToken);
        var movie = await BuildMovieAsync(range, providers, cancellationToken);
        var workers = await BuildWorkersAsync(cancellationToken);
        var recentAdminActions = await db.AdminOperationAuditEvents.AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(RecentItemLimit)
            .Select(item => new AdminOperationAuditDto(item.Id, item.ActorUserId, item.Action, item.TargetType, item.TargetId, item.Outcome, item.Reason, item.CreatedAt))
            .ToArrayAsync(cancellationToken);

        return new AdminOperationsDashboardDto(
            new AdminOperationsRangeDto(range.FromUtc, range.ToUtc),
            generation,
            usage,
            usersAndWorkspaces,
            assetsAndStorage,
            billing,
            signals,
            providers,
            movie,
            workers,
            recentAdminActions);
    }

    public async Task<AdminJobRecoveryResult?> RecoverExpiredJobAsync(
        Guid actorUserId,
        Guid jobId,
        string reason,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = NormalizeReason(reason);
        var now = DateTime.UtcNow;
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Request correlation IDs are server-owned and must not be used as
        // idempotency keys. A caller-provided key is optional, but when it is
        // present a retry after a response timeout replays the original
        // result instead of being reported as a new conflict.
        var previous = await FindRecoveryAuditAsync(normalizedIdempotencyKey, jobId, cancellationToken);
        if (previous is not null)
        {
            var replay = ReplayRecovery(previous, jobId);
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var source = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (source is null) return null;
        if (!MovieJobTypes.Contains(source.JobType, StringComparer.Ordinal))
            throw new AdminOperationConflictException("RECOVERY_SCOPE_UNSUPPORTED", "Only movie generation jobs can be recovered from this operations surface.");
        if (source.Status != GenerationJobStatus.Running || source.ClaimExpiresAt is null || source.ClaimExpiresAt >= now)
            throw new AdminOperationConflictException("JOB_NOT_STUCK", "The job does not have an expired worker lease.");

        var recoveryToken = Guid.NewGuid();
        var updated = await db.GenerationJobs
            .Where(item => item.Id == jobId
                && item.Status == GenerationJobStatus.Running
                && item.ClaimExpiresAt.HasValue
                && item.ClaimExpiresAt < now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, GenerationJobStatus.Queued)
                .SetProperty(item => item.QueuedAt, now)
                .SetProperty(item => item.StartedAt, (DateTime?)null)
                .SetProperty(item => item.ClaimExpiresAt, (DateTime?)null)
                .SetProperty(item => item.CancellationRequested, false)
                .SetProperty(item => item.RetryCount, item => item.RetryCount + 1)
                .SetProperty(item => item.ConcurrencyToken, recoveryToken), cancellationToken);
        if (updated == 0)
        {
            // A concurrent request with the same idempotency key may have won
            // the conditional update while this request was waiting on the row.
            // Replay its committed evidence when available; otherwise preserve
            // the existing conflict contract for a genuinely stale recovery.
            previous = await FindRecoveryAuditAsync(normalizedIdempotencyKey, jobId, cancellationToken);
            if (previous is not null)
            {
                var replay = ReplayRecovery(previous, jobId);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
            throw new AdminOperationConflictException("JOB_RECOVERY_RACE", "The job changed before recovery could be applied.");
        }

        db.AdminOperationAuditEvents.Add(new AdminOperationAuditEvent
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            Action = AdminOperationActions.RecoverExpiredGenerationJob,
            TargetType = "generation_job",
            TargetId = jobId,
            Outcome = AdminOperationOutcomes.Succeeded,
            Reason = normalizedReason,
            // RequestId is the legacy audit column used for this command's
            // idempotency key; operational tracing remains server-owned.
            RequestId = normalizedIdempotencyKey,
            BeforeState = $"status={GenerationJobStatus.Running};retryCount={source.RetryCount}",
            AfterState = $"status={GenerationJobStatus.Queued};retryCount={source.RetryCount + 1}",
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminJobRecoveryResult(jobId, GenerationJobStatus.Queued, source.RetryCount + 1, now, AdminOperationActions.RecoverExpiredGenerationJob);
    }

    private Task<AdminOperationAuditEvent?> FindRecoveryAuditAsync(
        string? idempotencyKey,
        Guid jobId,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(idempotencyKey)
            ? Task.FromResult<AdminOperationAuditEvent?>(null)
            : db.AdminOperationAuditEvents.AsNoTracking()
                .Where(item => item.Action == AdminOperationActions.RecoverExpiredGenerationJob
                    && item.TargetId == jobId
                    && item.RequestId == idempotencyKey
                    && item.Outcome == AdminOperationOutcomes.Succeeded)
                .OrderBy(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

    private static AdminJobRecoveryResult ReplayRecovery(AdminOperationAuditEvent audit, Guid jobId)
    {
        var retryCount = ParseRetryCount(audit.AfterState);
        return new(jobId, GenerationJobStatus.Queued, retryCount, audit.CreatedAt, AdminOperationActions.RecoverExpiredGenerationJob);
    }

    private static int ParseRetryCount(string? afterState)
    {
        const string prefix = "status=Queued;retryCount=";
        if (afterState?.StartsWith(prefix, StringComparison.Ordinal) != true
            || !int.TryParse(afterState[prefix.Length..], out var retryCount)
            || retryCount < 0)
            throw new AdminOperationConflictException("RECOVERY_RESULT_INVALID", "The prior recovery result could not be replayed safely.");
        return retryCount;
    }

    private async Task<AdminGenerationOverviewDto> BuildGenerationAsync((DateTime FromUtc, DateTime ToUtc) range, CancellationToken cancellationToken)
    {
        var jobs = db.GenerationJobs.AsNoTracking();
        var inRange = jobs.Where(item => item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc);
        var byStatus = await inRange
            .GroupBy(item => item.Status)
            .Select(group => new AdminCountBreakdownDto(group.Key.ToString(), group.Count()))
            .ToArrayAsync(cancellationToken);
        var byStudio = await inRange
            .GroupBy(item => item.JobType)
            .Select(group => new AdminCountBreakdownDto(group.Key, group.Count()))
            .ToArrayAsync(cancellationToken);
        var recentFailures = await jobs
            .Where(item => item.Status == GenerationJobStatus.Failed)
            .OrderByDescending(item => item.FailedAt ?? item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(RecentItemLimit)
            .Select(item => new AdminRecentFailureDto(item.Id, item.JobType, SanitizeCode(item.ErrorCode), item.FailedAt ?? item.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var failuresByCode = (await inRange
            .Where(item => item.Status == GenerationJobStatus.Failed)
            .GroupBy(item => item.ErrorCode)
            .Select(group => new { ErrorCode = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken))
            .Select(item => new AdminCountBreakdownDto(SanitizeCode(item.ErrorCode), item.Count))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => new AdminCountBreakdownDto(group.Key, group.Sum(item => item.Count)))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Key)
            .ToArray();
        var now = DateTime.UtcNow;
        var longRunningSince = now.Subtract(LongRunningThreshold);
        var runningRows = await jobs
            .Where(item => item.Status == GenerationJobStatus.Running)
            .OrderBy(item => item.StartedAt ?? item.QueuedAt ?? item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(RecentItemLimit)
            .Select(item => new
            {
                item.Id,
                item.JobType,
                item.ProgressPercent,
                item.QueuedAt,
                item.StartedAt,
                item.CreatedAt,
                item.RetryCount,
                item.ClaimExpiresAt,
            })
            .ToArrayAsync(cancellationToken);
        var runningJobs = runningRows.Select(item => new AdminRunningJobDto(
            item.Id,
            item.JobType,
            item.ProgressPercent,
            item.QueuedAt,
            item.StartedAt,
            item.CreatedAt,
            item.RetryCount,
            item.ClaimExpiresAt,
            (item.StartedAt ?? item.QueuedAt ?? item.CreatedAt) <= longRunningSince,
            item.ClaimExpiresAt.HasValue && item.ClaimExpiresAt.Value < now)).ToArray();
        var queuedOrPendingCount = await jobs.CountAsync(item => item.Status == GenerationJobStatus.Queued || item.Status == GenerationJobStatus.Pending, cancellationToken);
        var longRunningCount = await jobs.CountAsync(item => item.Status == GenerationJobStatus.Running && (item.StartedAt ?? item.QueuedAt ?? item.CreatedAt) <= longRunningSince, cancellationToken);
        var totalRetryCount = await inRange.SumAsync(item => item.RetryCount, cancellationToken);
        var totalJobsInRange = await inRange.CountAsync(cancellationToken);

        return new AdminGenerationOverviewDto(
            totalJobsInRange,
            byStatus.OrderBy(item => item.Key).ToArray(),
            byStudio.OrderByDescending(item => item.Count).ThenBy(item => item.Key).ToArray(),
            recentFailures,
            failuresByCode,
            runningJobs,
            queuedOrPendingCount,
            longRunningCount,
            totalRetryCount);
    }

    private async Task<AdminMovieOperationsDto> BuildMovieAsync(
        (DateTime FromUtc, DateTime ToUtc) range,
        IReadOnlyList<AdminProviderHealthDto> providers,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var movieJobsInRange = await db.GenerationJobs.AsNoTracking()
            .Where(item => MovieJobTypes.Contains(item.JobType)
                && item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc)
            .Select(item => new
            {
                item.Id,
                item.JobType,
                item.Status,
                item.RetryCount,
                item.ErrorCode,
                item.CreatedAt,
                item.QueuedAt,
                item.StartedAt,
                item.CompletedAt,
                item.ClaimExpiresAt,
            })
            .ToArrayAsync(cancellationToken);
        var queued = await db.GenerationJobs.AsNoTracking()
            .Where(item => MovieJobTypes.Contains(item.JobType)
                && (item.Status == GenerationJobStatus.Queued || item.Status == GenerationJobStatus.Pending))
            .Select(item => new { item.QueuedAt, item.CreatedAt })
            .ToArrayAsync(cancellationToken);
        var queueTimes = queued.Select(item => item.QueuedAt ?? item.CreatedAt).ToArray();
        DateTime? oldestQueuedAt = queueTimes.Length == 0 ? null : queueTimes.Min();
        var queueAges = queueTimes.Select(item => Math.Max(0d, (now - item).TotalSeconds)).ToArray();
        var stuckJobs = await db.GenerationJobs.AsNoTracking()
            .Where(item => MovieJobTypes.Contains(item.JobType)
                && item.Status == GenerationJobStatus.Running
                && item.ClaimExpiresAt.HasValue
                && item.ClaimExpiresAt < now)
            .OrderBy(item => item.ClaimExpiresAt)
            .ThenBy(item => item.Id)
            .Take(RecentItemLimit)
            .Select(item => new AdminStuckMovieJobDto(item.Id, item.JobType, item.Status, item.ClaimExpiresAt, item.StartedAt, item.RetryCount, SanitizeCode(item.ErrorCode)))
            .ToArrayAsync(cancellationToken);

        var movieJobIds = movieJobsInRange.Select(item => item.Id).ToArray();
        var movieAssets = movieJobIds.Length == 0
            ? []
            : await db.Assets.AsNoTracking()
                .Where(item => item.AssetType == AssetTypes.Video && item.SourceGenerationJobId.HasValue && movieJobIds.Contains(item.SourceGenerationJobId.Value))
                .Select(item => new { item.StoredFileId })
                .ToArrayAsync(cancellationToken);
        var movieUsage = await db.UsageTransactions.AsNoTracking()
            .Where(item => item.Feature == UsageFeature.Movie && item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc)
            .Select(item => new { item.GenerationJobId, item.Status, item.EstimatedProviderCostUsd, item.ProviderCostUsd })
            .ToArrayAsync(cancellationToken);
        var accountingJobIds = movieUsage.Where(item => item.GenerationJobId.HasValue).Select(item => item.GenerationJobId!.Value).ToHashSet();
        var completedMovieJobsWithoutAsset = movieJobsInRange.Count(item => item.Status == GenerationJobStatus.Succeeded
            && !movieAssets.Any(asset => false));
        if (movieJobIds.Length > 0)
        {
            var assetJobIds = await db.Assets.AsNoTracking()
                .Where(item => item.AssetType == AssetTypes.Video && item.SourceGenerationJobId.HasValue && movieJobIds.Contains(item.SourceGenerationJobId.Value))
                .Select(item => item.SourceGenerationJobId!.Value)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var assetJobIdSet = assetJobIds.ToHashSet();
            completedMovieJobsWithoutAsset = movieJobsInRange.Count(item => item.Status == GenerationJobStatus.Succeeded && !assetJobIdSet.Contains(item.Id));
        }
        var missingAccountingEvidence = movieJobsInRange.Count(item => item.Status is GenerationJobStatus.Succeeded or GenerationJobStatus.Failed
            && !accountingJobIds.Contains(item.Id));

        var qcByStatus = await db.MovieFinalMasters.AsNoTracking()
            .Where(item => item.RequestedAt >= range.FromUtc && item.RequestedAt < range.ToUtc)
            .GroupBy(item => item.QcStatus)
            .Select(group => new AdminCountBreakdownDto(group.Key, group.Count()))
            .ToArrayAsync(cancellationToken);
        var reviewRequiredTakes = await db.MovieTakes.AsNoTracking()
            .CountAsync(item => item.Status == MovieTakeStatuses.ReviewRequired && item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc, cancellationToken);
        var qcBreakdown = qcByStatus.ToList();
        if (reviewRequiredTakes > 0) qcBreakdown.Add(new AdminCountBreakdownDto(MovieTakeStatuses.ReviewRequired, reviewRequiredTakes));

        var movieProvider = providers.FirstOrDefault(item => string.Equals(item.Category, "movie", StringComparison.OrdinalIgnoreCase));
        var failureCodes = movieJobsInRange
            .Where(item => item.Status == GenerationJobStatus.Failed)
            .GroupBy(item => SanitizeCode(item.ErrorCode))
            .Select(group => new AdminCountBreakdownDto(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Key)
            .ToArray();
        var qcFailureCount = movieJobsInRange.Count(item => item.Status == GenerationJobStatus.Failed && IsQualityControlCode(item.ErrorCode));
        var providerDisabledFailureCount = movieJobsInRange.Count(item => string.Equals(item.ErrorCode, GenerationJobErrorCodes.MovieProviderUnavailable, StringComparison.Ordinal));

        return new AdminMovieOperationsDto(
            movieJobsInRange.Length,
            movieJobsInRange.GroupBy(item => item.Status.ToString()).Select(group => new AdminCountBreakdownDto(group.Key, group.Count())).OrderBy(item => item.Key).ToArray(),
            queued.Length,
            oldestQueuedAt,
            queueAges.Length == 0 ? null : queueAges.Max(),
            queueAges.Length == 0 ? null : queueAges.Average(),
            movieJobsInRange.Sum(item => item.RetryCount),
            movieJobsInRange.Count(item => item.RetryCount > 0),
            movieJobsInRange.Length == 0 ? 0 : movieJobsInRange.Max(item => item.RetryCount),
            failureCodes,
            providerDisabledFailureCount,
            qcFailureCount,
            qcBreakdown.OrderBy(item => item.Key).ToArray(),
            movieAssets.Length,
            movieAssets.Count(item => item.StoredFileId.HasValue),
            movieAssets.Count(item => !item.StoredFileId.HasValue),
            completedMovieJobsWithoutAsset,
            movieUsage.Length,
            movieUsage.Count(item => item.Status == UsageTransactionStatus.Pending),
            missingAccountingEvidence,
            movieUsage.Sum(item => item.EstimatedProviderCostUsd ?? 0m),
            movieUsage.Sum(item => item.ProviderCostUsd),
            movieProvider?.Status ?? "disabled",
            movieProvider?.Enabled ?? false,
            movieProvider?.Configured ?? false,
            stuckJobs.Length,
            stuckJobs);
    }

    private async Task<AdminWorkerOperationsDto> BuildWorkersAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var staleAfter = TimeSpan.FromSeconds(Math.Clamp(generationOptions.Value.HeartbeatStaleAfterSeconds, 30, 3_600));
        var rows = await db.GenerationWorkerHeartbeats.AsNoTracking()
            .OrderByDescending(item => item.LastSeenAt)
            .Take(100)
            .ToArrayAsync(cancellationToken);
        var workers = rows.Select(item => new AdminWorkerStatusDto(
            item.WorkerId,
            item.Status,
            item.StartedAt,
            item.LastSeenAt,
            item.LastClaimedAt,
            item.LastCompletedAt,
            item.ActiveJobId,
            item.ConsecutiveIterationFailures,
            item.WorkerConcurrency,
            item.Status != GenerationWorkerStatuses.Running || now - item.LastSeenAt > staleAfter)).ToArray();
        return new AdminWorkerOperationsDto(
            Math.Max(1, generationOptions.Value.WorkerConcurrency),
            workers.Length,
            workers.Count(item => !item.IsStale),
            workers.Count(item => item.IsStale),
            workers);
    }

    private async Task<AdminUsageOperationsDto> BuildUsageAsync((DateTime FromUtc, DateTime ToUtc) range, CancellationToken cancellationToken)
    {
        var usage = db.UsageTransactions.AsNoTracking()
            .Where(item => item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc);
        var summary = await usage.GroupBy(_ => 1).Select(group => new
        {
            RequestCount = group.Count(),
            CompletedRequestCount = group.Count(item => item.Status == UsageTransactionStatus.Completed),
            FailedRequestCount = group.Count(item => item.Status == UsageTransactionStatus.Failed),
            PendingRequestCount = group.Count(item => item.Status == UsageTransactionStatus.Pending),
            InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)),
            CachedInputTokens = group.Sum(item => (long)(item.CachedInputTokens ?? 0)),
            OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)),
            ImageInputTokens = group.Sum(item => (long)(item.ImageInputTokens ?? 0)),
            ImageOutputTokens = group.Sum(item => (long)(item.ImageOutputTokens ?? 0)),
            ProviderCostUsd = group.Sum(item => (double)item.ProviderCostUsd),
            CustomerChargesUsd = group.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
            PendingEstimatedProviderCostUsd = group.Sum(item => item.Status == UsageTransactionStatus.Pending ? (double)(item.EstimatedProviderCostUsd ?? 0m) : 0d),
        }).SingleOrDefaultAsync(cancellationToken);
        var features = await usage.GroupBy(item => item.Feature).Select(group => new
        {
            Feature = group.Key,
            RequestCount = group.Count(),
            CompletedRequestCount = group.Count(item => item.Status == UsageTransactionStatus.Completed),
            FailedRequestCount = group.Count(item => item.Status == UsageTransactionStatus.Failed),
            PendingRequestCount = group.Count(item => item.Status == UsageTransactionStatus.Pending),
            InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)),
            CachedInputTokens = group.Sum(item => (long)(item.CachedInputTokens ?? 0)),
            OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)),
            ImageInputTokens = group.Sum(item => (long)(item.ImageInputTokens ?? 0)),
            ImageOutputTokens = group.Sum(item => (long)(item.ImageOutputTokens ?? 0)),
            ProviderCostUsd = group.Sum(item => (double)item.ProviderCostUsd),
            CustomerChargesUsd = group.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
        }).ToArrayAsync(cancellationToken);

        return new AdminUsageOperationsDto(
            summary?.RequestCount ?? 0,
            summary?.CompletedRequestCount ?? 0,
            summary?.FailedRequestCount ?? 0,
            summary?.PendingRequestCount ?? 0,
            summary?.InputTokens ?? 0,
            summary?.CachedInputTokens ?? 0,
            summary?.OutputTokens ?? 0,
            summary?.ImageInputTokens ?? 0,
            summary?.ImageOutputTokens ?? 0,
            (decimal)(summary?.ProviderCostUsd ?? 0d),
            (decimal)(summary?.CustomerChargesUsd ?? 0d),
            (decimal)(summary?.PendingEstimatedProviderCostUsd ?? 0d),
            features.Select(item => new AdminUsageFeatureOperationsDto(
                item.Feature.ToString(), item.RequestCount, item.CompletedRequestCount, item.FailedRequestCount, item.PendingRequestCount,
                item.InputTokens, item.CachedInputTokens, item.OutputTokens, item.ImageInputTokens, item.ImageOutputTokens,
                (decimal)item.ProviderCostUsd, (decimal)item.CustomerChargesUsd))
                .OrderByDescending(item => item.RequestCount)
                .ThenBy(item => item.Feature)
                .ToArray());
    }

    private async Task<AdminUsersAndWorkspacesDto> BuildUsersAndWorkspacesAsync(CancellationToken cancellationToken)
    {
        var users = db.Users.AsNoTracking();
        var workspaces = db.Workspaces.AsNoTracking();
        return new AdminUsersAndWorkspacesDto(
            await users.CountAsync(cancellationToken),
            await users.CountAsync(item => item.IsActive, cancellationToken),
            await users.CountAsync(item => !item.IsActive, cancellationToken),
            await workspaces.CountAsync(cancellationToken),
            await workspaces.CountAsync(item => item.Type == WorkspaceType.Personal, cancellationToken),
            await workspaces.CountAsync(item => item.Type == WorkspaceType.Business, cancellationToken),
            await workspaces.CountAsync(item => item.IsArchived, cancellationToken));
    }

    private async Task<AdminAssetsAndStorageDto> BuildAssetsAndStorageAsync((DateTime FromUtc, DateTime ToUtc) range, CancellationToken cancellationToken)
    {
        var assets = db.Assets.AsNoTracking();
        var files = db.StoredFiles.AsNoTracking();
        var assetsByType = await assets.Where(item => item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc)
            .GroupBy(item => item.AssetType).Select(group => new AdminCountBreakdownDto(group.Key, group.Count())).ToArrayAsync(cancellationToken);
        var filesByStatus = await files.GroupBy(item => item.Status).Select(group => new AdminCountBreakdownDto(group.Key.ToString(), group.Count())).ToArrayAsync(cancellationToken);
        var filesByProvider = await files.GroupBy(item => item.StorageProvider).Select(group => new AdminCountBreakdownDto(group.Key, group.Count())).ToArrayAsync(cancellationToken);
        var filesByExtraction = await files.GroupBy(item => item.TextExtractionStatus).Select(group => new AdminCountBreakdownDto(group.Key.ToString(), group.Count())).ToArrayAsync(cancellationToken);
        var options = fileOptions.Value;

        return new AdminAssetsAndStorageDto(
            await assets.CountAsync(cancellationToken),
            assetsByType.OrderByDescending(item => item.Count).ThenBy(item => item.Key).ToArray(),
            await files.CountAsync(cancellationToken),
            await files.SumAsync(item => (long?)item.SizeBytes, cancellationToken) ?? 0,
            filesByStatus.OrderBy(item => item.Key).ToArray(),
            filesByProvider.OrderBy(item => item.Key).ToArray(),
            filesByExtraction.OrderBy(item => item.Key).ToArray(),
            await files.CountAsync(item => item.Status == StoredFileStatus.Failed && item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc, cancellationToken),
            await files.CountAsync(item => item.TextExtractionStatus == FileExtractionStatus.Failed && item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc, cancellationToken),
            options.StorageProvider,
            string.Equals(options.StorageProvider, FileStorageProviders.Local, StringComparison.OrdinalIgnoreCase) || options.IsS3Configured);
    }

    private async Task<AdminBillingOperationsDto> BuildBillingAsync((DateTime FromUtc, DateTime ToUtc) range, CancellationToken cancellationToken)
    {
        var subscriptions = await (from subscription in db.Subscriptions.AsNoTracking()
                                    join plan in db.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
                                    group subscription by new { plan.Code, subscription.Status } into grouped
                                    select new AdminSubscriptionBreakdownDto(grouped.Key.Code, grouped.Key.Status.ToString(), grouped.Count()))
            .ToArrayAsync(cancellationToken);
        var attempts = await db.PaymentAttempts.AsNoTracking().GroupBy(item => item.Status).Select(group => new AdminCountBreakdownDto(group.Key.ToString(), group.Count())).ToArrayAsync(cancellationToken);
        var events = await db.PaymentEvents.AsNoTracking().Where(item => item.ReceivedAt >= range.FromUtc && item.ReceivedAt < range.ToUtc).GroupBy(item => item.Status).Select(group => new AdminCountBreakdownDto(group.Key.ToString(), group.Count())).ToArrayAsync(cancellationToken);
        var options = billingOptions.Value;
        var providerConfigured = options.CustomerChargingEnabled && !string.Equals(options.Provider, "unconfigured", StringComparison.OrdinalIgnoreCase);

        return new AdminBillingOperationsDto(
            options.CustomerChargingEnabled,
            options.Provider,
            providerConfigured,
            subscriptions.OrderBy(item => item.PlanCode).ThenBy(item => item.Status).ToArray(),
            attempts.OrderBy(item => item.Key).ToArray(),
            events.OrderBy(item => item.Key).ToArray(),
            await db.PaymentReconciliationRecords.AsNoTracking().CountAsync(item => item.Status == ReconciliationStatus.Pending || item.Status == ReconciliationStatus.Mismatch, cancellationToken),
            await db.PaymentEvents.AsNoTracking().CountAsync(item => item.Status == PaymentEventStatus.Rejected && item.ReceivedAt >= range.FromUtc && item.ReceivedAt < range.ToUtc, cancellationToken));
    }

    private async Task<AdminOperationalSignalsDto> BuildSignalsAsync((DateTime FromUtc, DateTime ToUtc) range, AdminGenerationOverviewDto generation, CancellationToken cancellationToken)
    {
        var jobs = db.GenerationJobs.AsNoTracking();
        var usage = db.UsageTransactions.AsNoTracking();
        return new AdminOperationalSignalsDto(
            generation.RunningJobs.Count,
            generation.QueuedOrPendingCount,
            await jobs.CountAsync(item => item.Status == GenerationJobStatus.Failed && (item.FailedAt ?? item.CreatedAt) >= range.FromUtc && (item.FailedAt ?? item.CreatedAt) < range.ToUtc, cancellationToken),
            await usage.CountAsync(item => item.IsAnomalous && item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc, cancellationToken),
            await jobs.Where(item => item.Status == GenerationJobStatus.Succeeded && item.CompletedAt.HasValue).OrderByDescending(item => item.CompletedAt).Select(item => item.CompletedAt).FirstOrDefaultAsync(cancellationToken));
    }

    private static string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new AdminOperationConflictException("RECOVERY_REASON_REQUIRED", "A recovery reason is required.");
        var normalized = reason.Trim();
        if (normalized.Length > 500) throw new AdminOperationConflictException("RECOVERY_REASON_TOO_LONG", "The recovery reason is too long.");
        return normalized;
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey) =>
        string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim()[..Math.Min(128, idempotencyKey.Trim().Length)];

    private static string SanitizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "GENERATION_FAILURE";
        var safe = new string(code.Trim().Take(96).Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "GENERATION_FAILURE" : safe;
    }

    private static bool IsQualityControlCode(string? code) =>
        code?.Contains("OUTPUT_INVALID", StringComparison.OrdinalIgnoreCase) == true
        || code?.Contains("QUALITY", StringComparison.OrdinalIgnoreCase) == true
        || code?.Contains("QC", StringComparison.OrdinalIgnoreCase) == true;

    private static (DateTime FromUtc, DateTime ToUtc) NormalizeRange(DateTime? fromUtc, DateTime? toUtc)
    {
        var now = DateTime.UtcNow;
        var to = toUtc.HasValue ? DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc).Date.AddDays(1) : now;
        var from = fromUtc.HasValue ? DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc).Date : to.Date.AddDays(-29);
        if (to <= from) to = from.AddDays(1);
        if (to - from > TimeSpan.FromDays(366)) from = to.AddDays(-366);
        return (from, to);
    }
}
