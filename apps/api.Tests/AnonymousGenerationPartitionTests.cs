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
/// Forwarded client-address headers are consumed nowhere in this API, so rotating
/// them cannot move a budget either - pinned as an abuse-resistance contract.
/// The later tests extend that proof from key equality to the enforced limiter
/// itself (a saturated peer gains no extra permit from any forged header), to the
/// peerless-transport branch (headers are inert there too, and no client-supplied
/// input can alias or squat the fallback partition), and to a structural pin on
/// the pipeline: the API consumes no forwarded client address anywhere, so wiring
/// one in later trips this suite before it can silently become an exemption.
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
    public void Rotated_forwarded_ip_headers_cannot_move_or_widen_the_budget()
    {
        // Abuse resistance: this API consumes no forwarded client-address header
        // (docs/AUTHENTICATION.md), so a budget follows the transport peer alone. A
        // caller that rotates X-Forwarded-For, X-Real-IP or Forwarded per request may
        // therefore neither mint a fresh budget (stability under header churn) nor
        // reach another client's budget (no header material in the key), and identical
        // spoofed headers must not make two distinct peers collide. This is a contract
        // guard rather than a fix-dependent assertion: it holds on main unchanged, and
        // keeps holding if anyone later wires ForwardedHeaders middleware in without
        // re-reviewing the keying - at which point a rotated header would move the
        // budget and this test fails before that becomes a live exemption.
        var peer = IPAddress.Parse("203.0.113.5");
        var baseline = PartitionKey("generation", CreateAnonymousContext(GenerationPolicy, peer));

        var rotations = new (string Name, string Value)[]
        {
            ("X-Forwarded-For", "198.51.100.1"),
            ("X-Forwarded-For", "198.51.100.1, 203.0.113.100"),
            ("X-Real-IP", "198.51.100.2"),
            ("Forwarded", "for=198.51.100.3;by=203.0.113.5"),
        };

        foreach (var (name, value) in rotations)
        {
            var context = CreateAnonymousContext(GenerationPolicy, peer);
            context.Request.Headers[name] = value;
            var key = PartitionKey("generation", context);

            // Same transport peer under any header rotation: one budget, unmoved.
            Assert.Equal(baseline, key);
            // No header material reaches the key, so it cannot alias another client.
            Assert.DoesNotContain("198.51.100", key, StringComparison.Ordinal);
        }

        // Two distinct peers carrying identical spoofed headers stay distinct clients.
        var otherPeer = CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("198.51.100.9"));
        var spoofedPeer = CreateAnonymousContext(GenerationPolicy, peer);
        otherPeer.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        spoofedPeer.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        Assert.NotEqual(PartitionKey("generation", otherPeer), PartitionKey("generation", spoofedPeer));

        // The concurrency arm (generation-creation limiter) keys off the same
        // transport-derived identity, so header churn moves that budget neither.
        var plainPeer = CreateAnonymousContext(GenerationPolicy, peer);
        var churned = CreateAnonymousContext(GenerationPolicy, peer);
        churned.Request.Headers["X-Forwarded-For"] = "192.0.2.200";
        Assert.Equal(
            PartitionKey($"concurrency:{GenerationPolicy}", plainPeer),
            PartitionKey($"concurrency:{GenerationPolicy}", churned));
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

    [Fact]
    public async Task Rotated_spoofed_headers_on_the_real_peer_cannot_add_budget_to_the_enforced_limiter()
    {
        // End-to-end abuse resistance through the enforced limiter, not just key
        // equality: with the transport peer's generation budget already saturated,
        // every follow-up request is refused however its proxy-style headers are
        // forged. A single peer also cannot exceed the budget with clean headers, so
        // the refusal cannot be waved through as "extra spoofed requests" either.
        var peer = IPAddress.Loopback;
        var options = CreateOptions();
        var limiter = options.GlobalLimiter!;

        using var leases = await SaturateAsync(limiter, CreateAnonymousContext(GenerationPolicy, peer), GenerationConcurrencyPermitLimit);

        string[] spoofs =
        [
            "2001:db8:cafe::1337",
            "203.0.113.7, 198.51.100.1, 192.0.2.9",
            "for=\"[2001:db8::1]:8080\";by=internal",
        ];

        foreach (var spoof in spoofs)
        {
            foreach (var (name, value) in new[]
            {
                ("X-Forwarded-For", spoof),
                ("X-Real-IP", spoof),
                ("Forwarded", $"for={spoof}"),
                ("CF-Connecting-IP", spoof),
                ("True-Client-IP", spoof),
                ("X-Client-IP", spoof),
            })
            {
                var context = CreateAnonymousContext(GenerationPolicy, peer);
                context.Request.Headers[name] = value;
                using var overflow = await limiter.AcquireAsync(context);
                Assert.False(overflow.IsAcquired,
                    $"'{name}: {spoof}' minted extra budget for a peer whose concurrency limit is already exhausted.");
            }
        }

        // Control: the saturated peer's next clean request is refused too, so the
        // rejections above reflect the budget, not an artefact of the spoof headers.
        using var cleanOverflow = await limiter.AcquireAsync(CreateAnonymousContext(GenerationPolicy, peer));
        Assert.False(cleanOverflow.IsAcquired);
    }

    [Fact]
    public void Header_rotations_cannot_move_the_budget_of_a_peerless_transport_either()
    {
        // Rotated_forwarded_ip_headers... pins the address-derived branch. The
        // fallback branch - transports that expose no peer address - is keyed on the
        // server-assigned connection id and needs the same abuse-resistance proof:
        // client headers must be inert there as well, or the branch would hand out a
        // header-named budget to whoever forges it.
        var baseline = PartitionKey("generation", CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "conn-privacy-1"));

        var context = CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "conn-privacy-1");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        context.Request.Headers["X-Real-IP"] = "198.51.100.1";

        Assert.Equal(baseline, PartitionKey("generation", context));
        Assert.DoesNotContain("203.0.113", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Caller_supplied_inputs_never_alias_the_peerless_fallback_partition()
    {
        // The fallback branch must be reachable only by callers the server itself
        // could not separate (no peer address AND no connection id). If any
        // client-controllable input - header name casing, header value, path, query -
        // could reproduce that literal key, every caller reaching the socket-style
        // transport from the network would collapse into, or squat on, the one
        // shared budget instead of staying per-connection.
        const string PeerlessKey = "generation:anon:peerless";
        var peerless = PartitionKey("generation", CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: null));
        Assert.Equal(PeerlessKey, peerless);

        // Whitespace-only connection ids are treated as absent by the identity
        // function, so those callers share the one fallback budget. The abuse-bound
        // claim this relies on is that a real server transport always carries a
        // server-generated id; nothing client-supplied can clear it.
        Assert.Equal(PeerlessKey, PartitionKey("generation", CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "   ")));

        foreach (var spoof in new[] { "peerless", "conn-peerless", "203.0.113.7", "*:*" })
        {
            foreach (var name in new[] { "X-Forwarded-For", "X-ReAl-Ip", "x-real-ip", "Forwarded", "X-Client-IP", "True-Client-IP", "CF-Connecting-IP" })
            {
                var context = CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: "conn-distinct-42");
                context.Request.Headers[name] = spoof;
                context.Request.Path = "/api/generation/jobs";
                context.Request.QueryString = new QueryString($"?{Uri.EscapeDataString(name)}={Uri.EscapeDataString(spoof)}");
                Assert.NotEqual(PeerlessKey, PartitionKey("generation", context));
            }
        }
    }

    [Fact]
    public void Connection_digest_partition_keys_are_unguessable_and_cannot_be_squatted()
    {
        // Per-connection budgets only hold if an attacker cannot pre-create (squat)
        // another caller's key: the key embeds an HMAC of the server-assigned id
        // under a process-private salt, is fixed-length lowercase hex, and never
        // contains the id it came from - so neither the ids of neighbour connections
        // nor their digests are predictable from the outside.
        const string ConnectionId = "0HNBBT3QGG7V1:00000001";
        var key = PartitionKey("generation", CreateAnonymousContext(GenerationPolicy, peerAddress: null, connectionId: ConnectionId));

        var digest = key["generation:anon:conn-".Length..];
        Assert.Equal(32, digest.Length);
        Assert.Matches("^[0-9a-f]{32}$", digest);
        Assert.DoesNotContain(ConnectionId, key, StringComparison.OrdinalIgnoreCase);

        // The address-derived key has the same shape, so raw material is neither
        // present nor length-predictable there either.
        var addressKey = PartitionKey("generation", CreateAnonymousContext(GenerationPolicy, IPAddress.Parse("203.0.113.7")));
        var addressDigest = addressKey["generation:anon:".Length..];
        Assert.Equal(32, addressDigest.Length);
        Assert.Matches("^[0-9a-f]{32}$", addressDigest);
    }

    [Fact]
    public void Api_pipeline_consumes_no_forwarded_client_address_anywhere()
    {
        // The structural half of the "untrusted proxy headers cannot bypass the
        // limits" proof: the keying tests above show the partition identity is
        // header-inert today, and this pins the pipeline contract that nothing ever
        // feeds a forwarded address into that identity. The middleware is bound to
        // the scheme alone (Program.cs: ForwardedHeaders = XForwardedProto,
        // ForwardLimit = 1), so RemoteIpAddress - the only transport material the
        // key reads - is never rewritten from forwarded-IP headers, and no
        // application source references those header names or the forwarded-IP
        // enum at all. Adding forwarded-IP consumption anywhere trips this test and
        // forces the partition keying to be re-reviewed together with it.
        var sourceRoot = LocateApiSourceRoot();
        var apiSource = Path.Combine(sourceRoot, "apps", "api");
        var program = Path.Combine(apiSource, "Program.cs");
        Assert.True(File.Exists(program), $"Could not locate the API source tree from {AppContext.BaseDirectory}.");
        var programText = File.ReadAllText(program);
        const string ForwardedIpEnum = "XForwarded" + "For";
        Assert.Contains("options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;", programText, StringComparison.Ordinal);
        Assert.DoesNotContain(ForwardedIpEnum, programText, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(apiSource, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(ForwardedIpEnum, text, StringComparison.Ordinal);
            foreach (var header in new[] { "X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP", "True-Client-IP", "True-ClientIp", "X-Client-IP" })
            {
                Assert.True(!text.Contains(header, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(file)} references '{header}'; forwarded client addresses must not be consumed without re-reviewing the rate-limit partition keying.");
            }
        }
    }

    private static string LocateApiSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "apps", "api", "Program.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing apps/api/Program.cs not found.");
    }
}
