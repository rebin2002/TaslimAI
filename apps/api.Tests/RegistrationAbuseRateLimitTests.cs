using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging;
using Taslim.Api.Controllers;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Threat: POST /api/auth/register is a state-changing, provisioning-heavy endpoint
/// (workspace creation + billing ledger per account). On main it shares only the
/// login-tuned "authentication" fixed window (60 permits per minute per source IP),
/// so a scripted caller can provision roughly 86,400 accounts per day from a single
/// IP. Registration abuse limits must fail closed: a dedicated, far stricter
/// registration window is required, and it must still bound (never fully exempt)
/// same-host loopback traffic that local CI tooling legitimately uses.
/// </summary>
public sealed class RegistrationAbuseRateLimitTests
{
    // Referenced as a literal so these tests compile (and therefore fail with a
    // clean assertion error, not a build error) against main, where the policy
    // does not exist yet.
    private const string RegistrationPolicy = "registration";

    private static HttpContext CreateRemoteContext(string policyName, IPAddress peerIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peerIp;
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
            displayName: policyName));
        return context;
    }

    [Fact]
    public void Register_endpoint_declares_dedicated_registration_policy()
    {
        var action = typeof(AuthController).GetMethod(
            nameof(AuthController.Register),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.NotNull(action);
        var policy = Assert.Single(action!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));

        // The registration gate must be its own policy so login flooding cannot
        // consume (or be consumed by) the registration budget, and so the
        // registration window can fail closed far below the 60/min login rate.
        Assert.Equal(RegistrationPolicy, policy.PolicyName);
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true));
    }

    [Fact]
    public void Login_endpoint_still_uses_the_shared_authentication_policy()
    {
        var action = typeof(AuthController).GetMethod(
            nameof(AuthController.Login),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.NotNull(action);
        var policy = Assert.Single(action!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));

        // Guard: tightening registration must not relax or re-key the existing
        // login gate.
        Assert.Equal(RateLimiting.Authentication, policy.PolicyName);
    }

    [Fact]
    public async Task Registration_policy_is_configured_as_a_real_policy()
    {
        using var host = await CreateProbeHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", "203.0.113.10");

        // On main the "registration" policy does not exist, so an endpoint that
        // requires it cannot produce a deterministic 200 - the limiter fails the
        // request instead of silently going unlimited.
        using var ok = await client.PostAsync("/probe/register", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Remote_ip_gets_bounded_registration_permits_then_fails_closed_with_429()
    {
        using var host = await CreateProbeHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", "203.0.113.11");

        var accepted = 0;
        HttpStatusCode lastStatus = HttpStatusCode.OK;
        string? rejectedBody = null;
        for (var index = 0; index < 25; index++)
        {
            using var response = await client.PostAsync("/probe/register", null);
            lastStatus = response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                accepted++;
                continue;
            }

            rejectedBody = await response.Content.ReadAsStringAsync();
            break;
        }

        // A dedicated registration window must stop scripted signups well below
        // the 60/minute login window it previously shared.
        Assert.True(accepted >= 1, "Registration must accept ordinary traffic.");
        Assert.True(accepted <= 10, $"Registration window must be stricter than the 60/min login window (observed {accepted} permits).");
        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
        Assert.Contains("RATE_LIMITED", rejectedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_budget_is_partitioned_per_remote_ip()
    {
        using var host = await CreateProbeHostAsync();
        using var firstIpClient = host.GetTestClient();
        firstIpClient.DefaultRequestHeaders.Add("X-Test-Peer", "203.0.113.12");
        using var secondIpClient = host.GetTestClient();
        secondIpClient.DefaultRequestHeaders.Add("X-Test-Peer", "203.0.113.13");

        // Exhaust the first address's window.
        for (var index = 0; index < 40; index++)
        {
            using var response = await firstIpClient.PostAsync("/probe/register", null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) break;
        }

        using var rejected = await firstIpClient.PostAsync("/probe/register", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // A different source IP must not inherit the exhausted bucket - and the
        // first IP's throttling proves the bucket is enforced, not exempted.
        using var otherIp = await secondIpClient.PostAsync("/probe/register", null);
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);
    }

    [Fact]
    public async Task Same_host_loopback_registrations_are_bounded_generously_not_exempt()
    {
        using var host = await CreateProbeHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", IPAddress.Loopback.ToString());

        // The Browser E2E suite registers a fresh account per test from loopback
        // against one API process (60+ registrations in a burst), so the same-host
        // bucket must be far above that volume...
        for (var index = 0; index < 100; index++)
        {
            using var response = await client.PostAsync("/probe/register", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Same_host_loopback_registrations_still_fail_closed_at_the_hard_cap()
    {
        using var host = await CreateProbeHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", IPAddress.Loopback.ToString());

        // ...but it must remain finite: nothing may provision accounts from
        // same-host traffic without any bound.
        HttpStatusCode lastStatus = HttpStatusCode.OK;
        for (var index = 0; index < 700; index++)
        {
            using var response = await client.PostAsync("/probe/register", null);
            lastStatus = response.StatusCode;
            if (lastStatus == HttpStatusCode.TooManyRequests) break;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
    }

    [Fact]
    public async Task Registration_concurrency_is_bounded_without_queueing()
    {
        var options = new RateLimiterOptions();
        RateLimiting.Configure(options);
        var limiter = options.GlobalLimiter!;
        var context = CreateRemoteContext(RegistrationPolicy, IPAddress.Parse("203.0.113.20"));

        using var first = await limiter.AcquireAsync(context);
        using var second = await limiter.AcquireAsync(context);
        using var rejected = await limiter.AcquireAsync(context);

        // Registration provisions a workspace and billing ledger; slow downstream
        // work must not accumulate behind an otherwise valid rate.
        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(rejected.IsAcquired);
    }

    private static async Task<Microsoft.AspNetCore.Hosting.IWebHost> CreateProbeHostAsync()
    {
        var host = new WebHostBuilder()
            .UseEnvironment(Environments.Development)
            .UseTestServer()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddRateLimiter(RateLimiting.Configure);
            })
            .Configure(app =>
            {
                // Test-only peer injection: establishes the Connection.RemoteIpAddress a
                // real client would present, before the limiter computes its partition.
                app.Use(next => context =>
                {
                    var peer = context.Request.Headers["X-Test-Peer"].ToString();
                    context.Connection.RemoteIpAddress =
                        IPAddress.TryParse(peer, out var parsed) ? parsed : IPAddress.Loopback;
                    return next(context);
                });

                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(endpoints =>
                    endpoints.MapPost("/probe/register", () => Results.Ok(new { probe = true }))
                        .RequireRateLimiting(RegistrationPolicy));
            })
            .Build();

        await host.StartAsync();
        return host;
    }
}
