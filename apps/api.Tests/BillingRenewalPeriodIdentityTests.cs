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

// The remediation lane for the review finding that PR #1094 proved.
//
// PR #1094 (head 9dbb692efd1401bd8bc133fcbff983386dc406e) established two facts about
// PaymentLifecycleService.EnsurePeriodAndGrantAsync on two independent PostgreSQL transactions:
//
//   * The shipped guard against paying the included-credit allowance twice is the durable unique
//     index on BillingPeriod (SubscriptionId, StartsAt), enforced by the database engine.
//   * The guard is ENTIRELY that index. The grant key is `included:{subscriptionId}:{periodId}`,
//     and `periodId` was minted with Guid.NewGuid() per delivery, so two deliveries that each read
//     "no such period" computed two DIFFERENT keys. #1094's mutation test dropped the index and the
//     workspace was paid 2x the monthly allowance: the ledger's own unique index on
//     (WorkspaceId, IdempotencyKey) never saw a collision, because there was not one.
//
// This lane closes that gap instead of re-describing it. The period primary key is now derived from
// the durable renewal identity - the subscription and the period it renews, i.e. exactly the natural
// key the unique index already treats as one period - rather than from per-delivery entropy. Two
// independent deliveries therefore compute the SAME period key and the SAME grant key, so the
// duplicate is refused by constraints that already exist in the shipped schema: the BillingPeriod
// primary key and the unique indexes on CreditEntitlements and CreditLedgerEntries. No migration, no
// new index, no lock protocol, no provider call, no price, no entitlement amount and no charge is
// touched; CustomerChargingEnabled stays at the shipped default of false and every identifier below
// is synthetic.
//
// What each test pins:
//
//   A. The identity is a pure function of (subscription, period): stable, distinguishing, never
//      empty, and pinned to a golden value. A silently constant identity would be a worse bug than
//      the one this replaces - the first cut of this patch did exactly that through a mistyped hash
//      call, and every renewal collided on one primary key. This test is what makes that class of
//      mistake impossible to land quietly. It needs no database, so it runs in every CI lane.
//   B. The shipped renewal path end to end: concurrent duplicate deliveries converge on one
//      identity-derived grant key, one period, one allowance - and at least one delivery COMMITS.
//      The liveness half matters: a guard that refuses both deliveries leaves the workspace with no
//      provisioned period at all, which is a different outage, not a win.
//   C. The defence-in-depth claim, as a mutation test: with the (SubscriptionId, StartsAt) index
//      verified dropped from the schema, the double grant that #1094 observed in the same
//      circumstances no longer happens.
//   D. Backward compatibility: a period created before this derivation, with a random primary key
//      and its historical grant key, is still found and replayed rather than re-granted.
public sealed class BillingRenewalPeriodIdentityTests : IAsyncLifetime
{
    // Fixed 2026 UTC boundaries, identical on both sides of every race, so the two deliveries
    // contend for one (SubscriptionId, StartsAt) identity.
    private static readonly DateTime PeriodStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SeededAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long MonthlyAllowance = 500;
    private const int RaceRounds = 8;
    // Golden value for RenewalPeriodIdentity(1111...1111, 2026-03-01T00:00:00Z). Pinned so a change
    // to the derivation cannot be silent: it has to be an explicit, reviewed decision.
    private static readonly Guid GoldenIdentity = Guid.Parse("1ecaf78b-e243-5d6d-92c0-b222f64c8e83");
    private readonly string schema = $"bill_ident_{Guid.NewGuid():N}";
    private string connectionString = null!;
    private DbContextOptions<TaslimDbContext> options = null!;

