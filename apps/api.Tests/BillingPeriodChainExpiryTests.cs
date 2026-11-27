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
/// QA lane (test-only) over PR #1017 (billing period rollover expiry) and PR #1035 (ledger
/// provisioning idempotency). Both lanes pin a single rollover: one ended period expires when
/// the next one is provisioned, and replays never double-grant. Neither lane walks a chain of
/// more than two periods, and neither asserts what happens to a period row that was already
/// recorded as closed before provisioning moved on. This file pins exactly that uncovered,
/// deterministic boundary:
///   1. Provisioning across a three-period chain expires each ended period once, at its own
///      EndsAt boundary - it never closes a period at someone else's boundary and never
///      expires the period that has not ended - and a redelivered mid-chain renewal afterwards
///      adds neither a period row nor a credit grant.
///   2. A period that is already Expired with its own hand-written ClosedAt is never reopened
///      or re-closed by provisioning the next period: the expiry routine skips non-open rows,
///      so the audited status read model stays consistent with what was recorded first.
/// All fixtures use fixed UTC boundaries and the repo-style in-memory SQLite database with a
/// locally constructed plan, so no customer data, provider, secret, live charge, wall clock or
/// seeded data is involved, and the files, fixture names and allowance of the two existing
/// lanes are untouched and disjoint.
/// </summary>
public sealed class BillingPeriodChainExpiryTests
{
    private static readonly DateTime PeriodOneStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodOneEnd = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTwoStart = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTwoEnd = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodThreeStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodThreeEnd = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long Allowance = 750;

    [Fact]
    public async Task MultiPeriod_Rollover_Chain_Expires_Each_Ended_Period_Once_And_Replay_Adds_Nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        // Walk the chain forward across two boundaries: P1 -> P2 -> P3.
        await lifecycle.ActivateSubscriptionAsync(workspace.Id, subscription.Id, plan.Id, "test", "subscription-chain-1", PeriodOneStart, PeriodOneEnd, "Activation for the first period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-chain-1a", "Renewal into the second period.");
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodThreeStart, PeriodThreeEnd, "webhook:event-chain-1b", "Renewal into the third period.");

        // Redeliver the mid-chain renewal after the chain has moved on: the period row and the
        // durable grant key already exist, so the delivery must be a no-op on both.
        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-chain-1c", "Renewal into the second period.");

        var periods = await db.BillingPeriods.AsNoTracking()
            .Where(item => item.SubscriptionId == subscription.Id)
            .OrderBy(item => item.StartsAt)
            .ToListAsync();
        Assert.Equal(3, periods.Count);

        // Every boundary that has ended is expired exactly once, closed at its OWN EndsAt -
        // provisioning never rewrites a ClosedAt it already recorded, and never at a boundary
        // belonging to a different period.
        Assert.Equal(BillingPeriodStatus.Expired, periods[0].Status);
        Assert.Equal(PeriodOneEnd, periods[0].ClosedAt);
        Assert.Equal(BillingPeriodStatus.Expired, periods[1].Status);
        Assert.Equal(PeriodTwoEnd, periods[1].ClosedAt);

        // The chain never expires forward: the period that has not ended stays open and unclosed.
        Assert.Equal(BillingPeriodStatus.Open, periods[2].Status);
        Assert.Null(periods[2].ClosedAt);

        // Three deliveries plus one replay, three grants: the ledger count is decided by the
        // durable period rows, not by the delivery count, and the replay added no credit.
        var grants = await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id)
            .ToListAsync();
        Assert.Equal(3, grants.Count);
        Assert.All(grants, item => Assert.Equal(CreditLedgerEntryType.Grant, item.Type));
        Assert.Equal(3 * Allowance, grants.Sum(item => item.Amount));
        Assert.Equal(
            periods.Select(item => $"included:{subscription.Id}:{item.Id}").OrderBy(item => item, StringComparer.Ordinal),
            grants.Select(item => item.IdempotencyKey).OrderBy(item => item, StringComparer.Ordinal));
        Assert.Equal(3, await db.CreditEntitlements.CountAsync(item => item.WorkspaceId == workspace.Id));
    }

    [Fact]
    public async Task Already_Expired_Period_Is_Neither_Reopened_Nor_ReClosed_By_Provisioning()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var (workspace, plan, subscription) = await SeedSubscriptionAsync(db);
        var lifecycle = NewLifecycle(db);

        // The first boundary was already audited as ended before this provisioning path ever
        // ran: Status=Expired with a hand-written ClosedAt that deliberately does NOT equal
        // EndsAt, so any re-close by the rollover routine would be observable as a rewrite.
        var handWrittenClosedAt = PeriodOneEnd.AddMinutes(7);
        db.BillingPeriods.Add(new BillingPeriod
        {
            Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Expired,
            StartsAt = PeriodOneStart, EndsAt = PeriodOneEnd, IncludedCredits = Allowance,
            CreatedAt = PeriodOneStart, ClosedAt = handWrittenClosedAt,
        });
        await db.SaveChangesAsync();

        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodTwoStart, PeriodTwoEnd, "webhook:event-chain-2", "Renewal into the second period.");

        var first = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.StartsAt == PeriodOneStart);
        var second = await db.BillingPeriods.AsNoTracking().SingleAsync(item => item.StartsAt == PeriodTwoStart);

        // The expiry routine only touches Open rows: the audited boundary keeps the status and
        // the ClosedAt that were recorded first, and the new period stands on its own.
        Assert.Equal(BillingPeriodStatus.Expired, first.Status);
        Assert.Equal(handWrittenClosedAt, first.ClosedAt);
        Assert.Equal(BillingPeriodStatus.Open, second.Status);
        Assert.Null(second.ClosedAt);

        // One grant per period under the durable key: provisioning past an already-closed
        // boundary moves the ledger forward by exactly that period's allowance, no more.
        var grants = await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id)
            .ToListAsync();
        Assert.Single(grants);
        Assert.Equal(Allowance, grants[0].Amount);
        Assert.Equal($"included:{subscription.Id}:{second.Id}", grants[0].IdempotencyKey);
    }

    private static async Task<TaslimDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static PaymentLifecycleService NewLifecycle(TaslimDbContext db) => new(db, new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false })), new NullNotificationEventWriter(), NullLogger<PaymentLifecycleService>.Instance);

    private static async Task<(Workspace Workspace, Plan Plan, Subscription Subscription)> SeedSubscriptionAsync(TaslimDbContext db)
    {
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(), Name = "Chain Expiry Workspace", Slug = $"chain-expiry-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"chain-expiry-{Guid.NewGuid():N}", Name = "Chain expiry plan",
            MonthlyPriceUsd = 9m, MonthlyCreditAllowance = Allowance, Currency = "USD",
            IsActive = true, SortOrder = 92, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
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
