using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Operations;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Synthetic, provider-independent coverage for the operational readiness report's
/// degradation semantics. Every case is deterministic: heartbeats are seeded at a
/// fixed age against the default staleness window, and no test depends on wall-clock
/// scheduling, a real provider, or a real database server.
/// </summary>
public sealed class OperationalReadinessWorkerDegradationTests
{
    private static readonly TimeSpan StaleHeartbeatAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultStaleWindow = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Readiness_reports_worker_degraded_when_every_worker_heartbeat_is_stale()
    {
        var storage = new AlwaysAvailableStorage();
        var result = await ProbeReadinessAsync(
            storage,
            Heartbeat("stale-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow - StaleHeartbeatAge));

        // The defect: the database and storage checks are both healthy, so the
        // report used to say "ready" with no signal that no worker will ever
        // claim the queued jobs.
        Assert.Equal("degraded", Check(result.Response, "worker").Status);
        Assert.True(DefaultStaleWindow < StaleHeartbeatAge);
    }

    [Fact]
    public async Task Worker_degradation_never_gates_the_ready_verdict()
    {
        var result = await ProbeReadinessAsync(
            new AlwaysAvailableStorage(),
            Heartbeat("stale-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow - StaleHeartbeatAge));

        // Reporting degradation must not evict a replica that still serves HTTP.
        Assert.False(Check(result.Response, "worker").Required);
        Assert.True(result.IsReady);
        Assert.Equal("ready", result.Response.Status);
        Assert.Equal("available", Check(result.Response, "database").Status);
    }

    [Fact]
    public async Task Readiness_reports_worker_available_when_a_fresh_running_heartbeat_exists()
    {
        var result = await ProbeReadinessAsync(
            new AlwaysAvailableStorage(),
            Heartbeat("dead-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow - StaleHeartbeatAge),
            Heartbeat("live-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow));

        // Fleet-wide telemetry: one live worker anywhere is positive evidence that
        // a stale row from a retired replica must not be reported as an outage.
        Assert.Equal("available", Check(result.Response, "worker").Status);
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task Readiness_reports_worker_degraded_when_an_expected_worker_has_never_heartbeated()
    {
        var result = await ProbeReadinessAsync(new AlwaysAvailableStorage());

        // A worker that dies before its first write leaves no row at all; that is
        // indistinguishable from a healthy empty system unless it is reported.
        Assert.Equal("degraded", Check(result.Response, "worker").Status);
    }

    [Fact]
    public async Task Readiness_does_not_report_a_non_running_worker_as_available()
    {
        var result = await ProbeReadinessAsync(
            new AlwaysAvailableStorage(),
            Heartbeat("draining-worker:0", GenerationWorkerStatuses.Stopping, DateTime.UtcNow));

        // Same rule the administrator operations report applies: only a running
        // heartbeat proves a worker will claim the next job.
        Assert.Equal("degraded", Check(result.Response, "worker").Status);
    }

    [Fact]
    public async Task Readiness_reports_worker_unknown_when_the_database_is_unreachable()
    {
        await using var db = UnreachableDatabase();
        var service = new OperationalHealthService(
            db,
            new AlwaysAvailableStorage(),
            Options.Create(new HealthOptions { StorageRequired = true, ProbeTimeoutSeconds = 5 }),
            NullLogger<OperationalHealthService>.Instance);

        var result = await service.ReadinessAsync("worker-readiness-db-down", CancellationToken.None);

        // Database degradation stays the authoritative, required signal, and the
        // worker probe refuses to invent a second alarm it has no evidence for.
        Assert.Equal("unavailable", Check(result.Response, "database").Status);
        Assert.True(Check(result.Response, "database").Required);
        Assert.Equal("unknown", Check(result.Response, "worker").Status);
        Assert.False(result.IsReady);
        Assert.Equal("not_ready", result.Response.Status);
    }

    [Fact]
    public async Task Readiness_does_not_probe_the_worker_after_the_probe_budget_is_exhausted()
    {
        var storage = new BlockingStorageService();
        var database = await CreateDatabaseAsync(
            Heartbeat("live-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow));
        try
        {
            var service = new OperationalHealthService(
                database.Db,
                storage,
                Options.Create(new HealthOptions { StorageRequired = true, ProbeTimeoutSeconds = 1 }),
                NullLogger<OperationalHealthService>.Instance);

            var result = await service.ReadinessAsync("worker-readiness-budget", CancellationToken.None);

            Assert.Equal("unavailable", Check(result.Response, "storage").Status);
            // The storage task is deliberately abandoned by the probe once the
            // budget fires, so its cancellation is observed on a continuation
            // that can still be in flight at this point. Wait for the event
            // instead of sampling it: the ceiling below only trips if the
            // cancellation never propagates at all, which is exactly the defect
            // this case exists to catch. Sampling a bool here raced with the
            // scheduler and failed on a loaded hosted runner.
            var cancellationSignal = await Task.WhenAny(
                storage.CancellationObserved, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(
                ReferenceEquals(cancellationSignal, storage.CancellationObserved),
                "the abandoned storage probe never observed the shared probe-budget cancellation");
            // The shared budget is gone, so the worker verdict is unknown rather
            // than a fabricated "available" or "degraded".
            Assert.Equal("unknown", Check(result.Response, "worker").Status);
            Assert.False(result.IsReady);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task Readiness_appends_the_worker_check_after_database_and_storage()
    {
        var result = await ProbeReadinessAsync(
            new AlwaysAvailableStorage(),
            Heartbeat("live-worker:0", GenerationWorkerStatuses.Running, DateTime.UtcNow));

        // The smoke contract and journey tests address checks[0] and checks[1]
        // positionally, so the new advisory check must be appended, never inserted.
        Assert.Equal(
            new[] { "database", "storage", "worker" },
            result.Response.Checks.Select(check => check.Name).ToArray());
    }

    [Fact]
    public async Task Liveness_stays_a_dependency_free_process_check()
    {
        await using var db = UnreachableDatabase();
        var service = new OperationalHealthService(
            db,
            new BlockingStorageService(),
            Options.Create(new HealthOptions { StorageRequired = true, ProbeTimeoutSeconds = 1 }),
            NullLogger<OperationalHealthService>.Instance);

        var response = service.Live("worker-readiness-liveness");

        // Degradation reporting must not leak into liveness: a single in-process
        // check, no database or worker probe, and therefore no dependency that
        // could stall or be throttled beside the orchestrator's kill switch.
        var check = Assert.Single(response.Checks);
        Assert.Equal("process", check.Name);
        Assert.Equal("alive", check.Status);
        Assert.Equal("alive", response.Status);
    }

    private static async Task<ReadinessProbeResult> ProbeReadinessAsync(
        IFileStorageService storage,
        params GenerationWorkerHeartbeat[] heartbeats)
    {
        var database = await CreateDatabaseAsync(heartbeats);
        try
        {
            var service = new OperationalHealthService(
                database.Db,
                storage,
                Options.Create(new HealthOptions { StorageRequired = true, ProbeTimeoutSeconds = 5 }),
                NullLogger<OperationalHealthService>.Instance);
            var result = await service.ReadinessAsync("worker-readiness-test", CancellationToken.None);
            return new ReadinessProbeResult(result.Response, result.IsReady);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

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

    /// <summary>
    /// A context whose store can never be opened, so both dependency probes fail
    /// for the same deterministic reason without any timing dependence.
    /// </summary>
    private static TaslimDbContext UnreachableDatabase()
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite("Data Source=/proc/taslim-readiness-test/unreachable.db")
            .Options;
        return new TaslimDbContext(options);
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

    private static OperationalHealthCheckDto Check(OperationalHealthResponse response, string name)
    {
        var check = response.Checks.FirstOrDefault(item => item.Name == name);
        Assert.True(check is not null, $"the readiness document is missing the '{name}' check");
        return check!;
    }

    private sealed record ReadinessProbeResult(OperationalHealthResponse Response, bool IsReady);

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

    private sealed class AlwaysAvailableStorage : IFileStorageService
    {
        public string ProviderKey => "test-storage";
        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class BlockingStorageService : IFileStorageService
    {
        // The readiness probe calls ExistsAsync(...).WaitAsync(budget). When the
        // shared budget expires, WaitAsync throws and the probe returns
        // "unavailable" WITHOUT ever awaiting the storage task it started, so the
        // cancellation is observed later, on a thread-pool continuation, possibly
        // after ReadinessAsync has already returned. Sampling a bool at that moment
        // races with the scheduler and loses on a loaded runner, so the observation
        // is published as an awaitable signal and the test waits for the event
        // rather than polling for it.
        private readonly TaskCompletionSource<bool> cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderKey => "test-storage";
        public Task CancellationObserved => cancellationObserved.Task;

        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
                throw;
            }
        }
    }
}

/// <summary>
/// Endpoint-level contract: the advisory worker check is observable through the
/// real readiness document, the gate keeps its documented database-and-storage
/// semantics, and liveness exposes no dependency at all.
/// </summary>
public sealed class OperationalReadinessWorkerDegradationEndpointTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public OperationalReadinessWorkerDegradationEndpointTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Readiness_endpoint_reports_worker_advisory_without_changing_the_gate()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var checks = body.RootElement.GetProperty("checks");
        var worker = Find(checks, "worker");
        Assert.NotNull(worker);

        // The Testing host runs no generation worker, so the honest advisory
        // verdict is "disabled" rather than a false degradation.
        Assert.Equal("disabled", worker!.Value.GetProperty("status").GetString());
        Assert.False(worker.Value.GetProperty("required").GetBoolean());

        // The status code stays a pure function of the required checks.
        var database = Find(checks, "database")!.Value;
        var storage = Find(checks, "storage")!.Value;
        var gateHolds = database.GetProperty("status").GetString() == "available"
            && (!storage.GetProperty("required").GetBoolean() || storage.GetProperty("status").GetString() == "available");
        Assert.Equal(gateHolds ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(gateHolds ? "ready" : "not_ready", body.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task Liveness_endpoint_exposes_no_dependency_check(string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var checks = body.RootElement.GetProperty("checks");
        Assert.Equal(1, checks.GetArrayLength());
        Assert.All(checks.EnumerateArray(), check =>
        {
            Assert.Equal("process", check.GetProperty("name").GetString());
            Assert.False(WorkerOrDatabase(check));
        });
    }

    private static bool WorkerOrDatabase(JsonElement check) =>
        check.GetProperty("name").GetString() is "worker" or "database" or "storage";

    private static JsonElement? Find(JsonElement checks, string name) =>
        checks.EnumerateArray().FirstOrDefault(
            check => string.Equals(check.GetProperty("name").GetString(), name, StringComparison.Ordinal),
            default);
}
