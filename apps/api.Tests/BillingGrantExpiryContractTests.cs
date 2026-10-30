using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(connection.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(connection.Database, "taslim_tests", StringComparison.Ordinal) ||
            !string.Equals(connection.Username, "taslim", StringComparison.Ordinal) ||
            !string.Equals(connection.Password, "change-me", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to run the PostgreSQL contract test outside the disposable CI database.");

        var schema = $"issue330_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await using (var createSchema = adminConnection.CreateCommand())
        {
            createSchema.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            connection.SearchPath = schema;
            connection.Pooling = false;
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await using var db = new TaslimDbContext(options);
            await AssertExpiredGrantIsolationAsync(db);
        }
        finally
        {
            await using var dropSchema = adminConnection.CreateCommand();
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await dropSchema.ExecuteNonQueryAsync();
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
        var ledger = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true }));
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
        UserName = "grant-expiry-contract@example.test",
        NormalizedUserName = "GRANT-EXPIRY-CONTRACT@EXAMPLE.TEST",
        Email = "grant-expiry-contract@example.test",
        NormalizedEmail = "GRANT-EXPIRY-CONTRACT@EXAMPLE.TEST",
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
