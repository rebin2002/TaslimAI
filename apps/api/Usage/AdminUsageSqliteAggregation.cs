using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

/// <summary>
/// SQLite does not translate SUM(decimal) to an exact decimal aggregate. Read the filtered
/// rows as decimals instead, with ordered streaming passes so the application never buffers
/// the full ledger result or keeps an accumulator for every workspace/user. PostgreSQL uses
/// native numeric SUM in <see cref="AdminUsageService"/> and does not take this path.
/// </summary>
internal static class AdminUsageSqliteAggregation
{
    private const int ReportRankLimit = 20;
    private static readonly StringComparer Ordinal = StringComparer.Ordinal;
    private static readonly IComparer<WorkspaceRank> WorkspaceRankComparer = Comparer<WorkspaceRank>.Create(CompareWorkspaceRank);
    private static readonly IComparer<UserRank> UserRankComparer = Comparer<UserRank>.Create(CompareUserRank);

    internal static async Task<(AdminUsageSummaryDto Summary, AdminUsageBreakdownsDto Breakdowns)> BuildAsync(
        TaslimDbContext db,
        IQueryable<UsageTransaction> query,
        (DateTime FromUtc, DateTime ToUtc) range,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var summaryTotals = default(UsageTotals);
        var featureTotals = new Dictionary<UsageFeature, UsageTotals>();
        var dayTotals = new Dictionary<DateTime, UsageTotals>();
        var statusTotals = new Dictionary<UsageTransactionStatus, UsageTotals>();
        var workspaceRanks = new SortedSet<WorkspaceRank>(WorkspaceRankComparer);
        Guid? currentWorkspaceId = null;
        string? currentWorkspaceName = null;
        var currentWorkspaceTotals = default(UsageTotals);

        void CompleteWorkspace()
        {
            if (currentWorkspaceId is Guid id && currentWorkspaceName is not null)
            {
                KeepTop(workspaceRanks, new WorkspaceRank(id, currentWorkspaceName, currentWorkspaceTotals), WorkspaceRankComparer);
            }
        }

        // This first projected, no-tracking pass is ordered by workspace so a single
        // accumulator suffices for workspace totals. The report range is capped at 366 days;
        // feature/status buckets are enums, and only the top 20 workspaces are retained.
        // A left join preserves summary totals even if a legacy row has no matching workspace,
        // matching the existing summary query; such a row is excluded from the workspace list.
        var rows =
            from transaction in query
            join workspace in db.Workspaces.AsNoTracking()
                on transaction.WorkspaceId equals workspace.Id into workspaceMatches
            from workspace in workspaceMatches.DefaultIfEmpty()
            orderby transaction.WorkspaceId
            select new
            {
                transaction.WorkspaceId,
                WorkspaceName = workspace == null ? null : workspace.Name,
                transaction.Feature,
                transaction.Status,
                transaction.CreatedAt,
                transaction.ProviderCostUsd,
                transaction.ChargedAmount,
                transaction.ReversedAmount,
                transaction.EstimatedProviderCostUsd,
                transaction.IsAnomalous,
            };

        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            summaryTotals.Add(row.Status, row.ProviderCostUsd, row.ChargedAmount, row.ReversedAmount, row.EstimatedProviderCostUsd, row.IsAnomalous);
            Accumulate(featureTotals, row.Feature, row.Status, row.ProviderCostUsd, row.ChargedAmount, row.ReversedAmount, row.EstimatedProviderCostUsd, row.IsAnomalous);
            Accumulate(dayTotals, row.CreatedAt.Date, row.Status, row.ProviderCostUsd, row.ChargedAmount, row.ReversedAmount, row.EstimatedProviderCostUsd, row.IsAnomalous);
            Accumulate(statusTotals, row.Status, row.Status, row.ProviderCostUsd, row.ChargedAmount, row.ReversedAmount, row.EstimatedProviderCostUsd, row.IsAnomalous);

            if (currentWorkspaceId != row.WorkspaceId)
            {
                CompleteWorkspace();
                currentWorkspaceId = row.WorkspaceId;
                currentWorkspaceName = row.WorkspaceName;
                currentWorkspaceTotals = default;
            }

            if (row.WorkspaceName is not null)
            {
                currentWorkspaceName ??= row.WorkspaceName;
                currentWorkspaceTotals.Add(row.Status, row.ProviderCostUsd, row.ChargedAmount, row.ReversedAmount, row.EstimatedProviderCostUsd, row.IsAnomalous);
            }
        }
        CompleteWorkspace();

