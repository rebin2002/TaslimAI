using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationBudgetProjectCapTests
{
    [Fact]
    public async Task Existing_project_provider_exposure_is_rejected_by_hard_project_cap()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var jobToken = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId, Name = "Budget workspace", Slug = $"budget-{workspaceId:N}",
            Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = userId, UserName = "budget@example.test", NormalizedUserName = "BUDGET@EXAMPLE.TEST",
            Email = "budget@example.test", NormalizedEmail = "BUDGET@EXAMPLE.TEST",
            DisplayName = "Budget Test", CreatedAt = now, UpdatedAt = now,
        };
        var project = new Project
        {
            Id = projectId, WorkspaceId = workspaceId, Name = "Capped project", CreatedAt = now, UpdatedAt = now,
        };
        var job = new GenerationJob
        {
            Id = jobId, WorkspaceId = workspaceId, ProjectId = projectId, CreatedByUserId = userId,
            JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Running,
            InputJson = "{}", ConcurrencyToken = jobToken, CreatedAt = now,
        };
        var priorAttempt = new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(), GenerationJobId = jobId, JobConcurrencyToken = jobToken,
            IdempotencyKey = "generation:prior:attempt:1", AttemptNumber = 1,
            Provider = "synthetic", Capability = job.JobType, Status = GenerationProviderAttemptStatus.Succeeded,
            ResultClassification = "Succeeded", Currency = UsageCurrencies.Usd, StartedAt = now,
            FinalizationKey = "generation:prior:finalization:1", EstimatedProviderCostUsd = 0.80m,
            EstimatedProviderCostKnown = true,
        };
        db.AddRange(workspace, user, project, job, priorAttempt);
        await db.SaveChangesAsync();

        var service = new GenerationBudgetService(
            db,
            Options.Create(new GenerationBudgetOptions
            {
                Enabled = true,
                ProjectEstimatedCostCeilingUsd = 1m,
            }),
            NullLogger<GenerationBudgetService>.Instance);

        var exception = await Assert.ThrowsAsync<GenerationBudgetRejectedException>(() => service.BeginAttemptAsync(
            job,
            "synthetic",
            "provider-neutral",
            new GenerationCostEstimate(true, 0.30m, UsageCurrencies.Usd, "test", now, "synthetic", [])));

        Assert.Equal("PROJECT_COST_CAP_EXCEEDED", exception.Code);
        Assert.Equal(1, await db.GenerationProviderAttempts.CountAsync());
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
