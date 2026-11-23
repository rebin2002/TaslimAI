using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Operations;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Cost and availability bounding for the advisory worker-liveness read that the
/// readiness report added. The readiness route is anonymous and carries no
/// rate-limit policy, so before this bound every probe — including a retry storm
/// against a 503 — cost its own database round-trip for advice that cannot
/// change faster than the heartbeat window. Every case here is deterministic:
/// the bound is exercised through an injected clock, never a sleep, and the
/// verdict changes are driven by rows, not by wall-clock timing.
/// </summary>
public sealed class OperationalReadinessWorkerProbeBoundingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    // ---------------------------------------------------------------- cache

    [Fact]
    public async Task Probe_bound_collapses_repeated_probes_into_one_query()
    {
        var clock = new TestClock();
        var cache = new WorkerLivenessProbeCache(Bound, clock.Now);
        var queries = 0;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal("available", await ProbeAsync(cache, _ =>
            {
                queries++;
                return Task.FromResult("available");
            }));
        }

        Assert.Equal(1, queries);
        Assert.Equal(1, cache.ProbeExecutions);
    }

    [Fact]
    public async Task Probe_bound_expires_and_reports_the_recovered_verdict()
    {
        var clock = new TestClock();
        var cache = new WorkerLivenessProbeCache(Bound, clock.Now);
        var verdict = "available";
        var queries = 0;

        Task<string> Probe() => cache.ObserveAsync(
            _ =>
            {
                queries++;
                return Task.FromResult(verdict);
            },
            CancellationToken.None,
            CancellationToken.None);

        Assert.Equal("available", await Probe());

        // Still bound: the pool recovery/hang has not been looked at again.
        verdict = "degraded";
        Assert.Equal("available", await Probe());
        Assert.Equal(1, queries);

        // Past the window the advice is re-checked, so a worker that came back is
        // visible in seconds rather than after a whole heartbeat window.
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("degraded", await Probe());
        Assert.Equal(2, queries);

        // ...and the fresh verdict is bound again rather than re-read per probe.
        Assert.Equal("degraded", await Probe());
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task An_absence_of_evidence_is_never_served_twice()
    {
        var clock = new TestClock();
        var cache = new WorkerLivenessProbeCache(Bound, clock.Now);
        var queries = 0;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal("unknown", await cache.ObserveAsync(
                _ =>
                {
                    queries++;
                    return Task.FromResult("unknown");
                },
                CancellationToken.None,
                CancellationToken.None));
        }

        // "unknown" means the probe could not run. Repeating it is exactly what
        // an operator needs to see, and a cached unknown would hide a recovery.
        Assert.Equal(3, queries);
        Assert.False(WorkerLivenessProbeCache.IsWorkerPoolEvidence("unknown"));
        Assert.True(WorkerLivenessProbeCache.IsWorkerPoolEvidence("available"));
        Assert.True(WorkerLivenessProbeCache.IsWorkerPoolEvidence("degraded"));
    }

    [Fact]
    public async Task A_zero_bound_restores_one_query_per_probe()
    {
        var cache = new WorkerLivenessProbeCache(TimeSpan.Zero);
        var queries = 0;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal("available", await cache.ObserveAsync(
                _ =>
                {
                    queries++;
                    return Task.FromResult("available");
                },
                CancellationToken.None,
                CancellationToken.None));
        }

        Assert.Equal(2, queries);
        Assert.Equal(2, cache.ProbeExecutions);
    }

    [Fact]
    public async Task Concurrent_probes_share_the_query_in_flight()
    {
        var cache = new WorkerLivenessProbeCache(Bound);
        var queries = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> ProbeDelegate(CancellationToken token)
        {
            Interlocked.Increment(ref queries);
            started.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            return "available";
        }

        var inFlight = cache.ObserveAsync(ProbeDelegate, CancellationToken.None, CancellationToken.None);
        // Everything below queues behind the one probe that is already running.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var waiters = Enumerable.Range(0, 8)
            .Select(_ => cache.ObserveAsync(ProbeDelegate, CancellationToken.None, CancellationToken.None))
            .ToArray();

        release.TrySetResult();
        var verdicts = await Task.WhenAll([inFlight, .. waiters]);

        Assert.All(verdicts, verdict => Assert.Equal("available", verdict));
        Assert.Equal(1, queries);
        Assert.Equal(1, cache.ProbeExecutions);
    }

    [Fact]
    public async Task A_disconnected_caller_does_not_cancel_the_shared_query()
    {
        var cache = new WorkerLivenessProbeCache(Bound);
        var queries = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abandoning = new CancellationTokenSource();

        async Task<string> ProbeDelegate(CancellationToken token)
        {
            Interlocked.Increment(ref queries);
            started.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            return "degraded";
        }

// The first probe is the one that pays for the query.
        var holder = cache.ObserveAsync(ProbeDelegate, CancellationToken.None, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // A second, unrelated client hangs up while it is queued behind the
        // shared query.
        abandoning.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.ObserveAsync(ProbeDelegate, CancellationToken.None, abandoning.Token));

        // The abandoned caller must not have cancelled the query feeding the rest.
        release.TrySetResult();
        Assert.Equal("degraded", await holder);
        Assert.Equal(1, queries);
    }

    // -------------------------------------------------------------- service

    [Fact]
    public async Task Readiness_shares_one_heartbeat_read_across_probes()
    {
        var clock = new TestClock();
        var cache = new WorkerLivenessProbeCache(Bound, clock.Now);
        var database = await CreateDatabaseAsync(
            Heartbeat("live-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow));
        try
        {
            Assert.Equal("available", WorkerVerdict(await ProbeAsync(database.Db, cache)));
            Assert.Equal("available", WorkerVerdict(await ProbeAsync(database.Db, cache)));
            Assert.Equal(1, cache.ProbeExecutions);

            // The worker pool is gone, but the bound has not expired: the advice
            // is the last real observation, and readiness still says "ready".
            await database.Db.GenerationWorkerHeartbeats.ExecuteDeleteAsync();
            var stale = await ProbeAsync(database.Db, cache);
            Assert.Equal("available", WorkerVerdict(stale));
            Assert.Equal(1, cache.ProbeExecutions);
            Assert.True(stale.IsReady);

            // Past the bound the next probe sees the degradation.
            clock.Advance(TimeSpan.FromSeconds(3));
            var recovered = await ProbeAsync(database.Db, cache);
            Assert.Equal("degraded", WorkerVerdict(recovered));
            Assert.Equal(2, cache.ProbeExecutions);
            Assert.True(recovered.IsReady);
            Assert.False(WorkerCheck(recovered.Response).Required);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_service_built_without_the_cache_still_reads_every_probe()
    {
        // The focused-test path: no replica-wide cache is supplied, so nothing in
        // the pre-existing freshness contract changes for hand-built services.
        var database = await CreateDatabaseAsync(
            Heartbeat("live-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow));
        try
        {
            var first = await ProbeAsync(database.Db, workerProbeCache: null);
            Assert.Equal("available", WorkerVerdict(first));

            await database.Db.GenerationWorkerHeartbeats.ExecuteDeleteAsync();
            var second = await ProbeAsync(database.Db, workerProbeCache: null);
            Assert.Equal("degraded", WorkerVerdict(second));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_deployment_without_a_worker_never_reaches_the_probe_cache()
    {
        var cache = new WorkerLivenessProbeCache(Bound);
        var database = await CreateDatabaseAsync();
        try
        {
            var result = await ProbeAsync(database.Db, cache, workerEnabled: false);

            Assert.Equal("disabled", WorkerVerdict(result));
            // Web-only replicas and Testing hosts pay no database cost at all, so
            // the bound is not what keeps them free of the heartbeat query.
            Assert.Equal(0, cache.ProbeExecutions);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    public void The_configured_bound_defaults_to_two_seconds_and_is_clamped()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), new HealthOptions().WorkerProbeCache);

        // 0 is an explicit opt-out, and a negative value must not wrap.
        Assert.Equal(TimeSpan.Zero, new HealthOptions { WorkerProbeCacheSeconds = 0 }.WorkerProbeCache);
        Assert.Equal(TimeSpan.Zero, new HealthOptions { WorkerProbeCacheSeconds = -5 }.WorkerProbeCache);

        // A misconfigured monitor window cannot turn an advisory read into an
        // hour of stale advice.
        Assert.Equal(TimeSpan.FromSeconds(60), new HealthOptions { WorkerProbeCacheSeconds = 3600 }.WorkerProbeCache);
    }

    // --------------------------------------------------------------- helpers

    private static async Task<ReadinessOutcome> ProbeAsync(
        TaslimDbContext db,
        WorkerLivenessProbeCache? workerProbeCache,
        bool workerEnabled = true)
    {
        var service = new OperationalHealthService(
            db,
            new AlwaysAvailableStorage(),
            Options.Create(new HealthOptions { StorageRequired = true, ProbeTimeoutSeconds = 5 }),
            NullLogger<OperationalHealthService>.Instance,
            applicationLifetime: null,
            generationOptions: Options.Create(new GenerationJobOptions { WorkerEnabled = workerEnabled }),
            workerProbeCache: workerProbeCache);

        var result = await service.ReadinessAsync("worker-probe-bounding", CancellationToken.None);
        return new ReadinessOutcome(result.Response, result.IsReady);
    }

    private static Task<string> ProbeAsync(WorkerLivenessProbeCache cache, Func<CancellationToken, Task<string>> probe) =>
        cache.ObserveAsync(probe, CancellationToken.None, CancellationToken.None);

    private static string WorkerVerdict(ReadinessOutcome outcome) => WorkerCheck(outcome.Response).Status;

    private static OperationalHealthCheckDto WorkerCheck(OperationalHealthResponse response)
    {
        var check = response.Checks.FirstOrDefault(item => item.Name == "worker");
        Assert.True(check is not null, $"the readiness document is missing the 'worker' check: {string.Join(",", response.Checks.Select(item => item.Name))}");
        return check!;
    }

    private static GenerationWorkerHeartbeat Heartbeat(string workerId, string status, DateTime lastSeenAt) => new()
    {
        WorkerId = workerId,
        InstanceId = workerId.Split(':')[0],
        WorkerIndex = 0,
        WorkerConcurrency = 1,
        Status = status,
        StartedAt = lastSeenAt - TimeSpan.FromMinutes(5),
        LastSeenAt = lastSeenAt,
    };

    private static async Task<OwnedDatabase> CreateDatabaseAsync(params GenerationWorkerHeartbeat[] heartbeats)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        foreach (var heartbeat in heartbeats)
        {
            heartbeat.Id = Guid.NewGuid();
            db.GenerationWorkerHeartbeats.Add(heartbeat);
        }
        await db.SaveChangesAsync();
        return new OwnedDatabase(connection, db);
    }

    private sealed record ReadinessOutcome(OperationalHealthResponse Response, bool IsReady);

    private sealed class OwnedDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly TaslimDbContext db;

        public OwnedDatabase(SqliteConnection connection, TaslimDbContext db)
        {
            this.connection = connection;
            this.db = db;
        }

        public TaslimDbContext Db => db;

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>A clock the test advances deliberately, so bound expiry never depends on scheduling.</summary>
    private sealed class TestClock
    {
        private DateTimeOffset current = new(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now() => current;

        public void Advance(TimeSpan by) => current += by;
    }

    private sealed class AlwaysAvailableStorage : IFileStorageService
    {
        public string ProviderKey => "test-storage";
        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
