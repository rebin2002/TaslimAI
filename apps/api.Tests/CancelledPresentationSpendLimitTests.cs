using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class CancelledPresentationSpendLimitTests
{
    [Fact]
    public async Task Daily_and_monthly_workspace_caps_use_the_original_reservation_window_and_count_each_reported_attempt_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await CheckWorkspaceWindowsAsync(db);
    }

    private static async Task CheckWorkspaceWindowsAsync(TaslimDbContext db)
    {
        var user = AddUser(db);
        var workspace = AddWorkspace(db);
        var otherWorkspace = AddWorkspace(db);
        var today = DateTime.UtcNow.Date;
        var previousDay = today.AddDays(-1);

        var job = AddJob(db, user, workspace, today, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Cancelled);
        // Even a second historical reservation that points at this job cannot multiply an attempt.
        db.UsageTransactions.Add(new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace, UserId = user, GenerationJobId = job.Id, CreatedAt = today,
            RequestId = $"historical:{job.Id:N}", Status = UsageTransactionStatus.Cancelled,
        });
        AddAttempt(db, job, 1, 0.90m, 0.30000001m);
        AddAttempt(db, job, 2, 0.90m, 0.02m);
        AddAttempt(db, job, 3, 0.90m, null);
        AddAttempt(db, job, 4, 0.90m, 9m, costKnown: false);
        AddAttempt(db, job, 5, 0.90m, 10m, currency: "EUR");
        AddAttempt(db, AddJob(db, user, workspace, previousDay, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Cancelled), 1, null, 0.1m);
        AddAttempt(db, AddJob(db, user, workspace, today.AddMonths(-2), GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Cancelled), 1, null, 5m);
        AddAttempt(db, AddJob(db, user, otherWorkspace, today, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Cancelled), 1, null, 25m);
        AddAttempt(db, AddJob(db, user, workspace, today, GenerationJobTypes.DocumentGenerate, UsageTransactionStatus.Cancelled), 1, null, 12m);
        // This completed job already has its cost on the normal ledger. A cancelled attempt from
        // the same job does not add that expense again.
        var completedJob = AddJob(db, user, workspace, today, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Completed, 0.04m);
        AddAttempt(db, completedJob, 1, null, 0.04m);
        await db.SaveChangesAsync();

        const decimal daily = 0.36000001m;
        var monthly = daily + (previousDay.Month == today.Month ? 0.1m : 0m);
        foreach (var (settings, rejection) in new[]
        {
            (new UsageControlOptions { GuardrailsEnabled = true, DailyWorkspaceProviderCostCeilingUsd = daily }, "DAILY_COST_CEILING_EXCEEDED"),
            (new UsageControlOptions { GuardrailsEnabled = true, MonthlyWorkspaceProviderCostCeilingUsd = monthly }, "MONTHLY_COST_CEILING_EXCEEDED"),
        })
        {
            var control = new UsageCostControl(db, Options.Create(settings), NullLogger<UsageCostControl>.Instance);
            Assert.True((await control.CheckPreflightAsync(workspace, UsageFeature.Presentation, 0m)).Allowed);
            Assert.Equal(rejection, (await control.CheckPreflightAsync(workspace, UsageFeature.Presentation, 0.00000001m)).RejectionCode);
        }
        var reservations = await db.UsageTransactions.Where(row => row.GenerationJobId == job.Id).ToListAsync();
        Assert.Equal(2, reservations.Count);
        Assert.All(reservations, reservation =>
        {
            Assert.False(reservation.IsBillable);
            Assert.Equal(0m, reservation.ChargedAmount);
            Assert.Equal(0m, reservation.ProviderCostUsd);
        });
    }

    [Fact]
    public async Task All_time_workspace_and_cross_workspace_user_caps_replace_cancelled_estimates_with_reported_actuals()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await CheckUserAndWorkspaceCapsAsync(db);
    }

    private static async Task CheckUserAndWorkspaceCapsAsync(TaslimDbContext db)
    {
        var user = AddUser(db);
        var otherUser = AddUser(db);
        var workspace = AddWorkspace(db);
        var otherWorkspace = AddWorkspace(db);
        var past = DateTime.UtcNow.AddMonths(-3);
        var source = AddJob(db, user, workspace, past, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Cancelled);
        AddAttempt(db, source, 1, 20m, 0.40m);
        AddAttempt(db, source, 2, 20m, null);
        AddAttempt(db, AddJob(db, user, otherWorkspace, past, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Cancelled), 1, 20m, 0.20m);
        AddAttempt(db, AddJob(db, otherUser, workspace, past, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Cancelled), 1, 20m, 0.10m);
        await db.SaveChangesAsync();

        var settings = new GenerationBudgetOptions { Enabled = true, WorkspaceInternalSafetyCeilingUsd = 0.50m, UserEstimatedCostCeilingUsd = 0.60m };
        var preflight = new GenerationCostGuardrailService(db, Options.Create(settings));
        var estimate = new GenerationCostEstimate(true, 0m, UsageCurrencies.Usd, "test", past, "test", []);
        var atLimit = await preflight.EvaluateAsync(user, workspace, null, estimate, confirmationAccepted: true);
        Assert.True(atLimit.Allowed);
        Assert.Equal(0.50m, atLimit.WorkspaceCap!.UsedUsd);
        Assert.Equal(0.60m, atLimit.UserCap!.UsedUsd);
        var over = estimate with { AmountUsd = 0.01m };
        Assert.False((await preflight.EvaluateAsync(user, workspace, null, over, confirmationAccepted: true)).Allowed);

        // The worker has its own attempt-based admission. Check each limit independently there.
        foreach (var (options, rejection) in new[]
        {
            (new GenerationBudgetOptions { Enabled = true, WorkspaceInternalSafetyCeilingUsd = 0.50m }, "WORKSPACE_INTERNAL_SAFETY_CEILING_EXCEEDED"),
            (new GenerationBudgetOptions { Enabled = true, UserEstimatedCostCeilingUsd = 0.60m }, "USER_COST_CAP_EXCEEDED"),
        })
        {
            var budget = new GenerationBudgetService(db, Options.Create(options), NullLogger<GenerationBudgetService>.Instance);
            var candidate = AddJob(db, user, workspace, DateTime.UtcNow, GenerationJobTypes.PresentationGenerate, UsageTransactionStatus.Pending);
            await db.SaveChangesAsync();
            var accepted = await budget.BeginAttemptAsync(candidate, "synthetic", "model", estimate);
            Assert.Equal(GenerationProviderAttemptStatus.Started, accepted.Status);
            var refused = await Assert.ThrowsAsync<GenerationBudgetRejectedException>(() => budget.BeginAttemptAsync(candidate, "synthetic", "model", over));
            Assert.Equal(rejection, refused.Code);
        }
    }

    [DisposablePostgresFact]
    public async Task PostgreSql_admission_counts_reported_cancellation_cost_once_for_each_approved_scope()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"presentation_limits_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
            await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scoped).Options);
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            await CheckWorkspaceWindowsAsync(db);
            await CheckUserAndWorkspaceCapsAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Guid AddWorkspace(TaslimDbContext db)
    {
        var id = Guid.NewGuid();
        db.Workspaces.Add(new Workspace { Id = id, Name = "Synthetic", Slug = id.ToString("N"), Type = WorkspaceType.Personal });
        return id;
    }

    private static Guid AddUser(TaslimDbContext db)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, UserName = id.ToString("N"), DisplayName = "Synthetic owner" });
        return id;
    }

    private static GenerationJob AddJob(TaslimDbContext db, Guid user, Guid workspace, DateTime at, string type, UsageTransactionStatus status, decimal ledgerCost = 0m)
    {
        var id = Guid.NewGuid();
        var job = new GenerationJob
        {
            Id = id, WorkspaceId = workspace, CreatedByUserId = user, JobType = type, CreatedAt = at,
            ConcurrencyToken = Guid.NewGuid(),
            Status = status == UsageTransactionStatus.Completed ? GenerationJobStatus.Succeeded
                : status == UsageTransactionStatus.Pending ? GenerationJobStatus.Queued : GenerationJobStatus.Cancelled,
        };
        db.GenerationJobs.Add(job);
        db.UsageTransactions.Add(new UsageTransaction
        {
            Id = Guid.NewGuid(), GenerationJobId = id, WorkspaceId = workspace, UserId = user, CreatedAt = at,
            RequestId = $"generation:{id:N}", Feature = UsageFeature.Generation, Provider = "synthetic", Model = "model",
            Status = status, ProviderCostKnown = status == UsageTransactionStatus.Completed, ProviderCostUsd = ledgerCost,
        });
        return job;
    }

    private static void AddAttempt(TaslimDbContext db, GenerationJob job, int number, decimal? estimate, decimal? actual,
        bool costKnown = true, string currency = UsageCurrencies.Usd)
    {
        var key = $"synthetic:{job.Id:N}:{number}";
        db.GenerationProviderAttempts.Add(new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(), GenerationJobId = job.Id, JobConcurrencyToken = job.ConcurrencyToken,
            AttemptNumber = number, IdempotencyKey = key, FinalizationKey = key, Status = GenerationProviderAttemptStatus.Cancelled,
            EstimatedProviderCostUsd = estimate, EstimatedProviderCostKnown = estimate.HasValue,
            ActualProviderCostUsd = actual, ActualProviderCostKnown = costKnown && actual.HasValue, Currency = currency,
            StartedAt = job.CreatedAt, CompletedAt = job.CreatedAt,
        });
    }
}
