using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Threat/defect under test: the partition identity used for non-authenticated public
/// AI generation traffic (the HMAC-signed completion-bridge intake
/// POST /api/autopilot/events is the anonymous caller of the "generation-creation"
/// policy on main) is built by writing the transport peer address into the limiter key
/// verbatim, and by falling back to one literal shared key when a transport exposes no
/// peer address at all.
///
/// That single identity function fails in two directions at once, and the two failure
/// modes are normally traded against each other:
///   * Privacy - the raw client address is retained as long-lived in-process limiter
///     state, while the authenticated path deliberately stores only an opaque
///     "user:{id}" handle. This API reads the client address nowhere else, so the rate
///     limiter is the only place a network identifier outlives its request.
///   * Liveness - distinct healthy callers collapse into one budget, so one client's
///     in-flight work produces 429s for unrelated clients that cannot do anything about
///     it. Conversely, a naive "fix" (bucket per connection, or exempt anonymous
///     traffic) restores privacy and liveness by destroying the abuse bound.
///
/// These tests therefore pin both properties plus the enforcement they must not lose:
/// one client keeps one budget (stable, so the rate actually applies), different
/// clients keep different budgets (so healthy traffic is never starved by a neighbour),
/// the same client is not split across two budgets by the address family it happened to
/// be reached on, and no raw address material reaches a key or a response body.
///
/// All of it is deterministic: the concurrency limiter is saturated with held leases
/// (PermitLimit = 4, QueueLimit = 0) rather than by racing a clock, and the keyed digest
/// is asserted only through properties that hold for any salt (stability, injectivity,
/// absence of source material), never through a literal hash value.
/// </summary>
public sealed class AnonymousGenerationPartitionTests
{
    // Referenced as a literal so the file compiles unchanged against main.
    private const string GenerationPolicy = "generation-creation";

    // Mirrors the "generation-creation" concurrency budget in RateLimiting.Configure.
    private const int GenerationConcurrencyPermitLimit = 4;

