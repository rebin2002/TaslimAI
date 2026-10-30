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
