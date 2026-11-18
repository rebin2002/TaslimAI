using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-only lane guarding two branches of the anonymous client-partition identity that
/// the #898 boundary work does not pin, exercised entirely through synthetic in-process
/// <see cref="DefaultHttpContext"/> clients: no live endpoint, no provider, no network.
///
/// Gap 1 - the address-family merge is asserted on the wrong limiter arm.
/// A dual-stack listener reports one IPv4 caller as either <c>203.0.113.7</c> or its
/// IPv4-mapped form <c>::ffff:203.0.113.7</c>; the identity must treat those as one
/// client. The published lane asserts the merge only through the fixed-window
/// <c>"generation"</c> prefix, while the limiter that actually rejects live generation
/// traffic is the <c>GlobalLimiter</c> concurrency arm keyed
/// <c>"concurrency:generation-creation"</c> (PermitLimit 4, QueueLimit 0). The two arms
/// are constructed by different code paths, so an arm-local keying slip - or a future
/// change that stops routing the concurrency arm through the same identity - leaves the
/// caller able to double its live in-flight allowance by alternating transports, which is
/// exactly the exemption the merge exists to prevent. This lane asserts the merge on the
/// arm that issues the 429, both directions, and separately asserts that genuinely
/// distinct IPv6 callers stay distinct so the merge cannot be read as a relaxation.
///
/// Gap 2 - the peerless fallback is asserted only when a connection id exists.
/// When the transport exposes no peer address, the identity is derived from the
/// server-assigned connection id; when neither is present it falls back to one stable
/// literal inside the anonymous namespace. Only the connection-id branch is pinned, so
/// the terminal branch - the one that decides whether a caller with no transport
/// information can ever land in the authenticated <c>"user:"</c> partition - is untested,
/// and a key shape that leaked the connection identifier verbatim would go unnoticed.
///
/// Both properties are asserted through shape and behaviour that hold for any per-process
/// salt (stability, separation, anonymous namespace, absence of source material), never
/// through a literal digest value, so the lane stays deterministic.
/// </summary>
public sealed class AnonymousPartitionAddressFamilyConcurrencyTests
{
    // Literal policy names so this file compiles unchanged against main and against the
    // #898 head; the two arms are read from the configured options, not hard-coded.
    private const string GenerationPolicy = "generation-creation";

    // Mirrors the "generation-creation" concurrency budget in RateLimiting.Configure.
    private const int GenerationConcurrencyPermitLimit = 4;

    private const string PlainV4Peer = "203.0.113.7";
    private const string MappedV6Peer = "::ffff:203.0.113.7";

    private static string PartitionKey(string prefix, HttpContext context)
    {
        var partition = typeof(RateLimiting).GetMethod("Partition", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(partition);
        return (string?)partition!.Invoke(null, new object[] { prefix, context, false })
            ?? throw new InvalidOperationException("RateLimiting.Partition returned no key.");
    }

    private static HttpContext CreateAnonymousContext(IPAddress? peerAddress, string? connectionId)
    {
        var context = new DefaultHttpContext
        {
            // Unauthenticated principal: the anonymous branch of the partition identity.
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: null)),
        };

