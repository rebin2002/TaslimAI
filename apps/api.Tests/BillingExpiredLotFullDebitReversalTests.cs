using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #330, synthetic ledger only. This file exercises the literal example recorded in the
/// issue body: an expired grant of +100, a historical debit of -100 recorded against that period,
/// then a fresh active grant of +100 whose available balance must be exactly 100.
///
/// It is deliberately isolated from the other billing test files:
/// - <c>BillingTests.cs</c> and <c>apps/api/Billing/BillingServices.cs</c> belong to PR #109;
/// - <c>BillingGrantExpiryContractTests.cs</c> belongs to PR #690 and uses a partially consumed
///   lot (100 granted / 25 spent, then a 50-credit lot) plus manually seeded attributed debits;
/// - <c>BillingCreditHistoryTests.cs</c> belongs to PR #761 and covers history visibility.
/// Nothing here asserts a lot-allocation order or a refund-destination policy, both of which the
/// accountable accounting owner has deferred. No provider, payment gateway, customer balance or
/// production database is touched: every fixture row is synthetic and lives in a throwaway
/// in-memory SQLite database.
/// </summary>
public sealed class BillingExpiredLotFullDebitReversalTests
{
    private const long LotCredits = 100;

    [Fact]
    public async Task Sqlite_fully_consumed_expired_lot_leaves_next_grant_fully_available()
    {
        await using var scenario = await FullyConsumedExpiredLot.CreateAsync();

        var account = await scenario.GetAccountAsync();

        // The fresh lot granted 100 and has not been spent, so both projections must read 100.
        Assert.Equal(LotCredits, account.Credits.IncludedRemaining);
        Assert.Equal(LotCredits, account.Credits.TotalRemaining);
        Assert.Equal(0, await scenario.PaymentAttemptCountAsync());
    }

    [Fact]
    public async Task Sqlite_fully_consumed_expired_lot_admits_a_next_period_debit()
    {
        await using var scenario = await FullyConsumedExpiredLot.CreateAsync();

        var laterUsage = await scenario.AddCompletedUsageAsync("expiredlot:usage:new-period");
        var laterDebit = await scenario.ChargingEnabledLedger.RecordUsageDebitAsync(
            scenario.Workspace.Id, laterUsage.Id, 30, "expiredlot:debit:new-period",
            "Synthetic new-period usage against the fresh grant");

        Assert.NotNull(laterDebit);
        Assert.True(laterDebit!.Created);
        Assert.Equal(-30L, laterDebit.Entry.Amount);
        Assert.Equal(1, await scenario.DebitCountForUsageAsync(laterUsage.Id));

        var account = await scenario.GetAccountAsync();
        Assert.Equal(70L, account.Credits.TotalRemaining);
    }

    [Fact]
    public async Task Sqlite_reversing_the_expired_lot_debit_does_not_inflate_next_grant_availability()
    {
        await using var scenario = await FullyConsumedExpiredLot.CreateAsync();

        var rowsBefore = await scenario.CaptureLedgerRowsAsync();
        var entitlementsBefore = await scenario.CaptureEntitlementsAsync();
        const string refundKey = "expiredlot:refund:historical-debit";
        const string refundReason = "Synthetic reversal of the expired-period debit";

        var refund = await scenario.ChargingEnabledLedger.RefundAsync(
            scenario.Workspace.Id, scenario.HistoricalDebitId!.Value, refundKey, refundReason);

        Assert.True(refund.Created);
        Assert.Equal(CreditLedgerEntryType.Refund, refund.Entry.Type);
        Assert.Equal(LotCredits, refund.Entry.Amount);
        Assert.Equal(scenario.HistoricalDebitId, refund.Entry.ReversesEntryId);

        // Replay with the same key is idempotent; a second key may not compensate twice.
        var replay = await scenario.ChargingEnabledLedger.RefundAsync(
            scenario.Workspace.Id, scenario.HistoricalDebitId!.Value, refundKey, refundReason);
        Assert.False(replay.Created);
        Assert.Equal(refund.Entry.Id, replay.Entry.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.ChargingEnabledLedger.RefundAsync(
            scenario.Workspace.Id, scenario.HistoricalDebitId!.Value, "expiredlot:refund:second-key", refundReason));

        // Whatever the deferred refund-destination policy decides, reversing an expired-period
        // debit must never leave the next grant showing more than the 100 it actually granted.
        var afterRefund = await scenario.GetAccountAsync();
        Assert.Equal(LotCredits, afterRefund.Credits.TotalRemaining);

        // ...and debit admission must agree with that display: 30 fits, a further 71 does not.
        var firstUsage = await scenario.AddCompletedUsageAsync("expiredlot:usage:reversal-first");
        var accepted = await scenario.ChargingEnabledLedger.RecordUsageDebitAsync(
            scenario.Workspace.Id, firstUsage.Id, 30, "expiredlot:debit:reversal-first",
            "Synthetic post-reversal usage");
        Assert.NotNull(accepted);
        Assert.True(accepted!.Created);

        var overflowUsage = await scenario.AddCompletedUsageAsync("expiredlot:usage:reversal-overflow");
        var overflow = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.ChargingEnabledLedger.RecordUsageDebitAsync(
                scenario.Workspace.Id, overflowUsage.Id, 71, "expiredlot:debit:reversal-overflow",
                "Synthetic post-reversal overdraw attempt"));
        Assert.Contains("does not have enough credits", overflow.Message, StringComparison.Ordinal);

