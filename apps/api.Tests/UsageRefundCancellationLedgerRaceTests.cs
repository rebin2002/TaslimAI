using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-only, bounded, provider-free QA of the refund-adjustment idempotency key path
/// (<c>usage:{id}:refund</c>, <see cref="UsageLedgerService.RefundAsync"/>) when two separate
/// database contexts race the cancellation finalizers.
///
/// This closes the coverage gap the independent reviewer recorded on PR #1077
/// ("Refund-adjustment idempotency key path (usage:{id}:refund) is not exercised on the cancel race
/// by these tests"). It is deliberately disjoint from <c>DocumentExportCancellationAttemptTests</c>,
/// which owns the <c>GenerationProviderAttempt</c> compare-and-set lane on PR #1077, and it changes
/// no production billing/usage code and no file owned by that PR.
///
/// Invariants asserted for every interleaving:
/// 1. exactly one reversal row survives the race, keyed once by <c>usage:{id}:refund</c>;
/// 2. the usage row and that reversal agree - status, <c>ChargedAmount</c>, <c>ReversedAmount</c>
///    and <c>RefundedAt</c> are never mixed across two finalizers;
/// 3. no negative or double credit: <c>ReversedAmount</c> never exceeds <c>ChargedAmount</c>;
/// 4. replaying the key, or a competing retry key, afterwards is a no-op.
///
/// Fixture note: <see cref="SafeUsageChargingService"/> hard-wires the customer charge to zero, so a
/// refundable row (Completed, billable, <c>ChargedAmount &gt; 0</c>) cannot be produced through the
/// completion path today. The charge leg is therefore committed as an explicit synthetic UPDATE that
/// models the state charging activation will produce. The race below is latent rather than
/// customer-facing for exactly that reason, and the PR evidence says so.
///
/// All data is synthetic: a disposable SQLite file database, or a per-test schema inside the CI
/// provisioned <c>taslim_tests</c> PostgreSQL database. No customer data, payment gateway, paid
/// provider, secret, migration, deployment or real charge is involved.
/// </summary>
public sealed class UsageRefundCancellationLedgerRaceTests : IDisposable
{
    private const decimal ChargedAmountUsd = 0.42m;
    private const string CancellationCode = "GENERATION_CANCELLED";
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"taslim_refund_race_{Guid.NewGuid():N}.db");

    private string SqliteConnectionString => $"Data Source={_sqlitePath};Pooling=False";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _sqlitePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private DbContextOptions<TaslimDbContext> SqliteOptions =>
        new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(SqliteConnectionString).Options;

    private static UsageLedgerService Ledger(TaslimDbContext db) => new(
        db,
        new AiCostCalculator(new AiModelCatalog(new ConfigurationBuilder().Build())),
        new SafeUsageChargingService(),
        new UsageCostControl(db, Options.Create(new UsageControlOptions()), NullLogger<UsageCostControl>.Instance),
        NullLogger<UsageLedgerService>.Instance);

    /// <summary>
    /// Seeds a Pending usage transaction plus the cancelled provider attempt a PR #1077 fence
    /// finalizer would have settled, so the attempt row can be checked for agreement.
    /// </summary>
    private async Task<Guid> SeedPendingTransactionAsync(DbContextOptions<TaslimDbContext> options)
    {
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"refund-race-{userId:N}@example.com",
            NormalizedUserName = $"REFUND-RACE-{userId:N}@EXAMPLE.COM",
            Email = $"refund-race-{userId:N}@example.com",
            NormalizedEmail = $"REFUND-RACE-{userId:N}@EXAMPLE.COM",
            DisplayName = "Refund Race Tester",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Refund Race Workspace",
            Slug = $"refund-race-{workspaceId:N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var jobId = Guid.NewGuid();
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Cancelled,
            InputJson = "{}",
            CreatedAt = now,
        });
        db.GenerationProviderAttempts.Add(new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(),
            GenerationJobId = jobId,
            JobConcurrencyToken = Guid.NewGuid(),
            IdempotencyKey = $"generation:{jobId:N}:attempt:1",
            Capability = GenerationJobTypes.DocumentGenerate,
            AttemptNumber = 1,
            Provider = "synthetic-contender",
            Status = GenerationProviderAttemptStatus.Cancelled,
            ResultClassification = "Cancelled",
            FailureCode = CancellationCode,
            ActualProviderCostUsd = null,
            ActualProviderCostKnown = false,
            FinalizationKey = $"generation:{jobId:N}:attempt:1",
            StartedAt = now,
            CompletedAt = now,
        });
        db.UsageTransactions.Add(new UsageTransaction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            GenerationJobId = jobId,
            RequestId = $"generation:{jobId:N}",
            Feature = UsageFeature.Document,
            Provider = "pending",
            Model = "pending",
            Status = UsageTransactionStatus.Pending,
            ChargedUnit = UsageChargeUnit.Usd,
            Currency = UsageCurrencies.Usd,
            ProviderCostUsd = 0m,
            ProviderCostKnown = false,
            ChargedAmount = 0m,
            ReversedAmount = 0m,
            IsBillable = false,
            CostBasis = UsageCostBasis.Estimated,
            CreatedAt = now,
            ReservedAt = now,
        });
        await db.SaveChangesAsync();
        return await db.UsageTransactions.Select(item => item.Id).SingleAsync();
    }

    /// <summary>
    /// Commits the charge leg as a synthetic UPDATE (Completed, billable, charged), because customer
    /// charging is hard-wired to zero in <see cref="SafeUsageChargingService"/>.
    /// </summary>
    private async Task CommitChargeLegAsync(DbContextOptions<TaslimDbContext> options, Guid txId)
    {
        await using var db = new TaslimDbContext(options);
        var row = await db.UsageTransactions.SingleAsync(item => item.Id == txId);
        var now = DateTime.UtcNow;
        row.Status = UsageTransactionStatus.Completed;
        row.Provider = "synthetic-contender";
        row.Model = "synthetic-model";
        row.ProviderCostUsd = 0.0130m;
        row.ProviderCostKnown = true;
        row.ChargedAmount = ChargedAmountUsd;
        row.ReversedAmount = 0m;
        row.IsBillable = true;
        row.ActualRecordedAt = now;
        row.BillableAt = now;
        row.CompletedAt = now;
        await db.SaveChangesAsync();
    }

    private static void AssertRefundLegRow(IReadOnlyList<UsageTransactionAdjustment> adjustments, Guid txId)
    {
        var adjustment = Assert.Single(adjustments);
        Assert.Equal(UsageTransactionAdjustmentType.Refund, adjustment.Type);
        Assert.Equal($"usage:{txId:N}:refund", adjustment.IdempotencyKey);
        Assert.Equal(ChargedAmountUsd, adjustment.AmountUsd);
    }

    private static void AssertRowAgreesWithItsReversal(UsageTransaction row, UsageTransactionAdjustment adjustment, string lane)
    {
        Assert.True(
            row.Status == UsageTransactionStatus.Refunded
                && row.ReversedAmount == row.ChargedAmount
                && row.ReversedAmount == adjustment.AmountUsd
                && row.RefundedAt.HasValue,
            $"[{lane}] the durable refund leg was restated by another context's cancellation finalizer: "
            + $"Status={row.Status}, ChargedAmount={row.ChargedAmount}, ReversedAmount={row.ReversedAmount}, "
            + $"RefundedAt={(row.RefundedAt.HasValue ? "set" : "null")}, committed reversal={adjustment.AmountUsd}. "
            + "The usage row and its single reversal must agree.");
        Assert.True(row.ReversedAmount <= row.ChargedAmount,
            $"[{lane}] ReversedAmount={row.ReversedAmount} exceeds ChargedAmount={row.ChargedAmount}: the ledger "
            + "pays out more than it charged (the cancellation zeroed the charge, the refund leg paid it out again).");
        Assert.Equal(0m, row.NetChargedAmount);
    }

    [Fact]
    public async Task Refund_leg_before_a_cancel_finalizer_holding_a_completed_snapshot_keeps_one_reversal_and_a_consistent_row()
    {
        // Pin (expected GREEN). The cancellation finalizer read the row while it was already
        // Completed, so CancelAsync's terminal guard refuses it and the committed reversal trail
        // survives intact: one reversal, agreeing row, nothing to replay.
        var txId = await SeedPendingTransactionAsync(SqliteOptions);
        await CommitChargeLegAsync(SqliteOptions, txId);

        await using var refundDb = new TaslimDbContext(SqliteOptions);
        await using var cancelDb = new TaslimDbContext(SqliteOptions);
        var refundCopy = await refundDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        var cancelCopy = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Completed, refundCopy.Status);
        Assert.Equal(UsageTransactionStatus.Completed, cancelCopy.Status);

        var outcome = await Ledger(refundDb).RefundAsync(refundCopy);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Created);

        await Ledger(cancelDb).CancelAsync(cancelCopy, CancellationCode);

        using var verify = new TaslimDbContext(SqliteOptions);
        var row = await verify.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
        var adjustments = await verify.UsageTransactionAdjustments.AsNoTracking()
            .Where(item => item.UsageTransactionId == txId).ToListAsync();

        AssertRefundLegRow(adjustments, txId);
        AssertRowAgreesWithItsReversal(row, adjustments[0], "completed-snapshot cancel");

        var attempt = await verify.GenerationProviderAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        Assert.Null(attempt.ActualProviderCostUsd);
    }

    [Fact]
    public async Task Refund_leg_committed_after_a_second_context_observed_the_row_pending_keeps_one_reversal_and_a_consistent_row()
    {
        // Red by design on main and on PR #1077 head 396dc03d. This is the reachable
        // cancel-versus-refund anomaly on the usage:{id}:refund key: a worker keeps the usage row it
        // loaded at reservation time (Pending). While that snapshot is held, the charge leg commits
        // and the refund leg books its reversal. The stale cancellation finalizer then passes
        // CancelAsync's terminal guard, because its own snapshot is non-terminal, and EF writes back
        // only the properties that differ from it - Status, ChargedAmount, FailureCode. ReversedAmount
        // was 0 in the snapshot, so the refund's 0.42 is never reset, and the Refund adjustment row is
        // never removed. The durable ledger therefore ends as a Cancelled, zero-charge row that still
        // carries a paid-out full reversal: ReversedAmount > ChargedAmount, RefundedAt set beside
        // Status=Cancelled. That is the double credit - the cancellation already released the charge
        // and the refund leg paid it out again - and it is invisible to the attempt-level tests in
        // DocumentExportCancellationAttemptTests, which never touch this key.
        var txId = await SeedPendingTransactionAsync(SqliteOptions);

        await using var cancelDb = new TaslimDbContext(SqliteOptions);
        var staleCancelCopy = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Pending, staleCancelCopy.Status);

        await CommitChargeLegAsync(SqliteOptions, txId);

        await using var refundDb = new TaslimDbContext(SqliteOptions);
        var refundCopy = await refundDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        var outcome = await Ledger(refundDb).RefundAsync(refundCopy);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Created);

        await Ledger(cancelDb).CancelAsync(staleCancelCopy, CancellationCode);

        using var verify = new TaslimDbContext(SqliteOptions);
        var row = await verify.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
        var adjustments = await verify.UsageTransactionAdjustments.AsNoTracking()
            .Where(item => item.UsageTransactionId == txId).ToListAsync();

        // The row-count half of the guarantee must still hold after the losing write.
        AssertRefundLegRow(adjustments, txId);
        AssertRowAgreesWithItsReversal(row, adjustments[0], "pending-snapshot cancel");
    }

    [Fact]
    public async Task Replaying_the_refund_key_after_the_race_adds_no_second_reversal_and_moves_no_amount()
    {
        var txId = await SeedPendingTransactionAsync(SqliteOptions);

        await using var cancelDb = new TaslimDbContext(SqliteOptions);
        var staleCancelCopy = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Pending, staleCancelCopy.Status);
        await CommitChargeLegAsync(SqliteOptions, txId);

        await using var refundDb = new TaslimDbContext(SqliteOptions);
        var refundCopy = await refundDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        await Ledger(refundDb).RefundAsync(refundCopy);
        await Ledger(cancelDb).CancelAsync(staleCancelCopy, CancellationCode);

        using var before = new TaslimDbContext(SqliteOptions);
        var beforeRow = await before.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);

        using var replayDb = new TaslimDbContext(SqliteOptions);
        var replayCopy = await replayDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        var replay = await Ledger(replayDb).RefundAsync(replayCopy);

        Assert.NotNull(replay);
        Assert.False(replay!.Created);
        Assert.Equal($"usage:{txId:N}:refund", replay.Adjustment.IdempotencyKey);

        using var verify = new TaslimDbContext(SqliteOptions);
        var afterRow = await verify.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
        Assert.Equal(beforeRow.ChargedAmount, afterRow.ChargedAmount);
        Assert.Equal(beforeRow.ReversedAmount, afterRow.ReversedAmount);
        var adjustment = Assert.Single(await verify.UsageTransactionAdjustments.AsNoTracking()
            .Where(item => item.UsageTransactionId == txId).ToListAsync());
        Assert.Equal(ChargedAmountUsd, adjustment.AmountUsd);
        // A replay must never be able to push the row into paying out more than it charged.
        Assert.True(afterRow.ReversedAmount <= afterRow.ChargedAmount,
            $"Replay left ReversedAmount={afterRow.ReversedAmount} above ChargedAmount={afterRow.ChargedAmount}.");
    }

    [Fact]
    public async Task Competing_adjustment_key_on_the_same_row_commits_exactly_one_full_reversal()
    {
        // Pin (expected GREEN). The transaction-level unique index plus the DbUpdateException replay
        // branch must keep one reversal row when a second context retries with a DIFFERENT key, so a
        // retry can never stack a second full credit onto the same usage transaction.
        var txId = await SeedPendingTransactionAsync(SqliteOptions);
        await CommitChargeLegAsync(SqliteOptions, txId);

        await using var firstDb = new TaslimDbContext(SqliteOptions);
        await using var secondDb = new TaslimDbContext(SqliteOptions);
        var firstCopy = await firstDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        var secondCopy = await secondDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Completed, firstCopy.Status);
        Assert.Equal(UsageTransactionStatus.Completed, secondCopy.Status);

        var first = await Ledger(firstDb).RefundAsync(firstCopy);
        Assert.NotNull(first);
        Assert.True(first!.Created);

        var second = await Ledger(secondDb).ReverseAsync(secondCopy, $"usage:{txId:N}:retry-1", "Competing retry key");

        using var verify = new TaslimDbContext(SqliteOptions);
        var adjustments = await verify.UsageTransactionAdjustments.AsNoTracking()
            .Where(item => item.UsageTransactionId == txId).ToListAsync();
        var adjustment = Assert.Single(adjustments);
        Assert.Equal(UsageTransactionAdjustmentType.Refund, adjustment.Type);
        Assert.Equal(ChargedAmountUsd, adjustment.AmountUsd);
        Assert.NotNull(second);
        Assert.False(second!.Created);
        Assert.Equal(adjustment.Id, second.Adjustment.Id);

        var row = await verify.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
        Assert.Equal(ChargedAmountUsd, row.ReversedAmount);
        Assert.Equal(ChargedAmountUsd, row.ChargedAmount);
        Assert.Equal(UsageTransactionStatus.Refunded, row.Status);
        Assert.Equal(0m, row.NetChargedAmount);
    }

    [DisposablePostgresFact]
    public async Task Concurrent_postgres_refund_contenders_and_a_stale_pending_cancel_finalizer_keep_one_consistent_reversal()
    {
        // Cross-context contention on real PostgreSQL (per-test schema inside the disposable CI
        // taslim_tests database; skipped elsewhere). A cancellation finalizer holds its reservation
        // snapshot while two independent contexts, released by a barrier so both pass the
        // read-check-write pre-flight before either writes, book the refund leg with different keys.
        // The barrier makes the read phase genuinely overlap and pins the write phase, so the
        // assertion is deterministic rather than timing-dependent.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"refund_cancel_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        var scopedBuilder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scopedBuilder.ConnectionString).Options;
        try
        {
            var txId = await SeedPendingTransactionAsync(options);

            // The cancellation finalizer takes its snapshot first and keeps it for the whole race.
            await using var staleDb = new TaslimDbContext(options);
            var staleCancelCopy = await staleDb.UsageTransactions.SingleAsync(item => item.Id == txId);
            Assert.Equal(UsageTransactionStatus.Pending, staleCancelCopy.Status);

            var chargeCommitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bothContendersObservedCompleted = new CountdownEvent(2);

            async Task BookChargeLegAsync()
            {
                await CommitChargeLegAsync(options, txId);
                chargeCommitted.TrySetResult(true);
            }

            async Task<UsageAdjustmentResult?> ContendAsync(bool useRefundKey)
            {
                await chargeCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await using var db = new TaslimDbContext(options);
                var copy = await db.UsageTransactions.SingleAsync(item => item.Id == txId);
                Assert.Equal(UsageTransactionStatus.Completed, copy.Status);
                // Both contenders must clear the "no adjustment yet" read before either inserts.
                bothContendersObservedCompleted.Signal();
                Assert.True(bothContendersObservedCompleted.Wait(TimeSpan.FromSeconds(30)));
                return useRefundKey
                    ? await Ledger(db).RefundAsync(copy)
                    : await Ledger(db).ReverseAsync(copy, $"usage:{txId:N}:retry-1", "Competing retry key");
            }

            var chargeTask = Task.Run(BookChargeLegAsync);
            var refundTask = Task.Run(() => ContendAsync(true));
            var retryTask = Task.Run(() => ContendAsync(false));
            await chargeTask;
            var results = await Task.WhenAll(refundTask, retryTask);

            // Exactly one leg may report that it created the reversal; the loser adopts the winner.
            Assert.Equal(1, results.Count(item => item is not null && item.Created));

            // The stale cancellation finalizer now writes, holding the pre-charge snapshot.
            await Ledger(staleDb).CancelAsync(staleCancelCopy, CancellationCode);

            await using var verify = new TaslimDbContext(options);
            var row = await verify.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
            var adjustments = await verify.UsageTransactionAdjustments.AsNoTracking()
                .Where(item => item.UsageTransactionId == txId).ToListAsync();

            // Row-count and amount guarantees under real contention.
            var adjustment = Assert.Single(adjustments);
            Assert.Equal(UsageTransactionAdjustmentType.Refund, adjustment.Type);
            Assert.Equal(ChargedAmountUsd, adjustment.AmountUsd);

            // The consistency guarantee the reviewer flagged as untested.
            AssertRowAgreesWithItsReversal(row, adjustment, "postgres contention");

            var attempt = await verify.GenerationProviderAttempts.AsNoTracking().SingleAsync();
            Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
            Assert.Null(attempt.ActualProviderCostUsd);

            // Replay after the contended race is a no-op.
            await using var replayDb = new TaslimDbContext(options);
            var replayCopy = await replayDb.UsageTransactions.SingleAsync(item => item.Id == txId);
            var replay = await Ledger(replayDb).RefundAsync(replayCopy);
            Assert.NotNull(replay);
            Assert.False(replay!.Created);
            await using var after = new TaslimDbContext(options);
            Assert.Single(await after.UsageTransactionAdjustments.AsNoTracking()
                .Where(item => item.UsageTransactionId == txId).ToListAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
