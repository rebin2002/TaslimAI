using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using FileSettings = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Operations;

public interface IAdminOperationsService
{
    Task<AdminOperationsDashboardDto> GetDashboardAsync(AdminOperationsFilter filter, CancellationToken cancellationToken = default);
}

public sealed class AdminOperationsService(
    TaslimDbContext db,
    IOptions<BillingOptions> billingOptions,
    IOptions<FileSettings> fileOptions) : IAdminOperationsService
{
    private const int RecentItemLimit = 20;

    public async Task<AdminOperationsDashboardDto> GetDashboardAsync(AdminOperationsFilter filter, CancellationToken cancellationToken = default)
    {
        var range = NormalizeRange(filter.FromUtc, filter.ToUtc);
        var generation = await BuildGenerationAsync(range, cancellationToken);
        var usage = await BuildUsageAsync(range, cancellationToken);
        var usersAndWorkspaces = await BuildUsersAndWorkspacesAsync(cancellationToken);
        var assetsAndStorage = await BuildAssetsAndStorageAsync(range, cancellationToken);
        var billing = await BuildBillingAsync(cancellationToken);
        var signals = await BuildSignalsAsync(range, generation, cancellationToken);

        return new AdminOperationsDashboardDto(
            new AdminOperationsRangeDto(range.FromUtc, range.ToUtc),
            generation,
            usage,
            usersAndWorkspaces,
            assetsAndStorage,
            billing,
            signals);
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
            .Select(item => new AdminRecentFailureDto(item.Id, item.JobType, item.ErrorCode, item.FailedAt ?? item.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var runningJobs = await jobs
            .Where(item => item.Status == GenerationJobStatus.Running)
            .OrderBy(item => item.StartedAt ?? item.QueuedAt ?? item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(RecentItemLimit)
            .Select(item => new AdminRunningJobDto(item.Id, item.JobType, item.ProgressPercent, item.QueuedAt, item.StartedAt, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var queuedOrPendingCount = await jobs.CountAsync(item => item.Status == GenerationJobStatus.Queued || item.Status == GenerationJobStatus.Pending, cancellationToken);
        var totalJobsInRange = await inRange.CountAsync(cancellationToken);

        return new AdminGenerationOverviewDto(
            totalJobsInRange,
            byStatus.OrderBy(item => item.Key).ToArray(),
            byStudio.OrderByDescending(item => item.Count).ThenBy(item => item.Key).ToArray(),
            recentFailures,
            runningJobs,
            queuedOrPendingCount);
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
            CustomerChargesUsd = group.Sum(item => (double)item.ChargedAmount),
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
            CustomerChargesUsd = group.Sum(item => (double)item.ChargedAmount),
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
                item.Feature.ToString(),
                item.RequestCount,
                item.CompletedRequestCount,
                item.FailedRequestCount,
                item.PendingRequestCount,
                item.InputTokens,
                item.CachedInputTokens,
                item.OutputTokens,
                item.ImageInputTokens,
                item.ImageOutputTokens,
                (decimal)item.ProviderCostUsd,
                (decimal)item.CustomerChargesUsd))
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
        var assetsByType = await assets
            .Where(item => item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc)
            .GroupBy(item => item.AssetType)
            .Select(group => new AdminCountBreakdownDto(group.Key, group.Count()))
            .ToArrayAsync(cancellationToken);
        var filesByStatus = await files.GroupBy(item => item.Status)
            .Select(group => new AdminCountBreakdownDto(group.Key.ToString(), group.Count()))
            .ToArrayAsync(cancellationToken);
        var filesByProvider = await files.GroupBy(item => item.StorageProvider)
            .Select(group => new AdminCountBreakdownDto(group.Key, group.Count()))
            .ToArrayAsync(cancellationToken);
        var options = fileOptions.Value;

        return new AdminAssetsAndStorageDto(
            await assets.CountAsync(cancellationToken),
            assetsByType.OrderByDescending(item => item.Count).ThenBy(item => item.Key).ToArray(),
            await files.CountAsync(cancellationToken),
            await files.SumAsync(item => (long?)item.SizeBytes, cancellationToken) ?? 0,
            filesByStatus.OrderBy(item => item.Key).ToArray(),
            filesByProvider.OrderBy(item => item.Key).ToArray(),
            options.StorageProvider,
            string.Equals(options.StorageProvider, FileStorageProviders.Local, StringComparison.OrdinalIgnoreCase) || options.IsS3Configured);
    }

    private async Task<AdminBillingOperationsDto> BuildBillingAsync(CancellationToken cancellationToken)
    {
        var subscriptions = await (from subscription in db.Subscriptions.AsNoTracking()
                                   join plan in db.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
                                   group subscription by new { plan.Code, subscription.Status } into grouped
                                   select new AdminSubscriptionBreakdownDto(grouped.Key.Code, grouped.Key.Status.ToString(), grouped.Count()))
            .ToArrayAsync(cancellationToken);
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new AdminCountBreakdownDto(group.Key.ToString(), group.Count()))
            .ToArrayAsync(cancellationToken);
        var options = billingOptions.Value;
        var providerConfigured = options.CustomerChargingEnabled && !string.Equals(options.Provider, "unconfigured", StringComparison.OrdinalIgnoreCase);

        return new AdminBillingOperationsDto(
            options.CustomerChargingEnabled,
            options.Provider,
            providerConfigured,
            subscriptions.OrderBy(item => item.PlanCode).ThenBy(item => item.Status).ToArray(),
            attempts.OrderBy(item => item.Key).ToArray(),
            await db.PaymentReconciliationRecords.AsNoTracking().CountAsync(item => item.Status == ReconciliationStatus.Pending || item.Status == ReconciliationStatus.Mismatch, cancellationToken));
    }

    private async Task<AdminOperationalSignalsDto> BuildSignalsAsync(
        (DateTime FromUtc, DateTime ToUtc) range,
        AdminGenerationOverviewDto generation,
        CancellationToken cancellationToken)
    {
        var jobs = db.GenerationJobs.AsNoTracking();
        var usage = db.UsageTransactions.AsNoTracking();
        return new AdminOperationalSignalsDto(
            generation.RunningJobs.Count,
            generation.QueuedOrPendingCount,
            await jobs.CountAsync(item => item.Status == GenerationJobStatus.Failed && (item.FailedAt ?? item.CreatedAt) >= range.FromUtc && (item.FailedAt ?? item.CreatedAt) < range.ToUtc, cancellationToken),
            await usage.CountAsync(item => item.IsAnomalous && item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc, cancellationToken),
            await jobs.Where(item => item.Status == GenerationJobStatus.Succeeded && item.CompletedAt.HasValue)
                .OrderByDescending(item => item.CompletedAt)
                .Select(item => item.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken));
    }

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
