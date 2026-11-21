using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchSourceLifecycleTests
{
    [Fact]
    public async Task Stale_claim_cleanup_does_not_delete_sources_staged_by_recovered_worker()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-research-lifecycle-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var oldClaim = Guid.NewGuid();
        var recoveredClaim = Guid.NewGuid();
        var now = DateTime.UtcNow;

        try
        {
            await using (var seedConnection = new SqliteConnection(connectionString))
            {
                await seedConnection.OpenAsync();
                await using var seedDb = CreateDb(seedConnection);
                await seedDb.Database.EnsureCreatedAsync();
                var user = new ApplicationUser
                {
                    Id = userId,
                    UserName = $"research-lifecycle-{userId:N}@example.com",
                    NormalizedUserName = $"RESEARCH-LIFECYCLE-{userId:N}@EXAMPLE.COM",
                    Email = $"research-lifecycle-{userId:N}@example.com",
                    NormalizedEmail = $"RESEARCH-LIFECYCLE-{userId:N}@EXAMPLE.COM",
                    DisplayName = "Research Lifecycle Tester",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                var workspace = new Workspace
                {
                    Id = workspaceId,
                    Name = "Research Lifecycle Workspace",
                    Slug = $"research-lifecycle-{userId:N}",
                    Type = WorkspaceType.Personal,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                var job = new GenerationJob
                {
                    Id = jobId,
                    WorkspaceId = workspaceId,
                    CreatedByUserId = userId,
                    JobType = GenerationJobTypes.ResearchGenerate,
                    Status = GenerationJobStatus.Running,
                    InputJson = "{}",
                    ConcurrencyToken = oldClaim,
                    CreatedAt = now,
                    StartedAt = now,
                    ClaimExpiresAt = now.AddMinutes(5),
                };
                var source = new ResearchSource
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    GenerationJobId = jobId,
                    CitationId = "S1",
                    Url = "https://example.gov/old",
                    CanonicalUrl = "https://example.gov/old",
                    Title = "Old worker source",
                    Domain = "example.gov",
                    RetrievedAt = now,
                    SourceType = "web",
                    Rank = 1,
                    IsSelected = true,
                };
                seedDb.Users.Add(user);
                seedDb.Workspaces.Add(workspace);
                seedDb.GenerationJobs.Add(job);
                seedDb.ResearchSources.Add(source);
                seedDb.ResearchEvidence.Add(new ResearchEvidence
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    GenerationJobId = jobId,
                    ResearchSourceId = source.Id,
                    Topic = "old",
                    Excerpt = "old evidence",
                    CreatedAt = now,
                });
                await seedDb.SaveChangesAsync();
            }

            var interceptor = new ClaimRolloverBeforeCleanupInterceptor(connectionString, jobId, workspaceId, recoveredClaim, now);
            await using var workerConnection = new SqliteConnection(connectionString);
            await workerConnection.OpenAsync();
            await using var workerDb = CreateDb(workerConnection, interceptor);
            var staleJob = await workerDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);

            var cleared = await ResearchSourceLifecycle.ClearIfClaimOwnedAsync(workerDb, staleJob, CancellationToken.None);

            Assert.True(cleared);
            Assert.True(interceptor.Triggered);
            await using var verifyConnection = new SqliteConnection(connectionString);
            await verifyConnection.OpenAsync();
            await using var verifyDb = CreateDb(verifyConnection);
            var sources = await verifyDb.ResearchSources.AsNoTracking()
                .Where(item => item.GenerationJobId == jobId)
                .OrderBy(item => item.CitationId)
                .ToArrayAsync();
            Assert.Contains(sources, item => item.CitationId == "S2");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
            if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
            if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
        }
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection, DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new TaslimDbContext(builder.Options);
    }

    private sealed class ClaimRolloverBeforeCleanupInterceptor(
        string connectionString,
        Guid jobId,
        Guid workspaceId,
        Guid recoveredClaim,
        DateTime now) : DbCommandInterceptor
    {
        private int triggered;
        public bool Triggered => Volatile.Read(ref triggered) == 1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref triggered, 1) == 0
                && command.CommandText.Contains("DELETE FROM \"ResearchEvidence\"", StringComparison.OrdinalIgnoreCase))
            {
                await using var takeoverConnection = new SqliteConnection(connectionString);
                await takeoverConnection.OpenAsync(cancellationToken);
                await using var takeoverDb = CreateDb(takeoverConnection);
                var job = await takeoverDb.GenerationJobs.SingleAsync(item => item.Id == jobId, cancellationToken);
                job.ConcurrencyToken = recoveredClaim;
                job.Status = GenerationJobStatus.Running;
                await takeoverDb.SaveChangesAsync(cancellationToken);
                takeoverDb.ResearchSources.Add(new ResearchSource
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    GenerationJobId = jobId,
                    CitationId = "S2",
                    Url = "https://example.org/recovered",
                    CanonicalUrl = "https://example.org/recovered",
                    Title = "Recovered worker source",
                    Domain = "example.org",
                    RetrievedAt = now,
                    SourceType = "web",
                    Rank = 1,
                    IsSelected = true,
                });
                await takeoverDb.SaveChangesAsync(cancellationToken);
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
