using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Notifications;
using Taslim.Api.Payments;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Independent review finding against PR #1035 (head d285fe54a5b1fa058341ce4019c0e283579b5ade).
//
// #1035 pins the credit-ledger idempotency of the provisioning path, and #1017 counts the rows a
// rollover leaves behind. Both do it on a single in-memory SQLite context, so every "replayed
// delivery" they observe is a SEQUENTIAL replay: the first delivery has already committed, the
// second delivery reads it back, and the ledger deduplicates on the durable key. That proves the
// replay rule. It does not prove the INVARIANT, because two facts only collide under real
// concurrency:
//
//   * `PaymentLifecycleService.EnsurePeriodAndGrantAsync` derives the grant key from a
//     client-generated primary key: `included:{subscriptionId}:{periodId}`, where `periodId` is a
//     `Guid.NewGuid()` minted in the application (PaymentServices.cs:372-382). Two deliveries that
//     each decide "this period does not exist yet" therefore mint two DIFFERENT period ids and two
//     DIFFERENT grant keys. The ledger's unique index on (WorkspaceId, IdempotencyKey) cannot see a
//     collision that does not exist, so the idempotency key is NOT the guard under concurrency.
//   * The only thing that can stop a second paid-for allowance is the durable unique index on
//     BillingPeriod (SubscriptionId, StartsAt) (TaslimDbContext.cs:2143), which is enforced by the
//     database engine, not by the replay branch. A single SQLite connection opened once cannot
//     observe two independent transactions colliding on it.
//
// So the question #1035 leaves open is: when a provider redelivers the same renewal and both
// deliveries are admitted as two INDEPENDENT PostgreSQL transactions, can the workspace end up
// with more than one BillingPeriod row and more than one `IncludedMonthly` grant?
//
// These two tests answer it on a disposable local PostgreSQL, one DbContext per transaction (so
// two real server-side backends and two real transactions), with no payment provider, no customer
// and no charging: BillingOptions.CustomerChargingEnabled stays at the shipped default of false and
// every identifier below is synthetic.
//
//   1. A service-level race: repeated rounds of two concurrent `RenewSubscriptionAsync` deliveries
//      of the same renewal, asserting the invariant (at most one period, at most one grant, at most
//      one allowance of credits) on every round whatever the interleaving and whichever transaction
//      the database aborts.
//   2. A deterministic admission barrier: both transactions are held until each has OBSERVED "no
//      such period", then the loser writes against A's committed row. This forces the exact read
//      window the sequential SQLite tests cannot enter, and pins that the two deliveries really did
//      compute divergent grant keys - so the pass is attributable to the period unique index rather
//      than to luck or to ledger key replay.
//
// Test-only. No production behaviour, schema, migration, configuration, secret or provider
// integration is touched, and PR #1035's own test file is not modified.
public sealed class BillingRenewalConcurrentTransactionTests : IAsyncLifetime
{
    // Fixed 2026 UTC boundaries: identical values on both sides of every race, so the two
    // transactions genuinely contend for the same (SubscriptionId, StartsAt) key rather than for
    // two rows that differ only by clock jitter.
    private static readonly DateTime PeriodStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SeededAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long MonthlyAllowance = 500;

    // Number of service-level race rounds. Every round must satisfy the invariant; the point is
    // that the guarantee does not depend on which interleaving the scheduler happens to produce.
    private const int RaceRounds = 12;

