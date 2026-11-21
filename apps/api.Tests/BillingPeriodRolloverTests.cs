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
/// Billing period rollover: when a subscription moves into its next period, the period
/// boundary that has ended must be recorded as ended, and the included allowance granted
/// into each period must expire with that boundary. Grant replay must not duplicate the
/// grant, reopen a closed period, or expire a period that has not ended.
/// All fixtures use fixed UTC boundaries and the repo-style in-memory database, so no
/// provider, secret, charge, wall clock or seeded data is involved.
/// </summary>
public sealed class BillingPeriodRolloverTests
{
    private static readonly DateTime PeriodOneStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodOneEnd = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTwoStart = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTwoEnd = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long Allowance = 1_000;

    [Fact]
    public async Task Rollover_into_the_next_period_expires_the_period_that_ended()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-rollover-1", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-rollover-1", "Renewal into the second period.");

        var periods = await db.BillingPeriods.AsNoTracking()
            .Where(item => item.SubscriptionId == subscription.Id)
            .OrderBy(item => item.StartsAt)
            .ToListAsync();

        Assert.Equal(2, periods.Count);

        // The first period ended exactly when the second one opened. Its included
        // entitlement already stopped being spendable at that boundary, so the auditable
        // period row has to say the same thing instead of claiming the period is open.
        var ended = periods[0];
        Assert.Equal(PeriodOneStart, ended.StartsAt);
        Assert.Equal(BillingPeriodStatus.Expired, ended.Status);
        Assert.Equal(PeriodOneEnd, ended.ClosedAt);

        var current = periods[1];
        Assert.Equal(PeriodTwoStart, current.StartsAt);
        Assert.Equal(BillingPeriodStatus.Open, current.Status);
        Assert.Null(current.ClosedAt);
    }

    [Fact]
    public async Task Replayed_renewals_provision_the_next_period_exactly_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-rollover-2", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-rollover-2a", "Renewal into the second period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-rollover-2b", "Renewal into the second period.");

        // One period and one grant per billing period: the replayed delivery must reuse the
        // durable period row and the workspace-scoped grant key rather than provisioning twice.
        Assert.Equal(2, await db.BillingPeriods.CountAsync(item => item.SubscriptionId == subscription.Id));
        Assert.Equal(2, await db.CreditEntitlements.CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(2, await db.CreditLedgerEntries.CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(2 * Allowance, await db.CreditLedgerEntries.Where(item => item.Type == CreditLedgerEntryType.Grant).SumAsync(item => item.Amount));

        var ended = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.StartsAt == PeriodOneStart);
        Assert.Equal(BillingPeriodStatus.Expired, ended.Status);
        Assert.Equal(PeriodOneEnd, ended.ClosedAt);
    }

    [Fact]
    public async Task Rollover_does_not_expire_a_later_period_that_has_not_ended()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-rollover-3", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-rollover-3", "Renewal into the second period.");

        // A redelivered activation for the first period must not walk the later period back
        // in time: only boundaries that end at or before the period being provisioned expire,
        // and an already-expired period is never reopened.
        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-rollover-3", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");

        var first = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.StartsAt == PeriodOneStart);
        var second = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.StartsAt == PeriodTwoStart);
        Assert.Equal(BillingPeriodStatus.Expired, first.Status);
        Assert.Equal(BillingPeriodStatus.Open, second.Status);
        Assert.Null(second.ClosedAt);
        Assert.Equal(2, await db.CreditLedgerEntries.CountAsync(item => item.WorkspaceId == workspace.Id));
    }

    [Fact]
    public async Task Each_period_allowance_expires_on_its_own_period_boundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-rollover-4", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-rollover-4", "Renewal into the second period.");

        var entitlements = await db.CreditEntitlements.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id)
            .OrderBy(item => item.ExpiresAt)
            .ToListAsync();

        Assert.Equal(2, entitlements.Count);
        Assert.All(entitlements, item => Assert.Equal(CreditEntitlementType.IncludedMonthly, item.Type));
        Assert.Equal(Allowance, entitlements[0].GrantedCredits);
        Assert.Equal(Allowance, entitlements[1].GrantedCredits);
        Assert.Equal(PeriodOneEnd, entitlements[0].ExpiresAt);
        Assert.Equal(PeriodTwoEnd, entitlements[1].ExpiresAt);

        var first = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.StartsAt == PeriodOneStart);
        var second = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.StartsAt == PeriodTwoStart);
        Assert.Equal(first.Id, entitlements[0].BillingPeriodId);
        Assert.Equal(second.Id, entitlements[1].BillingPeriodId);
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
            Id = Guid.NewGuid(), Name = "Rollover Workspace", Slug = $"rollover-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"rollover-{Guid.NewGuid():N}", Name = "Rollover plan",
            MonthlyPriceUsd = 9m, MonthlyCreditAllowance = Allowance, Currency = "USD",
            IsActive = true, SortOrder = 90, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
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