        // Append-only auditability and the charging-disabled default survive the reversal.
        var rowsAfter = await scenario.CaptureLedgerRowsAsync();
        var byId = rowsAfter.ToDictionary(item => item.Id);
        foreach (var original in rowsBefore)
            Assert.Equal(original, byId[original.Id]);
        Assert.Equal(rowsBefore.Count + 2, rowsAfter.Count);

        var entitlementsAfter = await scenario.CaptureEntitlementsAsync();
        Assert.Equal(entitlementsBefore, entitlementsAfter);
        Assert.Equal(0, await scenario.PaymentAttemptCountAsync());
    }

    [Fact]
    public async Task Sqlite_expired_lot_scenario_writes_nothing_while_customer_charging_is_disabled()
    {
        await using var scenario = await FullyConsumedExpiredLot.CreateAsync(chargingEnabledForFixture: false);

        var rowsBefore = await scenario.CaptureLedgerRowsAsync();

        // The shipped default (BillingOptions.CustomerChargingEnabled = false) must stay mutation-free.
        var debit = await scenario.ChargingDisabledLedger.RecordUsageDebitAsync(
            scenario.Workspace.Id, scenario.HistoricalUsageId, LotCredits, "expiredlot:debit:charging-disabled",
            "Synthetic debit while charging is disabled");

        Assert.Null(debit);
        Assert.Equal(0, await scenario.DebitCountForUsageAsync(scenario.HistoricalUsageId));
        Assert.Equal(0, await scenario.PaymentAttemptCountAsync());

        var rowsAfter = await scenario.CaptureLedgerRowsAsync();
        Assert.Equal(rowsBefore, rowsAfter);

        var account = await scenario.GetAccountAsync();
        Assert.Equal("disabled", account.PaymentStatus.Status);
        Assert.False(account.Actions.CheckoutAvailable);
        Assert.Contains(
            "charging is intentionally disabled", account.Actions.DisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the issue example: a +100 included grant, a -100 usage debit recorded against it by
    /// the production admission path, normal expiry of that lot, then a fresh active +100 grant.
    /// The in-memory SQLite database stays open for the scenario lifetime and is discarded with it.
    /// </summary>
    private sealed class FullyConsumedExpiredLot : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly TaslimDbContext _db;

        private FullyConsumedExpiredLot(SqliteConnection connection, TaslimDbContext db, Workspace workspace, ApplicationUser user)
        {
            _connection = connection;
            _db = db;
            Workspace = workspace;
            User = user;
            ChargingEnabledLedger = new CreditLedgerService(db, NewOptions(enabled: true));
            ChargingDisabledLedger = new CreditLedgerService(db, NewOptions(enabled: false));
        }

        public Workspace Workspace { get; }

        public ApplicationUser User { get; }

        public CreditLedgerService ChargingEnabledLedger { get; }

        public CreditLedgerService ChargingDisabledLedger { get; }

        /// <summary>Identifier of the real -100 debit written by <see cref="CreditLedgerService"/>; null when charging was never enabled.</summary>
        public Guid? HistoricalDebitId { get; private set; }

        public Guid HistoricalUsageId { get; private set; }

        public static async Task<FullyConsumedExpiredLot> CreateAsync(bool chargingEnabledForFixture = true)
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
            await AddSubscriptionFixtureAsync(db, workspace, now);

            var scenario = new FullyConsumedExpiredLot(connection, db, workspace, user);
            var historicalUsage = NewCompletedUsage(workspace.Id, user.Id, "expiredlot:usage:old-period");
            db.UsageTransactions.Add(historicalUsage);
            await db.SaveChangesAsync();
            scenario.HistoricalUsageId = historicalUsage.Id;

            if (!chargingEnabledForFixture)
            {
                await scenario.ChargingDisabledLedger.GrantAsync(
                    workspace.Id, CreditEntitlementType.IncludedMonthly, LotCredits,
                    "expiredlot:grant:charging-disabled", "Synthetic grant while charging is disabled",
                    expiresAt: now.AddDays(1));
                return scenario;
            }

            // Both the lot and the debit go through the real service so the recorded debit is a
            // production ledger movement rather than a hand-seeded row.
            var oldGrant = await scenario.ChargingEnabledLedger.GrantAsync(
                workspace.Id, CreditEntitlementType.IncludedMonthly, LotCredits,
                "expiredlot:grant:old-period", "Synthetic old-period grant", expiresAt: now.AddDays(1));

            var debit = await scenario.ChargingEnabledLedger.RecordUsageDebitAsync(
                workspace.Id, historicalUsage.Id, LotCredits, "expiredlot:debit:old-period",
                "Synthetic old-period usage that fully consumes the lot");
            if (debit is null)
                throw new InvalidOperationException("The synthetic old-period debit was not recorded.");
            scenario.HistoricalDebitId = debit.Entry.Id;

            // Normal period rollover: the source lot expires after its debit was already recorded.
            var sourceEntitlement = await db.CreditEntitlements
                .SingleAsync(item => item.Id == oldGrant.Entry.CreditEntitlementId);
            sourceEntitlement.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            await scenario.ChargingEnabledLedger.GrantAsync(
                workspace.Id, CreditEntitlementType.IncludedMonthly, LotCredits,
                "expiredlot:grant:new-period", "Synthetic fresh grant in the new period",
                expiresAt: DateTime.UtcNow.AddDays(30));
            return scenario;
        }

        public async Task<UsageTransaction> AddCompletedUsageAsync(string requestId)
        {
            var usage = NewCompletedUsage(Workspace.Id, User.Id, requestId);
            _db.UsageTransactions.Add(usage);
            await _db.SaveChangesAsync();
            return usage;
        }

        public Task<BillingAccountDto> GetAccountAsync() =>
            new BillingAccountService(
                _db,
                new BillingProvisioningService(
                    _db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BillingProvisioningService>.Instance),
                NewOptions(enabled: false)).GetAccountAsync(Workspace.Id);

        public Task<int> PaymentAttemptCountAsync() =>
            _db.PaymentAttempts.AsNoTracking().CountAsync(item => item.WorkspaceId == Workspace.Id);

        public Task<int> DebitCountForUsageAsync(Guid usageTransactionId) =>
            _db.CreditLedgerEntries.AsNoTracking().CountAsync(item =>
                item.WorkspaceId == Workspace.Id && item.UsageTransactionId == usageTransactionId &&
                item.Type == CreditLedgerEntryType.Debit);

        public async Task<List<LedgerRow>> CaptureLedgerRowsAsync()
        {
            var rows = await _db.CreditLedgerEntries.AsNoTracking()
                .Where(item => item.WorkspaceId == Workspace.Id)
                .OrderBy(item => item.CreatedAt)
                .ToListAsync();
            return rows.Select(item => new LedgerRow(
                item.Id, item.CreditEntitlementId, item.UsageTransactionId, item.ReversesEntryId, item.Type,
                item.Amount, item.IdempotencyKey, item.Reason, item.ActorUserId, item.CreatedAt)).ToList();
        }

        public async Task<List<EntitlementRow>> CaptureEntitlementsAsync()
        {
            var rows = await _db.CreditEntitlements.AsNoTracking()
                .Where(item => item.WorkspaceId == Workspace.Id)
                .OrderBy(item => item.Id)
                .ToListAsync();
            return rows.Select(item => new EntitlementRow(
                item.Id, item.BillingPeriodId, item.Type, item.GrantedCredits, item.GrantedAt, item.ExpiresAt,
                item.IdempotencyKey, item.SourceReference, item.CreatedAt)).ToList();
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

        private static async Task AddSubscriptionFixtureAsync(TaslimDbContext db, Workspace workspace, DateTime now)
        {
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Code = $"issue330-full-lot-{Guid.NewGuid():N}",
                Name = "Issue 330 fully consumed lot fixture",
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
        }

        private static Workspace NewWorkspace() => new()
        {
            Id = Guid.NewGuid(),
            Name = "Expired lot reversal workspace",
            Slug = $"expired-lot-reversal-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        private static ApplicationUser NewUser() => new()
        {
            Id = Guid.NewGuid(),
            UserName = $"expired-lot-reversal-{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"EXPIRED-LOT-REVERSAL-{Guid.NewGuid():N}@EXAMPLE.TEST",
            Email = $"expired-lot-reversal-{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"EXPIRED-LOT-REVERSAL-{Guid.NewGuid():N}@EXAMPLE.TEST",
            DisplayName = "Expired Lot Reversal",
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

    private sealed record EntitlementRow(
        Guid Id, Guid? BillingPeriodId, CreditEntitlementType Type, long GrantedCredits, DateTime GrantedAt,
        DateTime? ExpiresAt, string IdempotencyKey, string? SourceReference, DateTime CreatedAt);
}
