using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Billing;

public sealed record CreditMovementResult(CreditLedgerEntry Entry, bool Created);

public interface IBillingProvisioningService
{
    Task<Subscription> EnsureProvisionedAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

public interface ICreditLedgerService
{
    Task<CreditMovementResult> GrantAsync(
        Guid workspaceId,
        CreditEntitlementType entitlementType,
        long credits,
        string idempotencyKey,
        string reason,
        DateTime? expiresAt = null,
        Guid? billingPeriodId = null,
        Guid? actorUserId = null,
        string? sourceReference = null,
        CancellationToken cancellationToken = default);

    Task<CreditMovementResult?> RecordUsageDebitAsync(
        Guid workspaceId,
        Guid usageTransactionId,
        long credits,
        string idempotencyKey,
        string reason,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);

    Task<CreditMovementResult> ReverseAsync(
        Guid workspaceId,
        Guid originalEntryId,
        string idempotencyKey,
        string reason,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);

    Task<CreditMovementResult> RefundAsync(
        Guid workspaceId,
        Guid originalEntryId,
        string idempotencyKey,
        string reason,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);
}

public sealed class BillingProvisioningService(TaslimDbContext db, ILogger<BillingProvisioningService> logger) : IBillingProvisioningService
{
    public async Task<Subscription> EnsureProvisionedAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var existing = await db.Subscriptions
            .Include(subscription => subscription.Plan)
            .Where(subscription => subscription.WorkspaceId == workspaceId)
            .OrderByDescending(subscription => subscription.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;

        var plan = await db.Plans.SingleOrDefaultAsync(item => item.Code == "free" && item.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("The default free plan is not available.");
        var now = DateTime.UtcNow;
        var periodStart = now.Date;
        var periodEnd = periodStart.AddMonths(1);
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = plan.Id,
            Status = SubscriptionStatus.Active, CurrentPeriodStart = periodStart,
            CurrentPeriodEnd = periodEnd, NextRenewalAt = periodEnd,
            CreatedAt = now, UpdatedAt = now,
        };
        var period = new BillingPeriod
        {
            Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Open,
            StartsAt = periodStart, EndsAt = periodEnd, IncludedCredits = plan.MonthlyCreditAllowance, CreatedAt = now,
        };
        var entitlement = new CreditEntitlement
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, BillingPeriodId = period.Id,
            Type = CreditEntitlementType.IncludedMonthly, GrantedCredits = plan.MonthlyCreditAllowance,
            GrantedAt = now, ExpiresAt = periodEnd, IdempotencyKey = $"included:{subscription.Id}:{period.Id}", CreatedAt = now,
        };
        var grant = new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditEntitlementId = entitlement.Id,
            Type = CreditLedgerEntryType.Grant, Amount = plan.MonthlyCreditAllowance,
            IdempotencyKey = entitlement.IdempotencyKey, Reason = "Monthly included allowance", CreatedAt = now,
        };
        db.Subscriptions.Add(subscription);
        db.BillingPeriods.Add(period);
        db.CreditEntitlements.Add(entitlement);
        db.CreditLedgerEntries.Add(grant);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Free billing foundation provisioned. WorkspaceId={WorkspaceId}; SubscriptionId={SubscriptionId}", workspaceId, subscription.Id);
        subscription.Plan = plan;
        return subscription;
    }
}

public sealed class CreditLedgerService(TaslimDbContext db, IOptions<BillingOptions> options) : ICreditLedgerService
{
    private readonly BillingOptions settings = options.Value;