        // A second ordered stream makes user groups contiguous. It retains only the current
        // user plus the deterministic top-20 set; cancellation is checked throughout both
        // linear scans so large SQLite reports remain interruptible without unbounded memory.
        var userRanks = new SortedSet<UserRank>(UserRankComparer);
        Guid? currentUserId = null;
        string? currentUserEmail = null;
        string? currentUserDisplayName = null;
        var currentUserTotals = default(UsageTotals);

        void CompleteUser()
        {
            if (currentUserId is Guid id && currentUserDisplayName is not null)
            {
                KeepTop(userRanks, new UserRank(id, currentUserEmail, currentUserDisplayName, currentUserTotals), UserRankComparer);
            }
        }

        var userRows =
            from transaction in query
            join user in db.Users.AsNoTracking()
                on transaction.UserId equals user.Id
            orderby transaction.UserId
            select new
            {
                transaction.UserId,
                user.Email,
                user.DisplayName,
                transaction.Status,
                transaction.ProviderCostUsd,
                transaction.ChargedAmount,
                transaction.ReversedAmount,
                transaction.EstimatedProviderCostUsd,
                transaction.IsAnomalous,
            };

        await foreach (var row in userRows.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentUserId != row.UserId)
            {
                CompleteUser();
                currentUserId = row.UserId;
                currentUserEmail = row.Email;
                currentUserDisplayName = row.DisplayName;
                currentUserTotals = default;
            }

