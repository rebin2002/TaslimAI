using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Resilience;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProviderFinalizationReplayTests
{
    [Fact]
    public async Task Terminal_replay_preserves_each_recorded_outcome_without_issuing_a_provider_attempt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var job = await SeedJobAsync(db);
        var store = new EfProviderResilienceStore(db);
        var router = CreateRouter(store);
        foreach (var outcome in Enum.GetValues<ProviderAttemptResultCategory>())
        {
            var key = $"route-{outcome}";
            var code = outcome == ProviderAttemptResultCategory.Success ? null : "SYNTHETIC_REASON";
            Assert.Equal(ProviderFinalizationClaimResult.Claimed, await store.TryClaimFinalizationAsync(
                job.Id, job.ConcurrencyToken, key, DateTime.UtcNow, TimeSpan.FromMinutes(1)));
            await store.CompleteFinalizationAsync(job.Id, job.ConcurrencyToken, key, outcome, errorCode: code);
            await store.CompleteFinalizationAsync(job.Id, job.ConcurrencyToken, key, ProviderAttemptResultCategory.Success);
            db.ChangeTracker.Clear();

            var replay = await router.ExecuteAsync<string, string>(job.Id, job.ConcurrencyToken, "synthetic", key, "fixture", []);
            Assert.True(replay.IsReplay);
            Assert.Equal(outcome, replay.Category);
            Assert.Equal(code, replay.ErrorCode);
            Assert.Equal(outcome == ProviderAttemptResultCategory.Success, replay.Succeeded);
            Assert.Equal(0, replay.AttemptCount);
        }

        const string zeroAttemptKey = "empty-route";
        var original = await router.ExecuteAsync<string, string>(job.Id, job.ConcurrencyToken, "synthetic", zeroAttemptKey, "fixture", []);
        await db.GenerationJobs.Where(row => row.Id == job.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, GenerationJobStatus.Failed));
        var repeated = await router.ExecuteAsync<string, string>(job.Id, job.ConcurrencyToken, "synthetic", zeroAttemptKey, "fixture", []);
        Assert.False(original.IsReplay);
        Assert.True(repeated.IsReplay);
        Assert.Equal(ProviderAttemptResultCategory.Unavailable, repeated.Category);
        Assert.Equal("NO_PROVIDER_AVAILABLE", repeated.ErrorCode);
        Assert.Equal(original.Category, repeated.Category);
        Assert.Equal(original.ErrorCode, repeated.ErrorCode);
        await Assert.ThrowsAsync<StaleProviderWorkerException>(() =>
            router.ExecuteAsync<string, string>(job.Id, Guid.NewGuid(), "synthetic", zeroAttemptKey, "fixture", []));
        Assert.False(await db.GenerationProviderAttempts.AnyAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("FutureCategory")]
    [InlineData("0")]
    public async Task A_historical_or_unrecognized_completion_is_replayed_as_unknown_without_resubmission(string? recorded)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var job = await SeedJobAsync(db);
        db.ProviderExecutionFinalizations.Add(new ProviderExecutionFinalization
        {
            Id = Guid.NewGuid(), GenerationJobId = job.Id, JobConcurrencyToken = job.ConcurrencyToken,
            IdempotencyKey = "historical-route", State = "completed", ClaimedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow, ResultCategory = recorded,
        });
        await db.SaveChangesAsync();
        var replay = await CreateRouter(new EfProviderResilienceStore(db)).ExecuteAsync<string, string>(
            job.Id, job.ConcurrencyToken, "synthetic", "historical-route", "fixture", []);
        Assert.True(replay.IsReplay);
        Assert.False(replay.Succeeded);
        Assert.Equal(ProviderAttemptResultCategory.Unavailable, replay.Category);
        Assert.Equal("EXECUTION_OUTCOME_UNAVAILABLE", replay.ErrorCode);
        Assert.False(await db.GenerationProviderAttempts.AnyAsync());
    }

    private static ProviderResilienceOrchestrator CreateRouter(EfProviderResilienceStore store) =>
        new(store, new AllowAllProviderCostGuard(), Options.Create(new ProviderResilienceOptions()),
            TimeProvider.System, NullLogger<ProviderResilienceOrchestrator>.Instance);

    private static async Task<GenerationJob> SeedJobAsync(TaslimDbContext db)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "synthetic", DisplayName = "Synthetic" };
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Synthetic", Slug = Guid.NewGuid().ToString("N") };
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreatedByUserId = user.Id,
            JobType = GenerationJobTypes.SystemTest, Status = GenerationJobStatus.Running, ConcurrencyToken = Guid.NewGuid(),
        };
        db.AddRange(user, workspace, job);
        await db.SaveChangesAsync();
        return job;
    }
}