    private readonly string schema = $"bill_conc_{Guid.NewGuid():N}";
    private DbContextOptions<TaslimDbContext> options = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
            .Options;

        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    [DisposablePostgresFact]
    public async Task Concurrent_duplicate_renewal_deliveries_never_grant_the_included_allowance_twice()
    {
        for (var round = 0; round < RaceRounds; round++)
        {
            // A fresh subscription per round keeps the invariant assertion unambiguous: any second
            // period or second grant can only come from this round's collision, never from history.
            var workspace = NewWorkspace($"race-{round}");
            var plan = NewPlan($"race-plan-{round}");
            var subscription = NewSubscription(workspace.Id, plan.Id);
            await SeedAsync(workspace, plan, subscription);

            // Two independent deliveries of the SAME renewal: same workspace, same subscription,
            // same period boundaries. Only the audit reason differs, which is what a redelivered
            // provider event looks like from the application's side.
            var outcomes = await Task.WhenAll(
                DeliverAsync(subscription, workspace, plan, "webhook:renewal-race-a", "Provider renewal succeeded (delivery 1)."),
                DeliverAsync(subscription, workspace, plan, "webhook:renewal-race-b", "Provider renewal succeeded (delivery 2)."));

            // The race is won by exactly one transaction. The loser is expected to be refused by the
            // database (unique violation on the durable period key, serialisation failure or
            // deadlock detection) - that refusal IS the guarantee. What must never happen is two
            // paid-for allowances reaching the ledger, whether or not both calls returned.
            foreach (var failure in outcomes.Where(item => item is not null))
                Assert.True(IsAdmissibleRaceFailure(failure!), $"Unexpected failure in round {round}: {failure}");

            await using var probe = new TaslimDbContext(options);

            Assert.Equal(1, await probe.BillingPeriods.AsNoTracking()
                .CountAsync(item => item.SubscriptionId == subscription.Id && item.StartsAt == PeriodStart));

            Assert.Equal(1, await probe.CreditLedgerEntries.AsNoTracking()
                .CountAsync(item => item.WorkspaceId == workspace.Id
                    && item.IdempotencyKey.StartsWith($"included:{subscription.Id}:")));

            Assert.Equal(1, await probe.CreditEntitlements.AsNoTracking()
                .CountAsync(item => item.WorkspaceId == workspace.Id && item.Type == CreditEntitlementType.IncludedMonthly));

            // The money statement: the workspace holds exactly one monthly allowance, not two.
            Assert.Equal(MonthlyAllowance, await probe.CreditLedgerEntries.AsNoTracking()
                .Where(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant)
                .SumAsync(item => (long?)item.Amount) ?? 0);
        }
    }

