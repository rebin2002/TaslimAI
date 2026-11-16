using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

public interface IAdminUsageService
{
    Task<AdminUsageReportDto> GetReportAsync(AdminUsageFilter filter, CancellationToken cancellationToken = default);
    Task<AdminUsageTransactionDto?> GetTransactionAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class AdminUsageService(TaslimDbContext db) : IAdminUsageService
{
    // The usage ledger maps every currency column on UsageTransaction as decimal(18, 8),
    // so the exact sum of any set of stored rows is always representable at eight decimal
    // places. SQLite cannot translate SUM(decimal), so the aggregates below accumulate
    // through the portable double type and cast the result back; that round trip leaks
    // binary rounding noise into the response (64 stored rows of 0.12345679 reported
    // 7.90123455999999 instead of 7.90123456). Rounding an aggregate back to the stored
    // scale is lossless for a correct total and removes only the binary artifact. This is
    // the same guard UsageController applies to the customer charge total, so the admin
    // report and the customer report can no longer disagree about the same rows.
    // It normalises the reported projection only: no stored amount, debit or credit is
    // recalculated here.
    private const int LedgerScale = 8;

    // Breakdown ranks are re-read by operators and rendered into exports, so a tied row
    // order has to be identical on every host and in every request. The default string
    // comparer is culture-sensitive, which makes tie order depend on the server culture;
    // every name comparison in this report is therefore ordinal.
    private static readonly StringComparer Ordinal = StringComparer.Ordinal;

    // The workspace and user ranks are reported as a truncated top list, so their ties
    // decide which members are reported at all.
    private const int ReportRankLimit = 20;

    private static decimal LedgerAmount(double amount) => decimal.Round((decimal)amount, LedgerScale);

    public async Task<AdminUsageReportDto> GetReportAsync(AdminUsageFilter filter, CancellationToken cancellationToken = default)
    {
        var range = NormalizeRange(filter.FromUtc, filter.ToUtc);
        var query = ApplyFilters(db.UsageTransactions.AsNoTracking(), filter, range);
        var summary = await BuildSummaryAsync(query, range, cancellationToken);
        var breakdowns = await BuildBreakdownsAsync(query, cancellationToken);
        var transactions = await BuildTransactionsAsync(query, filter.Page, filter.PageSize, cancellationToken);
        return new AdminUsageReportDto(summary, breakdowns, transactions);
    }

    public async Task<AdminUsageTransactionDto?> GetTransactionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await (from transaction in db.UsageTransactions.AsNoTracking()
                         join workspace in db.Workspaces.AsNoTracking() on transaction.WorkspaceId equals workspace.Id
                         join user in db.Users.AsNoTracking() on transaction.UserId equals user.Id
                         where transaction.Id == id
                         select new { transaction, WorkspaceName = workspace.Name, UserEmail = user.Email, UserDisplayName = user.DisplayName })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ProjectTransaction(row.transaction, row.WorkspaceName, row.UserEmail, row.UserDisplayName);
    }

    private async Task<AdminUsageSummaryDto> BuildSummaryAsync(
        IQueryable<UsageTransaction> query,
        (DateTime FromUtc, DateTime ToUtc) range,
        CancellationToken cancellationToken)
    {
        var aggregate = await query.GroupBy(_ => 1).Select(group => new
        {
            TransactionCount = group.Count(),
            SuccessfulCount = group.Count(item => item.Status == UsageTransactionStatus.Completed),
            FailedCount = group.Count(item => item.Status == UsageTransactionStatus.Failed),
            CancelledCount = group.Count(item => item.Status == UsageTransactionStatus.Cancelled),
            RefundedCount = group.Count(item => item.Status == UsageTransactionStatus.Refunded),
            TotalProviderCostUsd = group.Sum(item => (double)item.ProviderCostUsd),
            TotalCustomerChargesUsd = group.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
            PendingEstimatedProviderCostUsd = group.Sum(item => item.Status == UsageTransactionStatus.Pending ? (double)(item.EstimatedProviderCostUsd ?? 0m) : 0d),
            AnomalousCount = group.Count(item => item.IsAnomalous),
        }).SingleOrDefaultAsync(cancellationToken);

        return new AdminUsageSummaryDto(
            range.FromUtc,
            range.ToUtc,
            aggregate?.TransactionCount ?? 0,
            aggregate?.SuccessfulCount ?? 0,
            aggregate?.FailedCount ?? 0,
            aggregate?.CancelledCount ?? 0,
            aggregate?.RefundedCount ?? 0,
            LedgerAmount(aggregate?.TotalProviderCostUsd ?? 0d),
            LedgerAmount(aggregate?.TotalCustomerChargesUsd ?? 0d),
            LedgerAmount(aggregate?.PendingEstimatedProviderCostUsd ?? 0d),
            aggregate?.AnomalousCount ?? 0,
            UsageCurrencies.Usd);
    }

