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
    public async Task Completed_actual_cost_from_another_job_is_reconciled_before_project_admission()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        var fixture = CreateProjectFixture(jobCount: 2, now);
        var earlierAttempt = CreateAttempt(fixture.Jobs[0], 1, 0.50m, GenerationProviderAttemptStatus.Started, now);
        db.AddRange(fixture.Workspace, fixture.User, fixture.Project);
        db.GenerationJobs.AddRange(fixture.Jobs);
        db.GenerationProviderAttempts.Add(earlierAttempt);
        await db.SaveChangesAsync();

        var service = CreateService(db, projectCap: 1m);
        await service.CompleteAttemptAsync(
            earlierAttempt,
            actualProviderCostUsd: 1.10m,
            actualCostKnown: true,
            GenerationProviderAttemptStatus.Succeeded);

        var exception = await Assert.ThrowsAsync<GenerationBudgetRejectedException>(() => service.BeginAttemptAsync(
            fixture.Jobs[1],
            "synthetic",
            "provider-neutral",
            Estimate(0.20m, now)));

        Assert.Equal("PROJECT_COST_CAP_EXCEEDED", exception.Code);
        Assert.Equal(1.10m, earlierAttempt.ActualProviderCostUsd);
        Assert.True(earlierAttempt.ActualProviderCostKnown);
        Assert.Equal(1, await db.GenerationProviderAttempts.CountAsync());
    }

    [Fact]
    public async Task Project_cap_sum_uses_exact_decimal_values_at_and_just_above_the_boundary()
    {
        Assert.True(await CanStartAtProjectExposureAsync(0.87654322m)); // 0.12345678 + 0.87654322 == 1.00000000
        Assert.False(await CanStartAtProjectExposureAsync(0.87654323m)); // one 1e-8 USD unit above the cap
    }

    [Fact]
    public async Task Concurrent_jobs_cannot_both_reserve_cost_over_the_project_cap()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"generation-budget-{Guid.NewGuid():N}.sqlite");
        try
        {
            var now = DateTime.UtcNow;
            var fixture = CreateProjectFixture(jobCount: 2, now);
            var options = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            await using (var seedDb = new TaslimDbContext(options))
            {
                seedDb.Database.EnsureCreated();
                seedDb.AddRange(fixture.Workspace, fixture.User, fixture.Project);
                seedDb.GenerationJobs.AddRange(fixture.Jobs);
                await seedDb.SaveChangesAsync();
            }

            var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = fixture.Jobs.Select(TryStartAsync).ToArray();
            startGate.SetResult(true);
            var admitted = await Task.WhenAll(starts);

            Assert.Equal(1, admitted.Count(result => result));
            await using var verifyDb = new TaslimDbContext(options);
            Assert.Equal(1, await verifyDb.GenerationProviderAttempts.CountAsync());

            async Task<bool> TryStartAsync(GenerationJob job)
            {
                await startGate.Task;
                await using var workerDb = new TaslimDbContext(options);
                var service = CreateService(workerDb, projectCap: 1m);
                try
                {
                    await service.BeginAttemptAsync(job, "synthetic", "provider-neutral", Estimate(0.60m, now));
                    return true;
                }
                catch (GenerationBudgetRejectedException exception) when (exception.Code == "PROJECT_COST_CAP_EXCEEDED")
                {
                    return false;
                }
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(databasePath)!, Path.GetFileName(databasePath) + "*"))
                File.Delete(file);
        }
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

        var service = CreateService(db, projectCap: 1m, workspaceCap: 1.5m);
        var attempt = await service.BeginAttemptAsync(
            targetJob,
            "synthetic",
            "provider-neutral",
            Estimate(0.30m, now));

        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(3, await db.GenerationProviderAttempts.CountAsync());
    }

    private static async Task<bool> CanStartAtProjectExposureAsync(decimal secondPriorEstimate)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        var fixture = CreateProjectFixture(jobCount: 3, now);
        var firstAttempt = CreateAttempt(fixture.Jobs[0], 1, 0.12345678m, GenerationProviderAttemptStatus.Succeeded, now);
        var secondAttempt = CreateAttempt(fixture.Jobs[1], 1, secondPriorEstimate, GenerationProviderAttemptStatus.Succeeded, now);
        db.AddRange(fixture.Workspace, fixture.User, fixture.Project);
        db.GenerationJobs.AddRange(fixture.Jobs);
        db.GenerationProviderAttempts.AddRange(firstAttempt, secondAttempt);
        await db.SaveChangesAsync();

        var service = CreateService(db, projectCap: 1m);
        try
        {
            await service.BeginAttemptAsync(fixture.Jobs[2], "synthetic", "provider-neutral", Estimate(0m, now));
            return true;
        }
        catch (GenerationBudgetRejectedException exception) when (exception.Code == "PROJECT_COST_CAP_EXCEEDED")
        {
            return false;
        }
    }

    private static ProjectBudgetFixture CreateProjectFixture(int jobCount, DateTime now)
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId, Name = "Budget workspace", Slug = $"budget-{workspaceId:N}",
            Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = userId, UserName = $"budget-{userId:N}@example.test", NormalizedUserName = $"BUDGET-{userId:N}@EXAMPLE.TEST",
            Email = $"budget-{userId:N}@example.test", NormalizedEmail = $"BUDGET-{userId:N}@EXAMPLE.TEST",
            DisplayName = "Budget Test", CreatedAt = now, UpdatedAt = now,
        };
        var project = new Project
        {
            Id = projectId, WorkspaceId = workspaceId, Name = "Capped project", CreatedAt = now, UpdatedAt = now,
        };
        var jobs = Enumerable.Range(0, jobCount)
            .Select(_ => new GenerationJob
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, ProjectId = projectId, CreatedByUserId = userId,
                JobType = GenerationJobTypes.DocumentGenerate, Status = GenerationJobStatus.Running,
                InputJson = "{}", ConcurrencyToken = Guid.NewGuid(), CreatedAt = now,
            })
            .ToArray();
        return new ProjectBudgetFixture(workspace, user, project, jobs);
    }

    private static GenerationProviderAttempt CreateAttempt(
        GenerationJob job,
        int attemptNumber,
        decimal estimatedCost,
        GenerationProviderAttemptStatus status,
        DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        GenerationJobId = job.Id,
        JobConcurrencyToken = job.ConcurrencyToken,
        IdempotencyKey = $"generation:{job.Id:N}:attempt:{attemptNumber}",
        AttemptNumber = attemptNumber,
        Provider = "synthetic",
        Capability = job.JobType,
        Status = status,
        ResultClassification = status.ToString(),
        Currency = UsageCurrencies.Usd,
        StartedAt = now,
        FinalizationKey = $"generation:{job.Id:N}:finalization:{attemptNumber}",
        EstimatedProviderCostUsd = estimatedCost,
        EstimatedProviderCostKnown = true,
    };

    private static GenerationCostEstimate Estimate(decimal amount, DateTime now) =>
        new(true, amount, UsageCurrencies.Usd, "test", now, "synthetic", []);

    private static GenerationBudgetService CreateService(
        TaslimDbContext db,
        decimal projectCap,
        decimal? workspaceCap = null) => new(
        db,
        Options.Create(new GenerationBudgetOptions
        {
            Enabled = true,
            ProjectEstimatedCostCeilingUsd = projectCap,
            WorkspaceInternalSafetyCeilingUsd = workspaceCap,
        }),
        NullLogger<GenerationBudgetService>.Instance);

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed record ProjectBudgetFixture(
        Workspace Workspace,
        ApplicationUser User,
        Project Project,
        GenerationJob[] Jobs);
}