    [DisposablePostgresFact]
    public async Task Two_transactions_that_both_observe_no_period_cannot_both_grant_an_allowance()
    {
        var workspace = NewWorkspace("barrier");
        var plan = NewPlan("barrier-plan");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        await SeedAsync(workspace, plan, subscription);

        var winnerRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The period ids are minted here, outside the transactions, so each delivery's grant key is
        // observable even for the transaction the database refuses.
        var winnerPeriod = NewPeriod(subscription.Id);
        var loserPeriod = NewPeriod(subscription.Id);
        string? loserError = null;

        // Transaction A: reads "no period", waits until B has read the same, then writes and commits.
        var winner = Task.Run(async () =>
        {
            await using var db = new TaslimDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            Assert.Null(await FindPeriodAsync(db, subscription.Id));
            winnerRead.SetResult();
            await loserRead.Task;

            await ProvisionPeriodAndGrantAsync(db, workspace, plan, subscription, winnerPeriod, "subscription renewal: admitted first");
            await transaction.CommitAsync();
        });

        // Transaction B: enters the identical read window, then is released only after A has
        // committed, so it collides with durable state rather than with an in-flight lock.
        var loser = Task.Run(async () =>
        {
            try
            {
                await using var db = new TaslimDbContext(options);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                Assert.Null(await FindPeriodAsync(db, subscription.Id));
                loserRead.SetResult();
                await winnerRead.Task;
                await loserGo.Task;

                await ProvisionPeriodAndGrantAsync(db, workspace, plan, subscription, loserPeriod, "subscription renewal: admitted second");
                await transaction.CommitAsync();
            }
            catch (Exception exception)
            {
                loserError = exception.ToString();
            }
        });

        await winner;
        loserGo.SetResult();
        await loser;

        // The premise of the finding, now proven rather than assumed: the two deliveries minted two
        // different period ids, hence two different grant keys, so ledger key equality is
        // demonstrably NOT what protects the allowance on this path.
        Assert.NotEqual(winnerPeriod.Id, loserPeriod.Id);
        Assert.NotEqual(GrantKey(subscription.Id, winnerPeriod.Id), GrantKey(subscription.Id, loserPeriod.Id));

        Assert.NotNull(loserError);
        Assert.True(IsAdmissibleRaceText(loserError!), $"Expected the loser to be refused by the durable period index, got: {loserError}");

        await using var probe = new TaslimDbContext(options);
        Assert.Equal(1, await probe.BillingPeriods.AsNoTracking().CountAsync(item => item.SubscriptionId == subscription.Id));
        Assert.Equal(1, await probe.CreditEntitlements.AsNoTracking().CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(1, await probe.CreditLedgerEntries.AsNoTracking().CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(winnerPeriod.Id, (await probe.BillingPeriods.AsNoTracking().SingleAsync(item => item.SubscriptionId == subscription.Id)).Id);
        Assert.Equal(MonthlyAllowance, await probe.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant)
            .SumAsync(item => (long?)item.Amount) ?? 0);
    }

    private static BillingPeriod NewPeriod(Guid subscriptionId) => new()
    {
        Id = Guid.NewGuid(), SubscriptionId = subscriptionId, Status = BillingPeriodStatus.Open,
        StartsAt = PeriodStart, EndsAt = PeriodEnd, IncludedCredits = MonthlyAllowance, CreatedAt = SeededAt,
    };

    private static string GrantKey(Guid subscriptionId, Guid periodId) => $"included:{subscriptionId}:{periodId}";

    private static Task<BillingPeriod?> FindPeriodAsync(TaslimDbContext db, Guid subscriptionId) =>
        db.BillingPeriods.SingleOrDefaultAsync(item => item.SubscriptionId == subscriptionId && item.StartsAt == PeriodStart);

    // Mirrors the body of EnsurePeriodAndGrantAsync on a caller-owned transaction so the two
    // contexts can be parked inside the same read window. The shipped default charging switch stays
    // off, so nothing here can debit a customer.
    private static async Task ProvisionPeriodAndGrantAsync(
        TaslimDbContext db, Workspace workspace, Plan plan, Subscription subscription, BillingPeriod period, string reason)
    {
        db.BillingPeriods.Add(period);
        await db.SaveChangesAsync();

        var ledger = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false }));
        await ledger.GrantAsync(workspace.Id, CreditEntitlementType.IncludedMonthly, plan.MonthlyCreditAllowance,
            GrantKey(subscription.Id, period.Id), reason, PeriodEnd, period.Id,
            sourceReference: subscription.ProviderSubscriptionReference);
    }

    private async Task<Exception?> DeliverAsync(Subscription subscription, Workspace workspace, Plan plan, string key, string reason)
    {
        await using var db = new TaslimDbContext(options);
        try
        {
            await NewLifecycle(db).RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodStart, PeriodEnd, key, reason);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    // The losing transaction can surface at several depths: the raw PostgresException, wrapped by
    // EF as DbUpdateException, and/or wrapped again once Npgsql's retrying execution strategy has
    // exhausted its retries. Walk the whole chain rather than pattern-matching one level.
    private static bool IsAdmissibleRaceFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres && IsRaceSqlState(postgres.SqlState)) return true;
        return false;
    }

    private static bool IsRaceSqlState(string? sqlState) =>
        sqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    // The barrier test catches the failure as text so a refused write cannot be mistaken for a
    // skipped assertion; the same three SQL states are the only admissible outcomes.
    private static bool IsAdmissibleRaceText(string failure) =>
        failure.Contains(PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal)
        || failure.Contains(PostgresErrorCodes.SerializationFailure, StringComparison.Ordinal)
        || failure.Contains(PostgresErrorCodes.DeadlockDetected, StringComparison.Ordinal)
        || failure.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
        || failure.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);

    private async Task SeedAsync(Workspace workspace, Plan plan, Subscription subscription)
    {
        await using var db = new TaslimDbContext(options);
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
    }

    private static PaymentLifecycleService NewLifecycle(TaslimDbContext db) => new(
        db,
        new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false })),
        new NullNotificationEventWriter(),
        NullLogger<PaymentLifecycleService>.Instance);

    private static Workspace NewWorkspace(string label) => new()
    {
        Id = Guid.NewGuid(), Name = $"Concurrency {label}", Slug = $"bill-conc-{label}-{Guid.NewGuid():N}",
        Type = WorkspaceType.Personal, CreatedAt = SeededAt, UpdatedAt = SeededAt,
    };

    // Plan.Code is varchar(40) with a unique index, so the disambiguating suffix is a short
    // random token rather than a full guid.
    private static Plan NewPlan(string code) => new()
    {
        Id = Guid.NewGuid(), Code = $"{code}-{Guid.NewGuid():N}"[..40], Name = $"Concurrency plan {code}",
        MonthlyCreditAllowance = MonthlyAllowance, MonthlyPriceUsd = 0m, Currency = "USD", IsActive = true,
        SortOrder = 500, CreatedAt = SeededAt, UpdatedAt = SeededAt,
    };

    private static Subscription NewSubscription(Guid workspaceId, Guid planId) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = planId, Status = SubscriptionStatus.Active,
        BillingProvider = "synthetic", ProviderSubscriptionReference = $"sub_{Guid.NewGuid():N}",
        CurrentPeriodStart = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        CurrentPeriodEnd = PeriodStart, NextRenewalAt = PeriodStart,
        CreatedAt = SeededAt, UpdatedAt = SeededAt,
    };

    private sealed class NullNotificationEventWriter : INotificationEventWriter
    {
        public Task CreateGenerationCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationFailedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationAttentionAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateBillingPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