    private async Task<AdminUsageBreakdownsDto> BuildBreakdownsAsync(IQueryable<UsageTransaction> query, CancellationToken cancellationToken)
    {
        var featureRows = await query.GroupBy(item => item.Feature).Select(group => new
        {
Feature = group.Key,
            TransactionCount = group.Count(),
            SuccessfulCount = group.Count(item => item.Status == UsageTransactionStatus.Completed),
            FailedCount = group.Count(item => item.Status == UsageTransactionStatus.Failed),
            CancelledCount = group.Count(item => item.Status == UsageTransactionStatus.Cancelled),
            ProviderCostUsd = group.Sum(item => (double)item.ProviderCostUsd),
            CustomerChargesUsd = group.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
        }).ToListAsync(cancellationToken);
        var byFeature = featureRows.Select(item => new AdminUsageFeatureBreakdownDto(item.Feature.ToString(), item.TransactionCount, item.SuccessfulCount, item.FailedCount, item.CancelledCount, LedgerAmount(item.ProviderCostUsd), LedgerAmount(item.CustomerChargesUsd))).OrderByDescending(item => item.ProviderCostUsd).ThenBy(item => item.Feature, Ordinal).ToArray();

        var dayRows = await query.GroupBy(item => item.CreatedAt.Date).Select(group => new
        {
DayUtc = group.Key,
            TransactionCount = group.Count(),
            ProviderCostUsd = group.Sum(item => (double)item.ProviderCostUsd),
            CustomerChargesUsd = group.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
        }).ToListAsync(cancellationToken);
        var byDay = dayRows.Select(item => new AdminUsageDailyBreakdownDto(item.DayUtc, item.TransactionCount, LedgerAmount(item.ProviderCostUsd), LedgerAmount(item.CustomerChargesUsd))).OrderBy(item => item.DayUtc).ToArray();

        var workspaceRows = await (from transaction in query
                                   join workspace in db.Workspaces.AsNoTracking() on transaction.WorkspaceId equals workspace.Id
                                   group transaction by new { transaction.WorkspaceId, workspace.Name } into grouped
                                   select new
                                   {
                                       grouped.Key.WorkspaceId,
                                       WorkspaceName = grouped.Key.Name,
                                       TransactionCount = grouped.Count(),
                                       ProviderCostUsd = grouped.Sum(item => (double)item.ProviderCostUsd),
                                       CustomerChargesUsd = grouped.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
                                   }).ToListAsync(cancellationToken);
        // The rank list is truncated, so ties must be broken before the cut: without the
        // ordinal name and id tie-breakers the database decides which workspaces survive
        // Take(20), and two identical requests can report different members.
        var byWorkspace = workspaceRows.Select(item => new AdminUsageWorkspaceBreakdownDto(item.WorkspaceId, item.WorkspaceName, item.TransactionCount, LedgerAmount(item.ProviderCostUsd), LedgerAmount(item.CustomerChargesUsd))).OrderByDescending(item => item.ProviderCostUsd).ThenBy(item => item.WorkspaceName, Ordinal).ThenBy(item => item.WorkspaceId).Take(ReportRankLimit).ToArray();

        var userRows = await (from transaction in query
                              join user in db.Users.AsNoTracking() on transaction.UserId equals user.Id
                              group transaction by new { transaction.UserId, user.Email, user.DisplayName } into grouped
                              select new
                              {
                                  grouped.Key.UserId,
                                  grouped.Key.Email,
                                  grouped.Key.DisplayName,
                                  TransactionCount = grouped.Count(),
                                  ProviderCostUsd = grouped.Sum(item => (double)item.ProviderCostUsd),
                                  CustomerChargesUsd = grouped.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
                              }).ToListAsync(cancellationToken);
        var byUser = userRows.Select(item => new AdminUsageUserBreakdownDto(item.UserId, item.Email, item.DisplayName, item.TransactionCount, LedgerAmount(item.ProviderCostUsd), LedgerAmount(item.CustomerChargesUsd))).OrderByDescending(item => item.ProviderCostUsd).ThenBy(item => item.DisplayName, Ordinal).ThenBy(item => item.Email ?? string.Empty, Ordinal).ThenBy(item => item.UserId).Take(ReportRankLimit).ToArray();

        var statusRows = await query.GroupBy(item => item.Status).Select(group => new
        {
            Status = group.Key,
            TransactionCount = group.Count(),
            SuccessfulCount = group.Count(item => item.Status == UsageTransactionStatus.Completed),
            FailedCount = group.Count(item => item.Status == UsageTransactionStatus.Failed),
            CancelledCount = group.Count(item => item.Status == UsageTransactionStatus.Cancelled),
            ProviderCostUsd = group.Sum(item => (double)item.ProviderCostUsd),
            CustomerChargesUsd = group.Sum(item => (double)(item.ChargedAmount - item.ReversedAmount)),
        }).ToListAsync(cancellationToken);
        var byStatus = statusRows.Select(item => new AdminUsageStatusBreakdownDto(item.Status.ToString(), item.TransactionCount, item.SuccessfulCount, item.FailedCount, item.CancelledCount, LedgerAmount(item.ProviderCostUsd), LedgerAmount(item.CustomerChargesUsd))).OrderBy(item => item.Status, Ordinal).ToArray();

        return new AdminUsageBreakdownsDto(byFeature, byDay, byWorkspace, byUser, byStatus);
    }

