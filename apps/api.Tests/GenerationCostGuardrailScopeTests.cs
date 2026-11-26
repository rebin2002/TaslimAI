using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationCostGuardrailScopeTests
{
    [Fact]
    public async Task User_cost_cap_counts_known_exposure_across_workspaces()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var userId = Guid.NewGuid();
        var currentWorkspaceId = Guid.NewGuid();
        var otherWorkspaceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "cross-workspace-cap@example.com",
            NormalizedUserName = "CROSS-WORKSPACE-CAP@EXAMPLE.COM",
            Email = "cross-workspace-cap@example.com",
            NormalizedEmail = "CROSS-WORKSPACE-CAP@EXAMPLE.COM",
            DisplayName = "Cross Workspace Cap",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.AddRange(
            new Workspace { Id = currentWorkspaceId, Name = "Current", Slug = $"current-{currentWorkspaceId:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now },
            new Workspace { Id = otherWorkspaceId, Name = "Other", Slug = $"other-{otherWorkspaceId:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.UsageTransactions.Add(new UsageTransaction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = otherWorkspaceId,
            UserId = userId,
            RequestId = "known-exposure-in-other-workspace",
            Feature = UsageFeature.Generation,
            Provider = "internal",
            Model = "internal",
            Status = UsageTransactionStatus.Completed,
            ProviderCostUsd = 0.80m,
            ProviderCostKnown = true,
            ChargedAmount = 0m,
            Currency = UsageCurrencies.Usd,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var service = new GenerationCostGuardrailService(db, Options.Create(new GenerationBudgetOptions
        {
            Enabled = true,
            UserEstimatedCostCeilingUsd = 1m,
        }));

        var result = await service.EvaluateAsync(
            userId,
            currentWorkspaceId,
            projectId: null,
            new GenerationCostEstimate(true, 0.30m, UsageCurrencies.Usd, "test", now, "test", []),
            confirmationAccepted: true);

        Assert.False(result.Allowed);
        Assert.Equal("USER_COST_CAP_EXCEEDED", result.RejectionCode);
        Assert.NotNull(result.UserCap);
        Assert.Equal(0.80m, result.UserCap!.UsedUsd);
        Assert.Equal(0.20m, result.UserCap.RemainingUsd);
        Assert.True(result.UserCap.WouldExceed);
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
