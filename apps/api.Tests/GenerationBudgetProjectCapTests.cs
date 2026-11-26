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

    [Fact]
    public async Task Project_cap_is_scoped_to_workspace_and_project_provider_exposure()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        var workspaceId = Guid.NewGuid();
        var otherWorkspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var targetProjectId = Guid.NewGuid();
        var sameWorkspaceOtherProjectId = Guid.NewGuid();
        var otherWorkspaceProjectId = Guid.NewGuid();
        var targetJobId = Guid.NewGuid();
        var sameWorkspaceJobId = Guid.NewGuid();
        var otherWorkspaceJobId = Guid.NewGuid();
        var targetJobToken = Guid.NewGuid();
        var sameWorkspaceJobToken = Guid.NewGuid();
        var otherWorkspaceJobToken = Guid.NewGuid();

        var workspace = new Workspace
        {
            Id = workspaceId, Name = "Target workspace", Slug = $"target-{workspaceId:N}",
            Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now,
        };
        var otherWorkspace = new Workspace
        {
            Id = otherWorkspaceId, Name = "Other workspace", Slug = $"other-{otherWorkspaceId:N}",
            Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = userId, UserName = "isolation@example.test", NormalizedUserName = "ISOLATION@EXAMPLE.TEST",
            Email = "isolation@example.test", NormalizedEmail = "ISOLATION@EXAMPLE.TEST",
            DisplayName = "Isolation Test", CreatedAt = now, UpdatedAt = now,
        };
        var targetProject = new Project
        {
            Id = targetProjectId, WorkspaceId = workspaceId, Name = "Target project", CreatedAt = now, UpdatedAt = now,
        };
        var sameWorkspaceOtherProject = new Project
        {
            Id = sameWorkspaceOtherProjectId, WorkspaceId = workspaceId, Name = "Other project", CreatedAt = now, UpdatedAt = now,
        };
        var otherWorkspaceProject = new Project
        {
            Id = otherWorkspaceProjectId, WorkspaceId = otherWorkspaceId, Name = "Other workspace project", CreatedAt = now, UpdatedAt = now,
        };
        var targetJob = new GenerationJob
        {
            Id = targetJobId, WorkspaceId = workspaceId, ProjectId = targetProjectId, CreatedByUserId = userId,
            JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Running,
            InputJson = "{}", ConcurrencyToken = targetJobToken, CreatedAt = now,
        };
        var sameWorkspaceJob = new GenerationJob
        {
            Id = sameWorkspaceJobId, WorkspaceId = workspaceId, ProjectId = sameWorkspaceOtherProjectId, CreatedByUserId = userId,
            JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Running,
            InputJson = "{}", ConcurrencyToken = sameWorkspaceJobToken, CreatedAt = now,
        };
        var otherWorkspaceJob = new GenerationJob
        {
            Id = otherWorkspaceJobId, WorkspaceId = otherWorkspaceId, ProjectId = otherWorkspaceProjectId, CreatedByUserId = userId,
            JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Running,
            InputJson = "{}", ConcurrencyToken = otherWorkspaceJobToken, CreatedAt = now,
        };
        var sameWorkspaceOtherProjectAttempt = new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(), GenerationJobId = sameWorkspaceJobId, JobConcurrencyToken = sameWorkspaceJobToken,
            IdempotencyKey = "generation:other-project:attempt:1", AttemptNumber = 1,
            Provider = "synthetic", Capability = sameWorkspaceJob.JobType, Status = GenerationProviderAttemptStatus.Succeeded,
            ResultClassification = "Succeeded", Currency = UsageCurrencies.Usd, StartedAt = now,
            FinalizationKey = "generation:other-project:finalization:1", EstimatedProviderCostUsd = 0.95m,
            EstimatedProviderCostKnown = true,
        };
        var otherWorkspaceAttempt = new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(), GenerationJobId = otherWorkspaceJobId, JobConcurrencyToken = otherWorkspaceJobToken,
            IdempotencyKey = "generation:other-workspace:attempt:1", AttemptNumber = 1,
            Provider = "synthetic", Capability = otherWorkspaceJob.JobType, Status = GenerationProviderAttemptStatus.Succeeded,
            ResultClassification = "Succeeded", Currency = UsageCurrencies.Usd, StartedAt = now,
            FinalizationKey = "generation:other-workspace:finalization:1", EstimatedProviderCostUsd = 0.95m,
            EstimatedProviderCostKnown = true,
        };
        db.AddRange(
            workspace, otherWorkspace, user,
            targetProject, sameWorkspaceOtherProject, otherWorkspaceProject,
            targetJob, sameWorkspaceJob, otherWorkspaceJob,
            sameWorkspaceOtherProjectAttempt, otherWorkspaceAttempt);
        await db.SaveChangesAsync();

        var service = new GenerationBudgetService(
            db,
            Options.Create(new GenerationBudgetOptions
            {
                Enabled = true,
                WorkspaceInternalSafetyCeilingUsd = 1.5m,
                ProjectEstimatedCostCeilingUsd = 1m,
            }),
            NullLogger<GenerationBudgetService>.Instance);

        var attempt = await service.BeginAttemptAsync(
            targetJob,
            "synthetic",
            "provider-neutral",
            new GenerationCostEstimate(true, 0.30m, UsageCurrencies.Usd, "test", now, "synthetic", []));

        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(3, await db.GenerationProviderAttempts.CountAsync());
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
