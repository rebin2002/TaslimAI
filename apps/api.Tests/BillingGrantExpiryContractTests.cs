using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class BillingGrantExpiryContractTests
{
    [Fact]
    public async Task Sqlite_historical_debit_from_expired_grant_does_not_reduce_later_grant_period()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);

        await AssertExpiredGrantIsolationAsync(db);
    }

    [FactWhenEnvironmentVariablePresent("ConnectionStrings__Postgres")]
    public async Task Postgres_historical_debit_from_expired_grant_does_not_reduce_later_grant_period()
    {
        // CI provides a disposable localhost PostgreSQL service. Refuse any other
        // endpoint so this test cannot accidentally connect to production data.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The PostgreSQL test skip attribute did not detect the missing CI connection.");

        var connection = CreateDisposablePostgresConnection(connectionString);
        var schema = $"issue330_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await CreateSchemaAsync(adminConnection, schema);

        try
        {
            connection.SearchPath = schema;
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await using var db = new TaslimDbContext(options);
            await AssertExpiredGrantIsolationAsync(db);
        }
        finally
        {
            await DropSchemaAsync(adminConnection, schema);
        }
    }

    [Fact]
    public async Task Sqlite_balance_projection_excludes_expired_period_debit_from_later_grant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var workspace = NewWorkspace();
        var user = NewUser();
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await AddBillingProjectionFixtureAsync(db, workspace, now);

        var ledger = NewChargingEnabledLedger(db);
        var oldGrant = await ledger.GrantAsync(
            workspace.Id, CreditEntitlementType.IncludedMonthly, 100,
            "display:grant:old", "Synthetic old-period grant", expiresAt: now.AddDays(1));
        var oldEntitlement = await db.CreditEntitlements.SingleAsync(item => item.Id == oldGrant.Entry.CreditEntitlementId);
        var oldUsage = NewCompletedUsage(workspace.Id, user.Id, "display:old-usage");
        db.UsageTransactions.Add(oldUsage);
        await db.SaveChangesAsync();
        await ledger.RecordUsageDebitAsync(workspace.Id, oldUsage.Id, 25, "display:debit:old", "Synthetic old-period usage");

        oldEntitlement.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        await ledger.GrantAsync(
            workspace.Id, CreditEntitlementType.IncludedMonthly, 50,
            "display:grant:new", "Synthetic new-period grant", expiresAt: DateTime.UtcNow.AddDays(30));

        var account = await NewBillingAccountService(db).GetAccountAsync(workspace.Id);

        Assert.Equal(50, account.Credits.IncludedRemaining);
        Assert.Equal(50, account.Credits.TotalRemaining);
    }

    [Fact]
    public async Task Sqlite_overlapping_lots_reconcile_category_balances_to_total()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var workspace = NewWorkspace();
        var user = NewUser();
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await AddBillingProjectionFixtureAsync(db, workspace, now);

        var ledger = NewChargingEnabledLedger(db);
        await ledger.GrantAsync(
            workspace.Id, CreditEntitlementType.IncludedMonthly, 40,
            "overlap:grant:expires-first", "Synthetic expiring lot", expiresAt: now.AddDays(5));
        await ledger.GrantAsync(
            workspace.Id, CreditEntitlementType.Purchased, 40,
            "overlap:grant:expires-later", "Synthetic later-expiring lot", expiresAt: now.AddDays(30));
        var usage = NewCompletedUsage(workspace.Id, user.Id, "overlap:usage");
        db.UsageTransactions.Add(usage);
        await db.SaveChangesAsync();

        var debit = await ledger.RecordUsageDebitAsync(workspace.Id, usage.Id, 60, "overlap:debit", "Synthetic overlapping-lot usage");
        Assert.NotNull(debit);

        var account = await NewBillingAccountService(db).GetAccountAsync(workspace.Id);

        // The issue does not specify FEFO versus another spend order, so do not
        // encode one here. Regardless of allocation order, the two active-lot
        // category balances must reconcile to the available total.
        Assert.Equal(20L, account.Credits.TotalRemaining);
        Assert.Equal(account.Credits.TotalRemaining, account.Credits.IncludedRemaining + account.Credits.PurchasedRemaining);
    }

    [Fact]
    public async Task Sqlite_refund_is_append_only_and_same_key_replay_is_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var workspace = NewWorkspace();
        var user = NewUser();
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var ledger = NewChargingEnabledLedger(db);
        await ledger.GrantAsync(
            workspace.Id, CreditEntitlementType.Purchased, 100,
            "refund:grant", "Synthetic refundable lot", expiresAt: DateTime.UtcNow.AddDays(30));
        var usage = NewCompletedUsage(workspace.Id, user.Id, "refund:usage");
        db.UsageTransactions.Add(usage);
        await db.SaveChangesAsync();
        var debit = await ledger.RecordUsageDebitAsync(workspace.Id, usage.Id, 25, "refund:debit", "Synthetic refundable usage");
        Assert.NotNull(debit);
        var original = debit!.Entry;
        var beforeCount = await db.CreditLedgerEntries.CountAsync();

        var refund = await ledger.RefundAsync(workspace.Id, debit.Entry.Id, "refund:once", "Synthetic refund");
        var replay = await ledger.RefundAsync(workspace.Id, debit.Entry.Id, "refund:once", "Synthetic refund");

        Assert.True(refund.Created);
        Assert.False(replay.Created);
        Assert.Equal(refund.Entry.Id, replay.Entry.Id);
        Assert.Equal(CreditLedgerEntryType.Refund, refund.Entry.Type);
        Assert.Equal(25, refund.Entry.Amount);
        Assert.Equal(original.Id, refund.Entry.ReversesEntryId);
        Assert.Equal(original.CreditEntitlementId, refund.Entry.CreditEntitlementId);
        Assert.Equal(original.UsageTransactionId, refund.Entry.UsageTransactionId);
        var originalAfter = await db.CreditLedgerEntries.AsNoTracking().SingleAsync(item => item.Id == original.Id);
        Assert.Equal(original.Id, originalAfter.Id);
        Assert.Equal(original.WorkspaceId, originalAfter.WorkspaceId);
        Assert.Equal(original.CreditEntitlementId, originalAfter.CreditEntitlementId);
        Assert.Equal(original.UsageTransactionId, originalAfter.UsageTransactionId);
        Assert.Equal(original.ReversesEntryId, originalAfter.ReversesEntryId);
        Assert.Equal(original.Type, originalAfter.Type);
        Assert.Equal(original.Amount, originalAfter.Amount);
        Assert.Equal(original.IdempotencyKey, originalAfter.IdempotencyKey);
        Assert.Equal(original.Reason, originalAfter.Reason);
        Assert.Equal(original.ActorUserId, originalAfter.ActorUserId);
        Assert.Equal(original.CreatedAt, originalAfter.CreatedAt);
        Assert.Equal(beforeCount + 1, await db.CreditLedgerEntries.CountAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.RefundAsync(workspace.Id, debit.Entry.Id, "refund:second-key", "Second synthetic refund"));
        Assert.Equal(beforeCount + 1, await db.CreditLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Sqlite_post_expiry_refund_replay_is_append_only_and_source_isolated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);

        await AssertPostExpiryRefundReplayIsolationAsync(db);
    }

    [FactWhenEnvironmentVariablePresent("ConnectionStrings__Postgres")]
    public async Task Postgres_post_expiry_refund_replay_is_append_only_and_source_isolated()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The PostgreSQL test skip attribute did not detect the missing test connection.");

        var connection = CreateDisposablePostgresConnection(connectionString);
        var schema = $"issue330_postexpiry_refund_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await CreateSchemaAsync(adminConnection, schema);

        try
        {
            connection.SearchPath = schema;
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await using var db = new TaslimDbContext(options);
            await AssertPostExpiryRefundReplayIsolationAsync(db);
        }
        finally
        {
            await DropSchemaAsync(adminConnection, schema);
        }
    }

    [Fact]
    public async Task Sqlite_post_expiry_refund_replay_is_durable_across_context_restarts_without_charging()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"issue330-refund-replay-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        try
        {
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connectionString).Options;
            await AssertDurablePostExpiryRefundReplayAsync(options);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [FactWhenEnvironmentVariablePresent("ConnectionStrings__Postgres")]
    public async Task Postgres_post_expiry_refund_replay_is_durable_across_context_restarts_without_charging()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The PostgreSQL test skip attribute did not detect the missing test connection.");

        var connection = CreateDisposablePostgresConnection(connectionString);
        var schema = $"issue330_durable_refund_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await CreateSchemaAsync(adminConnection, schema);

        try
        {
            connection.SearchPath = schema;
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await AssertDurablePostExpiryRefundReplayAsync(options);
        }
        finally
        {
            await DropSchemaAsync(adminConnection, schema);
        }
    }

    [FactWhenEnvironmentVariablePresent("ConnectionStrings__Postgres")]
    public async Task Postgres_concurrent_debits_cannot_overdraw_one_synthetic_grant()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The PostgreSQL test skip attribute did not detect the missing CI connection.");

        var connection = CreateDisposablePostgresConnection(connectionString);
        var schema = $"issue330_concurrency_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await CreateSchemaAsync(adminConnection, schema);

        try
        {
            connection.SearchPath = schema;
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connection.ConnectionString).Options;
            var workspace = NewWorkspace();
            var user = NewUser();
            var firstUsage = NewCompletedUsage(workspace.Id, user.Id, "concurrency:usage:1");
            var secondUsage = NewCompletedUsage(workspace.Id, user.Id, "concurrency:usage:2");

            await using (var seedDb = new TaslimDbContext(options))
            {
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.Workspaces.Add(workspace);
                seedDb.Users.Add(user);
                seedDb.UsageTransactions.AddRange(firstUsage, secondUsage);
                await seedDb.SaveChangesAsync();
                await NewChargingEnabledLedger(seedDb).GrantAsync(
                    workspace.Id, CreditEntitlementType.IncludedMonthly, 100,
                    "concurrency:grant", "Synthetic concurrent-debit lot", expiresAt: DateTime.UtcNow.AddDays(1));
            }

            var attempts = await Task.WhenAll(
                TryRecordConcurrentDebitAsync(options, workspace.Id, firstUsage.Id, "concurrency:debit:1"),
                TryRecordConcurrentDebitAsync(options, workspace.Id, secondUsage.Id, "concurrency:debit:2"));
            Assert.Single(attempts, item => item.Result?.Created == true);
            var rejected = Assert.Single(attempts, item => item.Error is not null);
            Assert.True(IsExpectedConcurrencyRejection(rejected.Error), rejected.Error?.ToString());

            await using var verifyDb = new TaslimDbContext(options);
            var committedDebitTotal = await verifyDb.CreditLedgerEntries
                .Where(item => item.Type == CreditLedgerEntryType.Debit)
                .SumAsync(item => (long?)item.Amount) ?? 0;
            Assert.Equal(-60, committedDebitTotal);
            Assert.Equal(40, await verifyDb.CreditLedgerEntries.SumAsync(item => item.Amount));
        }
        finally
        {
            await DropSchemaAsync(adminConnection, schema);
        }
    }

    private static async Task AssertExpiredGrantIsolationAsync(TaslimDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var workspace = NewWorkspace();
        var user = NewUser();
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // CustomerChargingEnabled is enabled only on this synthetic test service
        // so the test can exercise debit admission; no provider or payment call is made.
        var ledger = NewChargingEnabledLedger(db);
        var expiredPeriodGrant = await ledger.GrantAsync(
            workspace.Id,
            CreditEntitlementType.IncludedMonthly,
            100,
            "grant:old-period",
            "Synthetic old-period grant",
            expiresAt: now.AddDays(1));
        var sourceEntitlement = await db.CreditEntitlements.SingleAsync(item => item.Id == expiredPeriodGrant.Entry.CreditEntitlementId);

        var oldUsage = NewCompletedUsage(workspace.Id, user.Id, "old-period-usage");
        db.UsageTransactions.Add(oldUsage);
        await db.SaveChangesAsync();
        var historicalDebit = await ledger.RecordUsageDebitAsync(
            workspace.Id,
            oldUsage.Id,
            25,
            "debit:old-period-usage",
            "Synthetic old-period usage");
        Assert.NotNull(historicalDebit);
        Assert.True(historicalDebit.Created);

        // Simulate normal period expiry after the debit has already been recorded.
        sourceEntitlement.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        await ledger.GrantAsync(
            workspace.Id,
            CreditEntitlementType.IncludedMonthly,
            50,
            "grant:new-period",
            "Synthetic new-period grant",
            expiresAt: DateTime.UtcNow.AddDays(30));

        var newUsage = NewCompletedUsage(workspace.Id, user.Id, "new-period-usage");
        db.UsageTransactions.Add(newUsage);
        await db.SaveChangesAsync();

        // The active lot has 50 credits. The prior 25-credit debit belonged to the
        // now-expired source grant and must not reduce admission for this new period.
        var laterDebit = await ledger.RecordUsageDebitAsync(
            workspace.Id,
            newUsage.Id,
            30,
            "debit:new-period-usage",
            "Synthetic new-period usage");

        Assert.NotNull(laterDebit);
        Assert.True(laterDebit.Created);
        Assert.Equal(-30, laterDebit.Entry.Amount);
        Assert.Equal(1, await db.CreditLedgerEntries.CountAsync(item =>
            item.Type == CreditLedgerEntryType.Debit && item.UsageTransactionId == newUsage.Id));
    }

    private static async Task AssertPostExpiryRefundReplayIsolationAsync(TaslimDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var workspace = NewWorkspace();
        var user = NewUser();
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var ledger = NewChargingEnabledLedger(db);
        var oldGrant = await ledger.GrantAsync(
            workspace.Id, CreditEntitlementType.IncludedMonthly, 100,
            "postexpiry:grant:old", "Synthetic old included grant", expiresAt: now.AddDays(1));
        var oldEntitlementId = oldGrant.Entry.CreditEntitlementId
            ?? throw new InvalidOperationException("The synthetic old grant must have an entitlement.");

        var oldUsage = NewCompletedUsage(workspace.Id, user.Id, "postexpiry:usage:old");
        db.UsageTransactions.Add(oldUsage);
        await db.SaveChangesAsync();
        var oldDebit = NewAttributedSyntheticDebit(
            workspace.Id, oldEntitlementId, oldUsage.Id, "postexpiry:debit:old", now);
        db.CreditLedgerEntries.Add(oldDebit);
        await db.SaveChangesAsync();

        var oldEntitlement = await db.CreditEntitlements.SingleAsync(item => item.Id == oldEntitlementId);
        oldEntitlement.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var laterGrant = await ledger.GrantAsync(
            workspace.Id, CreditEntitlementType.IncludedMonthly, 50,
            "postexpiry:grant:new", "Synthetic later-period included grant", expiresAt: now.AddDays(30));
        var laterEntitlementId = laterGrant.Entry.CreditEntitlementId
            ?? throw new InvalidOperationException("The synthetic later grant must have an entitlement.");
        var laterUsage = NewCompletedUsage(workspace.Id, user.Id, "postexpiry:usage:new");
        db.UsageTransactions.Add(laterUsage);
        await db.SaveChangesAsync();
        var laterDebit = NewAttributedSyntheticDebit(
            workspace.Id, laterEntitlementId, laterUsage.Id, "postexpiry:debit:new", now);
        db.CreditLedgerEntries.Add(laterDebit);
        await db.SaveChangesAsync();

        var oldEntitlementBefore = CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
            .SingleAsync(item => item.Id == oldEntitlementId));
        var laterEntitlementBefore = CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
            .SingleAsync(item => item.Id == laterEntitlementId));
        var originalRows = await CaptureLedgerRowsAsync(db, workspace.Id);
        var originalCount = originalRows.Count;
        var signedBalanceBefore = await GetSignedLedgerBalanceAsync(db, workspace.Id);

        // The synthetic debit is attributed to the expired source lot so this test
        // isolates refund/replay behavior from the separate debit-allocation defect.
        const string refundKey = "postexpiry:refund:old";
        const string refundReason = "Synthetic post-expiry refund";
        var refund = await ledger.RefundAsync(workspace.Id, oldDebit.Id, refundKey, refundReason);

        Assert.True(refund.Created);
        Assert.Equal(CreditLedgerEntryType.Refund, refund.Entry.Type);
        Assert.Equal(-oldDebit.Amount, refund.Entry.Amount);
        Assert.Equal(0, oldDebit.Amount + refund.Entry.Amount);
        Assert.Equal(oldDebit.Id, refund.Entry.ReversesEntryId);
        Assert.Equal(oldDebit.UsageTransactionId, refund.Entry.UsageTransactionId);

        var afterFirstRows = await CaptureLedgerRowsAsync(db, workspace.Id);
        Assert.Equal(originalCount + 1, afterFirstRows.Count);
        var signedBalanceAfterFirst = await GetSignedLedgerBalanceAsync(db, workspace.Id);
        Assert.Equal(signedBalanceBefore + refund.Entry.Amount, signedBalanceAfterFirst);

        var persistedRefund = Assert.Single(afterFirstRows, item => item.Id == refund.Entry.Id);
        var replay = await ledger.RefundAsync(workspace.Id, oldDebit.Id, refundKey, refundReason);
        Assert.False(replay.Created);
        Assert.Equal(refund.Entry.Id, replay.Entry.Id);
        Assert.Equal(signedBalanceAfterFirst, await GetSignedLedgerBalanceAsync(db, workspace.Id));

        // A key bound to the expired grant's debit cannot be replayed against a
        // different debit from the later entitlement.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.RefundAsync(workspace.Id, laterDebit.Id, refundKey, refundReason));
        // A second idempotency key also cannot compensate the same original twice.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.RefundAsync(workspace.Id, oldDebit.Id, "postexpiry:refund:second", "Second synthetic refund"));

        var finalRows = await CaptureLedgerRowsAsync(db, workspace.Id);
        Assert.Equal(originalCount + 1, finalRows.Count);
        Assert.Equal(signedBalanceAfterFirst, await GetSignedLedgerBalanceAsync(db, workspace.Id));
        var finalById = finalRows.ToDictionary(item => item.Id);
        foreach (var original in originalRows)
            Assert.Equal(original, finalById[original.Id]);
        Assert.Equal(persistedRefund, finalById[refund.Entry.Id]);
        Assert.Equal(oldEntitlementBefore, CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
            .SingleAsync(item => item.Id == oldEntitlementId)));
        Assert.Equal(laterEntitlementBefore, CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
            .SingleAsync(item => item.Id == laterEntitlementId)));
    }

    private static CreditLedgerEntry NewAttributedSyntheticDebit(
        Guid workspaceId, Guid entitlementId, Guid usageTransactionId, string idempotencyKey, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        CreditEntitlementId = entitlementId,
        UsageTransactionId = usageTransactionId,
        Type = CreditLedgerEntryType.Debit,
        Amount = -25,
        IdempotencyKey = idempotencyKey,
        Reason = "Synthetic attributed usage debit",
        CreatedAt = createdAt,
    };

    private static async Task<long> GetSignedLedgerBalanceAsync(TaslimDbContext db, Guid workspaceId) =>
        await db.CreditLedgerEntries.Where(item => item.WorkspaceId == workspaceId)
            .Select(item => (long?)item.Amount).SumAsync() ?? 0;

    private static async Task<List<CreditLedgerRowSnapshot>> CaptureLedgerRowsAsync(TaslimDbContext db, Guid workspaceId)
    {
        var rows = await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId)
            .ToListAsync();
        return rows.Select(item => new CreditLedgerRowSnapshot(
            item.Id, item.WorkspaceId, item.CreditEntitlementId, item.UsageTransactionId, item.ReversesEntryId,
            item.Type, item.Amount, item.IdempotencyKey, item.Reason, item.ActorUserId, item.CreatedAt)).ToList();
    }

    private static CreditEntitlementSnapshot CaptureEntitlement(CreditEntitlement item) => new(
        item.Id, item.WorkspaceId, item.BillingPeriodId, item.Type, item.GrantedCredits, item.GrantedAt,
        item.ExpiresAt, item.IdempotencyKey, item.SourceReference, item.CreatedAt);

    private static async Task AssertDurablePostExpiryRefundReplayAsync(DbContextOptions<TaslimDbContext> options)
    {
        const string refundKey = "durable:refund:old";
        const string refundReason = "Synthetic durable post-expiry refund";
        var workspace = NewWorkspace();
        var user = NewUser();
        Guid oldDebitId;
        Guid laterDebitId;
        Guid originalRefundId;
        Guid oldEntitlementId;
        Guid laterEntitlementId;
        int originalRowCount;
        long balanceAfterFirstRefund;
        CreditLedgerRowSnapshot persistedRefund;
        List<CreditLedgerRowSnapshot> originalRows;
        CreditEntitlementSnapshot oldEntitlementBefore;
        CreditEntitlementSnapshot laterEntitlementBefore;

        await using (var db = new TaslimDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Workspaces.Add(workspace);
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var ledger = NewChargingDisabledLedger(db);
            var now = DateTime.UtcNow;
            var oldGrant = await ledger.GrantAsync(
                workspace.Id, CreditEntitlementType.IncludedMonthly, 100,
                "durable:grant:old", "Synthetic old included grant", expiresAt: now.AddDays(1));
            oldEntitlementId = oldGrant.Entry.CreditEntitlementId
                ?? throw new InvalidOperationException("The synthetic old grant must have an entitlement.");

            var oldUsage = NewCompletedUsage(workspace.Id, user.Id, "durable:usage:old");
            db.UsageTransactions.Add(oldUsage);
            await db.SaveChangesAsync();
            var oldDebit = NewAttributedSyntheticDebit(
                workspace.Id, oldEntitlementId, oldUsage.Id, "durable:debit:old", now);
            db.CreditLedgerEntries.Add(oldDebit);
            await db.SaveChangesAsync();
            oldDebitId = oldDebit.Id;

            var oldEntitlement = await db.CreditEntitlements.SingleAsync(item => item.Id == oldEntitlementId);
            oldEntitlement.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            var laterGrant = await ledger.GrantAsync(
                workspace.Id, CreditEntitlementType.IncludedMonthly, 50,
                "durable:grant:new", "Synthetic later included grant", expiresAt: now.AddDays(30));
            laterEntitlementId = laterGrant.Entry.CreditEntitlementId
                ?? throw new InvalidOperationException("The synthetic later grant must have an entitlement.");
            var laterUsage = NewCompletedUsage(workspace.Id, user.Id, "durable:usage:new");
            db.UsageTransactions.Add(laterUsage);
            await db.SaveChangesAsync();
            var laterDebit = NewAttributedSyntheticDebit(
                workspace.Id, laterEntitlementId, laterUsage.Id, "durable:debit:new", now);
            db.CreditLedgerEntries.Add(laterDebit);
            await db.SaveChangesAsync();
            laterDebitId = laterDebit.Id;

            originalRows = await CaptureLedgerRowsAsync(db, workspace.Id);
            originalRowCount = originalRows.Count;
            oldEntitlementBefore = CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
                .SingleAsync(item => item.Id == oldEntitlementId));
            laterEntitlementBefore = CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
                .SingleAsync(item => item.Id == laterEntitlementId));

            var first = await ledger.RefundAsync(workspace.Id, oldDebitId, refundKey, refundReason);
            Assert.True(first.Created);
            Assert.Equal(CreditLedgerEntryType.Refund, first.Entry.Type);
            Assert.Equal(-oldDebit.Amount, first.Entry.Amount);
            Assert.Equal(0, oldDebit.Amount + first.Entry.Amount);
            Assert.Equal(oldDebitId, first.Entry.ReversesEntryId);
            originalRefundId = first.Entry.Id;

            var afterFirst = await CaptureLedgerRowsAsync(db, workspace.Id);
            Assert.Equal(originalRowCount + 1, afterFirst.Count);
            persistedRefund = Assert.Single(afterFirst, item => item.Id == originalRefundId);
            balanceAfterFirstRefund = await GetSignedLedgerBalanceAsync(db, workspace.Id);
        }

        // A new context (and for SQLite a fresh file-backed connection) represents
        // a process restart/lost response followed by an idempotent retry.
        await using (var db = new TaslimDbContext(options))
        {
            var ledger = NewChargingDisabledLedger(db);
            var replay = await ledger.RefundAsync(workspace.Id, oldDebitId, refundKey, refundReason);
            Assert.False(replay.Created);
            Assert.Equal(originalRefundId, replay.Entry.Id);
            Assert.Equal(persistedRefund, Assert.Single(await CaptureLedgerRowsAsync(db, workspace.Id),
                item => item.Id == originalRefundId));
            Assert.Equal(balanceAfterFirstRefund, await GetSignedLedgerBalanceAsync(db, workspace.Id));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ledger.RefundAsync(workspace.Id, oldDebitId, refundKey, "Conflicting synthetic reason"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ledger.RefundAsync(workspace.Id, laterDebitId, refundKey, refundReason));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ledger.RefundAsync(workspace.Id, oldDebitId, "durable:refund:second", "Second synthetic refund"));

            var finalRows = await CaptureLedgerRowsAsync(db, workspace.Id);
            Assert.Equal(originalRowCount + 1, finalRows.Count);
            var finalById = finalRows.ToDictionary(item => item.Id);
            foreach (var original in originalRows)
                Assert.Equal(original, finalById[original.Id]);
            Assert.Equal(persistedRefund, finalById[originalRefundId]);
            Assert.Equal(balanceAfterFirstRefund, await GetSignedLedgerBalanceAsync(db, workspace.Id));
            Assert.Equal(oldEntitlementBefore, CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
                .SingleAsync(item => item.Id == oldEntitlementId)));
            Assert.Equal(laterEntitlementBefore, CaptureEntitlement(await db.CreditEntitlements.AsNoTracking()
                .SingleAsync(item => item.Id == laterEntitlementId)));
        }
    }

    private static async Task AddBillingProjectionFixtureAsync(TaslimDbContext db, Workspace workspace, DateTime now)
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"issue330-{Guid.NewGuid():N}", Name = "Issue 330 projection fixture",
            MonthlyCreditAllowance = 1_000, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = now, UpdatedAt = now,
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = now.Date, CurrentPeriodEnd = now.Date.AddMonths(1), NextRenewalAt = now.Date.AddMonths(1),
            CreatedAt = now, UpdatedAt = now,
        };
        var period = new BillingPeriod
        {
            Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Open,
            StartsAt = now.Date, EndsAt = now.Date.AddMonths(1), IncludedCredits = plan.MonthlyCreditAllowance, CreatedAt = now,
        };
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        db.BillingPeriods.Add(period);
        await db.SaveChangesAsync();
    }

    private static BillingAccountService NewBillingAccountService(TaslimDbContext db) => new(
        db,
        new BillingProvisioningService(db, NullLogger<BillingProvisioningService>.Instance),
        Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }));

    private static CreditLedgerService NewChargingEnabledLedger(TaslimDbContext db) =>
        new(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true }));

    private static CreditLedgerService NewChargingDisabledLedger(TaslimDbContext db) =>
        new(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false }));

    private static NpgsqlConnectionStringBuilder CreateDisposablePostgresConnection(string connectionString)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(connection.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(connection.Database, "taslim_tests", StringComparison.Ordinal) ||
            !string.Equals(connection.Username, "taslim", StringComparison.Ordinal) ||
            !string.Equals(connection.Password, "change-me", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to run PostgreSQL contract tests outside the disposable CI database.");
        connection.Pooling = false;
        return connection;
    }

    private static async Task CreateSchemaAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA \"{schema}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSchemaAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(CreditMovementResult? Result, Exception? Error)> TryRecordConcurrentDebitAsync(
        DbContextOptions<TaslimDbContext> options, Guid workspaceId, Guid usageId, string key)
    {
        await using var db = new TaslimDbContext(options);
        try
        {
            var result = await NewChargingEnabledLedger(db).RecordUsageDebitAsync(
                workspaceId, usageId, 60, key, "Synthetic concurrent usage");
            return (result, null);
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    private static bool IsExpectedConcurrencyRejection(Exception? error)
    {
        if (error is InvalidOperationException invalid &&
            invalid.Message.Contains("does not have enough credits", StringComparison.Ordinal))
            return true;
        for (var current = error; current is not null; current = current.InnerException)
            if (current is PostgresException postgres && postgres.SqlState == "40001")
                return true;
        return false;
    }

    private static Workspace NewWorkspace() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Grant expiry contract workspace",
        Slug = $"grant-expiry-{Guid.NewGuid():N}",
        Type = WorkspaceType.Personal,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ApplicationUser NewUser() => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"grant-expiry-contract-{Guid.NewGuid():N}@example.test",
        NormalizedUserName = $"GRANT-EXPIRY-CONTRACT-{Guid.NewGuid():N}@EXAMPLE.TEST",
        Email = $"grant-expiry-contract-{Guid.NewGuid():N}@example.test",
        NormalizedEmail = $"GRANT-EXPIRY-CONTRACT-{Guid.NewGuid():N}@EXAMPLE.TEST",
        DisplayName = "Grant Expiry Contract",
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

    private sealed record CreditLedgerRowSnapshot(
        Guid Id, Guid WorkspaceId, Guid? CreditEntitlementId, Guid? UsageTransactionId,
        Guid? ReversesEntryId, CreditLedgerEntryType Type, long Amount, string IdempotencyKey,
        string Reason, Guid? ActorUserId, DateTime CreatedAt);

    private sealed record CreditEntitlementSnapshot(
        Guid Id, Guid WorkspaceId, Guid? BillingPeriodId, CreditEntitlementType Type,
        long GrantedCredits, DateTime GrantedAt, DateTime? ExpiresAt, string IdempotencyKey,
        string? SourceReference, DateTime CreatedAt);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactWhenEnvironmentVariablePresentAttribute : FactAttribute
{
    public FactWhenEnvironmentVariablePresentAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Skip = $"Requires {name}; the isolated PostgreSQL service is provided by the API CI job.";
    }
}
