using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// PR #249 availability acceptance (independent-review follow-up): provider-free,
/// HTTP-level regressions proving that the documented 2-second liveness/readiness
/// smoke cadence behind one proxy peer (a single anonymous RemoteIpAddress
/// partition) is never masked as 429 for the cheap liveness probe, while the
/// expensive readiness probe and anonymous abuse bounds stay enforced.
/// Arbitrary X-Forwarded-For values are never trusted, so same-peer probes
/// intentionally collide in the IP partition — the budgets must survive that.
///
/// Determinism rules for this lane:
/// - Each test owns a fresh host: token buckets are host state, so the
///   suite must not leak permits between tests.
/// - Exhaustion proofs hammer at least five windows' worth of traffic and stop
///   at the first 429. A fresh partition always carries its full burst ceiling,
///   so the early break happens within a few seconds and cannot depend on
///   wall-clock replenishment alignment.
/// - Never-rate-limited proofs stay strictly inside the burst ceiling of the
///   partition they exercise, so replenishment timing can only add headroom,
///   never a 429.
/// - No artificial delays: the smoke cadence is expressed as the documented
///   per-minute request counts (30 liveness + 30 readiness), not sleeps.
/// </summary>
public sealed class HealthProbeRateLimitTests
{
    private sealed class ProbeStorage(Func<CancellationToken, Task<bool>> exists) : IFileStorageService
    {
        public string ProviderKey => "in-memory-probe";
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => exists(cancellationToken);
        public Task StoreAsync(string key, Stream stream, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ProbeApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        private readonly ProbeStorage storage;

        public ProbeApiFactory(Func<CancellationToken, Task<bool>>? exists = null)
        {
            connection.Open();
            storage = new ProbeStorage(exists ?? (_ => Task.FromResult(true)));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ApplyMigrations"] = "false",
                ["ConnectionStrings:Postgres"] = "Host=test;Port=5432;Database=test;Username=test;Password=test",
                ["AllowedOrigins:0"] = "https://web.example.test",
                ["Files:S3Endpoint"] = "https://storage.example.test",
                ["Files:S3Region"] = "auto",
                ["Files:S3Bucket"] = "test-bucket",
                ["Files:S3AccessKey"] = "test-access-key",
                ["Files:S3SecretKey"] = "test-secret-key",
                ["Health:StorageRequired"] = "true",
                ["Health:ProbeTimeoutSeconds"] = "30",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.RemoveAll<IFileStorageService>();
                services.AddSingleton<IFileStorageService>(storage);
                services.AddSingleton(connection);
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) connection.Dispose();
        }
    }

    private static ProbeApiFactory CreateFreshHost(Func<CancellationToken, Task<bool>>? exists = null)
    {
        var factory = new ProbeApiFactory(exists);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        return factory;
    }