        context.Connection.RemoteIpAddress = peerAddress;
        context.Connection.Id = connectionId;

        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(GenerationPolicy)),
            displayName: GenerationPolicy));
        return context;
    }

    private static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter()
    {
        var options = new RateLimiterOptions();
        RateLimiting.Configure(options);

        // The arm that actually rejects live traffic: the process-wide concurrency
        // limiter, not the per-policy fixed-window limiter.
        return options.GlobalLimiter!;
    }

    private static async Task<LeaseSet> SaturateAsync(
        PartitionedRateLimiter<HttpContext> limiter,
        HttpContext context,
        int count)
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
            foreach (var lease in this.leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void Ipv4_mapped_and_plain_addresses_key_the_concurrency_arm_to_one_client()
    {
        // Same caller, two transports, one budget - asserted on the arm whose rejection
        // the caller actually sees, not only on the fixed-window arm.
        var mapped = CreateAnonymousContext(IPAddress.Parse(MappedV6Peer), "conn-family-mapped");
        var plain = CreateAnonymousContext(IPAddress.Parse(PlainV4Peer), "conn-family-plain");
        var concurrencyPrefix = $"concurrency:{GenerationPolicy}";

        var mappedKey = PartitionKey(concurrencyPrefix, mapped);
        var plainKey = PartitionKey(concurrencyPrefix, plain);

        Assert.Equal(plainKey, mappedKey);

        // Merging must not put the client's transport material into the key.
        Assert.DoesNotContain(PlainV4Peer, mappedKey, StringComparison.Ordinal);
        Assert.DoesNotContain(MappedV6Peer, mappedKey, StringComparison.Ordinal);
        Assert.DoesNotContain(PlainV4Peer.Replace('.', ':'), mappedKey, StringComparison.Ordinal);

        // ... and must not make one client's concurrency budget depend on which
        // connection it arrived on: the same peer over two connection ids is still one key.
        Assert.Equal(
            plainKey,
            PartitionKey(concurrencyPrefix, CreateAnonymousContext(IPAddress.Parse(PlainV4Peer), "conn-family-other")));
    }

    [Fact]
    public async Task A_client_cannot_double_its_concurrency_budget_by_switching_address_family()
    {
        var limiter = CreateGlobalLimiter();
        var plain = CreateAnonymousContext(IPAddress.Parse(PlainV4Peer), "conn-double-plain");
        var mapped = CreateAnonymousContext(IPAddress.Parse(MappedV6Peer), "conn-double-mapped");

        // Saturate the budget reached over IPv4, then ask again over the IPv4-mapped
        // IPv6 form of the identical peer. A split key would hand out a second,
        // untouched allowance.
        using var held = await SaturateAsync(limiter, plain, GenerationConcurrencyPermitLimit);
        using var otherFamily = await limiter.AcquireAsync(mapped);

        Assert.False(otherFamily.IsAcquired,
            "The same anonymous client was granted a second concurrency budget by presenting its address in IPv4-mapped IPv6 form.");
    }

    [Fact]
    public async Task A_client_cannot_double_its_concurrency_budget_from_the_mapped_side_either()
    {
        // The mirror direction: the merge may not be a one-way alias that leaves the
        // mapped transport with its own untouched budget.
        var limiter = CreateGlobalLimiter();
        var mapped = CreateAnonymousContext(IPAddress.Parse(MappedV6Peer), "conn-double-mapped-first");
        var plain = CreateAnonymousContext(IPAddress.Parse(PlainV4Peer), "conn-double-plain-second");

        using var held = await SaturateAsync(limiter, mapped, GenerationConcurrencyPermitLimit);
        using var otherFamily = await limiter.AcquireAsync(plain);

        Assert.False(otherFamily.IsAcquired,
            "The same anonymous client was granted a second concurrency budget by switching from the mapped to the plain IPv4 transport.");
    }

    [Fact]
    public async Task Distinct_ipv6_clients_do_not_share_one_concurrency_budget()
    {
        // Companion guard: the merge is scoped to one client's two spellings of the same
        // address, and never widens to unrelated v6 callers. A caller that cannot be
        // starved by a neighbour is the property the lane must not trade away.
        var limiter = CreateGlobalLimiter();
        var first = CreateAnonymousContext(IPAddress.Parse("2001:db8::1"), "conn-v6-first");
        var second = CreateAnonymousContext(IPAddress.Parse("2001:db8::2"), "conn-v6-second");
        var third = CreateAnonymousContext(IPAddress.Parse("2001:db8::3"), "conn-v6-third");

        Assert.NotEqual(
            PartitionKey($"concurrency:{GenerationPolicy}", first),
            PartitionKey($"concurrency:{GenerationPolicy}", second));
        Assert.NotEqual(
            PartitionKey($"concurrency:{GenerationPolicy}", first),
            PartitionKey($"concurrency:{GenerationPolicy}", third));

        using var held = await SaturateAsync(limiter, first, GenerationConcurrencyPermitLimit);
        using var neighbour = await limiter.AcquireAsync(second);

        Assert.True(neighbour.IsAcquired,
            "An unrelated IPv6 client inherited another client's exhausted concurrency budget.");
    }

    [Fact]
    public void A_transport_with_neither_peer_address_nor_connection_id_stays_in_the_anonymous_namespace()
    {
        // Terminal fallback branch: no peer address and no connection id must still be an
        // anonymous key, never the authenticated partition and never a raw identifier.
        var first = CreateAnonymousContext(peerAddress: null, connectionId: null);
        var second = CreateAnonymousContext(peerAddress: null, connectionId: "   ");

        var key = PartitionKey($"concurrency:{GenerationPolicy}", first);

        Assert.StartsWith($"concurrency:{GenerationPolicy}:anon:", key, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", key, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", key, StringComparison.Ordinal);

        // One stable budget for that transport shape, so the fallback is deterministic
        // rather than a fresh partition per request.
        Assert.Equal(key, PartitionKey($"concurrency:{GenerationPolicy}", second));

        // The fallback is not reachable from a connection id, so the two branches cannot
        // alias each other.
        var withConnectionId = CreateAnonymousContext(peerAddress: null, connectionId: "conn-distinct-id");
        Assert.NotEqual(key, PartitionKey($"concurrency:{GenerationPolicy}", withConnectionId));

        // And the connection-derived key does not retain the connection identifier.
        var connectionKey = PartitionKey($"concurrency:{GenerationPolicy}", withConnectionId);
        Assert.DoesNotContain("conn-distinct-id", connectionKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Peerless_fallback_clients_share_one_concurrency_budget_without_touching_addressed_clients()
    {
        // Documents the consequence of the literal fallback that the lane must keep
        // honest: callers with neither address nor connection id are one budget (by
        // design, and bounded), while callers that do have a peer address are untouched.
        var limiter = CreateGlobalLimiter();
        var peerless = CreateAnonymousContext(peerAddress: null, connectionId: null);
        var addressed = CreateAnonymousContext(IPAddress.Parse("198.51.100.240"), "conn-addressed-peer");

        using var held = await SaturateAsync(limiter, peerless, GenerationConcurrencyPermitLimit);
        using var overflow = await limiter.AcquireAsync(CreateAnonymousContext(peerAddress: null, connectionId: null));
        using var neighbour = await limiter.AcquireAsync(addressed);

        Assert.False(overflow.IsAcquired,
            "The peerless fallback partition stopped bounding concurrency for its own clients.");
        Assert.True(neighbour.IsAcquired,
            "A client with a peer address inherited the budget of peerless fallback traffic.");
    }
}
