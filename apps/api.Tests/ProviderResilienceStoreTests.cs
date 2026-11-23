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
        var databaseName = $"file:provider-finalization-race-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var now = new DateTime(2026, 10, 26, 1, 30, 0, DateTimeKind.Utc);
        var jobId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var idempotencyKey = "race-finalization";

        await using var keeper = new SqliteConnection($"Data Source={databaseName}");
        await keeper.OpenAsync();
        var schemaOptions = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(keeper).Options;
        await using (var schema = new TaslimDbContext(schemaOptions))
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

        var barrier = new FinalizationReadBarrier();
        await using var connectionA = new SqliteConnection($"Data Source={databaseName}");
        await using var connectionB = new SqliteConnection($"Data Source={databaseName}");
        await connectionA.OpenAsync();
        await connectionB.OpenAsync();
        var optionsA = CreateOptions(connectionA, barrier);
        var optionsB = CreateOptions(connectionB, barrier);
        await using var dbA = new TaslimDbContext(optionsA);
        await using var dbB = new TaslimDbContext(optionsB);

        var results = await Task.WhenAll(
            new EfProviderResilienceStore(dbA).TryClaimFinalizationAsync(jobId, token, idempotencyKey, now, TimeSpan.FromMinutes(2)),
            new EfProviderResilienceStore(dbB).TryClaimFinalizationAsync(jobId, token, idempotencyKey, now, TimeSpan.FromMinutes(2)));

        Assert.Equal(1, results.Count(result => result == ProviderFinalizationClaimResult.Claimed));
        Assert.Equal(1, results.Count(result => result == ProviderFinalizationClaimResult.AlreadyClaimed));
    }

    private static DbContextOptions<TaslimDbContext> CreateOptions(SqliteConnection connection, FinalizationReadBarrier barrier) =>
        new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(barrier)
            .Options;

    private sealed class FinalizationReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> bothReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int participants;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ProviderExecutionFinalizations", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref participants) == 2)
                    bothReads.TrySetResult(true);
                await bothReads.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }
}
