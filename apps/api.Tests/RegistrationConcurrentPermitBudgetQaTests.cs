using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Concurrent-registration permit-budget race lane (QA for the dedicated registration
/// abuse policy shipped by PR #890, head ebe2a435b568c7d9131d28f9e39a16675e94686b).
///
/// BOUNDED QUESTION: when anonymous registration requests arrive at the same instant
/// from ONE trusted transport peer, can the race oversell the configured registration
/// budget - by admitting more than the fixed-window permit limit, by queueing instead
/// of failing closed, or by letting a rejected request reach the provisioning handler
/// at all?
///
/// Why this lane is not already covered. Every existing registration burst is strictly
/// sequential. `RegistrationAbuseRateLimitTests` (#890) awaits each response before
/// sending the next request, so at most one request is ever inside the limiter, and its
/// only concurrency assertion is three back-to-back (again sequential) `AcquireAsync`
/// calls against the global limiter, which never races window accounting across
/// arriving requests. `RateLimitingTests` covers the per-user concurrency arm of other
/// policies, not registration. `RegistrationForwardedIpAbuseQaTests` (#1113) is a
/// different axis entirely: whether a client can forge the peer value. Nothing pins the
/// budget ceiling itself under simultaneous arrival, which is precisely the shape of a
/// scripted signup flood - an attacker does not wait for a 429 before sending the next
/// request, and a limiter that reads its remaining permits before reserving them admits
/// N requests for one remaining permit under contention.
///
/// Deliberately in scope: an in-process rate-limiter pipeline and nothing else. The
/// probes are synthetic in-memory endpoints behind the same `RateLimiting.Configure`
/// policies the application registers. No database, no email, no charging, no real
/// account provisioned, no provider or external network contacted, no production file
/// modified. The peer is set on `Connection.RemoteIpAddress` (the socket peer the
/// production partition key reads) and no forwarded-header middleware or
/// `X-Forwarded-For` value is involved; that is #1113's lane.
///
/// Colour: RED on main, where the `registration` policy and its switch arm do not
/// exist, GREEN at #890's head.
/// </summary>
public sealed class RegistrationConcurrentPermitBudgetQaTests : IDisposable
{
    // Literals, not RateLimiting.Registration, so the file compiles (and therefore
    // fails with a clean assertion rather than a build error) against main.
    private const string RegistrationPolicy = "registration";
    private const string AuthenticationPolicy = "authentication";

    // Keep in step with apps/api/Infrastructure/RateLimiting.cs: a remote peer gets
    // 10 permits per 15-minute fixed window and at most 2 concurrent in-flight
    // requests with no queueing. The window is far longer than this suite runs, so no
    // permit is replenished mid-test and every admission is charged to one budget.
    private const int RemotePermitLimit = 10;
    private const int RemoteConcurrencyLimit = 2;

    private static readonly IPAddress PeerAlpha = IPAddress.Parse("203.0.113.241");
    private static readonly IPAddress PeerBeta = IPAddress.Parse("203.0.113.242");

    private readonly ProbeTelemetry _telemetry = new();

    /// <summary>
    /// Counts what the pipeline actually let through, so "a rejected request
    /// provisions nothing" is measured rather than assumed: `Executed` is the stand-in
    /// for the workspace/membership/ledger writes the real handler performs.
    /// </summary>
    private sealed class ProbeTelemetry
    {
        private int _executed;
        private int _inFlight;
        private int _maxInFlight;

        public int Executed => Volatile.Read(ref _executed);
        public int InFlight => Volatile.Read(ref _inFlight);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public TaskCompletionSource<bool> HoldGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enter()
        {
            var now = Interlocked.Increment(ref _inFlight);
            while (true)
            {
                var seen = Volatile.Read(ref _maxInFlight);
                if (now <= seen || Interlocked.CompareExchange(ref _maxInFlight, now, seen) == seen)
                {
                    return;
                }
            }
        }

        public void Exit() => Interlocked.Decrement(ref _inFlight);

        public void ExecutedOne() => Interlocked.Increment(ref _executed);
    }

    private readonly record struct Response(HttpStatusCode Status, string? Body, string? RetryAfter);

    private readonly record struct Outcome(int Accepted, int Rejected, int Other, IReadOnlyList<Response> Responses)
    {
        public static Outcome From(IReadOnlyList<Response> responses) => new(
            responses.Count(r => r.Status == HttpStatusCode.OK),
            responses.Count(r => r.Status == HttpStatusCode.TooManyRequests),
            responses.Count(r => r.Status is not (HttpStatusCode.OK or HttpStatusCode.TooManyRequests)),
            responses);

        public string Describe() =>
            string.Join(",", Responses.GroupBy(r => (int)r.Status)
                .Select(group => $"{group.Key}x{group.Count()}"));
    }

    // ---------------------------------------------------------------- pipeline

    private async Task<IWebHost> CreateProbeHostAsync()
    {
        var host = new WebHostBuilder()
            .UseEnvironment(Environments.Development)
            .UseTestServer()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddRateLimiter(Infrastructure.RateLimiting.Configure);
            })
            .Configure(app =>
            {
                // Model the transport: a network listener has already fixed the socket
                // peer before any middleware runs, so the peer is injected ahead of
                // routing and the limiter. No client-supplied forwarded header is read:
                // the address here is the trusted peer under test.
                app.Use(next => context =>
                {
                    var peer = context.Request.Headers["X-Qa-Peer"].ToString();
                    context.Connection.RemoteIpAddress =
                        IPAddress.TryParse(peer, out var parsed) ? parsed : IPAddress.Loopback;
                    return next(context);
                });

                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(endpoints =>
                {
                    // Fast probe: stands in for POST /api/auth/register.
                    endpoints.MapPost("/probe/register", async () =>
                    {
                        _telemetry.Enter();
                        try
                        {
                            // A real registration is not free (workspace + ledger
                            // writes), and that latency is what makes a simultaneous
                            // burst dangerous. A yield keeps requests genuinely
                            // overlapping without depending on wall clock.
                            await Task.Yield();
                            _telemetry.ExecutedOne();
                            return Results.Ok(new { probe = "register", executed = true });
                        }
                        finally
                        {
                            _telemetry.Exit();
                        }
                    }).RequireRateLimiting(RegistrationPolicy);

                    // Same policy, handler blocked until the test releases the gate:
                    // lets the limiter be observed while permits are held.
                    endpoints.MapPost("/probe/register-held", async () =>
                    {
                        _telemetry.Enter();
                        try
                        {
                            await _telemetry.HoldGate.Task;
                            _telemetry.ExecutedOne();
                            return Results.Ok(new { probe = "register-held", executed = true });
                        }
                        finally
                        {
                            _telemetry.Exit();
                        }
                    }).RequireRateLimiting(RegistrationPolicy);

                    // Login keeps its own shared bucket; used only to prove a
                    // registration flood cannot borrow from it.
                    endpoints.MapPost("/probe/login", () =>
                    {
                        _telemetry.ExecutedOne();
                        return Results.Ok(new { probe = "login" });
                    }).RequireRateLimiting(AuthenticationPolicy);
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static HttpClient CreatePeerClient(IWebHost host, IPAddress peer)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Qa-Peer", peer.ToString());
        return client;
    }

    private static async Task<IReadOnlyList<Response>> SendBurstAsync(
        HttpClient client,
        string path,
        int attempts)
    {
        var sends = new Task<HttpResponseMessage>[attempts];
        for (var index = 0; index < attempts; index++)
        {
            sends[index] = client.PostAsync(path, null);
        }

        // Wall-clock guard: a limiter that queues rejected registrations must fail the
        // test instead of hanging the suite forever.
        var all = Task.WhenAll(sends);
        var winner = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)));
        Assert.True(ReferenceEquals(all, winner),
            $"The simultaneous burst on {path} did not finish within 120s: rejected registrations are " +
            "queueing behind slow provisioning instead of failing closed.");

        var outcomes = new List<Response>(attempts);
        foreach (var response in await all)
        {
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync();
                var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
                    ? string.Join(",", values)
                    : null;
                outcomes.Add(new Response(response.StatusCode, body, retryAfter));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Waves of simultaneous requests from one peer until that peer is throttled. This
    /// is the realistic abusive caller: it does not serialise itself, and the
    /// concurrency arm means a single huge wave cannot exhaust the window on its own.
    /// </summary>
    private static async Task<Outcome> RaceUntilThrottledAsync(
        HttpClient client,
        string path,
        int waveSize,
        int maxWaves)
    {
        var responses = new List<Response>();
        for (var wave = 1; wave <= maxWaves; wave++)
        {
            responses.AddRange(await SendBurstAsync(client, path, waveSize));
            if (responses.Any(r => r.Status == HttpStatusCode.TooManyRequests))
            {
                break;
            }
        }

        return Outcome.From(responses);
    }

    // ---------------------------------------------------------------- tests

    /// <summary>
    /// Core reproduction. A same-instant burst from one trusted peer must never be
    /// admitted past the configured permit budget, every excess must be the standard
    /// 429 envelope, and a rejected request must never reach the handler. Sequential
    /// tests cannot show this: an oversell only appears when the permit check and the
    /// permit decrement interleave across arriving requests.
    /// </summary>
    [Fact]
    public async Task Simultaneous_registrations_from_one_trusted_peer_cannot_oversell_the_permit_budget()
    {
        using var host = await CreateProbeHostAsync();
        using var client = CreatePeerClient(host, PeerAlpha);

        // Waves, because with an in-flight bound of 2 a single giant wave is refused
        // capacity it was never granted: the fixed window, not the concurrency arm, is
        // what must hold the ceiling over the whole race.
        var admitted = 0;
        var responses = new List<Response>();
        for (var wave = 1; wave <= 3; wave++)
        {
            responses.AddRange(await SendBurstAsync(client, "/probe/register", 12));
            admitted = responses.Count(r => r.Status == HttpStatusCode.OK);
            Assert.True(admitted <= RemotePermitLimit,
                $"After wave {wave} a racing caller had already been admitted {admitted} registrations " +
                $"from one trusted peer against a budget of {RemotePermitLimit}.");
        }

        // Then settle the account with single-file traffic: a concurrency refusal does
        // not spend a window permit, so only a drain can show whether the race left the
        // budget whole. An oversell would surface here as a total above the cap.
        var outcome = Outcome.From(responses);
        HttpStatusCode? firstRefusal = null;
        for (var index = 0; index < RemotePermitLimit + 6 && firstRefusal is null; index++)
        {
            using var response = await client.PostAsync("/probe/register", null);
            var body = await response.Content.ReadAsStringAsync();
            var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
                ? string.Join(",", values)
                : null;
            responses.Add(new Response(response.StatusCode, body, retryAfter));
            if (response.StatusCode == HttpStatusCode.OK) admitted++;
            else firstRefusal = response.StatusCode;
        }

        outcome = Outcome.From(responses);

        Assert.True(outcome.Other == 0,
            $"The race produced {outcome.Other} responses outside 200/429 ({outcome.Describe()}); on main the " +
            "registration policy is absent, so the limiter faults instead of throttling.");
        Assert.True(outcome.Rejected >= 1,
            $"A racing caller from one address was never throttled ({outcome.Describe()}).");

        // Exact accounting, not just a ceiling: the same 10 permits are the whole
        // budget whether they are asked for one at a time or all at once, so a race can
        // neither multiply nor silently destroy capacity.
        Assert.Equal(RemotePermitLimit, admitted);
        Assert.Equal(HttpStatusCode.TooManyRequests, firstRefusal);

        foreach (var response in outcome.Responses.Where(r => r.Status == HttpStatusCode.TooManyRequests))
        {
            Assert.Contains("RATE_LIMITED", response.Body, StringComparison.Ordinal);
            Assert.Equal("60", response.RetryAfter);
        }

        // Rejected requests never reached the provisioning stand-in.
        Assert.Equal(admitted, _telemetry.Executed);
    }

    /// <summary>
    /// The ceiling must hold for sustained abuse rather than one lucky burst: repeated
    /// waves and interleaved sequential requests from the same peer draw on the same
    /// 15-minute window, and once it is spent the gate stays shut.
    /// </summary>
    [Fact]
    public async Task Repeated_parallel_waves_and_sequential_topups_share_one_window_budget()
    {
        using var host = await CreateProbeHostAsync();
        using var client = CreatePeerClient(host, PeerAlpha);

        var admitted = 0;
        var throttled = 0;
        var observedCeilingViolation = false;

        for (var wave = 1; wave <= 3; wave++)
        {
            var responses = await SendBurstAsync(client, "/probe/register", 8);
            var outcome = Outcome.From(responses);
            admitted += outcome.Accepted;
            throttled += outcome.Rejected;

            Assert.Equal(0, outcome.Other);
            Assert.Equal(8, outcome.Accepted + outcome.Rejected);

            // Interleave sequential requests: a real script mixes both patterns, and a
            // budget that is only enforced per-wave would leak here.
            for (var index = 0; index < 4; index++)
            {
                using var response = await client.PostAsync("/probe/register", null);
                if (response.StatusCode == HttpStatusCode.OK) admitted++;
                else if (response.StatusCode == HttpStatusCode.TooManyRequests) throttled++;
                else Assert.Fail($"Unexpected status {(int)response.StatusCode} during sequential top-up.");
            }

            observedCeilingViolation |= admitted > RemotePermitLimit;
            Assert.True(admitted <= RemotePermitLimit,
                $"After wave {wave} the same peer had been admitted {admitted} registrations, above the " +
                $"{RemotePermitLimit}-permit budget: the budget leaked across concurrent waves.");
        }

        Assert.True(throttled >= 1, "Sustained abuse from one address must eventually be throttled.");
        Assert.False(observedCeilingViolation);
        Assert.Equal(admitted, _telemetry.Executed);

        // Still closed for the rest of the window: 15 minutes is far longer than this
        // test, so once spent the gate must not quietly reopen.
        using var final = await client.PostAsync("/probe/register", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, final.StatusCode);
        Assert.Equal(admitted, _telemetry.Executed);
    }

    /// <summary>
    /// While the in-flight bound is saturated by registrations already inside the
    /// handler, further simultaneous attempts must be refused outright - no queueing
    /// behind slow workspace and ledger provisioning, and no execution for a refused
    /// request.
    /// </summary>
    [Fact]
    public async Task Concurrent_registrations_cannot_exceed_the_in_flight_bound_or_queue_behind_it()
    {
        using var host = await CreateProbeHostAsync();
        using var client = CreatePeerClient(host, PeerAlpha);

        // Occupy the in-flight permits with registrations parked inside the handler.
        var held = new Task<HttpResponseMessage>[RemoteConcurrencyLimit];
        for (var index = 0; index < RemoteConcurrencyLimit; index++)
        {
            held[index] = client.PostAsync("/probe/register-held", null);
        }

        var deadline = Stopwatch.StartNew();
        while (_telemetry.InFlight < RemoteConcurrencyLimit)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(60))
            {
                Assert.Fail($"Only {_telemetry.InFlight} of {RemoteConcurrencyLimit} registrations reached " +
                            "the handler within 60s; the gate never opened for ordinary traffic.");
            }

            await Task.Delay(10);
        }

        var stopwatch = Stopwatch.StartNew();
        var contenders = Outcome.From(await SendBurstAsync(client, "/probe/register", 10));
        stopwatch.Stop();

        // Nothing piles up behind a slow registration: with the bound saturated the
        // excess is refused, so slow provisioning cannot accumulate.
        Assert.True(contenders.Rejected >= contenders.Responses.Count - 1,
            $"With the in-flight bound saturated, {contenders.Accepted} of " +
            $"{contenders.Responses.Count} contending registrations were admitted anyway " +
            $"({contenders.Describe()}).");
        Assert.Equal(0, contenders.Other);

        // Refusal is immediate rather than a slow-but-bounded wait: evidence of no
        // queueing, not just of a short queue.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"Contending registrations took {stopwatch.Elapsed.TotalSeconds:N1}s to be refused; a " +
            "fail-closed limiter must reject immediately instead of queueing behind provisioning.");

        // The held handlers have not returned yet, so nothing at all has been
        // provisioned: refused requests did not even reach the handler.
        Assert.Equal(0, _telemetry.Executed);

        _telemetry.HoldGate.SetResult(true);
        var heldResponses = await Task.WhenAll(held);
        foreach (var response in heldResponses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        Assert.Equal(RemoteConcurrencyLimit, _telemetry.MaxInFlight);
        Assert.Equal(RemoteConcurrencyLimit, _telemetry.Executed);
        Assert.True(contenders.Accepted + RemoteConcurrencyLimit <= RemotePermitLimit,
            $"Held plus contending admissions ({contenders.Accepted + RemoteConcurrencyLimit}) exceed the " +
            $"{RemotePermitLimit}-permit window.");
    }

    /// <summary>
    /// The budget belongs to the peer, so one abusive address racing itself cannot
    /// multiply its capacity by opening more simultaneous requests, and a neighbour's
    /// flood must neither be merged into its bucket nor starve an unrelated peer.
    /// </summary>
    [Fact]
    public async Task Two_peers_racing_in_parallel_get_their_own_budgets_and_never_share_one_bucket()
    {
        using var host = await CreateProbeHostAsync();
        using var alpha = CreatePeerClient(host, PeerAlpha);
        using var beta = CreatePeerClient(host, PeerBeta);

        // First take peer A exactly to its cap with single-file traffic, so its window
        // is provably spent before the shared race begins.
        var alphaDrained = 0;
        for (var index = 0; index < RemotePermitLimit + 6; index++)
        {
            using var response = await alpha.PostAsync("/probe/register", null);
            if (response.StatusCode == HttpStatusCode.OK) alphaDrained++;
        }

        Assert.Equal(RemotePermitLimit, alphaDrained);

        // Now race: an exhausted peer and a fresh peer arrive at the same instant, in
        // waves, which is where a merged bucket, a shared counter, or a race that lets
        // one caller borrow another's capacity would show up.
        var betaAdmitted = 0;
        var betaResponses = new List<Response>();
        var alphaResponses = new List<Response>();
        for (var wave = 1; wave <= 3; wave++)
        {
            var simultaneous = await Task.WhenAll(
                SendBurstAsync(alpha, "/probe/register", 8),
                SendBurstAsync(beta, "/probe/register", 8));

            alphaResponses.AddRange(simultaneous[0]);
            betaResponses.AddRange(simultaneous[1]);
            betaAdmitted = betaResponses.Count(r => r.Status == HttpStatusCode.OK);

            Assert.True(betaAdmitted <= RemotePermitLimit,
                $"Peer {PeerBeta} was served {betaAdmitted} registrations while racing an already " +
                $"exhausted neighbour, above its own budget of {RemotePermitLimit}.");
        }

        // Settle B's account with single-file traffic: its own window must run out at
        // exactly its own cap, and A's exhausted gate must not reopen for it.
        for (var probe = 0; probe < RemotePermitLimit + 6; probe++)
        {
            using var response = await beta.PostAsync("/probe/register", null);
            if (response.StatusCode == HttpStatusCode.OK) betaAdmitted++;
            else break;
        }

        var betaOutcome = Outcome.From(betaResponses);
        var alphaOutcome = Outcome.From(alphaResponses);

        Assert.Equal(0, betaOutcome.Other);
        Assert.Equal(0, alphaOutcome.Other);
        Assert.True(alphaOutcome.Accepted == 0,
            $"An exhausted peer was served {alphaOutcome.Accepted} more registrations during the race " +
            $"({alphaOutcome.Describe()}): the gate reopened.");
        Assert.Equal(RemotePermitLimit, betaAdmitted);

        // Combined admissions exceed one window: the budget is keyed to the peer, so a
        // neighbour's flood can neither be merged into it nor starve an unrelated caller.
        Assert.True(alphaDrained + betaAdmitted > RemotePermitLimit,
            $"Two peers were served {alphaDrained} and {betaAdmitted} registrations, within a single " +
            $"shared budget of {RemotePermitLimit}: the per-peer split is not visible.");

        Assert.Equal(alphaDrained + betaAdmitted, _telemetry.Executed);
    }

    /// <summary>
    /// Collateral-damage guard: draining a peer's registration budget through a race
    /// must not re-key, relax or consume the shared login bucket, and must not leave
    /// registration open again afterwards.
    /// </summary>
    [Fact]
    public async Task Exhausting_registration_under_a_race_leaves_the_login_bucket_untouched()
    {
        using var host = await CreateProbeHostAsync();
        using var client = CreatePeerClient(host, PeerAlpha);

        var outcome = await RaceUntilThrottledAsync(client, "/probe/register", waveSize: 8, maxWaves: 6);
        Assert.Contains(HttpStatusCode.TooManyRequests, outcome.Responses.Select(r => r.Status));
        Assert.True(outcome.Accepted <= RemotePermitLimit,
            $"The registration race admitted {outcome.Accepted} against a budget of {RemotePermitLimit}.");
        var executedAfterRace = _telemetry.Executed;

        // Finish the window with single-file traffic: a refusal from the concurrency
        // arm does not spend a window permit, so the race alone does not yet prove the
        // budget is spent. Ordinary sequential traffic must hit the same ceiling.
        var admitted = outcome.Accepted;
        HttpStatusCode? firstRefusal = null;
        for (var index = 0; index < RemotePermitLimit + 4 && firstRefusal is null; index++)
        {
            using var attempt = await client.PostAsync("/probe/register", null);
            if (attempt.StatusCode == HttpStatusCode.OK) admitted++;
            else firstRefusal = attempt.StatusCode;
        }

        Assert.True(admitted <= RemotePermitLimit,
            $"A race followed by a sequential drain admitted {admitted} registrations from one peer " +
            $"against a budget of {RemotePermitLimit}: the ceiling only holds while requests overlap.");
        Assert.Equal(HttpStatusCode.TooManyRequests, firstRefusal);

        // Login is a separate policy with its own partition prefix: it must still serve
        // this same peer at full strength, proving the two buckets stayed independent.
        for (var index = 0; index < 5; index++)
        {
            using var login = await client.PostAsync("/probe/login", null);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        // Registration stays shut for the remainder of the window.
        using var stillClosed = await client.PostAsync("/probe/register", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, stillClosed.StatusCode);
        Assert.Equal(admitted + 5, _telemetry.Executed);

        Assert.True(_telemetry.Executed <= RemotePermitLimit + 5,
            $"Handler executions ({_telemetry.Executed}) outran the combined registration and login " +
            $"probes ({executedAfterRace} after the race); the buckets are no longer independent.");
    }

    /// <summary>
    /// Pins the policy shape this lane depends on, at the limiter the production
    /// pipeline actually installs: the registration arm of the concurrency limiter is
    /// finite and refuses rather than queues, while an unconfigured policy name falls
    /// open to "no limiter". On main "registration" has no arm at all, so this acquire
    /// succeeds and the assertion fails: red for the right reason.
    /// </summary>
    [Fact]
    public async Task Registration_concurrency_arm_is_finite_and_refuses_without_queueing()
    {
        var options = new RateLimiterOptions();
        Infrastructure.RateLimiting.Configure(options);

        Assert.Equal(429, options.RejectionStatusCode);
        Assert.NotNull(options.GlobalLimiter);

        var context = CreateRemoteContext(RegistrationPolicy, PeerAlpha);

        // One permit at a time up to the bound, then a request beyond it: the bound is
        // enforced atomically for a multi-permit acquire, which is the race shape.
        using var first = await options.GlobalLimiter!.AcquireAsync(context);
        using var second = await options.GlobalLimiter.AcquireAsync(context);
        using var overBound = await options.GlobalLimiter.AcquireAsync(context, RemoteConcurrencyLimit);

        Assert.True(first.IsAcquired, "The first registration must be served.");
        Assert.True(second.IsAcquired, "The second concurrent registration must be served.");
        Assert.False(overBound.IsAcquired,
            $"The registration concurrency arm admitted beyond its bound of {RemoteConcurrencyLimit} " +
            "(on main it is not configured at all, so nothing bounds slow provisioning concurrency).");

        // And the excess does not wait for a permit: it is a lease the caller must give
        // back, not a queue.
        overBound.Dispose();
        using var released = await options.GlobalLimiter.AcquireAsync(context);
        Assert.False(released.IsAcquired);
    }

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

    public void Dispose() => _telemetry.HoldGate.TrySetResult(true);
}