    public async Task<CreditMovementResult> GrantAsync(
        Guid workspaceId, CreditEntitlementType entitlementType, long credits, string idempotencyKey,
        string reason, DateTime? expiresAt = null, Guid? billingPeriodId = null, Guid? actorUserId = null,
        string? sourceReference = null, CancellationToken cancellationToken = default)
    {
        ValidatePositiveCredits(credits);
        var existing = await FindEntryAsync(workspaceId, idempotencyKey, cancellationToken);
        if (existing is not null) return new(existing, false);
        var now = DateTime.UtcNow;
        var entitlement = new CreditEntitlement
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, BillingPeriodId = billingPeriodId,
            Type = entitlementType, GrantedCredits = credits, GrantedAt = now, ExpiresAt = expiresAt,
            IdempotencyKey = idempotencyKey, SourceReference = sourceReference, CreatedAt = now,
        };
        var entry = NewEntry(workspaceId, entitlement.Id, CreditLedgerEntryTypeFor(entitlementType), credits, idempotencyKey, reason, actorUserId, now);
        db.CreditEntitlements.Add(entitlement);
        db.CreditLedgerEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return new(entry, true);
    }

    public async Task<CreditMovementResult?> RecordUsageDebitAsync(
        Guid workspaceId, Guid usageTransactionId, long credits, string idempotencyKey, string reason,
        Guid? actorUserId = null, CancellationToken cancellationToken = default)
    {
        ValidatePositiveCredits(credits);
        if (!settings.CustomerChargingEnabled) return null;
        var existing = await FindEntryAsync(workspaceId, idempotencyKey, cancellationToken);
        if (existing is not null) return new(existing, false);
        var entry = NewEntry(workspaceId, null, CreditLedgerEntryType.Debit, -credits, idempotencyKey, reason, actorUserId, DateTime.UtcNow);
        entry.UsageTransactionId = usageTransactionId;
        db.CreditLedgerEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return new(entry, true);
    }

    public async Task<CreditMovementResult> ReverseAsync(
        Guid workspaceId, Guid originalEntryId, string idempotencyKey, string reason,
        Guid? actorUserId = null, CancellationToken cancellationToken = default)
        => await ReverseInternalAsync(workspaceId, originalEntryId, idempotencyKey, reason, CreditLedgerEntryType.Reversal, actorUserId, cancellationToken);

    public async Task<CreditMovementResult> RefundAsync(
        Guid workspaceId, Guid originalEntryId, string idempotencyKey, string reason,
        Guid? actorUserId = null, CancellationToken cancellationToken = default)
        => await ReverseInternalAsync(workspaceId, originalEntryId, idempotencyKey, reason, CreditLedgerEntryType.Refund, actorUserId, cancellationToken);

    private async Task<CreditMovementResult> ReverseInternalAsync(
        Guid workspaceId, Guid originalEntryId, string idempotencyKey, string reason,
        CreditLedgerEntryType entryType, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var existing = await FindEntryAsync(workspaceId, idempotencyKey, cancellationToken);
        if (existing is not null) return new(existing, false);
        var original = await db.CreditLedgerEntries.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Id == originalEntryId && entry.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException("The credit ledger entry to reverse was not found.");
        if (original.Amount == 0) throw new InvalidOperationException("A zero-value credit entry cannot be reversed.");
        var entry = NewEntry(workspaceId, original.CreditEntitlementId, entryType, -original.Amount, idempotencyKey, reason, actorUserId, DateTime.UtcNow);
        entry.ReversesEntryId = original.Id;
        entry.UsageTransactionId = original.UsageTransactionId;
        db.CreditLedgerEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return new(entry, true);
    }

    private Task<CreditLedgerEntry?> FindEntryAsync(Guid workspaceId, string idempotencyKey, CancellationToken cancellationToken) =>
        db.CreditLedgerEntries.SingleOrDefaultAsync(entry => entry.WorkspaceId == workspaceId && entry.IdempotencyKey == idempotencyKey, cancellationToken);

    private static CreditLedgerEntry NewEntry(Guid workspaceId, Guid? entitlementId, CreditLedgerEntryType type, long amount, string idempotencyKey, string reason, Guid? actorUserId, DateTime now) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditEntitlementId = entitlementId, Type = type,
        Amount = amount, IdempotencyKey = idempotencyKey, Reason = reason.Trim(), ActorUserId = actorUserId, CreatedAt = now,
    };

    private static CreditLedgerEntryType CreditLedgerEntryTypeFor(CreditEntitlementType type) => type switch
    {
        CreditEntitlementType.AdministrativeCorrection => CreditLedgerEntryType.AdministrativeCorrection,
        _ => CreditLedgerEntryType.Grant,
    };

    private static void ValidatePositiveCredits(long credits)
    {
        if (credits <= 0) throw new ArgumentOutOfRangeException(nameof(credits), "Credit movements must be greater than zero.");
    }
}