    [Fact]
    public async Task Four_same_peer_readiness_requests_can_overlap_without_blocking_liveness()
    {
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        Task<bool> HoldStorageProbe(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref entered) == 4) allEntered.TrySetResult();
            return release.Task.WaitAsync(cancellationToken);
        }

        using var factory = CreateFreshHost(HoldStorageProbe);
        using var handler = factory.Server.CreateHandler(context =>
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.44"));
        using var client = new HttpClient(handler) { BaseAddress = factory.Server.BaseAddress };
        var pending = new[] { "/health/ready", "/readiness", "/health/ready", "/readiness" }
            .Select(path => client.GetAsync(path)).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var excess = await client.GetAsync("/readiness");
            Assert.Equal(HttpStatusCode.TooManyRequests, excess.StatusCode);
            Assert.Equal(4, Volatile.Read(ref entered));

            using var live = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            var health = await live.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("alive", health.GetProperty("status").GetString());
        }
        finally
        {
            release.TrySetResult(true);
            foreach (var task in pending)
            {
                using var response = await task;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        using var next = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    /// <summary>
    /// Sends up to <paramref name="maxProbes"/> same-peer requests and returns
    /// the first rejected (429) response, or null when the bound never fired.
    /// Callers choose maxProbes well above five windows of the partition budget
    /// so a window rollover mid-loop cannot mask a missing bound.
    /// </summary>
    private static async Task<HttpResponseMessage?> FirstRejectionAsync(HttpClient client, string path, int maxProbes)
    {
        for (var probe = 0; probe < maxProbes; probe++)
        {
            var response = await client.GetAsync(path);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            response.Dispose();
        }

        return null;
    }

    [Fact]
    public async Task Smoke_cadence_alternating_liveness_and_readiness_never_returns_429_for_healthy_process()
    {
        // Two independent monitors can interleave the same two-second retry cadence
        // used in the release smoke. Both paths must remain within budget.
        using var factory = CreateFreshHost();
        using var client = factory.CreateClient();
        var livenessStatuses = new List<HttpStatusCode>();
        var readinessStatuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 30; attempt++)
        {
            livenessStatuses.Add((await client.GetAsync("/health/live")).StatusCode);
            readinessStatuses.Add((await client.GetAsync("/health/ready")).StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, livenessStatuses);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, readinessStatuses);
        // A healthy process answers liveness with 200. Readiness reports its
        // honest dependency state (this harness swaps Postgres for in-memory
        // SQLite, so the ready document here is 200; with the unreachable
        // database of the reviewer reproduction it was 503). The property
        // under test is that neither probe is ever disguised as a 429 a
        // prober cannot tell apart from overload.
        Assert.All(livenessStatuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.All(readinessStatuses, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    [Fact]
    public async Task Required_storage_failure_returns_unavailable_while_liveness_stays_healthy()
    {
        using var factory = CreateFreshHost(_ => Task.FromException<bool>(new FileStorageUnavailableException()));
        using var client = factory.CreateClient();
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        var body = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_ready", body.GetProperty("status").GetString());

        using var liveness = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [Fact]
    public async Task Liveness_stays_trustworthy_after_readiness_budget_is_exhausted()
    {
        using var factory = CreateFreshHost();
        using var client = factory.CreateClient();

        // 604 same-peer probes exceed any five 30/minute windows of the
        // readiness partition, so the expensive path must reject.
        using var rejected = await FirstRejectionAsync(client, "/health/ready", 604);
        Assert.NotNull(rejected);

        // The misclassification this lane removes: readiness exhaustion must not
        // make a healthy liveness probe untrustworthy. Liveness has its own
        // budget that this loop never touched.
        using var liveness = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        var document = await liveness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alive", document.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_stays_trustworthy_after_liveness_probe_load()
    {
        using var factory = CreateFreshHost();
        using var client = factory.CreateClient();

        // A busy container plus orchestrator polling both cheap liveness aliases
        // from the same proxy peer — 50 requests, well above the documented
        // smoke volume but inside the shared liveness burst ceiling — must not
        // starve readiness of its own untouched budget.
        for (var probe = 0; probe < 25; probe++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }

        using var readiness = await client.GetAsync("/health/ready");
        // Readiness keeps its own untouched budget: the probe is answered
        // honestly (never 429) even after 50 same-peer liveness requests.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, readiness.StatusCode);
    }

    [Fact]
    public async Task Anonymous_liveness_abuse_remains_bounded_by_a_dedicated_finite_budget()
    {
        using var factory = CreateFreshHost();
        using var client = factory.CreateClient();

        // Exempting liveness entirely would be a security regression; the
        // dedicated budget is finite. A tight same-peer flood outruns the
        // 60-token ceiling and the once-per-minute replenishment, so anonymous
        // flooding is rejected...
        using var rejected = await FirstRejectionAsync(client, "/health/live", 1204);
        Assert.NotNull(rejected);

        // ...while the privacy-preserving rejection envelope is unchanged, and
        // ordinary monitors inside a fresh budget are proven by the smoke test.
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected!.StatusCode);
        Assert.Equal("60", rejected.Headers.RetryAfter?.ToString());
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("RATE_LIMITED", body, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_peer_readiness_abuse_gets_safe_rate_limited_envelope()
    {
        using var factory = CreateFreshHost();
        using var client = factory.CreateClient();

        using var rejected = await FirstRejectionAsync(client, "/health/ready", 604);
        Assert.NotNull(rejected);

        // Readiness keeps its own finite bound with the generic,
        // non-identifying envelope — protecting the expensive probe without
        // leaking partition or client identity.
        Assert.Equal("60", rejected!.Headers.RetryAfter?.ToString());
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("RATE_LIMITED", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/health", RateLimiting.HealthLiveness)]
    [InlineData("/health/live", RateLimiting.HealthLiveness)]
    [InlineData("/health/ready", RateLimiting.Health)]
    [InlineData("/readiness", RateLimiting.Health)]
    public void Health_routes_declare_their_probe_budget_lane(string path, string expectedPolicy)
    {
        // Endpoint metadata contract: liveness aliases carry the dedicated cheap
        // budget; readiness aliases keep the expensive protected budget. The
        // partition identity remains the connection RemoteIpAddress for anonymous
        // callers — forwarded client IP headers are not trusted.
        using var factory = CreateFreshHost();
        var sources = factory.Services.GetServices<EndpointDataSource>();
        var endpoint = sources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SingleOrDefault(candidate => string.Equals(
                candidate.RoutePattern.RawText, path, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(endpoint);
        var policy = endpoint!.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        Assert.NotNull(policy);
        Assert.Equal(expectedPolicy, policy!.PolicyName);
    }
}
