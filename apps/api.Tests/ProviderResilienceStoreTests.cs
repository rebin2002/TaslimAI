using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Resilience;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProviderResilienceStoreTests
{
    [Fact]
    public async Task Expired_finalization_claim_is_acquired_by_only_one_concurrent_worker()
    {
        var connectionString = $"Data Source=provider-finalization-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var now = new DateTime(2026, 10, 26, 1, 30, 0, DateTimeKind.Utc);
        var jobId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string idempotencyKey = "race-finalization";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using (var schema = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(keeper).Options))
        {
            await schema.Database.EnsureCreatedAsync();
            schema.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "provider-race@example.com",
                NormalizedUserName = "PROVIDER-RACE@EXAMPLE.COM",
                Email = "provider-race@example.com",
                NormalizedEmail = "PROVIDER-RACE@EXAMPLE.COM",
                DisplayName = "Provider Race",
                CreatedAt = now,
                UpdatedAt = now,
            });
            schema.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Provider Race Workspace",
                Slug = $"provider-race-{workspaceId:N}",
                Type = WorkspaceType.Personal,
                CreatedAt = now,
                UpdatedAt = now,
            });
            schema.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = WorkspaceRole.Owner,
                JoinedAt = now,
            });
            schema.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId,
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                JobType = GenerationJobTypes.SystemTest,
                Status = GenerationJobStatus.Running,
                InputJson = "{}",
                ConcurrencyToken = token,
                CreatedAt = now,
            });
            schema.ProviderExecutionFinalizations.Add(new ProviderExecutionFinalization
            {
                Id = Guid.NewGuid(),
                GenerationJobId = jobId,
                JobConcurrencyToken = token,
                IdempotencyKey = idempotencyKey,
                State = "claimed",
                ClaimedAt = now.AddMinutes(-2),
                ClaimExpiresAt = now.AddMinutes(-1),
            });
            await schema.SaveChangesAsync();
        }

        var saveBarrier = new FinalizationSaveChangesBarrier();
        var updateBarrier = new FinalizationUpdateOrderingInterceptor();
        await using var connectionA = new SqliteConnection(connectionString);
        await using var connectionB = new SqliteConnection(connectionString);
        await connectionA.OpenAsync();
        await connectionB.OpenAsync();
        await using var dbA = new TaslimDbContext(CreateDb(connectionA, saveBarrier, updateBarrier));
        await using var dbB = new TaslimDbContext(CreateDb(connectionB, saveBarrier, updateBarrier));
        var lease = TimeSpan.FromMinutes(2);

        var results = await Task.WhenAll(
            new EfProviderResilienceStore(dbA).TryClaimFinalizationAsync(jobId, token, idempotencyKey, now, lease),
            new EfProviderResilienceStore(dbB).TryClaimFinalizationAsync(jobId, token, idempotencyKey, now, lease));

        Assert.True(saveBarrier.Participants == 2 || updateBarrier.UpdateAttempts == 2,
            "The test must rendezvous both expired-claim contenders before asserting ownership.");
        Assert.Equal(1, results.Count(result => result == ProviderFinalizationClaimResult.Claimed));
        Assert.Equal(1, results.Count(result => result == ProviderFinalizationClaimResult.AlreadyClaimed));

        await using var verify = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(keeper).Options);
        var persisted = await verify.ProviderExecutionFinalizations.SingleAsync(
            item => item.GenerationJobId == jobId && item.IdempotencyKey == idempotencyKey);
        Assert.Equal("claimed", persisted.State);
        Assert.Equal(token, persisted.JobConcurrencyToken);
        Assert.Equal(now, persisted.ClaimedAt);
        Assert.Equal(now.Add(lease), persisted.ClaimExpiresAt);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task Successful_expired_takeover_completes_when_winner_context_preloaded_old_finalization()
    {
        var connectionString = $"Data Source=provider-finalization-tracked-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var now = new DateTime(2026, 10, 26, 2, 0, 0, DateTimeKind.Utc);
        var jobId = Guid.NewGuid();
        var currentToken = Guid.NewGuid();
        var previousToken = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string idempotencyKey = "tracked-finalization-takeover";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using (var schema = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(keeper).Options))
        {
            await schema.Database.EnsureCreatedAsync();
            schema.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "tracked-provider-race@example.com",
                NormalizedUserName = "TRACKED-PROVIDER-RACE@EXAMPLE.COM",
                Email = "tracked-provider-race@example.com",
                NormalizedEmail = "TRACKED-PROVIDER-RACE@EXAMPLE.COM",
                DisplayName = "Tracked Provider Race",
                CreatedAt = now,
                UpdatedAt = now,
            });
            schema.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Tracked Provider Race Workspace",
                Slug = $"tracked-provider-race-{workspaceId:N}",
                Type = WorkspaceType.Personal,
                CreatedAt = now,
                UpdatedAt = now,
            });
            schema.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = WorkspaceRole.Owner,
                JoinedAt = now,
            });
            schema.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId,
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                JobType = GenerationJobTypes.SystemTest,
                Status = GenerationJobStatus.Running,
                InputJson = "{}",
                ConcurrencyToken = currentToken,
                CreatedAt = now,
            });
            schema.ProviderExecutionFinalizations.Add(new ProviderExecutionFinalization
            {
                Id = Guid.NewGuid(),
                GenerationJobId = jobId,
                JobConcurrencyToken = previousToken,
                IdempotencyKey = idempotencyKey,
                State = "claimed",
                ClaimedAt = now.AddMinutes(-2),
                ClaimExpiresAt = now.AddMinutes(-1),
            });
            await schema.SaveChangesAsync();
        }

        await using var workerConnection = new SqliteConnection(connectionString);
        await workerConnection.OpenAsync();
        await using var workerDb = new TaslimDbContext(CreateDb(workerConnection));
        var trackedFinalization = await workerDb.ProviderExecutionFinalizations.SingleAsync(
            item => item.GenerationJobId == jobId && item.IdempotencyKey == idempotencyKey);
        var unrelatedTrackedWorkspace = await workerDb.Workspaces.SingleAsync(item => item.Id == workspaceId);
        Assert.Equal(previousToken, trackedFinalization.JobConcurrencyToken);

        var store = new EfProviderResilienceStore(workerDb);
        var lease = TimeSpan.FromMinutes(2);
        var claim = await store.TryClaimFinalizationAsync(jobId, currentToken, idempotencyKey, now, lease);

        Assert.Equal(ProviderFinalizationClaimResult.Claimed, claim);
        Assert.Same(unrelatedTrackedWorkspace, workerDb.Workspaces.Local.Single(item => item.Id == workspaceId));

        await store.CompleteFinalizationAsync(
            jobId,
            currentToken,
            idempotencyKey,
            ProviderAttemptResultCategory.Success);

        Assert.Equal("completed", trackedFinalization.State);
        Assert.Equal(currentToken, trackedFinalization.JobConcurrencyToken);
        Assert.Equal(now, trackedFinalization.ClaimedAt);
        Assert.Null(trackedFinalization.ClaimExpiresAt);
        await using var verify = new TaslimDbContext(CreateDb(keeper));
        var persisted = await verify.ProviderExecutionFinalizations.AsNoTracking().SingleAsync(
            item => item.GenerationJobId == jobId && item.IdempotencyKey == idempotencyKey);
        Assert.Equal("completed", persisted.State);
        Assert.Equal(currentToken, persisted.JobConcurrencyToken);
        Assert.Null(persisted.ClaimExpiresAt);
        Assert.NotNull(persisted.CompletedAt);
    }

    private static DbContextOptions<TaslimDbContext> CreateDb(SqliteConnection connection, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptors)
            .Options;

    private sealed class FinalizationSaveChangesBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> bothTakeoversReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int participants;

        public int Participants => Volatile.Read(ref participants);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            eventData.Context?.ChangeTracker.DetectChanges();
            if (eventData.Context?.ChangeTracker.Entries<ProviderExecutionFinalization>()
                    .Any(entry => entry.State == EntityState.Modified) == true)
            {
                if (Interlocked.Increment(ref participants) == 2)
                    bothTakeoversReady.TrySetResult(true);
                await bothTakeoversReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private sealed class FinalizationUpdateOrderingInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> secondUpdateCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int updateAttempts;

        public int UpdateAttempts => Volatile.Read(ref updateAttempts);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsFinalizationUpdate(command) && Interlocked.Increment(ref updateAttempts) == 1)
                await secondUpdateCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsFinalizationUpdate(command)) secondUpdateCompleted.TrySetResult(true);
            return new(result);
        }

        private static bool IsFinalizationUpdate(DbCommand command) =>
            command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("ProviderExecutionFinalizations", StringComparison.OrdinalIgnoreCase);
    }
}
