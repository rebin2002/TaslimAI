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
/// - Each test owns a fresh host: fixed-window counters are host state, so the
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
    /// <summary>
    /// Production-environment host on an intentionally unreachable database,
    /// exactly like the repository's api-health-smoke gate: readiness is
    /// legitimately 503 while the process is healthy and liveness is 200.
    /// Health:StorageRequired=false keeps every probe deterministic, bounded,
    /// and free of provider calls.
    /// </summary>
    public class ProbeApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public ProbeApiFactory() => connection.Open();

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
                ["Health:StorageRequired"] = "false",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
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

    private static ProbeApiFactory CreateFreshHost()
    {
        var factory = new ProbeApiFactory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        return factory;
    }

    [Fact]
    public async Task Two_inflight_readiness_leases_do_not_deny_the_liveness_concurrency_partition()
    {
        // The second half of the reviewer-measured failure: with one shared
        // 2-permit, no-queue concurrency partition, two slow readiness calls
        // from the same proxy peer turned a perfectly healthy liveness probe
        // into 429. Exercised through the real composed GlobalLimiter with
        // endpoint metadata exactly as the pipeline presents it, so leases are
        // held deterministically without relying on request timing.
        var options = new RateLimiterOptions();
        RateLimiting.Configure(options);
        var limiter = options.GlobalLimiter!;
        var peer = "192.0.2.44";

        using var firstReadiness = await limiter.AcquireAsync(AnonymousContext(RateLimiting.Health, peer));
        using var secondReadiness = await limiter.AcquireAsync(AnonymousContext(RateLimiting.Health, peer));
        using var thirdReadiness = await limiter.AcquireAsync(AnonymousContext(RateLimiting.Health, peer));
        using var liveness = await limiter.AcquireAsync(AnonymousContext(RateLimiting.HealthLiveness, peer));

        Assert.True(firstReadiness.IsAcquired);
        Assert.True(secondReadiness.IsAcquired);
        // Readiness keeps its own expensive 2-permit bound...
        Assert.False(thirdReadiness.IsAcquired);
        // ...and occupying it fully can no longer starve liveness.
        Assert.True(liveness.IsAcquired);
    }

    private static HttpContext AnonymousContext(string policyName, string ipAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ipAddress);
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
            displayName: policyName));
        return context;
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
        // Reproduction of the reviewer-measured failure: scripts/api-health-smoke.sh
        // alternates one liveness probe and one readiness probe every 2 seconds,
        // so a complete run sends 60 health requests per minute from one source.
        // Under the original single combined 30/minute bucket the 31st probe — a
        // perfectly alive liveness check — was rejected as 429, masking real
        // process health. Ordinary smoke cadence must never be rate limited.
        // After the split the sequence spends 30 of the liveness partition's
        // 60-token ceiling and 30 of the readiness partition's 60-token
        // ceiling — each lane alone, none shared, no rejection.
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
