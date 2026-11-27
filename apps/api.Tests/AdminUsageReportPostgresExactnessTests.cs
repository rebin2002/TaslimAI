using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Verifies the production provider executes currency totals as PostgreSQL numeric SUMs,
/// including a low-order ledger unit that binary floating point loses at a large magnitude.
/// The attribute confines execution to the disposable API CI database.
/// </summary>
public sealed class AdminUsageReportPostgresExactnessTests
{
    private static readonly DateTime WindowStart = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [DisposablePostgresFact]
    public async Task Native_numeric_sums_preserve_low_order_units_in_summary_and_breakdowns()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"usage_report_exact_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var createSchema = admin.CreateCommand();
            createSchema.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
                .Options;
            await using var db = new TaslimDbContext(options);
            Assert.Contains("Npgsql", db.Database.ProviderName, StringComparison.Ordinal);
            await db.Database.EnsureCreatedAsync();

            var workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = "Exactness Workspace",
                Slug = $"usage-report-exact-{Guid.NewGuid():N}",
                Type = WorkspaceType.Business,
                CreatedAt = WindowStart,
                UpdatedAt = WindowStart,
            };
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "exactness@example.com",
                NormalizedUserName = "EXACTNESS@EXAMPLE.COM",
                Email = "exactness@example.com",
                NormalizedEmail = "EXACTNESS@EXAMPLE.COM",
                DisplayName = "Exactness User",
                CreatedAt = WindowStart,
                UpdatedAt = WindowStart,
            };
            db.Workspaces.Add(workspace);
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                JoinedAt = WindowStart,
            });

            db.UsageTransactions.AddRange(
                CreateTransaction(workspace.Id, user.Id, 1_000_000_000.00000000m, 1_000_000_000.00000001m, 0.00000001m, 1_000_000_000.00000000m, 1),
                CreateTransaction(workspace.Id, user.Id, 0.00000001m, 0.00000002m, 0.00000001m, 0.00000001m, 2));
            await db.SaveChangesAsync();

            var filter = new AdminUsageFilter(WindowStart.AddDays(-1), WindowStart.AddDays(1), null, null, null, null, null, 1, 100);
            var report = await new AdminUsageService(db).GetReportAsync(filter);
            const decimal exactTotal = 1_000_000_000.00000001m;

            Assert.Equal(exactTotal, report.Summary.TotalProviderCostUsd);
            Assert.Equal(exactTotal, report.Summary.TotalCustomerChargesUsd);
            Assert.Equal(exactTotal, report.Summary.PendingEstimatedProviderCostUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByFeature).ProviderCostUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByFeature).CustomerChargesUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByDay).ProviderCostUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByDay).CustomerChargesUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByWorkspace).ProviderCostUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByWorkspace).CustomerChargesUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByUser).ProviderCostUsd);
            Assert.Equal(exactTotal, Assert.Single(report.Breakdowns.ByUser).CustomerChargesUsd);
            var status = Assert.Single(report.Breakdowns.ByStatus);
            Assert.Equal("Pending", status.Status);
            Assert.Equal(exactTotal, status.ProviderCostUsd);
            Assert.Equal(exactTotal, status.CustomerChargesUsd);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await using var dropSchema = admin.CreateCommand();
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private static UsageTransaction CreateTransaction(
        Guid workspaceId,
        Guid userId,
        decimal providerCostUsd,
        decimal chargedAmount,
        decimal reversedAmount,
        decimal estimatedProviderCostUsd,
        int second) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        RequestId = Guid.NewGuid().ToString("N"),
        Feature = UsageFeature.Chat,
        Provider = "mock",
        Model = "mock-model",
        Status = UsageTransactionStatus.Pending,
        InputTokens = 10,
        OutputTokens = 10,
        ProviderCostUsd = providerCostUsd,
        ChargedAmount = chargedAmount,
        ReversedAmount = reversedAmount,
        EstimatedProviderCostUsd = estimatedProviderCostUsd,
        ProviderCostKnown = true,
        CostBasis = "Actual",
        Currency = UsageCurrencies.Usd,
        CreatedAt = WindowStart.AddSeconds(second),
    };
}
