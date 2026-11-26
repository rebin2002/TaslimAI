using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Ai;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Images;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed partial class MovieFinalAssemblyQueueRedrive696Tests
{
    [Fact]
    public async Task Sqlite_assembly_job_link_and_usage_publish_once_across_faults_and_racing_retries()
    {
        var directory = Directory.CreateTempSubdirectory("assembly-publish-");
        try
        {
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory.FullName, "movie.db"), Pooling = false,
            };
            await VerifyAtomicPublication(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection.ToString()));
        }
        finally { directory.Delete(recursive: true); }
    }

    [DisposablePostgresFact]
    public async Task Postgres_assembly_job_link_and_usage_publish_once_across_faults_and_racing_retries()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__Postgres"));
        var schema = $"movie_publish_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE SCHEMA \"{schema}\"";
        await command.ExecuteNonQueryAsync();
        try
        {
            connection.SearchPath = schema;
            await VerifyAtomicPublication(new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connection.ConnectionString));
        }
        finally
        {
            command.CommandText = $"DROP SCHEMA \"{schema}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyAtomicPublication(DbContextOptionsBuilder<TaslimDbContext> builder)
    {
        // Each observer has its own connection. It sees only committed transactions.
        var options = builder.Options;
        await using var observer = new TaslimDbContext(options);
        await observer.Database.EnsureCreatedAsync();
        var world = await SeedAsync(observer);
        var commit = new PublicationCommit();
        var linkFault = new RejectAssemblyLinkOnce();
        await using var writer = new TaslimDbContext(builder.AddInterceptors(commit, linkFault).Options);
        var enqueue = new ObservedAssemblyQueue(writer, commit);
        var jobs = RealAssemblyJobs(writer, enqueue);
        var service = CreateService(writer, jobs);
        Task<MovieFinalAssemblyDto?> Submit() =>
            service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);

        // A fault at the link write happens after inserting the pending job, before reservation
        // or publication. Nothing worker-visible may survive this failed transaction.
        await Assert.ThrowsAsync<IOException>(Submit);
        Assert.True(linkFault.Observed);
        Assert.Equal(0, enqueue.Calls);
        Assert.False(await observer.GenerationJobs.AnyAsync());
        Assert.False(await observer.UsageTransactions.AnyAsync());

        // Even after the real queue SQL runs, the worker cannot see any part of the transaction.
        // Losing the connection at this point must roll back both links and the usage reservation.
        async Task BeforeCommit(Guid jobId)
        {
            Assert.False(await observer.GenerationJobs.AsNoTracking().AnyAsync(job => job.Id == jobId));
            Assert.False(await observer.UsageTransactions.AsNoTracking().AnyAsync(row => row.GenerationJobId == jobId));
            Assert.Null(await observer.MovieAssemblies.AsNoTracking().Select(row => row.GenerationJobId).SingleAsync());
        }
        enqueue.AfterEnqueue = async jobId =>
        {
            await BeforeCommit(jobId);
            throw new IOException("Synthetic queue failure before commit");
        };
        Assert.Equal(MovieAssemblyStatuses.Failed, (await Submit())!.Status);
        var target = await observer.MovieAssemblies.AsNoTracking().SingleAsync();
        Assert.Null(target.GenerationJobId);
        Assert.Equal(GenerationJobErrorCodes.MovieAssemblyQueueFailed, target.LastErrorCode);
        Assert.False(await observer.GenerationJobs.AnyAsync());
        Assert.False(await observer.UsageTransactions.AnyAsync());

        // A process can also disappear after first persisting the plan without reaching job
        // creation. A fresh plan still belongs to its writer. Only an expired plan can re-drive.
        var plannedCheckpoint = "{\"phase\":\"planned\",\"progressPercent\":0}";
        await observer.MovieAssemblies.Where(row => row.Id == target.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.Status, MovieAssemblyStatuses.Planned)
            .SetProperty(row => row.CheckpointJson, plannedCheckpoint)
            .SetProperty(row => row.LastErrorCode, (string?)null)
            .SetProperty(row => row.UpdatedAt, DateTime.UtcNow));
        writer.ChangeTracker.Clear();
        Assert.Equal(MovieAssemblyStatuses.Planned, (await Submit())!.Status);
        Assert.Equal(1, enqueue.Calls);
        await observer.MovieAssemblies.Where(row => row.Id == target.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.UpdatedAt, DateTime.UtcNow.AddHours(-1)));
        writer.ChangeTracker.Clear();

        // Hold the first re-drive before commit while an independent requester retries the same key.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        enqueue.AfterEnqueue = async jobId =>
        {
            await BeforeCommit(jobId);
            entered.SetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(20));
        };
        var winner = Submit();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var contender = new TaslimDbContext(options);
            var competingQueue = new ObservedAssemblyQueue(contender, new PublicationCommit());
            var competing = await CreateService(contender, RealAssemblyJobs(contender, competingQueue))
                .RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
            Assert.Equal(target.Id, competing!.Id);
            Assert.Equal(0, competingQueue.Calls);
        }
        finally { resume.TrySetResult(); }

        var published = await winner;
        Assert.Equal(target.Id, published!.Id);
        var initialJob = await observer.GenerationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(initialJob.Id, published.GenerationJobId);
        Assert.Equal(GenerationJobStatus.Queued, initialJob.Status);
        Assert.Equal(initialJob.Id, (await observer.UsageTransactions.AsNoTracking().SingleAsync()).GenerationJobId);
        Assert.Equal(initialJob.Id, (await Submit())!.GenerationJobId);
        Assert.Equal(2, enqueue.Calls);

        // An ordinary failed/cancelled execution can only transfer to an explicit retry.
        // Run a worker checkpoint after the database commits but before the retry returns.
        await observer.GenerationJobs.Where(job => job.Id == initialJob.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.Status, GenerationJobStatus.Failed));
        await observer.MovieAssemblies.Where(row => row.Id == target.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, MovieAssemblyStatuses.Failed));
        Assert.Equal(initialJob.Id, (await Submit())!.GenerationJobId);
        enqueue.AfterEnqueue = _ => Task.CompletedTask;
        commit.AfterCommit = async jobId =>
        {
            var queued = await observer.MovieAssemblies.AsNoTracking().SingleAsync();
            Assert.Equal(jobId, queued.GenerationJobId);
            Assert.Contains("retry_resume", queued.CheckpointJson);
            Assert.Equal(MovieAssemblyStatuses.Queued, queued.Status);
            var token = Guid.NewGuid();
            Assert.Equal(1, await observer.GenerationJobs.Where(job => job.Id == jobId && job.Status == GenerationJobStatus.Queued)
                .ExecuteUpdateAsync(set => set.SetProperty(job => job.Status, GenerationJobStatus.Running)
                    .SetProperty(job => job.ConcurrencyToken, token)));
            var store = new MovieFinalAssemblyExecutionStore(observer);
            await store.MarkRunningAsync(jobId, token, CancellationToken.None);
            await store.TouchCheckpointAsync(target.Id, jobId, token, "rendering", 20, CancellationToken.None);
            throw new IOException("Synthetic lost acknowledgement after commit");
        };
        await Assert.ThrowsAsync<IOException>(() => jobs.RetryAsync(world.UserId, initialJob.Id, idempotencyKey: "explicit-retry"));
        commit.AfterCommit = null;
        var retry = await jobs.RetryAsync(world.UserId, initialJob.Id, idempotencyKey: "explicit-retry");
        var inFlight = await observer.MovieAssemblies.AsNoTracking().SingleAsync();
        Assert.Equal(initialJob.Id, retry!.RetryOfJobId);
        Assert.Equal(retry.Id, inFlight.GenerationJobId);
        Assert.Equal(MovieAssemblyStatuses.Assembling, inFlight.Status);
        Assert.Equal(20, inFlight.ProgressPercent);
        Assert.Equal(1, inFlight.AttemptCount);
        Assert.Equal(3, enqueue.Calls);
        Assert.Equal(2, await observer.GenerationJobs.CountAsync());
        Assert.Equal(2, await observer.UsageTransactions.CountAsync());
        Assert.False(await observer.CreditLedgerEntries.AnyAsync());
        await Assert.ThrowsAsync<GenerationJobValidationException>(() => jobs.RetryAsync(world.UserId, initialJob.Id, idempotencyKey: "another-retry"));
        Assert.Equal(2, await observer.GenerationJobs.CountAsync());

        // The initial submitter can also lose its acknowledgement after the worker claims
        // the visible job. Its compensation and its replay must keep the worker checkpoint.
        var secondKey = Key + "-lost-ack";
        commit.AfterCommit = async jobId =>
        {
            var token = Guid.NewGuid();
            await observer.GenerationJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(set => set
                .SetProperty(job => job.Status, GenerationJobStatus.Running).SetProperty(job => job.ConcurrencyToken, token));
            await new MovieFinalAssemblyExecutionStore(observer).MarkRunningAsync(jobId, token, CancellationToken.None);
            throw new IOException("Synthetic lost initial commit acknowledgement");
        };
        await Assert.ThrowsAsync<IOException>(() => service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), secondKey, CancellationToken.None));
        commit.AfterCommit = null;
        var replay = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), secondKey, CancellationToken.None);
        Assert.Equal(MovieAssemblyStatuses.Assembling, replay!.Status);
        Assert.Equal(1, replay.AttemptCount);
        Assert.Equal(5, replay.ProgressPercent);
        Assert.Equal(4, enqueue.Calls);
        Assert.Equal(3, await observer.GenerationJobs.CountAsync());
        Assert.Equal(3, await observer.UsageTransactions.CountAsync());
        Assert.False(await observer.CreditLedgerEntries.AnyAsync());
    }

    private sealed class RejectAssemblyLinkOnce : DbCommandInterceptor
    {
        public bool Observed { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Observed && command.Transaction is not null
                && command.CommandText.StartsWith("UPDATE \"MovieAssemblies\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"GenerationJobId\" =", StringComparison.Ordinal))
            {
                Observed = true;
                throw new IOException("Synthetic target link failure inside publication transaction");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static GenerationJobService RealAssemblyJobs(TaslimDbContext db, IGenerationJobQueue queue)
    {
        var ledger = new UsageLedgerService(db, new AiCostCalculator(new AiModelCatalog(new ConfigurationBuilder().Build())),
            new SafeUsageChargingService(), new UsageCostControl(db, Options.Create(new UsageControlOptions()),
                NullLogger<UsageCostControl>.Instance), NullLogger<UsageLedgerService>.Instance);
        var access = new WorkspaceAccessService(db);
        return new GenerationJobService(db, access, queue, new GenerationJobUsageService(ledger),
            new GenerationCostGuardrailService(db, Options.Create(new GenerationBudgetOptions())), new HttpContextAccessor(),
            Options.Create(new ImageGenerationOptions()), movieCollaboration: new MovieCollaborationAccess(db, access));
    }

    private sealed class ObservedAssemblyQueue(TaslimDbContext db, PublicationCommit commit) : IGenerationJobQueue
    {
        public int Calls { get; private set; }
        public Func<Guid, Task>? AfterEnqueue { get; set; }

        public async Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal(1, await db.MovieAssemblies.AsNoTracking().CountAsync(row => row.GenerationJobId == jobId, cancellationToken));
            Assert.True(await db.UsageTransactions.AsNoTracking().AnyAsync(row => row.GenerationJobId == jobId, cancellationToken));
            await new DatabaseGenerationJobQueue(db).EnqueueAsync(jobId, cancellationToken);
            commit.JobId = jobId;
            commit.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            if (AfterEnqueue is not null) await AfterEnqueue(jobId);
        }
    }

    private sealed class PublicationCommit : DbTransactionInterceptor
    {
        public DbTransaction? Transaction { get; set; }
        public Guid JobId { get; set; }
        public Func<Guid, Task>? AfterCommit { get; set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) =>
            ReferenceEquals(transaction, Transaction) && AfterCommit is not null ? AfterCommit(JobId) : Task.CompletedTask;
    }
}
