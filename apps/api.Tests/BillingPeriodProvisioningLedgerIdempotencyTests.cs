using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Notifications;
using Taslim.Api.Payments;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Credit-ledger idempotency for the billing provisioning path: every period a subscription
/// moves into is granted its included allowance under one durable, workspace-scoped key
/// ("included:{subscriptionId}:{periodId}"), and that key - not the delivery count - decides
/// how many ledger movements exist. Replayed provider deliveries must therefore be a no-op on
/// the ledger, a delivery that disagrees with the movement already recorded must be refused
/// instead of silently re-attributed, and opening the next period must never rewrite or
/// append to the historical rows of the period it passed.
///
/// These facts are deliberately about the ledger only: none of them depends on how the
/// provisioning path records the period boundary itself, so they pin the money-moving surface
/// independently of that decision. All fixtures use fixed UTC boundaries, the repo-style
/// in-memory database and a locally constructed plan, so no provider, secret, live charge,
/// wall clock or seeded data is involved.
/// </summary>
public sealed class BillingPeriodProvisioningLedgerIdempotencyTests
{
    private static readonly DateTime PeriodOneStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodOneEnd = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTwoStart = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTwoEnd = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long Allowance = 1_000;

    [Fact]
    public async Task Each_provisioned_period_is_granted_exactly_once_under_its_own_durable_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-ledger-1", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-ledger-1a", "Renewal into the second period.");
        // Two more deliveries for boundaries that are already provisioned: a redelivered
        // renewal of the current period and a redelivered activation of the older one.
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-ledger-1b", "Renewal into the second period.");
        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-ledger-1", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");

        var periods = await db.BillingPeriods.AsNoTracking()
            .Where(item => item.SubscriptionId == subscription.Id)
            .OrderBy(item => item.StartsAt)
            .ToListAsync();
        Assert.Equal(2, periods.Count);

        // Four deliveries, two movements: the ledger is keyed by the durable period it belongs
        // to, so the key itself is what makes a redelivery harmless.
        var grants = await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, item => Assert.Equal(CreditLedgerEntryType.Grant, item.Type));
        Assert.Equal(
            periods.Select(item => $"included:{subscription.Id}:{item.Id}").OrderBy(item => item, StringComparer.Ordinal),
            grants.Select(item => item.IdempotencyKey).OrderBy(item => item, StringComparer.Ordinal));
        Assert.Equal(2 * Allowance, grants.Sum(item => item.Amount));
        Assert.All(grants, item => Assert.Equal(Allowance, item.Amount));

        // Each movement stays attached to the entitlement of its own period, so the ledger can
        // be reconciled per period rather than per delivery.
        foreach (var grant in grants)
        {
            Assert.NotNull(grant.CreditEntitlementId);
            var entitlement = await db.CreditEntitlements.AsNoTracking().SingleAsync(item => item.Id == grant.CreditEntitlementId);
            Assert.Equal(workspace.Id, entitlement.WorkspaceId);
            Assert.Equal(CreditEntitlementType.IncludedMonthly, entitlement.Type);
            Assert.Equal(Allowance, entitlement.GrantedCredits);
            Assert.Equal(grant.IdempotencyKey, entitlement.IdempotencyKey);
            var period = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.Id == entitlement.BillingPeriodId);
            Assert.Equal(subscription.Id, period.SubscriptionId);
        }
    }

    [Fact]
    public async Task Provisioning_the_next_period_leaves_the_previous_grant_untouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-ledger-2", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        var before = await db.CreditLedgerEntries.AsNoTracking().Where(item => item.WorkspaceId == workspace.Id).ToListAsync();
        Assert.Single(before);

        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-ledger-2", "Renewal into the second period.");
        var after = await db.CreditLedgerEntries.AsNoTracking().Where(item => item.WorkspaceId == workspace.Id).ToListAsync();

        // The ledger is append-only history: crossing a period boundary may move period state
        // forward, but it may not rewrite the movement that was already recorded for the period
        // that ended, and it may not compensate it with a second entry.
        var original = after.Single(item => item.Id == before[0].Id);
        Assert.Equal(before[0].Amount, original.Amount);
        Assert.Equal(before[0].Type, original.Type);
        Assert.Equal(before[0].IdempotencyKey, original.IdempotencyKey);
        Assert.Equal(before[0].Reason, original.Reason);
        Assert.Equal(before[0].ActorUserId, original.ActorUserId);
        Assert.Equal(before[0].CreditEntitlementId, original.CreditEntitlementId);
        Assert.Equal(before[0].UsageTransactionId, original.UsageTransactionId);
        Assert.Equal(before[0].ReversesEntryId, original.ReversesEntryId);
        Assert.Equal(before[0].CreatedAt, original.CreatedAt);
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, item => item.Type is CreditLedgerEntryType.Debit or CreditLedgerEntryType.Refund or CreditLedgerEntryType.Reversal or CreditLedgerEntryType.Expiry or CreditLedgerEntryType.AdministrativeCorrection);
        Assert.Equal(2 * Allowance, after.Sum(item => item.Amount));
    }

    [Fact]
    public async Task A_replayed_delivery_with_a_different_allowance_is_refused_without_moving_credits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-ledger-3", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");

        // The allowance behind the plan moves after the period was provisioned, so a later
        // delivery for the very same period carries a different amount under the same durable
        // key. Reconciling that silently would mint credits from a redelivery.
        plan.MonthlyCreditAllowance = Allowance * 2;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodOneStart, PeriodOneEnd, "webhook:event-ledger-3", "Renewal into the first period."));

        var entries = await db.CreditLedgerEntries.AsNoTracking().Where(item => item.WorkspaceId == workspace.Id).ToListAsync();
        Assert.Single(entries);
        Assert.Equal(Allowance, entries[0].Amount);
        Assert.Equal(CreditLedgerEntryType.Grant, entries[0].Type);
        var entitlement = await db.CreditEntitlements.AsNoTracking().SingleAsync(item => item.Id == entries[0].CreditEntitlementId);
        Assert.Equal(Allowance, entitlement.GrantedCredits);
    }

    [Fact]
    public async Task A_replayed_delivery_with_a_different_audit_reason_is_refused_without_moving_credits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-ledger-4", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");

        // Same period, same amount, different provenance: the durable key already belongs to a
        // recorded movement, so re-attributing that movement is refused rather than rewritten.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodOneStart, PeriodOneEnd, "webhook:event-ledger-4", "Renewal wording for the first period."));

        var entries = await db.CreditLedgerEntries.AsNoTracking().Where(item => item.WorkspaceId == workspace.Id).ToListAsync();
        Assert.Single(entries);
        Assert.Equal("subscription activation: Activation for the first period.", entries[0].Reason);
        Assert.Equal(Allowance, entries[0].Amount);
    }

    [Fact]
    public async Task Included_allowance_needs_no_charging_switch_and_writes_no_debit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        // The shipped default: customer charging stays off. Provisioning the included allowance
        // must still be fully auditable, and must never touch the charging side of the ledger.
        var lifecycle = new PaymentLifecycleService(db, new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false })), new NullNotificationEventWriter(), NullLogger<PaymentLifecycleService>.Instance);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-ledger-5", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-ledger-5", "Renewal into the second period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-ledger-5b", "Renewal into the second period.");

        var entries = await db.CreditLedgerEntries.AsNoTracking().Where(item => item.WorkspaceId == workspace.Id).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, item => Assert.Equal(CreditLedgerEntryType.Grant, item.Type));
        Assert.Equal(2 * Allowance, entries.Sum(item => item.Amount));
        Assert.DoesNotContain(entries, item => item.Amount < 0);
    }

    private static async Task<TaslimDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static PaymentLifecycleService NewLifecycle(TaslimDbContext db) => new(db, new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true })), new NullNotificationEventWriter(), NullLogger<PaymentLifecycleService>.Instance);

    private static async Task<(Workspace Workspace, Plan Plan, Subscription Subscription)> SeedSubscriptionAsync(TaslimDbContext db)
    {
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(), Name = "Provisioning Ledger Workspace", Slug = $"provisioning-ledger-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"provisioning-ledger-{Guid.NewGuid():N}", Name = "Provisioning ledger plan",
            MonthlyPriceUsd = 9m, MonthlyCreditAllowance = Allowance, Currency = "USD",
            IsActive = true, SortOrder = 91, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, PlanId = plan.Id,
            Status = SubscriptionStatus.PastDue, CurrentPeriodStart = PeriodOneStart,
            CurrentPeriodEnd = PeriodOneEnd, NextRenewalAt = PeriodOneEnd,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return (workspace, plan, subscription);
    }

    private sealed class NullNotificationEventWriter : INotificationEventWriter
    {
        public Task CreateGenerationCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationFailedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationAttentionAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateBillingPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
