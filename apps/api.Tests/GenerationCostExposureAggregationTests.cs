using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationCostExposureAggregationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_ceilings_do_not_read_the_ledger(bool enabled)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var dbOptions = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var dbWithoutTables = new TaslimDbContext(dbOptions);
        var service = new GenerationCostGuardrailService(dbWithoutTables, Options.Create(new GenerationBudgetOptions { Enabled = enabled }));
        var result = await service.EvaluateAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Estimate(), true);
        Assert.True(result.Allowed);
        Assert.Null(result.UserCap);
        Assert.Null(result.WorkspaceCap);
        Assert.Null(result.ProjectCap);
    }

    [Fact]
    public async Task Sqlite_counts_exact_exposure_for_each_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await VerifyScopesAsync(db);
    }

    [DisposablePostgresFact]
    public async Task Postgres_counts_exact_exposure_for_each_scope()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"generation_cap_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
        await create.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false };
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scoped.ConnectionString).Options;
            await using var db = new TaslimDbContext(options);
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            await VerifyScopesAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyScopesAsync(TaslimDbContext db)
    {
        var viewer = new ApplicationUser { Id = Guid.NewGuid(), DisplayName = "Viewer" };
        var colleague = new ApplicationUser { Id = Guid.NewGuid(), DisplayName = "Colleague" };
        var current = new Workspace { Id = Guid.NewGuid(), Name = "Current", Slug = "current" };
        var other = new Workspace { Id = Guid.NewGuid(), Name = "Other", Slug = "other" };
        db.AddRange(viewer, colleague, current, other);
        var limits = new GenerationBudgetOptions { Enabled = true, UserEstimatedCostCeilingUsd = 1000000000.00000001m, WorkspaceInternalSafetyCeilingUsd = 10m };
        var service = new GenerationCostGuardrailService(db, Options.Create(limits));
        var empty = await service.EvaluateAsync(viewer.Id, current.Id, null, Estimate(), true);
        Assert.Equal(0m, empty.UserCap!.UsedUsd);
        Assert.True(empty.UserCap.UsageKnown);

        void Add(Guid user, Guid workspace, UsageTransactionStatus status, decimal cost = 0m, bool known = false, decimal? estimate = null)
        {
            var id = Guid.NewGuid();
            var row = new UsageTransaction
            {
                Id = id, RequestId = id.ToString(), WorkspaceId = workspace, UserId = user,
                Provider = "fixture", Model = "fixture", Feature = UsageFeature.Generation,
                Status = status, ProviderCostUsd = cost, ProviderCostKnown = known, EstimatedProviderCostUsd = estimate,
                Currency = UsageCurrencies.Usd, CreatedAt = DateTime.UtcNow,
            };
            db.Add(row);
        }

        Add(viewer.Id, other.Id, UsageTransactionStatus.Completed, 999999999m, true);
        Add(viewer.Id, current.Id, UsageTransactionStatus.Failed, 0.5m, true);
        Add(viewer.Id, current.Id, UsageTransactionStatus.Refunded, 0.5m, true);
        Add(viewer.Id, other.Id, UsageTransactionStatus.Pending, estimate: 0.00000001m);
        Add(viewer.Id, other.Id, UsageTransactionStatus.Cancelled, 5m, false);
        Add(colleague.Id, current.Id, UsageTransactionStatus.Completed, 1m, true);
        Add(colleague.Id, other.Id, UsageTransactionStatus.Pending);
        await db.SaveChangesAsync();

        var result = await service.EvaluateAsync(viewer.Id, current.Id, null, Estimate(), true);
        var preview = GenerationCostPreviewMapper.ToDto(result);
        Assert.Equal("USER_COST_CAP_EXCEEDED", result.RejectionCode);
        Assert.Equal(1000000000.00000001m, preview.UserCap!.UsedUsd);
        Assert.Equal(0m, preview.UserCap.RemainingUsd);
        Assert.True(preview.UserCap.UsageKnown);
        Assert.Equal(2m, preview.WorkspaceCap!.UsedUsd);
        Assert.True(preview.WorkspaceCap.UsageKnown);

        Add(viewer.Id, other.Id, UsageTransactionStatus.Failed);
        await db.SaveChangesAsync();
        var unknown = await service.EvaluateAsync(viewer.Id, current.Id, null, Estimate(), true);
        Assert.Equal("USER_COST_UNKNOWN", unknown.RejectionCode);
        Assert.False(unknown.UserCap!.UsageKnown);
        Assert.True(unknown.WorkspaceCap!.UsageKnown);
        Assert.False(await db.UsageTransactions.AnyAsync(row => row.ChargedAmount != 0m));
    }

    private static GenerationCostEstimate Estimate() =>
        new(true, 0.00000001m, UsageCurrencies.Usd, "test", DateTime.UtcNow, "test", []);
}