    private async Task<AdminUsageTransactionListDto> BuildTransactionsAsync(IQueryable<UsageTransaction> query, int requestedPage, int requestedPageSize, CancellationToken cancellationToken)
    {
        var page = ApiPagination.NormalizePage(requestedPage);
        var pageSize = ApiPagination.NormalizePageSize(requestedPageSize);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await (from transaction in query
                          join workspace in db.Workspaces.AsNoTracking() on transaction.WorkspaceId equals workspace.Id
                          join user in db.Users.AsNoTracking() on transaction.UserId equals user.Id
                          orderby transaction.CreatedAt descending, transaction.Id descending
                          select new { transaction, WorkspaceName = workspace.Name, UserEmail = user.Email, UserDisplayName = user.DisplayName })
            .Skip(ApiPagination.GetOffset(page, pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        var items = rows.Select(item => ProjectTransaction(item.transaction, item.WorkspaceName, item.UserEmail, item.UserDisplayName)).ToArray();

        return new AdminUsageTransactionListDto(items, page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    private static AdminUsageTransactionDto ProjectTransaction(UsageTransaction transaction, string workspaceName, string? userEmail, string userDisplayName) => new(
        transaction.Id,
        transaction.CreatedAt,
        transaction.CompletedAt,
        transaction.WorkspaceId,
        workspaceName,
        transaction.UserId,
        userEmail,
        userDisplayName,
        transaction.ProjectId,
        transaction.ConversationId,
        transaction.GenerationJobId,
        transaction.Feature.ToString(),
        transaction.Status.ToString(),
        transaction.Provider,
        transaction.Model,
        transaction.InputTokens,
        transaction.CachedInputTokens,
        transaction.OutputTokens,
        transaction.ImageInputTokens,
        transaction.ImageOutputTokens,
        transaction.LatencyMs,
        transaction.EstimatedProviderCostUsd,
        transaction.ProviderCostUsd,
        transaction.ProviderCostKnown,
        transaction.ChargedAmount - transaction.ReversedAmount,
        transaction.Currency,
        transaction.CostBasis,
        transaction.PricingVersion,
        transaction.PricingSnapshotJson,
        transaction.CostEstimateJson,
        transaction.SafeMetadataJson,
        transaction.FailureCode,
        transaction.IsAnomalous,
        transaction.AnomalyCode,
        transaction.RefundedAt);

    private static IQueryable<UsageTransaction> ApplyFilters(IQueryable<UsageTransaction> query, AdminUsageFilter filter, (DateTime FromUtc, DateTime ToUtc) range)
    {
        query = query.Where(item => item.CreatedAt >= range.FromUtc && item.CreatedAt < range.ToUtc);
        if (filter.Feature.HasValue) query = query.Where(item => item.Feature == filter.Feature.Value);
        if (filter.Status.HasValue) query = query.Where(item => item.Status == filter.Status.Value);
        if (filter.WorkspaceId.HasValue) query = query.Where(item => item.WorkspaceId == filter.WorkspaceId.Value);
        if (filter.UserId.HasValue) query = query.Where(item => item.UserId == filter.UserId.Value);
        if (filter.GenerationJobId.HasValue) query = query.Where(item => item.GenerationJobId == filter.GenerationJobId.Value);
        return query;
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
