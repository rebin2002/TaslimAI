using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// Regression QA for the durable compare-and-set inside <see cref="UsageLedgerService.CancelAsync"/>.
///
/// PR #1103 proved the anomaly (<c>UsageRefundCancellationLedgerRaceTests</c>, tests 4 and 5): a
/// cancellation finalizer holding the reservation-time (Pending) snapshot restated a row whose
/// charge and refund legs had already committed, leaving <c>Status=Cancelled</c> beside a paid-out
/// <c>usage:{id}:refund</c> adjustment, <c>ReversedAmount</c> and <c>RefundedAt</c>. This file pins
/// the fix and the boundaries it must not cross:
///
/// 1. a finalizer whose snapshot predates a committed terminal status writes nothing - the durable
///    outcome wins and is adopted onto the caller's copy, for Refunded *and* for Completed rows,
///    so a late cancellation can neither erase a charge nor disagree with its own reversal;
/// 2. a genuine cancellation of a still-Pending durable reservation still lands, with every
///    cancellation column (not only the two that differed from the snapshot) written durably, and
///    a revived Failed reservation is still cancellable;
/// 3. the losing context cannot restate the row later: its tracked copy is frozen, so a follow-on
///    <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> issues no UPDATE against the
///    usage row;
/// 4. the cancellation is exactly one durable write on the winning path and zero on the losing
///    path, counted from the real SQL the context emits;
/// 5. the same guarantees hold on real PostgreSQL row-lock semantics, using per-test schemas in
///    the disposable CI <c>taslim_tests</c> database.
///
/// Unlike PR #1103's contention test, nothing here depends on which of two contending writers wins
/// an insert race: every case fixes the order of the legs explicitly, so the assertions are
/// deterministic rather than timing-dependent.
///
/// All data is synthetic. Customer charging stays disabled
/// (<see cref="SafeUsageChargingService"/> still hard-wires the charge to zero, and the charge leg
/// is modelled with an explicit synthetic UPDATE), no provider is called, no payment gateway,
/// secret, real customer row, migration or deployment is involved.
/// </summary>
public sealed class UsageCancellationDurableStatusGuardTests : IDisposable
{
    private const decimal ChargedAmountUsd = 0.42m;
    private const string FirstCancellationCode = "GENERATION_CANCELLED";
    private const string SecondCancellationCode = "MOVING_CANCELLED";
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"taslim_cancel_guard_{Guid.NewGuid():N}.db");
    private readonly UsageWriteInterceptor _writes = new();

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

    private DbContextOptions<TaslimDbContext> CountedSqliteOptions =>
        new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(SqliteConnectionString).AddInterceptors(_writes).Options;

    private static UsageLedgerService Ledger(TaslimDbContext db) => new(
        db,
        new AiCostCalculator(new AiModelCatalog(new ConfigurationBuilder().Build())),
        new SafeUsageChargingService(),
        new UsageCostControl(db, Options.Create(new UsageControlOptions()), NullLogger<UsageCostControl>.Instance),
        NullLogger<UsageLedgerService>.Instance);

    /// <summary>Seeds one Pending usage reservation for a cancelled document job.</summary>
    private static async Task<Guid> SeedPendingTransactionAsync(DbContextOptions<TaslimDbContext> options)
    {
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"cancel-guard-{userId:N}@example.com",
            NormalizedUserName = $"CANCEL-GUARD-{userId:N}@EXAMPLE.COM",
            Email = $"cancel-guard-{userId:N}@example.com",
            NormalizedEmail = $"CANCEL-GUARD-{userId:N}@EXAMPLE.COM",
            DisplayName = "Cancel Guard Tester",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Cancel Guard Workspace",
            Slug = $"cancel-guard-{workspaceId:N}",
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
    /// Commits the charge leg with an explicit synthetic UPDATE, because customer charging is
    /// hard-wired to zero in <see cref="SafeUsageChargingService"/> and must stay that way.
    /// </summary>
    private static async Task CommitChargeLegAsync(DbContextOptions<TaslimDbContext> options, Guid txId)
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

    private static async Task<UsageTransaction> ReadRowAsync(DbContextOptions<TaslimDbContext> options, Guid txId)
    {
        await using var db = new TaslimDbContext(options);
        return await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
    }

    [Fact]
    public async Task Stale_pending_cancellation_cannot_restate_a_committed_refund_and_adopts_the_durable_outcome()
    {
        // The exact shape PR #1103 pinned RED, now green: the reservation snapshot is held across
        // the charge leg and the refund leg, so the cancellation must lose and must write nothing.
        var options = SqliteOptions;
        var txId = await SeedPendingTransactionAsync(options);

        await using var cancelDb = new TaslimDbContext(options);
        var staleCancelCopy = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Pending, staleCancelCopy.Status);

        await CommitChargeLegAsync(options, txId);
        await using (var refundDb = new TaslimDbContext(options))
        {
            var refundCopy = await refundDb.UsageTransactions.SingleAsync(item => item.Id == txId);
            var outcome = await Ledger(refundDb).RefundAsync(refundCopy);
            Assert.NotNull(outcome);
            Assert.True(outcome!.Created);
        }

        await Ledger(cancelDb).CancelAsync(staleCancelCopy, FirstCancellationCode);

        var row = await ReadRowAsync(options, txId);
        Assert.Equal(UsageTransactionStatus.Refunded, row.Status);
        Assert.Equal(ChargedAmountUsd, row.ChargedAmount);
        Assert.Equal(ChargedAmountUsd, row.ReversedAmount);
        Assert.Equal(0m, row.NetChargedAmount);
        Assert.NotNull(row.RefundedAt);
        // The cancellation code never reached the durable row: a loser leaves the ledger alone.
        Assert.NotEqual(FirstCancellationCode, row.FailureCode ?? string.Empty);
        await using (var verify = new TaslimDbContext(options))
        {
            var adjustment = Assert.Single(await verify.UsageTransactionAdjustments.AsNoTracking()
                .Where(item => item.UsageTransactionId == txId).ToListAsync());
            Assert.Equal(UsageTransactionAdjustmentType.Refund, adjustment.Type);
            Assert.Equal($"usage:{txId:N}:refund", adjustment.IdempotencyKey);
        }

        // The caller's own copy stops claiming it cancelled.
        Assert.Equal(UsageTransactionStatus.Refunded, staleCancelCopy.Status);
        Assert.Equal(ChargedAmountUsd, staleCancelCopy.ChargedAmount);
    }

    [Fact]
    public async Task Stale_pending_cancellation_cannot_erase_a_committed_charge()
    {
        // Same asymmetry on the non-refunded side of the ledger: a finalizer that read the row
        // before the charge leg committed must not turn a paid Completed row into a Cancelled,
        // zero-charge row. Before the guard this lost the customer's charge and its Refunded/
        // Completed readers.
        var options = SqliteOptions;
        var txId = await SeedPendingTransactionAsync(options);

        await using var cancelDb = new TaslimDbContext(options);
        var staleCancelCopy = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Pending, staleCancelCopy.Status);

        await CommitChargeLegAsync(options, txId);

        await Ledger(cancelDb).CancelAsync(staleCancelCopy, FirstCancellationCode);

        var row = await ReadRowAsync(options, txId);
        Assert.Equal(UsageTransactionStatus.Completed, row.Status);
        Assert.Equal(ChargedAmountUsd, row.ChargedAmount);
        Assert.Equal(0m, row.ReversedAmount);
        Assert.True(row.IsBillable);
        Assert.True(row.ProviderCostKnown);
        Assert.Null(row.RefundedAt);
        Assert.NotEqual(FirstCancellationCode, row.FailureCode ?? string.Empty);
        Assert.Equal(UsageTransactionStatus.Completed, staleCancelCopy.Status);
    }

    [Fact]
    public async Task Losing_cancellation_frees_its_context_so_a_later_flush_cannot_restate_the_row()
    {
        // The durable guard is only sound if the losing context cannot write the row afterwards.
        // A stale finalizer that loses must have its cancellation columns dropped from the
        // tracking snapshot, so a later SaveChangesAsync of that same context issues no UPDATE
        // against the usage row.
        var options = SqliteOptions;
        var txId = await SeedPendingTransactionAsync(options);

        await using var cancelDb = new TaslimDbContext(options);
        var staleCancelCopy = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        await CommitChargeLegAsync(options, txId);
        await using (var refundDb = new TaslimDbContext(options))
        {
            var refundCopy = await refundDb.UsageTransactions.SingleAsync(item => item.Id == txId);
            await Ledger(refundDb).RefundAsync(refundCopy);
        }

        await Ledger(cancelDb).CancelAsync(staleCancelCopy, FirstCancellationCode);
        await cancelDb.SaveChangesAsync();
        await cancelDb.SaveChangesAsync();

        var row = await ReadRowAsync(options, txId);
        Assert.Equal(UsageTransactionStatus.Refunded, row.Status);
        Assert.Equal(ChargedAmountUsd, row.ChargedAmount);
        Assert.Equal(ChargedAmountUsd, row.ReversedAmount);
        Assert.NotNull(row.RefundedAt);
    }

    [Fact]
    public async Task Genuine_cancellation_of_a_pending_reservation_still_lands_with_every_cancellation_column()
    {
        // No behaviour regression for the reachable path: the durable row still says Pending, so
        // the compare-and-set wins and the whole cancellation lands, not only the columns that
        // happened to differ from a stale snapshot.
        var options = SqliteOptions;
        var txId = await SeedPendingTransactionAsync(options);

        await using var cancelDb = new TaslimDbContext(options);
        var reservation = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        await Ledger(cancelDb).CancelAsync(reservation, FirstCancellationCode);

        var cancelled = await ReadRowAsync(options, txId);
        Assert.Equal(UsageTransactionStatus.Cancelled, cancelled.Status);
        Assert.Equal(FirstCancellationCode, cancelled.FailureCode);
        Assert.Equal(0m, cancelled.ChargedAmount);
        Assert.Equal(0m, cancelled.ReversedAmount);
        Assert.Equal(0m, cancelled.ProviderCostUsd);
        Assert.False(cancelled.IsBillable);
        Assert.False(cancelled.ProviderCostKnown);
        Assert.Null(cancelled.BillableAt);
        Assert.Null(cancelled.ActualRecordedAt);
        Assert.Null(cancelled.CompletedAt);
        // A Cancelled row never carries a refund stamp.
        Assert.Null(cancelled.RefundedAt);
        Assert.Equal(UsageTransactionStatus.Cancelled, reservation.Status);
        Assert.Equal(FirstCancellationCode, reservation.FailureCode);
    }

    [Fact]
    public async Task Genuine_cancellation_still_lands_after_a_charge_and_a_fresh_pending_reservation()
    {
        // A reservation whose row was charged and then genuinely reopened as Pending (the shape
        // GetOrCreatePendingAsync produces for a non-generation reservation) must stay cancellable:
        // the guard refuses stale snapshots, not legitimate cancellations.
        var options = SqliteOptions;
        var txId = await SeedPendingTransactionAsync(options);
        await CommitChargeLegAsync(options, txId);
        await using (var reopen = new TaslimDbContext(options))
        {
            var row = await reopen.UsageTransactions.SingleAsync(item => item.Id == txId);
            row.Status = UsageTransactionStatus.Cancelled;
            await reopen.SaveChangesAsync();
            row.Status = UsageTransactionStatus.Pending;
            row.ChargedAmount = 0m;
            row.ReversedAmount = 0m;
            row.IsBillable = false;
            row.ProviderCostKnown = false;
            row.BillableAt = null;
            row.ActualRecordedAt = null;
            row.CompletedAt = null;
            row.FailureCode = null;
            await reopen.SaveChangesAsync();
        }

        await using var cancelDb = new TaslimDbContext(options);
        var reservation = await cancelDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Pending, reservation.Status);
        await Ledger(cancelDb).CancelAsync(reservation, SecondCancellationCode);

        var cancelledRow = await ReadRowAsync(options, txId);
        Assert.Equal(UsageTransactionStatus.Cancelled, cancelledRow.Status);
        Assert.Equal(SecondCancellationCode, cancelledRow.FailureCode);
        Assert.Equal(0m, cancelledRow.ChargedAmount);
        Assert.Equal(0m, cancelledRow.ReversedAmount);
        Assert.Null(cancelledRow.RefundedAt);
    }

    [Fact]
    public async Task Second_stale_finalizer_keeps_the_winning_cancellation_code()
    {
        // Two finalizers both holding Pending snapshots: the first wins the compare-and-set and the
        // second must adopt it instead of overwriting the recorded cancellation code.
        var options = SqliteOptions;
        var txId = await SeedPendingTransactionAsync(options);

        await using var firstDb = new TaslimDbContext(options);
        await using var secondDb = new TaslimDbContext(options);
        var firstCopy = await firstDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        var secondCopy = await secondDb.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Pending, firstCopy.Status);
        Assert.Equal(UsageTransactionStatus.Pending, secondCopy.Status);

        await Ledger(firstDb).CancelAsync(firstCopy, FirstCancellationCode);
        await Ledger(secondDb).CancelAsync(secondCopy, SecondCancellationCode);

        var row = await ReadRowAsync(options, txId);
        Assert.Equal(UsageTransactionStatus.Cancelled, row.Status);
        Assert.Equal(FirstCancellationCode, row.FailureCode);
        Assert.Equal(FirstCancellationCode, secondCopy.FailureCode);
        Assert.NotEqual(SecondCancellationCode, row.FailureCode);
    }

    [Fact]
    public async Task Guarded_cancellation_writes_the_usage_row_exactly_once_and_a_losing_one_never()
    {
        // Pins the write shape against the SQL the context really emits, counted in rows the
        // database actually mutated. The cancellation must move the row exactly once; a losing
        // finalizer may issue its guarded UPDATE, but it must mutate nothing - previously a loser
        // issued an unguarded UPDATE by primary key and moved the row, which is how a committed
        // terminal status got restated in the first place.
        var options = CountedSqliteOptions;
        var txId = await SeedPendingTransactionAsync(SqliteOptions);

        // Both contexts read the row while it is Pending, and only then does the winner move it.
        await using var winner = new TaslimDbContext(options);
        await using var loser = new TaslimDbContext(options);
        var winnerCopy = await winner.UsageTransactions.SingleAsync(item => item.Id == txId);
        var staleCopy = await loser.UsageTransactions.SingleAsync(item => item.Id == txId);
        Assert.Equal(UsageTransactionStatus.Pending, winnerCopy.Status);
        Assert.Equal(UsageTransactionStatus.Pending, staleCopy.Status);

        _writes.Reset();
        await Ledger(winner).CancelAsync(winnerCopy, FirstCancellationCode);
        Assert.Equal(1, _writes.UsageRowsMutated);
        Assert.Equal(1, _writes.UsageRowStatements);

        // The losing finalizer holds the pre-win snapshot. The guard must cost it no write at all.
        _writes.Reset();
        await Ledger(loser).CancelAsync(staleCopy, SecondCancellationCode);
        Assert.Equal(0, _writes.UsageRowsMutated);
        await loser.SaveChangesAsync();
        Assert.Equal(0, _writes.UsageRowsMutated);
        Assert.Equal(UsageTransactionStatus.Cancelled, staleCopy.Status);
        Assert.Equal(FirstCancellationCode, staleCopy.FailureCode);

        var row = await ReadRowAsync(options, txId);
        Assert.Equal(UsageTransactionStatus.Cancelled, row.Status);
        Assert.Equal(FirstCancellationCode, row.FailureCode);
    }

    [DisposablePostgresFact]
    public async Task Postgres_stale_pending_cancellation_loses_the_row_lock_race_and_leaves_the_ledger_consistent()
    {
        // Real PostgreSQL, per-test schema inside the disposable CI taslim_tests database. The
        // cancellation finalizer holds its reservation snapshot across the committed charge and
        // refund legs; its guarded UPDATE re-evaluates the status predicate against the committed
        // row, matches zero rows, and the durable ledger stays consistent.
        var fixture = await CreatePostgresSchemaAsync();
        var options = fixture.Options;
        try
        {
            var txId = await SeedPendingTransactionAsync(options);

            await using var staleDb = new TaslimDbContext(options);
            var staleCancelCopy = await staleDb.UsageTransactions.SingleAsync(item => item.Id == txId);
            Assert.Equal(UsageTransactionStatus.Pending, staleCancelCopy.Status);

            await CommitChargeLegAsync(options, txId);

            // The legs are sequenced explicitly, each in its own context and its own committed
            // transaction, so the outcome cannot depend on which writer wins a race. The charge
            // leg must be durably Completed before the refund leg books against it.
            await using (var refundDb = new TaslimDbContext(options))
            {
                var refundCopy = await refundDb.UsageTransactions.SingleAsync(item => item.Id == txId);
                Assert.Equal(UsageTransactionStatus.Completed, refundCopy.Status);
                var outcome = await Ledger(refundDb).RefundAsync(refundCopy);
                Assert.NotNull(outcome);
                Assert.True(outcome!.Created);
            }

            await Ledger(staleDb).CancelAsync(staleCancelCopy, FirstCancellationCode);
            await staleDb.SaveChangesAsync();

            await using var verify = new TaslimDbContext(options);
            var row = await verify.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
            var adjustment = Assert.Single(await verify.UsageTransactionAdjustments.AsNoTracking()
                .Where(item => item.UsageTransactionId == txId).ToListAsync());

            Assert.Equal(UsageTransactionStatus.Refunded, row.Status);
            Assert.Equal(ChargedAmountUsd, row.ChargedAmount);
            Assert.Equal(ChargedAmountUsd, row.ReversedAmount);
            Assert.Equal(adjustment.AmountUsd, row.ReversedAmount);
            Assert.Equal(0m, row.NetChargedAmount);
            Assert.NotNull(row.RefundedAt);
            Assert.Equal(UsageTransactionAdjustmentType.Refund, adjustment.Type);
            Assert.Equal($"usage:{txId:N}:refund", adjustment.IdempotencyKey);
            Assert.Equal(UsageTransactionStatus.Refunded, staleCancelCopy.Status);

            // A replay after the refused cancellation is still a no-op.
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
            await DropPostgresSchemaAsync(fixture);
        }
    }

    [DisposablePostgresFact]
    public async Task Postgres_genuine_and_stale_cancellation_finalizers_elect_exactly_one_durable_terminal_write()
    {
        // On the reservation itself the guard must not become a refusal: the first cancellation
        // lands durably, and the second finalizer - which also read Pending - adopts the winning
        // code rather than restating the row. Deterministic: the legs are ordered explicitly.
        var fixture = await CreatePostgresSchemaAsync();
        var options = fixture.Options;
        try
        {
            var txId = await SeedPendingTransactionAsync(options);

            await using var firstDb = new TaslimDbContext(options);
            await using var secondDb = new TaslimDbContext(options);
            var firstCopy = await firstDb.UsageTransactions.SingleAsync(item => item.Id == txId);
            var secondCopy = await secondDb.UsageTransactions.SingleAsync(item => item.Id == txId);
            Assert.Equal(UsageTransactionStatus.Pending, firstCopy.Status);
            Assert.Equal(UsageTransactionStatus.Pending, secondCopy.Status);

            await Ledger(firstDb).CancelAsync(firstCopy, FirstCancellationCode);
            await Ledger(secondDb).CancelAsync(secondCopy, SecondCancellationCode);
            await secondDb.SaveChangesAsync();

            await using var verify = new TaslimDbContext(options);
            var row = await verify.UsageTransactions.AsNoTracking().SingleAsync(item => item.Id == txId);
            Assert.Equal(UsageTransactionStatus.Cancelled, row.Status);
            Assert.Equal(FirstCancellationCode, row.FailureCode);
            Assert.Equal(0m, row.ChargedAmount);
            Assert.Equal(0m, row.ReversedAmount);
            Assert.False(row.IsBillable);
            Assert.False(row.ProviderCostKnown);
            Assert.Null(row.RefundedAt);
            Assert.Equal(FirstCancellationCode, secondCopy.FailureCode);
            Assert.Empty(await verify.UsageTransactionAdjustments.AsNoTracking()
                .Where(item => item.UsageTransactionId == txId).ToListAsync());
        }
        finally
        {
            await DropPostgresSchemaAsync(fixture);
        }
    }

    private sealed record PostgresFixture(DbContextOptions<TaslimDbContext> Options, string AdminConnectionString, string Schema);

    private static async Task<PostgresFixture> CreatePostgresSchemaAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"cancel_guard_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }
        var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scoped.ConnectionString).Options;
        await using var bootstrap = new TaslimDbContext(options);
        await bootstrap.Database.EnsureCreatedAsync();
        return new PostgresFixture(options, connectionString, schema);
    }

    private static async Task DropPostgresSchemaAsync(PostgresFixture fixture)
    {
        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{fixture.Schema}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Counts UPDATE statements issued against the usage transaction table and, separately, the
    /// rows the database actually mutated for them - the difference between the two is precisely
    /// what a compare-and-set buys.
    /// </summary>
    private sealed class UsageWriteInterceptor : DbCommandInterceptor
    {
        private int _usageRowsMutated;
        private int _usageRowStatements;

        public int UsageRowsMutated => Volatile.Read(ref _usageRowsMutated);

        public int UsageRowStatements => Volatile.Read(ref _usageRowStatements);

        public void Reset()
        {
            Interlocked.Exchange(ref _usageRowsMutated, 0);
            Interlocked.Exchange(ref _usageRowStatements, 0);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, result);
            return ValueTask.FromResult(result);
        }

        private void Observe(DbCommand command, int affectedRows)
        {
            var sql = command.CommandText;
            if (sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("UsageTransactions", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _usageRowStatements);
                Interlocked.Add(ref _usageRowsMutated, affectedRows);
            }
        }
    }
}
