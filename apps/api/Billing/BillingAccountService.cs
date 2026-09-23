using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Billing;

public interface IBillingAccountService
{
    Task<BillingAccountDto> GetAccountAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

public sealed class BillingAccountService(TaslimDbContext db, IBillingProvisioningService provisioning) : IBillingAccountService
{
    public async Task<BillingAccountDto> GetAccountAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await provisioning.EnsureProvisionedAsync(workspaceId, cancellationToken);
        var subscription = await db.Subscriptions.AsNoTracking()
            .Include(item => item.Plan)
            .Include(item => item.BillingPeriods)
            .Where(item => item.WorkspaceId == workspaceId)
            .OrderByDescending(item => item.CreatedAt)
            .FirstAsync(cancellationToken);
        var period = subscription.BillingPeriods
            .OrderByDescending(item => item.StartsAt)
            .First(item => item.StartsAt == subscription.CurrentPeriodStart);
        var entitlements = await db.CreditEntitlements.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && (!item.ExpiresAt.HasValue || item.ExpiresAt > DateTime.UtcNow))
            .ToListAsync(cancellationToken);
        var entitlementIds = entitlements.Select(item => item.Id).ToArray();
        var entries = await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && (!item.CreditEntitlementId.HasValue || entitlementIds.Contains(item.CreditEntitlementId.Value)))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        var includedIds = entitlements.Where(item => item.Type == CreditEntitlementType.IncludedMonthly).Select(item => item.Id).ToHashSet();
        var purchasedIds = entitlements.Where(item => item.Type == CreditEntitlementType.Purchased).Select(item => item.Id).ToHashSet();
        var adjustmentIds = entitlements.Where(item => item.Type == CreditEntitlementType.AdministrativeCorrection).Select(item => item.Id).ToHashSet();
        var includedRemaining = entries.Where(item => item.CreditEntitlementId.HasValue && includedIds.Contains(item.CreditEntitlementId.Value)).Sum(item => item.Amount);
        var purchasedRemaining = entries.Where(item => item.CreditEntitlementId.HasValue && purchasedIds.Contains(item.CreditEntitlementId.Value)).Sum(item => item.Amount);
        var adjustmentBalance = entries.Where(item => item.CreditEntitlementId.HasValue && adjustmentIds.Contains(item.CreditEntitlementId.Value)).Sum(item => item.Amount);
        var unallocatedMovements = entries.Where(item => !item.CreditEntitlementId.HasValue).Sum(item => item.Amount);
        return new BillingAccountDto(
            new(subscription.Plan.Code, subscription.Plan.Name, subscription.Plan.MonthlyPriceUsd, subscription.Plan.MonthlyCreditAllowance, subscription.Plan.Currency),
            new(subscription.Status.ToString(), subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd, subscription.NextRenewalAt, subscription.CancelAtPeriodEnd),
            new(period.Id, period.Status.ToString(), period.StartsAt, period.EndsAt, period.IncludedCredits),
            new(period.IncludedCredits, Math.Max(0, includedRemaining), Math.Max(0, purchasedRemaining), adjustmentBalance, Math.Max(0, includedRemaining + purchasedRemaining + adjustmentBalance + unallocatedMovements)),
            entries.Select(item => new CreditLedgerEntryDto(item.Id, item.Type.ToString(), item.Amount, item.Reason, item.CreatedAt)).ToArray(),
            true);
    }
}
