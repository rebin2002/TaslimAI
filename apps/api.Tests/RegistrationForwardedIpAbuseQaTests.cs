using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Taslim.Api.Persistence;
using Taslim.Api.Contracts;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA lane for the dedicated registration rate-limit policy.
///
/// BOUNDED QUESTION: a spoofed, untrusted X-Forwarded-For header must not let one
/// anonymous client evade the registration-specific limit, and genuine register
/// attempts over the limit must return the standard 429 without creating users,
/// workspaces, or billing-ledger rows. Login policy is deliberately not changed and
/// is only touched by one guard test that proves it still works.
///
/// Why these tests are needed: the policy's partition key is Connection.RemoteIpAddress,
/// i.e. exactly the value that forwarded-header middleware rewrites, and that middleware
/// is the FIRST thing in the production pipeline (apps/api/Program.cs), long before
/// app.UseRateLimiter. The existing abuse tests inject a synthetic peer through a test
/// header on a probe host that never loads forwarded-header processing, so they prove the
/// per-peer budget but not that the peer is unspoofable. The protection rests entirely on
/// Program.cs pinning ForwardedHeaders to XForwardedProto only. Nothing pinned that, so a
/// later "log the real client IP" change would silently turn the 10-permit registration
/// window into an unbounded budget while every existing test still passed.
///
/// No production code is changed here: these tests exercise the real
/// RateLimiting.Configure policy and the real application pipeline. Local, synthetic,
/// in-memory only.
/// </summary>
public sealed class RegistrationForwardedIpAbuseQaTests
{
    private const string RegistrationPolicy = "registration";

    // Remote (non-loopback) peers are capped here; keep in step with the policy.
    private const int RemotePermitLimit = 10;

    // ---- Synthetic pipeline: production forwarded-header semantics + fixed TCP peer ----

