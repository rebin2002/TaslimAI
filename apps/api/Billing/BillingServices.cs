using System.Data;
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
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedReason = NormalizeReason(reason);
        var existing = await FindEntryAsync(workspaceId, normalizedKey, cancellationToken);
        if (existing is not null)
        {
            EnsureGrantReplayMatches(existing, entitlementType, credits, normalizedReason);
            return new(existing, false);
        }
        if (billingPeriodId.HasValue)
        {
            var period = await db.BillingPeriods
                .Include(item => item.Subscription)
                .SingleOrDefaultAsync(item => item.Id == billingPeriodId.Value, cancellationToken)
                ?? throw new InvalidOperationException("The billing period was not found.");
            if (period.Subscription.WorkspaceId != workspaceId)
                throw new InvalidOperationException("The billing period belongs to a different workspace.");
        }
        var now = DateTime.UtcNow;
        var entitlement = new CreditEntitlement
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, BillingPeriodId = billingPeriodId,
            Type = entitlementType, GrantedCredits = credits, GrantedAt = now, ExpiresAt = expiresAt,
            IdempotencyKey = normalizedKey, SourceReference = sourceReference?.Trim(), CreatedAt = now,
        };
        var entry = NewEntry(workspaceId, entitlement.Id, CreditLedgerEntryTypeFor(entitlementType), credits, normalizedKey, normalizedReason, actorUserId, now);
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
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedReason = NormalizeReason(reason);
        if (!settings.CustomerChargingEnabled) return null;
        var existing = await FindEntryAsync(workspaceId, normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != CreditLedgerEntryType.Debit || existing.Amount != -credits || existing.UsageTransactionId != usageTransactionId || existing.Reason != normalizedReason)
                throw new InvalidOperationException("The credit debit idempotency key belongs to a different movement.");
            return new(existing, false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var usage = await db.UsageTransactions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == usageTransactionId && item.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException("The usage transaction was not found in the requested workspace.");
        if (usage.Status != UsageTransactionStatus.Completed || !usage.IsBillable)
            throw new InvalidOperationException("Only a completed, billable usage transaction can create a credit debit.");

        var priorDebit = await db.CreditLedgerEntries.AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.UsageTransactionId == usageTransactionId && item.Type == CreditLedgerEntryType.Debit, cancellationToken);
        if (priorDebit is not null)
            throw new InvalidOperationException("The usage transaction already has a credit debit.");

        var now = DateTime.UtcNow;
        var availableCredits = await db.CreditLedgerEntries
            .Where(item => item.WorkspaceId == workspaceId &&
                (!item.CreditEntitlementId.HasValue || db.CreditEntitlements.Any(entitlement =>
                    entitlement.Id == item.CreditEntitlementId.Value &&
                    (!entitlement.ExpiresAt.HasValue || entitlement.ExpiresAt > now))))
            .Select(item => (long?)item.Amount)
            .SumAsync(cancellationToken) ?? 0;
        if (availableCredits < credits)
            throw new InvalidOperationException("The workspace does not have enough credits for this usage debit.");

        var entry = NewEntry(workspaceId, null, CreditLedgerEntryType.Debit, -credits, normalizedKey, normalizedReason, actorUserId, now);
        entry.UsageTransactionId = usageTransactionId;
        db.CreditLedgerEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedReason = NormalizeReason(reason);
        var existing = await FindEntryAsync(workspaceId, normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != entryType || existing.ReversesEntryId != originalEntryId || existing.Reason != normalizedReason)
                throw new InvalidOperationException("The credit reversal idempotency key belongs to a different movement.");
            return new(existing, false);
        }
        var original = await db.CreditLedgerEntries.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Id == originalEntryId && entry.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException("The credit ledger entry to reverse was not found.");
        if (original.Amount == 0) throw new InvalidOperationException("A zero-value credit entry cannot be reversed.");
        var priorReversal = await db.CreditLedgerEntries.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.ReversesEntryId == original.Id, cancellationToken);
        if (priorReversal is not null)
            throw new InvalidOperationException("The credit ledger entry has already been reversed.");
        var entry = NewEntry(workspaceId, original.CreditEntitlementId, entryType, -original.Amount, normalizedKey, normalizedReason, actorUserId, DateTime.UtcNow);
        entry.ReversesEntryId = original.Id;
        entry.UsageTransactionId = original.UsageTransactionId;
        db.CreditLedgerEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return new(entry, true);
    }

    private Task<CreditLedgerEntry?> FindEntryAsync(Guid workspaceId, string idempotencyKey, CancellationToken cancellationToken) =>
        db.CreditLedgerEntries
            .Include(entry => entry.CreditEntitlement)
            .SingleOrDefaultAsync(entry => entry.WorkspaceId == workspaceId && entry.IdempotencyKey == idempotencyKey, cancellationToken);

    private static void EnsureGrantReplayMatches(CreditLedgerEntry existing, CreditEntitlementType entitlementType, long credits, string reason)
    {
        if (existing.Amount != credits || existing.Type != CreditLedgerEntryTypeFor(entitlementType) || existing.CreditEntitlement?.Type != entitlementType || existing.Reason != reason)
            throw new InvalidOperationException("The credit grant idempotency key belongs to a different movement.");
    }

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

    private static string NormalizeIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A credit movement idempotency key is required.", nameof(value));
        var normalized = value.Trim();
        if (normalized.Length > 180) throw new ArgumentException("The credit movement idempotency key is too long.", nameof(value));
        return normalized;
    }

    private static string NormalizeReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An auditable credit movement reason is required.", nameof(value));
        var normalized = value.Trim();
        if (normalized.Length > 500) throw new ArgumentException("The credit movement reason is too long.", nameof(value));
        return normalized;
    }
}
