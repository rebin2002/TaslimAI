using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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

            await using var workerConnection = new SqliteConnection(connectionString);
            await workerConnection.OpenAsync();
            await using var workerDb = CreateDb(workerConnection);
            var staleJob = await workerDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);

            // A recovered worker takes over the claim and stages its own source before the stale
            // cleanup runs, committed and visible in the database.
            await using (var takeoverConnection = new SqliteConnection(connectionString))
            {
                await takeoverConnection.OpenAsync();
                await using var takeoverDb = CreateDb(takeoverConnection);
                var takenOver = await takeoverDb.GenerationJobs.SingleAsync(item => item.Id == jobId);
                takenOver.ConcurrencyToken = recoveredClaim;
                await takeoverDb.SaveChangesAsync();
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
                    Rank = 2,
                    IsSelected = true,
                });
                await takeoverDb.SaveChangesAsync();
            }

            var cleared = await ResearchSourceLifecycle.ClearIfClaimOwnedAsync(workerDb, staleJob, CancellationToken.None);

            // The claim fence rejects the stale worker because the committed token no longer
            // matches, so cleanup never runs: neither the recovered worker's S2 nor the leftover
            // S1 is deleted, and the call must report failure rather than the previously
            // pinned unconditional success.
            Assert.False(cleared);
            await using var verifyConnection = new SqliteConnection(connectionString);
            await verifyConnection.OpenAsync();
            await using var verifyDb = CreateDb(verifyConnection);
            var sources = await verifyDb.ResearchSources.AsNoTracking()
                .Where(item => item.GenerationJobId == jobId)
                .OrderBy(item => item.CitationId)
                .ToArrayAsync();
            Assert.Contains(sources, item => item.CitationId == "S2");
            Assert.Contains(sources, item => item.CitationId == "S1");
            var claim = await verifyDb.GenerationJobs.AsNoTracking()
                .Where(item => item.Id == jobId)
                .Select(item => item.ConcurrencyToken)
                .SingleAsync();
            Assert.Equal(recoveredClaim, claim);
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

    [Fact]
    public async Task Stale_worker_cannot_stage_sources_after_claim_takeover_sqlite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-research-staging-{Guid.NewGuid():N}.db");
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
                seedDb.Users.Add(new ApplicationUser
                {
                    Id = userId,
                    UserName = $"research-staging-{userId:N}@example.com",
                    NormalizedUserName = $"RESEARCH-STAGING-{userId:N}@EXAMPLE.COM",
                    DisplayName = "Research Staging Tester",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                seedDb.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Research Staging Workspace", Slug = $"research-staging-{userId:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
                seedDb.GenerationJobs.Add(new GenerationJob
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
                });
                await seedDb.SaveChangesAsync();
            }

            await using var workerConnection = new SqliteConnection(connectionString);
            await workerConnection.OpenAsync();
            await using var workerDb = CreateDb(workerConnection);
            var staleJob = await workerDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);

            // The recovered worker takes over and stages S2, committed, before the stale worker's
            // persistence attempt runs; the fence must lose before any cleanup or staging executes.
            await using (var takeoverConnection = new SqliteConnection(connectionString))
            {
                await takeoverConnection.OpenAsync();
                await using var takeoverDb = CreateDb(takeoverConnection);
                var takenOver = await takeoverDb.GenerationJobs.SingleAsync(item => item.Id == jobId);
                takenOver.ConcurrencyToken = recoveredClaim;
                await takeoverDb.SaveChangesAsync();
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
                    Rank = 2,
                    IsSelected = true,
                });
                await takeoverDb.SaveChangesAsync();
            }

            var staged = await ResearchSourceLifecycle.TryPersistUnderClaimAsync(workerDb, staleJob, ct =>
            {
                workerDb.ResearchSources.Add(new ResearchSource
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    GenerationJobId = jobId,
                    CitationId = "STALE",
                    CanonicalUrl = "https://example.gov/stale",
                    Title = "Stale worker source",
                    Domain = "example.gov",
                    RetrievedAt = DateTime.UtcNow,
                    SourceType = "web",
                    Rank = 1,
                    IsSelected = true,
                });
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.False(staged);
            await using var verifyConnection = new SqliteConnection(connectionString);
            await verifyConnection.OpenAsync();
            await using var verifyDb = CreateDb(verifyConnection);
            Assert.False(await verifyDb.ResearchSources.AsNoTracking().AnyAsync(item => item.CitationId == "STALE"));
            Assert.True(await verifyDb.ResearchSources.AsNoTracking().AnyAsync(item => item.CitationId == "S2"));
            var claim = await verifyDb.GenerationJobs.AsNoTracking().Where(item => item.Id == jobId).Select(item => item.ConcurrencyToken).SingleAsync();
            Assert.Equal(recoveredClaim, claim);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
            if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
            if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
        }
    }

    [Fact]
    public async Task Claim_takeover_cannot_commit_while_worker_holds_the_claim_fence()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-research-fencelock-{Guid.NewGuid():N}.db");
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
        var attemptedClaim = Guid.NewGuid();
        var now = DateTime.UtcNow;
        try
        {
            await using (var seedConnection = new SqliteConnection(connectionString))
            {
                await seedConnection.OpenAsync();
                await using var seedDb = CreateDb(seedConnection);
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.Users.Add(new ApplicationUser
                {
                    Id = userId,
                    UserName = $"research-fencelock-{userId:N}@example.com",
                    NormalizedUserName = $"RESEARCH-FENCELOCK-{userId:N}@EXAMPLE.COM",
                    DisplayName = "Research Fence Lock Tester",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                seedDb.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Research Fence Lock Workspace", Slug = $"research-fencelock-{userId:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
                seedDb.GenerationJobs.Add(new GenerationJob
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
                });
                var source = new ResearchSource
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    GenerationJobId = jobId,
                    CitationId = "S1",
                    Url = "https://example.gov/old",
                    CanonicalUrl = "https://example.gov/old",
                    Title = "Original worker source",
                    Domain = "example.gov",
                    RetrievedAt = now,
                    SourceType = "web",
                    Rank = 1,
                    IsSelected = true,
                };
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

            // The owner runs a fenced cleanup while a would-be second worker attempts to commit a
            // claim takeover exactly at the fence statement. The takeover must be blocked until the
            // fenced transaction finishes, so the ownership decision, cleanup and any staging stay
            // atomic: the legitimate owner completes consistently and the claim never transfers.
            var interceptor = new ClaimRolloverBeforeCleanupInterceptor(connectionString, jobId, workspaceId, attemptedClaim, now);
            await using var workerConnection = new SqliteConnection(connectionString);
            await workerConnection.OpenAsync();
            await using var workerDb = CreateDb(workerConnection, interceptor);
            var ownedJob = await workerDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);

            var cleared = await ResearchSourceLifecycle.ClearIfClaimOwnedAsync(workerDb, ownedJob, CancellationToken.None);

            Assert.True(interceptor.Attempted);
            Assert.True(interceptor.TakeoverBlocked);
            Assert.True(cleared);
            await using var verifyConnection = new SqliteConnection(connectionString);
            await verifyConnection.OpenAsync();
            await using var verifyDb = CreateDb(verifyConnection);
            Assert.False(await verifyDb.ResearchSources.AsNoTracking().AnyAsync(item => item.GenerationJobId == jobId));
            Assert.False(await verifyDb.ResearchEvidence.AsNoTracking().AnyAsync(item => item.GenerationJobId == jobId));
            Assert.False(await verifyDb.ResearchSources.AsNoTracking().AnyAsync(item => item.CitationId == "S2"));
            var claim = await verifyDb.GenerationJobs.AsNoTracking()
                .Where(item => item.Id == jobId)
                .Select(item => item.ConcurrencyToken)
                .SingleAsync();
            Assert.Equal(oldClaim, claim);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
            if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
            if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
        }
    }

    [DisposablePostgresFact]
    public async Task Stale_worker_cannot_stage_sources_after_claim_takeover_postgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"research_staging_fence_{Guid.NewGuid():N}";
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var oldClaim = Guid.NewGuid();
        var recoveredClaim = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scoped.ConnectionString).Options;
        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE SCHEMA \"{schema}\"";
                await create.ExecuteNonQueryAsync();
            }
            await using (var setup = new TaslimDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Users.Add(new ApplicationUser
                {
                    Id = userId,
                    UserName = $"research-pg-{userId:N}@example.com",
                    NormalizedUserName = $"RESEARCH-PG-{userId:N}@EXAMPLE.COM",
                    DisplayName = "Research Postgres Tester",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                setup.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Research Pg Workspace", Slug = $"research-pg-{userId:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
                setup.GenerationJobs.Add(new GenerationJob
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
                });
                await setup.SaveChangesAsync();
            }

            Guid staleClaimToken;
            await using (var reader = new TaslimDbContext(options))
            {
                staleClaimToken = (await reader.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)).ConcurrencyToken;
            }
            var staleJob = new GenerationJob { Id = jobId, WorkspaceId = workspaceId, CreatedByUserId = userId, JobType = GenerationJobTypes.ResearchGenerate, Status = GenerationJobStatus.Running, InputJson = "{}", ConcurrencyToken = staleClaimToken, CreatedAt = now, StartedAt = now, ClaimExpiresAt = now.AddMinutes(5) };

            // Recovered worker takes over and stages its source before the stale attempt runs.
            await using (var recovered = new TaslimDbContext(options))
            {
                var job = await recovered.GenerationJobs.SingleAsync(item => item.Id == jobId);
                job.ConcurrencyToken = recoveredClaim;
                recovered.ResearchSources.Add(new ResearchSource
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    GenerationJobId = jobId,
                    CitationId = "S2",
                    CanonicalUrl = "https://example.org/recovered",
                    Title = "Recovered worker source",
                    Domain = "example.org",
                    RetrievedAt = now,
                    SourceType = "web",
                    Rank = 1,
                    IsSelected = true,
                });
                await recovered.SaveChangesAsync();
            }

            await using var worker = new TaslimDbContext(options);
            var staged = await ResearchSourceLifecycle.TryPersistUnderClaimAsync(worker, staleJob, ct =>
            {
                worker.ResearchSources.Add(new ResearchSource
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    GenerationJobId = jobId,
                    CitationId = "STALE",
                    CanonicalUrl = "https://example.gov/stale",
                    Title = "Stale worker source",
                    Domain = "example.gov",
                    RetrievedAt = DateTime.UtcNow,
                    SourceType = "web",
                    Rank = 1,
                    IsSelected = true,
                });
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.False(staged);
            await using var verify = new TaslimDbContext(options);
            Assert.False(await verify.ResearchSources.AsNoTracking().AnyAsync(item => item.CitationId == "STALE"));
            Assert.True(await verify.ResearchSources.AsNoTracking().AnyAsync(item => item.CitationId == "S2"));
            var claim = await verify.GenerationJobs.AsNoTracking().Where(item => item.Id == jobId).Select(item => item.ConcurrencyToken).SingleAsync();
            Assert.Equal(recoveredClaim, claim);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ClaimRolloverBeforeCleanupInterceptor(
        string connectionString,
        Guid jobId,
        Guid workspaceId,
        Guid recoveredClaim,
        DateTime now) : DbCommandInterceptor
    {
        private int attempted;

        // True once the interceptor has reached the fence statement and tried to commit a takeover.
        public bool Attempted => Volatile.Read(ref attempted) == 1;

        // True when the concurrent takeover could not commit because the worker's fenced
        // transaction already holds the database write lock.
        public bool TakeoverBlocked { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref attempted, 1) == 0
                && command.CommandText.Contains("UPDATE \"GenerationJobs\"", StringComparison.OrdinalIgnoreCase))
            {
                try
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
                catch (Exception ex) when (ex is DbUpdateException or SqliteException)
                {
                    // SQLite's shared-cache write lock held by the fence transaction makes the
                    // takeover fail outright; ownership therefore never transfers mid-transaction.
                    TakeoverBlocked = true;
                }
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
