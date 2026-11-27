using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #330 backend accounting fix, synthetic ledger rows only.
/// Usage debits must be bound to the credit lot they consumed whenever the
/// attribution is unambiguous, so a debit recorded against a since-expired
/// period can never reduce a later grant period - neither in debit admission
/// nor in the Account Billing projection. Where a debit genuinely spans lots
/// (no single active lot covers it) or several lots could each cover it (the
/// lot-priority policy is deferred to the accountable owner), the ledger row
/// deliberately stays unallocated; the projection then charges that
/// unallocated aggregate against the active spendable lots so the displayed
/// categories reconcile to the ledger total. Nothing here encodes FEFO or any
/// other spend order in the ledger, reverses the compensating-entry guard, or
/// touches refunds' lot inheritance.
/// Provider-free: every fixture lives in a throwaway in-memory SQLite
/// database, no provider, payment gateway, customer balance or production
/// database is contacted, and the shipped customer-charging-disabled default
/// is asserted to write nothing.
/// </summary>
public sealed class BillingDebitLotAttributionTests
{
    [Fact]
    public async Task Sqlite_debit_with_a_single_covering_lot_is_attributed_at_write()
    {
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 100);

        var usage = await scenario.AddCompletedUsageAsync("attribution:usage:1");
        var debit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, usage.Id, 40, "attribution:debit:1", "Synthetic single-lot usage");

        Assert.NotNull(debit);
        Assert.True(debit!.Created);
        Assert.Equal(-40L, debit.Entry.Amount);
        // Unambiguous coverage: the debit must be bound to the lot it consumed.
        Assert.Equal(scenario.FirstLotId, debit.Entry.CreditEntitlementId);

        var account = await scenario.GetAccountAsync();
        Assert.Equal(60L, account.Credits.IncludedRemaining);
        Assert.Equal(60L, account.Credits.TotalRemaining);
    }

    [Fact]
    public async Task Sqlite_full_consumed_expired_lot_leaves_next_grant_available_in_projection_and_admission()
    {
        // The literal #330 example: +100, its -100 debit, normal expiry, fresh +100.
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 100);

        var oldUsage = await scenario.AddCompletedUsageAsync("attribution:usage:old");
        var oldDebit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, oldUsage.Id, 100, "attribution:debit:old", "Synthetic old-period usage");
        Assert.NotNull(oldDebit);
        Assert.Equal(scenario.FirstLotId, oldDebit!.Entry.CreditEntitlementId);

        await scenario.ExpireFirstLotAsync();
        await scenario.ChargingEnabled.GrantAsync(
            scenario.WorkspaceId, CreditEntitlementType.IncludedMonthly, 100,
            "attribution:grant:fresh", "Synthetic fresh-period grant", expiresAt: DateTime.UtcNow.AddDays(30));

        // Projection: the expired lot (grant and its attributed debit) leaves the
        // balance entirely; the fresh grant is fully available.
        var account = await scenario.GetAccountAsync();
        Assert.Equal(100L, account.Credits.IncludedRemaining);
        Assert.Equal(100L, account.Credits.TotalRemaining);

        // Admission must agree: 30 fits the fresh grant, a further 71 does not.
        var newUsage = await scenario.AddCompletedUsageAsync("attribution:usage:new");
        var newDebit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, newUsage.Id, 30, "attribution:debit:new", "Synthetic new-period usage");
        Assert.NotNull(newDebit);
        Assert.True(newDebit!.Created);

        var overflowUsage = await scenario.AddCompletedUsageAsync("attribution:usage:overflow");
        var overflow = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.ChargingEnabled.RecordUsageDebitAsync(
                scenario.WorkspaceId, overflowUsage.Id, 71, "attribution:debit:overflow", "Synthetic overdraw attempt"));
        Assert.Contains("does not have enough credits", overflow.Message, StringComparison.Ordinal);

        account = await scenario.GetAccountAsync();
        Assert.Equal(70L, account.Credits.TotalRemaining);
        Assert.Equal(0, await scenario.PaymentAttemptCountAsync());
    }

    [Fact]
    public async Task Sqlite_partial_expired_lot_debit_does_not_reduce_later_grant()
    {
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 100);

        var oldUsage = await scenario.AddCompletedUsageAsync("partial:usage:old");
        var oldDebit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, oldUsage.Id, 25, "partial:debit:old", "Synthetic partial old-period usage");
        Assert.NotNull(oldDebit);
        Assert.Equal(scenario.FirstLotId, oldDebit!.Entry.CreditEntitlementId);

        await scenario.ExpireFirstLotAsync();
        await scenario.ChargingEnabled.GrantAsync(
            scenario.WorkspaceId, CreditEntitlementType.IncludedMonthly, 50,
            "partial:grant:fresh", "Synthetic fresh-period grant", expiresAt: DateTime.UtcNow.AddDays(30));

        // The old -25 belonged to the expired lot and must not block a 30-credit
        // debit against the fresh 50-credit grant (the recorded #330 rejection).
        var newUsage = await scenario.AddCompletedUsageAsync("partial:usage:new");
        var newDebit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, newUsage.Id, 30, "partial:debit:new", "Synthetic new-period usage");
        Assert.NotNull(newDebit);
        Assert.True(newDebit!.Created);

        var account = await scenario.GetAccountAsync();
        Assert.Equal(20L, account.Credits.IncludedRemaining);
        Assert.Equal(20L, account.Credits.TotalRemaining);
    }

    [Fact]
    public async Task Sqlite_split_debit_stays_unallocated_and_projection_reconciles_categories_to_total()
    {
        // Two lots of 40; a 60-credit debit fits no single lot. The split
        // representation and the spend order are deferred policy decisions, so
        // the row must not silently bind to either lot, but the customer-facing
        // categories still have to reconcile to the ledger total.
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 0);
        await scenario.ChargingEnabled.GrantAsync(
            scenario.WorkspaceId, CreditEntitlementType.IncludedMonthly, 40,
            "split:grant:included", "Synthetic expiring lot", expiresAt: DateTime.UtcNow.AddDays(5));
        await scenario.ChargingEnabled.GrantAsync(
            scenario.WorkspaceId, CreditEntitlementType.Purchased, 40,
            "split:grant:purchased", "Synthetic later-expiring lot", expiresAt: DateTime.UtcNow.AddDays(30));

        var usage = await scenario.AddCompletedUsageAsync("split:usage");
        var debit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, usage.Id, 60, "split:debit", "Synthetic cross-lot usage");

        Assert.NotNull(debit);
        Assert.True(debit!.Created);
        Assert.Null(debit.Entry.CreditEntitlementId);
        Assert.Null((await scenario.EntryAsync(debit.Entry.Id)).CreditEntitlementId);

        var account = await scenario.GetAccountAsync();
        Assert.Equal(20L, account.Credits.TotalRemaining);
        Assert.Equal(account.Credits.TotalRemaining, account.Credits.IncludedRemaining + account.Credits.PurchasedRemaining);

        // Admission agrees with the reconciled total: one more credit does not fit.
        var nextUsage = await scenario.AddCompletedUsageAsync("split:usage:next");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.ChargingEnabled.RecordUsageDebitAsync(
                scenario.WorkspaceId, nextUsage.Id, 21, "split:debit:next", "Synthetic overdraw attempt"));
    }

    [Fact]
    public async Task Sqlite_ambiguous_multi_lot_coverage_stays_unallocated_without_guessing_priority()
    {
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 0);
        await scenario.ChargingEnabled.GrantAsync(
            scenario.WorkspaceId, CreditEntitlementType.IncludedMonthly, 100,
            "ambiguous:grant:included", "Synthetic included lot", expiresAt: DateTime.UtcNow.AddDays(30));
        await scenario.ChargingEnabled.GrantAsync(
            scenario.WorkspaceId, CreditEntitlementType.Purchased, 100,
            "ambiguous:grant:purchased", "Synthetic purchased lot", expiresAt: DateTime.UtcNow.AddDays(60));

        var usage = await scenario.AddCompletedUsageAsync("ambiguous:usage");
        var debit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, usage.Id, 50, "ambiguous:debit", "Synthetic ambiguous usage");

        // Lot priority is a deferred policy decision: with more than one covering
        // lot the writer must not invent an allocation order in the ledger.
        Assert.NotNull(debit);
        Assert.Null(debit!.Entry.CreditEntitlementId);

        var account = await scenario.GetAccountAsync();
        Assert.Equal(150L, account.Credits.TotalRemaining);
        Assert.Equal(150L, account.Credits.IncludedRemaining + account.Credits.PurchasedRemaining);
    }

    [Fact]
    public async Task Sqlite_refund_of_expired_lot_debit_never_inflates_the_next_grant()
    {
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 100);

        var oldUsage = await scenario.AddCompletedUsageAsync("refund-ceiling:usage:old");
        var oldDebit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, oldUsage.Id, 100, "refund-ceiling:debit:old", "Synthetic old-period usage");
        Assert.NotNull(oldDebit);
        await scenario.ExpireFirstLotAsync();
        await scenario.ChargingEnabled.GrantAsync(
            scenario.WorkspaceId, CreditEntitlementType.IncludedMonthly, 100,
            "refund-ceiling:grant:fresh", "Synthetic fresh-period grant", expiresAt: DateTime.UtcNow.AddDays(30));

        var rowsBefore = await scenario.CaptureLedgerRowsAsync();
        var refund = await scenario.ChargingEnabled.RefundAsync(
            scenario.WorkspaceId, oldDebit!.Entry.Id, "refund-ceiling:refund", "Synthetic refund of expired-period debit");

        Assert.True(refund.Created);
        // Existing refund contract: the compensating row inherits the source lot,
        // so it lands on the expired lot and cannot mint spendable credits.
        Assert.Equal(scenario.FirstLotId, refund.Entry.CreditEntitlementId);

        // Same key replays; a second key must not compensate twice.
        var replay = await scenario.ChargingEnabled.RefundAsync(
            scenario.WorkspaceId, oldDebit!.Entry.Id, "refund-ceiling:refund", "Synthetic refund of expired-period debit");
        Assert.False(replay.Created);
        Assert.Equal(refund.Entry.Id, replay.Entry.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.ChargingEnabled.RefundAsync(
            scenario.WorkspaceId, oldDebit!.Entry.Id, "refund-ceiling:refund:second-key", "Synthetic refund of expired-period debit"));

        var account = await scenario.GetAccountAsync();
        Assert.Equal(100L, account.Credits.TotalRemaining);

        // Admission agrees: 30 fits, 71 does not; availability is never 200.
        var fitUsage = await scenario.AddCompletedUsageAsync("refund-ceiling:usage:fit");
        var fit = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, fitUsage.Id, 30, "refund-ceiling:debit:fit", "Synthetic post-refund usage");
        Assert.NotNull(fit);
        var overUsage = await scenario.AddCompletedUsageAsync("refund-ceiling:usage:over");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.ChargingEnabled.RecordUsageDebitAsync(
                scenario.WorkspaceId, overUsage.Id, 71, "refund-ceiling:debit:over", "Synthetic post-refund overdraw"));

        // Append-only: no pre-existing row changed, exactly the refund and the
        // admitted debit were added, and no PaymentAttempt was created.
        var rowsAfter = await scenario.CaptureLedgerRowsAsync();
        var byId = rowsAfter.ToDictionary(item => item.Id);
        foreach (var original in rowsBefore)
            Assert.Equal(original, byId[original.Id]);
        Assert.Equal(rowsBefore.Count + 2, rowsAfter.Count);
        Assert.Equal(0, await scenario.PaymentAttemptCountAsync());
    }

    [Fact]
    public async Task Sqlite_attributed_debit_replay_is_idempotent_and_rejects_a_second_key_or_usage()
    {
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 100);

        var usage = await scenario.AddCompletedUsageAsync("replay:usage");
        var first = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, usage.Id, 25, "replay:debit", "Synthetic replayed usage");
        Assert.NotNull(first);

        var replay = await scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, usage.Id, 25, "replay:debit", "Synthetic replayed usage");
        Assert.NotNull(replay);
        Assert.False(replay!.Created);
        Assert.Equal(first!.Entry.Id, replay.Entry.Id);
        Assert.Equal(scenario.FirstLotId, replay.Entry.CreditEntitlementId);

        // A second idempotency key for the same usage transaction is rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, usage.Id, 25, "replay:debit:second-key", "Synthetic replayed usage"));

        // The same key bound to a different usage transaction is rejected.
        var otherUsage = await scenario.AddCompletedUsageAsync("replay:usage:other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.ChargingEnabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, otherUsage.Id, 25, "replay:debit", "Different movement, same key"));

        Assert.Equal(2, await scenario.LedgerRowCountAsync());
    }

    [Fact]
    public async Task Sqlite_expired_lot_scenario_writes_nothing_while_customer_charging_is_disabled()
    {
        await using var scenario = await BillingScenario.CreateAsync(lotCredits: 100, chargingEnabledForFixture: false);

        var rowsBefore = await scenario.CaptureLedgerRowsAsync();
        var debit = await scenario.ChargingDisabled.RecordUsageDebitAsync(
            scenario.WorkspaceId, scenario.FixtureUsageId, 100, "disabled:debit", "Synthetic debit while charging is disabled");

        Assert.Null(debit);
        Assert.Equal(rowsBefore, await scenario.CaptureLedgerRowsAsync());
        Assert.Equal(0, await scenario.PaymentAttemptCountAsync());

        var account = await scenario.GetAccountAsync();
        Assert.Equal("disabled", account.PaymentStatus.Status);
        Assert.False(account.Actions.CheckoutAvailable);
        Assert.Contains("charging is intentionally disabled", account.Actions.DisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Throwaway in-memory SQLite fixture: workspace, user, plan/subscription/period
    /// shell (no auto-provisioned grant), and optionally one synthetic active lot.
    /// </summary>
    private sealed class BillingScenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly TaslimDbContext _db;

        private BillingScenario(SqliteConnection connection, TaslimDbContext db, Workspace workspace, Guid firstLotId)
        {
            _connection = connection;
            _db = db;
            Workspace = workspace;
            FirstLotId = firstLotId;
            ChargingEnabled = new CreditLedgerService(db, NewOptions(enabled: true));
            ChargingDisabled = new CreditLedgerService(db, NewOptions(enabled: false));
        }

        public Workspace Workspace { get; }

        public Guid WorkspaceId => Workspace.Id;

        public Guid FirstLotId { get; }

        public Guid FixtureUsageId { get; private set; }

        public CreditLedgerService ChargingEnabled { get; }

        public CreditLedgerService ChargingDisabled { get; }

        public static async Task<BillingScenario> CreateAsync(int lotCredits = 0, bool chargingEnabledForFixture = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
            var db = new TaslimDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            var workspace = NewWorkspace();
            var user = NewUser();
            db.Workspaces.Add(workspace);
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Code = $"issue330-attribution-{Guid.NewGuid():N}",
                Name = "Issue 330 attribution fixture",
                MonthlyCreditAllowance = 1_000,
                Currency = "USD",
                IsActive = true,
                SortOrder = 99,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                PlanId = plan.Id,
                Status = SubscriptionStatus.Active,
                CurrentPeriodStart = now.Date,
                CurrentPeriodEnd = now.Date.AddMonths(1),
                NextRenewalAt = now.Date.AddMonths(1),
                CreatedAt = now,
                UpdatedAt = now,
            };
            var period = new BillingPeriod
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscription.Id,
                Status = BillingPeriodStatus.Open,
                StartsAt = now.Date,
                EndsAt = now.Date.AddMonths(1),
                IncludedCredits = plan.MonthlyCreditAllowance,
                CreatedAt = now,
            };
            db.Plans.Add(plan);
            db.Subscriptions.Add(subscription);
            db.BillingPeriods.Add(period);
            await db.SaveChangesAsync();

            var usage = NewCompletedUsage(workspace.Id, user.Id, "fixture:usage");
            db.UsageTransactions.Add(usage);
            await db.SaveChangesAsync();

            var ledger = new CreditLedgerService(db, NewOptions(enabled: chargingEnabledForFixture));
            var firstLotId = Guid.Empty;
            if (lotCredits > 0)
            {
                var grant = await ledger.GrantAsync(
                    workspace.Id, CreditEntitlementType.IncludedMonthly, lotCredits,
                    "fixture:grant:first-lot", "Synthetic first-period lot", expiresAt: now.AddDays(1));
                firstLotId = grant.Entry.CreditEntitlementId!.Value;
            }

            var scenario = new BillingScenario(connection, db, workspace, firstLotId);
            scenario.FixtureUsageId = usage.Id;
            return scenario;
        }

        public async Task ExpireFirstLotAsync()
        {
            var entitlement = await _db.CreditEntitlements.SingleAsync(item => item.Id == FirstLotId);
            entitlement.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await _db.SaveChangesAsync();
        }

        public async Task<UsageTransaction> AddCompletedUsageAsync(string requestId)
        {
            var usage = NewCompletedUsage(WorkspaceId, _db.Users.AsNoTracking().Select(item => item.Id).Single(), requestId);
            _db.UsageTransactions.Add(usage);
            await _db.SaveChangesAsync();
            return usage;
        }

        public Task<CreditLedgerEntry> EntryAsync(Guid entryId) =>
            _db.CreditLedgerEntries.AsNoTracking().SingleAsync(item => item.Id == entryId);

        public Task<BillingAccountDto> GetAccountAsync() =>
            new BillingAccountService(
                _db,
                new BillingProvisioningService(_db, NullLogger<BillingProvisioningService>.Instance),
                NewOptions(enabled: false)).GetAccountAsync(WorkspaceId);

        public Task<int> PaymentAttemptCountAsync() =>
            _db.PaymentAttempts.AsNoTracking().CountAsync(item => item.WorkspaceId == WorkspaceId);

        public Task<int> LedgerRowCountAsync() =>
            _db.CreditLedgerEntries.AsNoTracking().CountAsync(item => item.WorkspaceId == WorkspaceId);

        public async Task<List<LedgerRow>> CaptureLedgerRowsAsync()
        {
            var rows = await _db.CreditLedgerEntries.AsNoTracking()
                .Where(item => item.WorkspaceId == WorkspaceId)
                .OrderBy(item => item.CreatedAt)
                .ToListAsync();
            return rows.Select(item => new LedgerRow(
                item.Id, item.CreditEntitlementId, item.UsageTransactionId, item.ReversesEntryId, item.Type,
                item.Amount, item.IdempotencyKey, item.Reason, item.ActorUserId, item.CreatedAt)).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            _connection.Dispose();
        }

        private static IOptions<BillingOptions> NewOptions(bool enabled) =>
            Options.Create(new BillingOptions
            {
                CustomerChargingEnabled = enabled,
                Provider = "unconfigured",
            });

        private static Workspace NewWorkspace() => new()
        {
            Id = Guid.NewGuid(),
            Name = "Debit lot attribution workspace",
            Slug = $"debit-lot-attribution-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        private static ApplicationUser NewUser() => new()
        {
            Id = Guid.NewGuid(),
            UserName = $"debit-lot-attribution-{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"DEBIT-LOT-ATTRIBUTION-{Guid.NewGuid():N}@EXAMPLE.TEST",
            Email = $"debit-lot-attribution-{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"DEBIT-LOT-ATTRIBUTION-{Guid.NewGuid():N}@EXAMPLE.TEST",
            DisplayName = "Debit Lot Attribution",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        private static UsageTransaction NewCompletedUsage(Guid workspaceId, Guid userId, string requestId) => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RequestId = requestId,
            Feature = UsageFeature.Generation,
            Provider = "synthetic",
            Model = "synthetic",
            Status = UsageTransactionStatus.Completed,
            IsBillable = true,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private sealed record LedgerRow(
        Guid Id, Guid? CreditEntitlementId, Guid? UsageTransactionId, Guid? ReversesEntryId,
        CreditLedgerEntryType Type, long Amount, string IdempotencyKey, string Reason,
        Guid? ActorUserId, DateTime CreatedAt);
}