    private static string PartitionKey(string prefix, HttpContext context)
    {
        var partition = typeof(RateLimiting).GetMethod("Partition", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(partition);
        return (string?)partition!.Invoke(null, new object[] { prefix, context, false })
            ?? throw new InvalidOperationException("RateLimiting.Partition returned no key.");
    }

    private static HttpContext CreateAnonymousContext(string policyName, IPAddress? peerAddress, string? connectionId = null)
    {
        var context = new DefaultHttpContext
        {
            // Unauthenticated principal: the anonymous branch of the partition identity.
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: null)),
        };
        context.Connection.RemoteIpAddress = peerAddress;
        if (connectionId is not null)
        {
            context.Connection.Id = connectionId;
        }

        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
            displayName: policyName));
        return context;
    }

    private static HttpContext CreateUserContext(string policyName, Guid userId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString("N"))],
                "test")),
        };
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
            displayName: policyName));
        return context;
    }

    private static RateLimiterOptions CreateOptions()
    {
        var options = new RateLimiterOptions();
        RateLimiting.Configure(options);
        return options;
    }

    private static async Task<LeaseSet> SaturateAsync(PartitionedRateLimiter<HttpContext> limiter, HttpContext context, int count)
    {
        var leases = new RateLimitLease[count];
        for (var i = 0; i < count; i++)
        {
            leases[i] = await limiter.AcquireAsync(context);
            Assert.True(leases[i].IsAcquired, $"Expected permit {i + 1} of the saturated budget to be acquired.");
        }

        return new LeaseSet(leases);
    }

    /// <summary>Holds the permits that saturate a budget open for the duration of a test.</summary>
    private sealed class LeaseSet : IDisposable
    {
        private readonly RateLimitLease[] leases;

        public LeaseSet(RateLimitLease[] leases) => this.leases = leases;

        public void Dispose()
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void Anonymous_partition_key_does_not_retain_the_client_address()
    {
        var v4 = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("203.0.113.7"));
        var v6 = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("2001:db8::42"));

        foreach (var (context, address) in new[] { (v4, "203.0.113.7"), (v6, "2001:db8::42") })
        {
            foreach (var prefix in new[] { "generation", "concurrency:generation-creation", "ai", "search" })
            {
                var key = PartitionKey(prefix, context);
                Assert.DoesNotContain(address, key, StringComparison.Ordinal);
                // Truncated forms must not leak either: the limiter key is a
                // colon-delimited composite, so a bare octet/hextet run would survive
                // as a partial identifier.
                Assert.DoesNotContain(address.Replace('.', ':'), key, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Anonymous_partition_key_is_stable_for_one_client_across_requests()
    {
        // Two separate requests (separate HttpContext instances) from one client must
        // land in one budget, otherwise a privacy-preserving key is only an exemption.
        var first = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("198.51.100.9"), connectionId: "conn-stable-1");
        var second = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("198.51.100.9"), connectionId: "conn-stable-2");

        Assert.Equal(PartitionKey("generation", first), PartitionKey("generation", second));
    }

    [Fact]
    public void Same_client_is_not_split_across_address_families()
    {
        // A dual-stack listener reports the identical IPv4 client as either a plain v4
        // address or an IPv4-mapped v6 address. Those must not become two budgets, or
        // one caller doubles its allowance by alternating transports.
        var mapped = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("::ffff:203.0.113.7"));
        var plain = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("203.0.113.7"));

        Assert.Equal(PartitionKey("generation", mapped), PartitionKey("generation", plain));
    }

    [Fact]
    public async Task Healthy_anonymous_client_is_not_starved_by_a_neighbour_without_a_peer_address()
    {
        // Transports that expose no peer address (unix socket, in-memory host) are not
        // one client. One saturated caller must not 429 every other caller that shares
        // the literal fallback key.
        var options = CreateOptions();
        var limiter = options.GlobalLimiter!;
        var abusive = CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "conn-abusive");
        var healthy = CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "conn-healthy");

        using var abusiveLeases = await SaturateAsync(limiter, abusive, GenerationConcurrencyPermitLimit);
        using var neighbourLease = await limiter.AcquireAsync(healthy);

        Assert.True(neighbourLease.IsAcquired,
            "A healthy anonymous caller was rejected by a budget consumed entirely by an unrelated client.");
    }

    [Fact]
    public async Task One_anonymous_client_still_cannot_exceed_its_own_concurrency_budget()
    {
        // The coexistence half of the previous test: spreading healthy callers apart
        // must not turn the anonymous budget into an unbounded allowance per client.
        var options = CreateOptions();
        var limiter = options.GlobalLimiter!;
        var client = CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "conn-single");

        using var leases = await SaturateAsync(limiter, client, GenerationConcurrencyPermitLimit);
        using var overflow = await limiter.AcquireAsync(client);

        Assert.False(overflow.IsAcquired, "One anonymous client was granted more concurrency than the policy permits.");
    }

    [Fact]
    public async Task Anonymous_clients_behind_addresses_keep_independent_budgets()
    {
        var options = CreateOptions();
        var limiter = options.GlobalLimiter!;
        var first = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("203.0.113.20"));
        var second = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("198.51.100.20"));

        Assert.NotEqual(PartitionKey("concurrency:generation-creation", first), PartitionKey("concurrency:generation-creation", second));

        using var firstLeases = await SaturateAsync(limiter, first, GenerationConcurrencyPermitLimit);
        using var firstOverflow = await limiter.AcquireAsync(first);
        using var secondLease = await limiter.AcquireAsync(second);

        Assert.False(firstOverflow.IsAcquired, "The saturated client was silently given extra budget.");
        Assert.True(secondLease.IsAcquired, "An unrelated client inherited another client's exhausted budget.");
    }

    [Fact]
    public void Authenticated_partition_identity_is_unchanged_by_anonymous_keying()
    {
        // Guard for every other limiter lane: registration, password recovery and the
        // authenticated generation budgets keep addressing users by opaque id.
        var userId = Guid.NewGuid();
        var key = PartitionKey("generation", CreateUserContext(GenerationPolicy, userId));

        Assert.Equal($"generation:user:{userId:N}", key);
    }

    [Fact]
    public void Anonymous_keys_never_enter_the_authenticated_user_namespace()
    {
        // A client cannot reach the "user:" partition - where per-account budgets and,
        // for other policies, per-account isolation live - by having no address, and an
        // address-derived key cannot be mistaken for an identity-derived one.
        var peerless = CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "conn-namespace");
        var addressed = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("203.0.113.99"));

        Assert.StartsWith("generation:anon:", PartitionKey("generation", peerless), StringComparison.Ordinal);
        Assert.StartsWith("generation:anon:", PartitionKey("generation", addressed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_anonymous_request_leaks_no_client_identity()
    {
        var options = CreateOptions();
        var context = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("203.0.113.200"));
        context.Response.Body = new MemoryStream();
        var limiter = options.GlobalLimiter!;

        using var leases = await SaturateAsync(limiter, context, GenerationConcurrencyPermitLimit);
        using var rejected = await limiter.AcquireAsync(context);
        Assert.False(rejected.IsAcquired);

        context.Response.StatusCode = options.RejectionStatusCode;
        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("RATE_LIMITED", body, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.200", body, StringComparison.Ordinal);
    }
}