    public async Task InitializeAsync()
    {
        connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? string.Empty;
        // The identity derivation test in this class needs no database, so it must still run on the
        // default lane. Without a PostgreSQL service the schema is simply not created and the three
        // concurrency tests skip through their attribute; they cannot fail here.
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public void Renewal_identity_is_a_function_of_the_renewal_and_not_of_the_delivery()
    {
        var subscription = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        // Stable: the same renewal, minted again by a redelivered webhook or a new process, is the
        // same identity. This is the whole guard, so it must not depend on call order or entropy.
        Assert.Equal(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(subscription, PeriodStart));
        Assert.Equal(PaymentLifecycleService.RenewalPeriodIdentity(subscription, PeriodStart), PaymentLifecycleService.RenewalPeriodIdentity(subscription, PeriodStart));
        // Distinguishing: a different subscription, and the same subscription in a different renewal
        // period, are different identities. Constant or colliding values would fuse unrelated
        // renewals together, which is why both directions are asserted.
        Assert.NotEqual(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(other, PeriodStart));
        Assert.NotEqual(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(subscription, PeriodStart.AddMonths(1)));
        Assert.NotEqual(Guid.Empty, PaymentLifecycleService.RenewalPeriodIdentity(subscription, PeriodStart));
        // Name-based (RFC 9562 version 5) so the derivation is identifiable in the data itself.
        Assert.Equal('5', PaymentLifecycleService.RenewalPeriodIdentity(subscription, PeriodStart).ToString("N")[12]);
    }

    [DisposablePostgresFact]
    public async Task Concurrent_renewal_deliveries_converge_on_one_identity_derived_grant_and_stay_live()
    {
        for (var round = 0; round < RaceRounds; round++)
        {
            var workspace = NewWorkspace($"converge-{round}");
            var plan = NewPlan($"converge-plan-{round}");
            var subscription = NewSubscription(workspace.Id, plan.Id);
            await SeedAsync(workspace, plan, subscription);
            var outcomes = await Task.WhenAll(
                DeliverAsync(subscription, workspace, "webhook:renewal-identity-a", "Provider renewal succeeded (delivery 1)."),
                DeliverAsync(subscription, workspace, "webhook:renewal-identity-b", "Provider renewal succeeded (delivery 2)."));
            // Liveness: exactly the guarantee a duplicate-delivery guard must not break. If both
            // deliveries were refused, the renewal would leave no period behind.
            Assert.Contains(outcomes, item => item is null);
            foreach (var failure in outcomes.Where(item => item is not null))
                Assert.True(IsAdmissibleRaceFailure(failure!), $"Unexpected failure in round {round}: {failure}");
            var identity = PaymentLifecycleService.RenewalPeriodIdentity(subscription.Id, PeriodStart);
            await using var probe = new TaslimDbContext(options);
            var period = await probe.BillingPeriods.AsNoTracking().SingleAsync(item => item.SubscriptionId == subscription.Id);
            // The shipped path now mints the identity, rather than only the replicated body below.
            Assert.Equal(identity, period.Id);
            Assert.Equal(MonthlyAllowance, period.IncludedCredits);
            var keys = await probe.CreditLedgerEntries.AsNoTracking()
                .Where(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant)
                .Select(item => item.IdempotencyKey).ToListAsync();
            Assert.Equal(1, keys.Count);
            // One grant key for two deliveries: the collision the ledger deduplicates on now exists.
            Assert.Equal($"included:{subscription.Id}:{identity}", keys[0]);
            Assert.Equal(1, await probe.CreditEntitlements.AsNoTracking()
                .CountAsync(item => item.WorkspaceId == workspace.Id && item.Type == CreditEntitlementType.IncludedMonthly));
            Assert.Equal(MonthlyAllowance, await probe.CreditLedgerEntries.AsNoTracking()
                .Where(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant)
                .SumAsync(item => (long?)item.Amount) ?? 0);
        }
    }

    [DisposablePostgresFact]
    public async Task One_allowance_survives_even_with_the_renewal_unique_index_dropped()
    {
        // The mutation that made PR #1094's evidence load-bearing: remove the durable unique index on
        // BillingPeriod (SubscriptionId, StartsAt) and, before this patch, two transactions both paid
        // the allowance (periods=2, grants=2, credits=1000). Same mutation, now refused by the
        // identity itself. The drop is verified, so a wrong index name cannot masquerade as evidence.
        var dropped = await DropRenewalUniqueIndexAsync();
        Assert.True(dropped.RemovedCount >= 1, $"Expected to drop the (SubscriptionId, StartsAt) index; indexes found: {dropped.Instances}");
        Assert.Equal(0, dropped.RemainingCount);
        var workspace = NewWorkspace("mutation");
        var plan = NewPlan("mutation-plan");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        await SeedAsync(workspace, plan, subscription);
        var winnerRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Both deliveries mint the period identity exactly as the shipped service does, outside the
        // transactions, so each delivery's grant key is observable even for the refused one.
        var winnerPeriod = NewPeriod(subscription.Id);
        var loserPeriod = NewPeriod(subscription.Id);
        string? loserError = null;
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
        // The premise, mirrored from #1094: the two deliveries no longer compute divergent keys. It
        // is the identity, not the index, that now carries the invariant.
        Assert.Equal(winnerPeriod.Id, loserPeriod.Id);
        Assert.Equal(GrantKey(subscription.Id, winnerPeriod.Id), GrantKey(subscription.Id, loserPeriod.Id));
        Assert.NotNull(loserError);
        Assert.True(IsAdmissibleRaceText(loserError!), $"Expected the loser to be refused by the identity itself, got: {loserError}");
        await using var probe = new TaslimDbContext(options);
        Assert.Equal(1, await probe.BillingPeriods.AsNoTracking().CountAsync(item => item.SubscriptionId == subscription.Id));
        Assert.Equal(1, await probe.CreditEntitlements.AsNoTracking().CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(1, await probe.CreditLedgerEntries.AsNoTracking().CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(winnerPeriod.Id, (await probe.BillingPeriods.AsNoTracking().SingleAsync(item => item.SubscriptionId == subscription.Id)).Id);
        // The money statement, under the mutation: one allowance, not two.
        Assert.Equal(MonthlyAllowance, await probe.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant)
            .SumAsync(item => (long?)item.Amount) ?? 0);
    }

    [DisposablePostgresFact]
    public async Task Periods_created_before_the_identity_replay_on_their_historical_key()
    {
        var workspace = NewWorkspace("legacy");
        var plan = NewPlan("legacy-plan");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        // A row as the shipped code minted it before this lane: entropy for the primary key, and a
        // grant key built from that entropy.
        var legacyPeriod = new BillingPeriod
        {
            Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Open,
            StartsAt = PeriodStart, EndsAt = PeriodEnd, IncludedCredits = MonthlyAllowance, CreatedAt = SeededAt,
        };
        await SeedAsync(workspace, plan, subscription);
        await using (var seed = new TaslimDbContext(options))
            // Same audit text the shipped renewal path produces for this redelivery: the ledger
            // replay rule compares the reason, so a redelivered event is a matching replay.
            await ProvisionPeriodAndGrantAsync(seed, workspace, plan, subscription, legacyPeriod, "subscription renewal: Provider renewal succeeded (redelivery).");
        var identity = PaymentLifecycleService.RenewalPeriodIdentity(subscription.Id, PeriodStart);
        Assert.NotEqual(identity, legacyPeriod.Id);
        // A redelivered renewal for that same period must reuse the stored row and its historical
        // key. This is the compatibility claim: the derivation cannot re-grant a legacy period.
        await using var db = new TaslimDbContext(options);
        await NewLifecycle(db).RenewSubscriptionAsync(workspace.Id, subscription.Id, PeriodStart, PeriodEnd, "webhook:renewal-legacy-redelivery", "Provider renewal succeeded (redelivery).");
        await using var probe = new TaslimDbContext(options);
        var period = await probe.BillingPeriods.AsNoTracking().SingleAsync(item => item.SubscriptionId == subscription.Id);
        Assert.Equal(legacyPeriod.Id, period.Id);
        Assert.Equal(1, await probe.CreditLedgerEntries.AsNoTracking()
            .CountAsync(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant));
        Assert.Equal(MonthlyAllowance, await probe.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant)
            .SumAsync(item => (long?)item.Amount) ?? 0);
    }

    // Drops every unique index on BillingPeriod (SubscriptionId, StartsAt) and re-reads the catalog,
    // so the caller can assert both that a real drop happened and that nothing of that shape is left.
    private async Task<(int RemovedCount, int RemainingCount, string Instances)> DropRenewalUniqueIndexAsync()
    {
        var found = new List<string>();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString);
        await connection.OpenAsync();
        await using (var find = connection.CreateCommand())
        {
            find.CommandText = """
                SELECT indexname FROM pg_indexes
                WHERE schemaname = current_schema() AND tablename = 'BillingPeriods'
                  AND indexdef ILIKE '%subscriptionid%' AND indexdef ILIKE '%startsat%'
                  AND indexdef ILIKE '%unique%'
                """;
            await using var reader = await find.ExecuteReaderAsync();
            while (await reader.ReadAsync()) found.Add(reader.GetString(0));
        }
        foreach (var index in found)
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP INDEX IF EXISTS \"{schema}\".\"{index}\"";
            await drop.ExecuteNonQueryAsync();
        }
        await using (var recheck = connection.CreateCommand())
        {
            recheck.CommandText = """
                SELECT count(*) FROM pg_indexes
                WHERE schemaname = current_schema() AND tablename = 'BillingPeriods'
                  AND indexdef ILIKE '%subscriptionid%' AND indexdef ILIKE '%startsat%'
                  AND indexdef ILIKE '%unique%'
                """;
            var remaining = Convert.ToInt32(await recheck.ExecuteScalarAsync());
            return (found.Count, remaining, string.Join(" | ", found));
        }
    }

    private static BillingPeriod NewPeriod(Guid subscriptionId) => new()
    {
        // The shipped derivation, not test entropy: this is what makes the collision real.
        Id = PaymentLifecycleService.RenewalPeriodIdentity(subscriptionId, PeriodStart),
        SubscriptionId = subscriptionId, Status = BillingPeriodStatus.Open,
        StartsAt = PeriodStart, EndsAt = PeriodEnd, IncludedCredits = MonthlyAllowance, CreatedAt = SeededAt,
    };
    private static string GrantKey(Guid subscriptionId, Guid periodId) => $"included:{subscriptionId}:{periodId}";
    private static Task<BillingPeriod?> FindPeriodAsync(TaslimDbContext db, Guid subscriptionId) =>
        db.BillingPeriods.SingleOrDefaultAsync(item => item.SubscriptionId == subscriptionId && item.StartsAt == PeriodStart);
    // Mirrors the body of EnsurePeriodAndGrantAsync on a caller-owned transaction so two contexts can
    // be parked in the same read window. Charging stays off, so nothing here can debit anyone.
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
    private async Task<Exception?> DeliverAsync(Subscription subscription, Workspace workspace, string key, string reason)
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
    // The refused transaction can surface at several depths: the raw PostgresException, wrapped by EF
    // as DbUpdateException, and/or wrapped again by the execution strategy. Walk the whole chain.
    private static bool IsAdmissibleRaceFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres && IsRaceSqlState(postgres.SqlState)) return true;
        return false;
    }
    private static bool IsRaceSqlState(string? sqlState) =>
        sqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
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
        Id = Guid.NewGuid(), Name = $"Identity {label}", Slug = $"bill-ident-{label}-{Guid.NewGuid():N}",
        Type = WorkspaceType.Personal, CreatedAt = SeededAt, UpdatedAt = SeededAt,
    };
    // Plan.Code is varchar(40) with a unique index, so the suffix is a short random token.
    private static Plan NewPlan(string code) => new()
    {
        Id = Guid.NewGuid(), Code = $"{code}-{Guid.NewGuid():N}"[..40], Name = $"Identity plan {code}",
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