            currentUserTotals.Add(row.Status, row.ProviderCostUsd, row.ChargedAmount, row.ReversedAmount, row.EstimatedProviderCostUsd, row.IsAnomalous);
        }
        CompleteUser();

        var summary = new AdminUsageSummaryDto(
            range.FromUtc,
            range.ToUtc,
            summaryTotals.TransactionCount,
            summaryTotals.SuccessfulCount,
            summaryTotals.FailedCount,
            summaryTotals.CancelledCount,
            summaryTotals.RefundedCount,
            summaryTotals.ProviderCostUsd,
            summaryTotals.CustomerChargesUsd,
            summaryTotals.PendingEstimatedProviderCostUsd,
            summaryTotals.AnomalousCount,
            UsageCurrencies.Usd);

        var byFeature = featureTotals
            .Select(pair => new AdminUsageFeatureBreakdownDto(
                pair.Key.ToString(),
                pair.Value.TransactionCount,
                pair.Value.SuccessfulCount,
                pair.Value.FailedCount,
                pair.Value.CancelledCount,
                pair.Value.ProviderCostUsd,
                pair.Value.CustomerChargesUsd))
            .OrderByDescending(item => item.ProviderCostUsd)
            .ThenBy(item => item.Feature, Ordinal)
            .ToArray();

        var byDay = dayTotals
            .Select(pair => new AdminUsageDailyBreakdownDto(
                pair.Key,
                pair.Value.TransactionCount,
                pair.Value.ProviderCostUsd,
                pair.Value.CustomerChargesUsd))
            .OrderBy(item => item.DayUtc)
            .ToArray();

        var byWorkspace = workspaceRanks
            .Select(item => new AdminUsageWorkspaceBreakdownDto(
                item.WorkspaceId,
                item.WorkspaceName,
                item.Totals.TransactionCount,
                item.Totals.ProviderCostUsd,
                item.Totals.CustomerChargesUsd))
            .ToArray();

        var byUser = userRanks
            .Select(item => new AdminUsageUserBreakdownDto(
                item.UserId,
                item.Email,
                item.DisplayName,
                item.Totals.TransactionCount,
                item.Totals.ProviderCostUsd,
                item.Totals.CustomerChargesUsd))
            .ToArray();

        var byStatus = statusTotals
            .Select(pair => new AdminUsageStatusBreakdownDto(
                pair.Key.ToString(),
                pair.Value.TransactionCount,
                pair.Value.SuccessfulCount,
                pair.Value.FailedCount,
                pair.Value.CancelledCount,
                pair.Value.ProviderCostUsd,
                pair.Value.CustomerChargesUsd))
            .OrderBy(item => item.Status, Ordinal)
            .ToArray();

        return (summary, new AdminUsageBreakdownsDto(byFeature, byDay, byWorkspace, byUser, byStatus));
    }

    private static void Accumulate<TKey>(
        Dictionary<TKey, UsageTotals> groups,
        TKey key,
        UsageTransactionStatus status,
        decimal providerCostUsd,
        decimal chargedAmount,
        decimal reversedAmount,
        decimal? estimatedProviderCostUsd,
        bool isAnomalous)
        where TKey : notnull
    {
        groups.TryGetValue(key, out var totals);
        totals.Add(status, providerCostUsd, chargedAmount, reversedAmount, estimatedProviderCostUsd, isAnomalous);
        groups[key] = totals;
    }

    private static void KeepTop<T>(SortedSet<T> ranked, T candidate, IComparer<T> comparer)
        where T : class
    {
        if (ranked.Count == ReportRankLimit && comparer.Compare(candidate, ranked.Max!) >= 0)
        {
            return;
        }

        ranked.Add(candidate);
        if (ranked.Count > ReportRankLimit)
        {
            ranked.Remove(ranked.Max!);
        }
    }

    private static int CompareWorkspaceRank(WorkspaceRank left, WorkspaceRank right)
    {
        var byCost = right.Totals.ProviderCostUsd.CompareTo(left.Totals.ProviderCostUsd);
        if (byCost != 0) return byCost;
        var byName = Ordinal.Compare(left.WorkspaceName, right.WorkspaceName);
        return byName != 0 ? byName : left.WorkspaceId.CompareTo(right.WorkspaceId);
    }

    private static int CompareUserRank(UserRank left, UserRank right)
    {
        var byCost = right.Totals.ProviderCostUsd.CompareTo(left.Totals.ProviderCostUsd);
        if (byCost != 0) return byCost;
        var byDisplayName = Ordinal.Compare(left.DisplayName, right.DisplayName);
        if (byDisplayName != 0) return byDisplayName;
        var byEmail = Ordinal.Compare(left.Email ?? string.Empty, right.Email ?? string.Empty);
        return byEmail != 0 ? byEmail : left.UserId.CompareTo(right.UserId);
    }

    private sealed record WorkspaceRank(Guid WorkspaceId, string WorkspaceName, UsageTotals Totals);
    private sealed record UserRank(Guid UserId, string? Email, string DisplayName, UsageTotals Totals);

    private struct UsageTotals
    {
        internal int TransactionCount { get; private set; }
        internal int SuccessfulCount { get; private set; }
        internal int FailedCount { get; private set; }
        internal int CancelledCount { get; private set; }
        internal int RefundedCount { get; private set; }
        internal int AnomalousCount { get; private set; }
        internal decimal ProviderCostUsd { get; private set; }
        internal decimal CustomerChargesUsd { get; private set; }
        internal decimal PendingEstimatedProviderCostUsd { get; private set; }

        internal void Add(
            UsageTransactionStatus status,
            decimal providerCostUsd,
            decimal chargedAmount,
            decimal reversedAmount,
            decimal? estimatedProviderCostUsd,
            bool isAnomalous)
        {
            TransactionCount++;
            if (status == UsageTransactionStatus.Completed) SuccessfulCount++;
            if (status == UsageTransactionStatus.Failed) FailedCount++;
            if (status == UsageTransactionStatus.Cancelled) CancelledCount++;
            if (status == UsageTransactionStatus.Refunded) RefundedCount++;
            if (status == UsageTransactionStatus.Pending) PendingEstimatedProviderCostUsd += estimatedProviderCostUsd ?? 0m;
            if (isAnomalous) AnomalousCount++;
            ProviderCostUsd += providerCostUsd;
            CustomerChargesUsd += chargedAmount - reversedAmount;
        }
    }
}