    private static async Task<IWebHost> CreateForwardingProbeHostAsync(
        ForwardedHeaders headers,
        IPAddress fixedPeer,
        ConcurrentBag<string> executedEndpoints)
    {
        var host = new WebHostBuilder()
            .UseEnvironment(Environments.Production)
            .UseTestServer()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddRateLimiter(Infrastructure.RateLimiting.Configure);
                services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = headers;
                    options.ForwardLimit = 1;
                    // Mirror the production branch of Program.cs: Railway ingress source
                    // IPs are not a stable app setting, so every upstream hop is trusted.
                    // This is the worst case for header spoofing.
                    options.KnownNetworks.Clear();
                    options.KnownProxies.Clear();
                });
            })
            .Configure(app =>
            {
                // Model the transport: a network listener has already fixed the socket
                // peer before any middleware runs. It must therefore be established
                // BEFORE UseForwardedHeaders, so that forwarded-header processing gets
                // the same chance to overwrite it that it has in production.
                app.Use(next => context =>
                {
                    context.Connection.RemoteIpAddress = fixedPeer;
                    return next(context);
                });

                app.UseForwardedHeaders();
                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(endpoints =>
                    endpoints.MapPost("/probe/register", () =>
                    {
                        executedEndpoints.Add("executed");
                        return Results.Ok(new { probe = true });
                    }).RequireRateLimiting(RegistrationPolicy));
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static HttpRequestMessage SpoofedRegisterProbe(int attempt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/probe/register");
        // A fresh claimed client IP for every request: the classic way to multiply an
        // IP-keyed quota from one socket.
        request.Headers.Add("X-Forwarded-For", $"203.0.113.{attempt}, 198.51.100.{attempt}");
        request.Headers.Add("X-Real-IP", $"198.51.100.{attempt}");
        return request;
    }

    private static async Task<(int permits, HttpStatusCode lastStatus, string? rejectionBody)> BurstProbeAsync(
        HttpClient client,
        int attempts)
    {
        var permits = 0;
        HttpStatusCode lastStatus = HttpStatusCode.OK;
        string? rejectionBody = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var response = await client.SendAsync(SpoofedRegisterProbe(attempt));
            lastStatus = response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                permits++;
                continue;
            }

            rejectionBody = await response.Content.ReadAsStringAsync();
            break;
        }

        return (permits, lastStatus, rejectionBody);
    }

    [Fact]
    public async Task Spoofed_untrusted_x_forwarded_for_does_not_expand_the_registration_budget()
    {
        var executed = new ConcurrentBag<string>();
        using var host = await CreateForwardingProbeHostAsync(
            ForwardedHeaders.XForwardedProto, IPAddress.Parse("203.0.113.77"), executed);
        using var client = host.GetTestClient();

        var (permits, lastStatus, rejectionBody) = await BurstProbeAsync(client, attempts: 25);

        // Every attempt claimed a different client IP, yet the one real peer is
        // charged for all of them.
        Assert.True(permits >= 1, "Ordinary registration traffic must still be served.");
        Assert.True(
            permits <= RemotePermitLimit,
            $"Spoofed X-Forwarded-For let the registration bucket grow to {permits} permits (cap {RemotePermitLimit}).");
        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
        Assert.Contains("RATE_LIMITED", rejectionBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spoofing_budget_is_the_peers_so_the_same_socket_is_still_blocked_without_the_header()
    {
        var executed = new ConcurrentBag<string>();
        using var host = await CreateForwardingProbeHostAsync(
            ForwardedHeaders.XForwardedProto, IPAddress.Parse("203.0.113.78"), executed);
        using var client = host.GetTestClient();

        // Consume the window entirely with spoofed provenance...
        var (firstPermits, _, _) = await BurstProbeAsync(client, attempts: 25);

        // ...then present nothing: the budget belongs to the peer, not to the claim,
        // so dropping the header cannot buy a fresh allowance.
        using var clean = await client.PostAsync("/probe/register", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, clean.StatusCode);

        Assert.Equal(RemotePermitLimit, firstPermits);
        Assert.Equal(RemotePermitLimit, executed.Count);
    }

    [Fact]
    public async Task An_unrelated_peer_does_not_inherit_the_spoof_flooded_bucket()
    {
        // The flip side: fail-closed must not become shared-punishment, or attackers
        // could deny service to other signups just by burning a common source address.
        var flooded = new ConcurrentBag<string>();
        using var floodedHost = await CreateForwardingProbeHostAsync(
            ForwardedHeaders.XForwardedProto, IPAddress.Parse("203.0.113.79"), flooded);
        using var floodedClient = floodedHost.GetTestClient();
        await BurstProbeAsync(floodedClient, attempts: 25);

        var neighbour = new ConcurrentBag<string>();
        using var neighbourHost = await CreateForwardingProbeHostAsync(
            ForwardedHeaders.XForwardedProto, IPAddress.Parse("198.51.100.4"), neighbour);
        using var neighbourClient = neighbourHost.GetTestClient();
        using var other = await neighbourClient.PostAsync("/probe/register", null);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Over_limit_registrations_are_rejected_before_the_endpoint_runs()
    {
        var executed = new ConcurrentBag<string>();
        using var host = await CreateForwardingProbeHostAsync(
            ForwardedHeaders.XForwardedProto, IPAddress.Parse("203.0.113.80"), executed);
        using var client = host.GetTestClient();

        var (permits, lastStatus, _) = await BurstProbeAsync(client, attempts: 25);

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);

        // The limiter short-circuits inside middleware, so the handler never executes
        // for rejected attempts: no provisioning, no workspace, no ledger write.
        Assert.Equal(permits, executed.Count);
        Assert.True(permits < 25, "Expected at least one rejected attempt.");
    }

    [Fact]
    public async Task Guard_is_meaningful_trusting_forwarded_for_does_let_one_client_evade_the_cap()
    {
        var executed = new ConcurrentBag<string>();
        // Mutation control: the ONLY difference is that the pipeline consumes
        // X-Forwarded-For, which a future "log the real client IP" change would do.
        using var host = await CreateForwardingProbeHostAsync(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            IPAddress.Parse("203.0.113.81"),
            executed);
        using var client = host.GetTestClient();

        var (permits, lastStatus, _) = await BurstProbeAsync(client, attempts: 25);

        // If this ever stops failing, the spoofing tests above are vacuous: they would
        // be passing for some reason unrelated to the proto-only configuration.
        Assert.True(
            permits > RemotePermitLimit,
            $"Expected spoofed X-Forwarded-For to evade the cap once forwarded-for is trusted, saw {permits} permits.");
        Assert.Equal(HttpStatusCode.OK, lastStatus);
    }

    // ---- Real application pipeline: production middleware + real Register ----

    [Fact]
    public async Task Real_production_register_rejects_over_limit_with_429_and_provisions_nothing()
    {
        using var factory = new ProductionApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.Database.EnsureCreated();

        const int attempts = 8;
        var emails = Enumerable.Range(1, attempts)
            .Select(index => $"qa-xff-{index}-{Guid.NewGuid():N}@example.com")
            .ToList();

        var statuses = new ConcurrentBag<HttpStatusCode>();
        var successes = new ConcurrentBag<string>();

        // One anonymous caller, one transport, eight distinct spoofed client IPs,
        // issued together against the registration concurrency bound (2, no queueing).
        // If a forwarded claim fed the partition key, each request would land in its
        // own bucket and none of them would be rejected.
        await Task.WhenAll(emails.Select((email, index) =>
            SendRealRegisterAsync(factory, email, index, statuses, successes)));

        var rejected = statuses.Count(status => status == HttpStatusCode.TooManyRequests);
        Assert.True(rejected >= 1,
            $"Expected the shared registration limiter to reject concurrent signups; saw {string.Join(",", statuses)}");

        var createdUsers = await db.Users.CountAsync();
        var createdWorkspaces = await db.Workspaces.CountAsync();
        var createdMembers = await db.WorkspaceMembers.CountAsync();
        var createdSubscriptions = await db.Subscriptions.CountAsync();

        // Exactly the permitted requests provisioned: the throttled ones left nothing
        // behind in any provisioning table.
        Assert.Equal(successes.Count, createdUsers);
        Assert.Equal(successes.Count, createdWorkspaces);
        Assert.Equal(successes.Count, createdMembers);

        // Every billing row must belong to a workspace that was actually created.
        var workspaceIds = await db.Workspaces.Select(workspace => workspace.Id).ToListAsync();
        var orphanLedger = await db.CreditLedgerEntries
            .Where(entry => !workspaceIds.Contains(entry.WorkspaceId))
            .CountAsync();
        var orphanSubscriptions = await db.Subscriptions
            .Where(subscription => !workspaceIds.Contains(subscription.WorkspaceId))
            .CountAsync();
        Assert.Equal(0, orphanLedger);
        Assert.Equal(0, orphanSubscriptions);
        Assert.True(createdSubscriptions <= successes.Count,
            $"Subscriptions ({createdSubscriptions}) cannot outnumber provisioned accounts ({successes.Count}).");
        Assert.True(successes.Count >= 1,
            $"Expected the window to serve ordinary traffic before rejecting; saw {string.Join(",", statuses)}");
    }

    [Fact]
    public async Task Real_production_register_does_not_rekey_or_relax_the_login_gate()
    {
        // Out of scope for this lane by design, guarded against regression: login must
        // keep working on its own bucket after registration is tightened.
        using var factory = new ProductionApiFactory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var email = $"qa-login-{Guid.NewGuid():N}@example.com";

        var registered = await SendRegisterAsync(client, email, spoofIndex: null);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

        // A spoofed provenance header on login must not consume, or be consumed by,
        // the registration budget: login is a different policy and is unchanged here.
        using var spoofed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        spoofed.Headers.Add("X-Forwarded-Proto", "https");
        spoofed.Headers.Add("X-Forwarded-For", "203.0.113.200, 198.51.100.200");
        spoofed.Content = JsonContent.Create(new { email, password = "StrongPassword!123" });
        await AddCsrfAsync(client, spoofed);

        using var login = await client.SendAsync(spoofed);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task SendRealRegisterAsync(
        ProductionApiFactory factory,
        string email,
        int index,
        ConcurrentBag<HttpStatusCode> statuses,
        ConcurrentBag<string> successes)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var response = await SendRegisterAsync(client, email, spoofIndex: index);
        statuses.Add(response.StatusCode);
        if (response.IsSuccessStatusCode) successes.Add(email);
    }

    private static async Task<HttpResponseMessage> SendRegisterAsync(
        HttpClient client,
        string email,
        int? spoofIndex)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-Forwarded-Proto", "https");
        // A different claimed client IP per attempt; must never buy extra capacity.
        if (spoofIndex is not null) request.Headers.Add("X-Forwarded-For", $"203.0.113.{spoofIndex.Value}");
        request.Content = JsonContent.Create(new
        {
            displayName = "QA XFF Owner",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        await AddCsrfAsync(client, request);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// The production CSRF cookie is Secure + SameSite=None, so the test client's
    /// cookie container will not replay it over the in-memory http transport. Echo it
    /// by hand: validation only requires the cookie and the header to agree.
    /// </summary>
    private static async Task AddCsrfAsync(HttpClient client, HttpRequestMessage request)
    {
        // Production issues the CSRF cookie only when the request is presented as
        // TLS, which the real deployment gets from the trusted ingress hop through
        // X-Forwarded-Proto. That header changes the scheme and nothing else: it can
        // never move the rate-limit partition, because Program.cs does not consume
        // forwarded client IP at all.
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        csrfRequest.Headers.Add("X-Forwarded-Proto", "https");
        using var csrf = await client.SendAsync(csrfRequest);
        csrf.EnsureSuccessStatusCode();

        var body = await csrf.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var cookie = csrf.Headers.GetValues("Set-Cookie")
            .First(value => value.StartsWith("taslim.csrf=", StringComparison.OrdinalIgnoreCase))
            .Split(';')[0];

        request.Headers.Add("X-CSRF-TOKEN", token!);
        request.Headers.Add("Cookie", cookie);
    }
}
