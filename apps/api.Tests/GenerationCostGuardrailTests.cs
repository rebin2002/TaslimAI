using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationCostGuardrailTests
{
    [Fact]
    public async Task Expensive_known_estimate_returns_warning_and_requires_confirmation()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var service = new GenerationCostGuardrailService(db, Options.Create(new GenerationBudgetOptions
        {
            Enabled = true,
            ExpensiveGenerationWarningThresholdUsd = 1m,
            RequireConfirmationForExpensiveGeneration = true,
        }));

        var result = await service.EvaluateAsync(Guid.NewGuid(), Guid.NewGuid(), null, KnownEstimate(1.25m));

        Assert.True(result.Allowed);
        Assert.True(result.ConfirmationRequired);
        Assert.False(result.CanProceed);
        Assert.Contains(result.Warnings, item => item.Code == "EXPENSIVE_GENERATION");
        var preview = GenerationCostPreviewMapper.ToDto(result);
        Assert.Equal("known", preview.EstimateStatus);
        var serialized = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("\"provider\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"model\"", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_estimate_is_explicit_and_fail_closed_when_guardrails_are_enabled()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var service = new GenerationCostGuardrailService(db, Options.Create(new GenerationBudgetOptions
        {
            Enabled = true,
            RejectUnknownEstimates = true,
            RequireConfirmationForUnknownEstimates = true,
        }));

        var result = await service.EvaluateAsync(Guid.NewGuid(), Guid.NewGuid(), null, GenerationCostEstimate.Unknown("pricing_rule_missing"));

        Assert.False(result.Allowed);
        Assert.Equal("COST_ESTIMATE_UNKNOWN", result.RejectionCode);
        Assert.Null(result.Estimate.AmountUsd);
        Assert.Equal("pricing_rule_missing", result.Estimate.UnknownReason);
        Assert.Equal("unknown", GenerationCostPreviewMapper.ToDto(result).EstimateStatus);
    }

    [Fact]
    public async Task Project_cap_rejects_without_automatic_overage_and_exposes_remaining_cap()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Cap test", Slug = $"cap-{workspaceId:N}", Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "cap@example.com", NormalizedUserName = "CAP@EXAMPLE.COM", Email = "cap@example.com", NormalizedEmail = "CAP@EXAMPLE.COM", DisplayName = "Cap Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.Projects.Add(new Project { Id = projectId, WorkspaceId = workspaceId, Name = "Capped project", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.UsageTransactions.Add(new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, ProjectId = projectId,
            RequestId = "existing-exposure", Feature = UsageFeature.Movie, Provider = "internal", Model = "internal",
            Status = UsageTransactionStatus.Completed, ProviderCostUsd = 0.80m, ProviderCostKnown = true,
            ChargedAmount = 0m, Currency = UsageCurrencies.Usd, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = new GenerationCostGuardrailService(db, Options.Create(new GenerationBudgetOptions
        {
            Enabled = true,
            ProjectEstimatedCostCeilingUsd = 1m,
            UserEstimatedCostCeilingUsd = 5m,
        }));

        var result = await service.EvaluateAsync(userId, workspaceId, projectId, KnownEstimate(0.30m), confirmationAccepted: true);

        Assert.False(result.Allowed);
        Assert.Equal("PROJECT_COST_CAP_EXCEEDED", result.RejectionCode);
        Assert.NotNull(result.ProjectCap);
        Assert.Equal(0.80m, result.ProjectCap!.UsedUsd);
        Assert.Equal(0.20m, result.ProjectCap.RemainingUsd);
        Assert.True(result.ProjectCap.WouldExceed);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public void Static_guardrail_enforces_user_and_project_cap_decisions()
    {
        var estimate = KnownEstimate(0.20m);
        var user = GenerationBudgetGuardrail.Evaluate(estimate, new GenerationBudgetSnapshot(0, 0m, 0m, 1m, 0m), new GenerationBudgetOptions { Enabled = true, UserEstimatedCostCeilingUsd = 1m });
        var project = GenerationBudgetGuardrail.Evaluate(estimate, new GenerationBudgetSnapshot(0, 0m, 0m, 0m, 1m), new GenerationBudgetOptions { Enabled = true, ProjectEstimatedCostCeilingUsd = 1m });

        Assert.Equal("USER_COST_CAP_EXCEEDED", user.RejectionCode);
        Assert.Equal("PROJECT_COST_CAP_EXCEEDED", project.RejectionCode);
    }

    private static GenerationCostEstimate KnownEstimate(decimal amount) => new(true, amount, UsageCurrencies.Usd, "test", DateTime.UtcNow, "test", []);

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
