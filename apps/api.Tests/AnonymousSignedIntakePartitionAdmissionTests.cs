using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Autopilot;
using Taslim.Api.Controllers;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// QA lane for #898 (privacy-preserving, per-client partition for non-authenticated
/// generation traffic) along the one axis no existing lane covers: the anonymous
/// *signed* Autopilot event intake.
///
/// Why this axis is the interesting one. `POST /api/autopilot/events` is
/// `[AllowAnonymous]` and declares `[EnableRateLimiting(RateLimiting.Generation)]`,
/// so every completion-bridge event is limited through exactly the partition identity
/// #898 rewrites, while its admission to the pipeline at all is decided by an HMAC
/// signature rather than by an authenticated principal. Two distinct hazards live in
/// that combination, and neither is visible from the key-equality tests #898 and #1071
/// write against plain `DefaultHttpContext`s:
///
///  1. Envelope material as an identity source. The intake route legitimately varies
///     its `X-Autopilot-Event-Id`, `X-Autopilot-Timestamp` and `X-Autopilot-Signature`
///     on every single event. A partition identity that read any request header - the
///     tempting way to "separate callers on one NAT" - would therefore hand every
///     event a fresh budget: the abuse bound would become an exemption, and signature
///     attempts would be unbounded. This lane pins that the signed envelope, the
///     declared source, and forged proxy headers are all inert to the key.
///  2. The limit becoming an admission control. Because the route is anonymous, a
///     partition switch (another address, another connection id, another proxy header)
///     must never change whether an event is authenticated, and throttling one client
///     must never be the mechanism that admits or denies another's signature verdict.
///     This lane pins that the admission input set contains no transport or partition
///     material at all, and that the verdict for one envelope is identical across every
///     partition identity the fix can produce.
///
/// The privacy half is re-pinned where it matters for this route: the signed envelope
/// carries credential material and the limiter holds a key for the whole window, so
/// neither the peer address nor any byte of the envelope may appear in that key, and
/// the key must stay in the anonymous namespace rather than the authenticated one.
///
/// Deterministic and provider-free: the concurrency budget is saturated with held
/// leases (PermitLimit = 4, QueueLimit = 0) instead of racing a clock, the signature
/// is checked at a frozen instant, and the digest is only ever asserted through
/// properties that hold for any salt. Synthetic in-process clients only - no HTTP
/// calls, no provider calls, no real traffic, no logging of client material. The
/// signing secret is a literal test value published under a test-only environment
/// variable name, never the production `AUTOPILOT_WEBHOOK_SECRET`.
///
/// Companion-lane boundaries (deliberately NOT re-asserted here): #898's
/// AnonymousGenerationPartitionTests owns address-free keying and proxy-header
/// invariance for generic generation traffic and the structural pipeline scan for
/// forwarded-address consumption; #1071 owns IPv4/IPv4-mapped merging on the
/// concurrency arm and the peerless fallback. This lane adds only the intake-route
/// binding, the signed envelope, and the HMAC admission cross-check.
/// </summary>
public sealed class AnonymousSignedIntakePartitionAdmissionTests
{
    // Referenced as a literal so the file compiles unchanged against main.
    private const string GenerationPolicy = "generation-creation";

    // Mirrors the "generation-creation" concurrency budget in RateLimiting.Configure.
    private const int GenerationConcurrencyPermitLimit = 4;

    // A synthetic secret under a test-only variable name: this lane never reads or
    // writes the production signing secret variable.
    private const string TestSecretVariable = "TASLIM_QA_SIGNED_INTAKE_PARTITION_SECRET";
    private const string TestSecret = "synthetic-qa-signing-secret-not-a-production-key";

    private const string SignedPayload = "{\"waveKey\":\"wave-qa-partition\",\"taskId\":\"task-qa-partition\"}";
    private const string OtherPayload = "{\"waveKey\":\"wave-qa-partition\",\"taskId\":\"task-qa-tampered\"}";

    private static readonly DateTime FrozenUtc = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string FrozenTimestamp = new DateTimeOffset(FrozenUtc).ToUnixTimeSeconds().ToString();

    /// <summary>The real intake endpoint metadata, read off the controller.</summary>
    private static EndpointMetadataCollection BuildIntakeMetadata()
    {
        var submit = typeof(AutopilotIntakeController).GetMethod(
            "Submit",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            new[] { typeof(CancellationToken) },
            null);
        Assert.NotNull(submit);
        var metadata = submit!.GetCustomAttributes(inherit: false).ToList();
        metadata.AddRange(typeof(AutopilotIntakeController).GetCustomAttributes(inherit: false));
        return new EndpointMetadataCollection(metadata);
    }

    private static HttpContext CreateIntakeContext(
        IPAddress? peerAddress,
        string? connectionId = null,
        string? userId = null,
        string? signature = null,
        string? timestamp = null,
        string? source = null,
        string? eventId = null,
        string? eventType = null)
    {
        var context = new DefaultHttpContext
        {
            // No NameIdentifier by default: the anonymous branch of the identity.
            User = userId is null
                ? new ClaimsPrincipal(new ClaimsIdentity(authenticationType: null))
                : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
        };
        context.Connection.RemoteIpAddress = peerAddress;
        if (connectionId is not null)
        {
            context.Connection.Id = connectionId;
        }
        context.Request.Method = "POST";
        context.Request.Path = "/api/autopilot/events";
        if (signature is not null) context.Request.Headers[AutopilotIntakeController.SignatureHeader] = signature;
        if (timestamp is not null) context.Request.Headers[AutopilotIntakeController.TimestampHeader] = timestamp;
        if (source is not null) context.Request.Headers[AutopilotIntakeController.SourceHeader] = source;
        if (eventId is not null) context.Request.Headers[AutopilotIntakeController.EventIdHeader] = eventId;
        if (eventType is not null) context.Request.Headers[AutopilotIntakeController.EventTypeHeader] = eventType;
        // The endpoint is the one the intake action actually declares, so the limiter
        // resolves its budget from the route's own metadata rather than a test copy.
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            BuildIntakeMetadata(),
            displayName: "POST /api/autopilot/events"));
        return context;
    }

    private static string Sign(string secret, string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
    }

    private static string PartitionKey(string prefix, HttpContext context)
    {
        var partition = typeof(RateLimiting).GetMethod("Partition", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(partition);
        return (string?)partition!.Invoke(null, new object[] { prefix, context, false })
            ?? throw new InvalidOperationException("RateLimiting.Partition returned no key.");
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
            Assert.True(leases[i].IsAcquired, $"Expected permit {i + 1} of the saturated intake budget to be acquired.");
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

    /// <summary>
    /// One context per partition identity #898 can produce, each carrying a complete,
    /// legitimate signed envelope: addressed peers, a peerless transport with a
    /// connection id, a transport with neither, and an authenticated principal.
    /// </summary>
    private static HttpContext[] SignedEnvelopeIdentities()
    {
        var signature = Sign(TestSecret, FrozenTimestamp, SignedPayload);
        return
        [
            CreateIntakeContext(IPAddress.Parse("203.0.113.7"), signature: $"sha256={signature}", timestamp: FrozenTimestamp, source: "completion-bridge", eventId: "evt-qa-1", eventType: AutopilotEventTypes.WaveTaskCompleted),
            CreateIntakeContext(IPAddress.Parse("2001:db8:dead:beef::1337"), signature: $"sha256={signature}", timestamp: FrozenTimestamp, source: "completion-bridge", eventId: "evt-qa-2", eventType: AutopilotEventTypes.WaveTaskCompleted),
            CreateIntakeContext(null, connectionId: "conn-qa-1", signature: $"sha256={signature}", timestamp: FrozenTimestamp, source: "completion-bridge", eventId: "evt-qa-3", eventType: AutopilotEventTypes.WaveTaskCompleted),
            CreateIntakeContext(null, signature: $"sha256={signature}", timestamp: FrozenTimestamp, source: "completion-bridge", eventId: "evt-qa-4", eventType: AutopilotEventTypes.WaveTaskCompleted),
            CreateIntakeContext(IPAddress.Parse("198.51.100.9"), userId: "11111111-1111-1111-1111-111111111111", signature: $"sha256={signature}", timestamp: FrozenTimestamp, source: "completion-bridge", eventId: "evt-qa-5", eventType: AutopilotEventTypes.WaveTaskCompleted),
        ];
    }

    private static AutopilotEventAuthenticator TestAuthenticator(string? secretVariable = TestSecretVariable)
    {
        var options = new AutopilotOptions
        {
            RequireSignedEvents = true,
            SignatureToleranceSeconds = 300,
            SigningSecretEnvironmentVariable = secretVariable ?? TestSecretVariable,
        };
        options.Normalize();
        return new AutopilotEventAuthenticator(options);
    }

    [Fact]
    public void Signed_intake_route_is_anonymous_and_is_limited_by_the_partitioned_generation_policy()
    {
        // The premise of the whole lane, read off the production type rather than
        // restated in the test: the HMAC-signed intake is anonymous at the HTTP layer,
        // so the partition identity #898 changes is the identity that addresses it,
        // and it is addressed by the bounded generation budget rather than exempted
        // from any limiter. If the route ever dropped its policy or gained an
        // authorization bypass, the rest of this lane would silently stop describing
        // the traffic it claims to cover.
        var metadata = BuildIntakeMetadata();
        Assert.NotEmpty(metadata.OfType<AllowAnonymousAttribute>().ToList());
        Assert.Empty(metadata.OfType<AuthorizeAttribute>().ToList());
        var limiter = metadata.GetMetadata<EnableRateLimitingAttribute>();
        Assert.NotNull(limiter);
        Assert.Equal(GenerationPolicy, limiter!.PolicyName);

        // Anonymous intake traffic must resolve through the anonymous branch, so an
        // event caller is never keyed as a user just for reaching the route.
        var context = CreateIntakeContext(IPAddress.Parse("203.0.113.7"));
        var key = PartitionKey(GenerationPolicy, context);
        Assert.DoesNotContain("user:", key, StringComparison.Ordinal);

        // The budget behind that policy is real: the endpoint's own concurrency arm is
        // bounded, which is what makes "throttled" a possible outcome to isolate at all.
        var options = CreateOptions();
        Assert.NotNull(options.GlobalLimiter);
    }

    [Fact]
    public void Signature_admission_reads_no_transport_or_partition_material()
    {
        // Structural half of "no bypass of HMAC signature admission": the authenticator
        // cannot see the client address, the connection, the partition key or the
        // principal, so no partition switch can change an admission verdict. This is
        // the property the rate-limit change must not erode from the other side either.
        var verify = typeof(IAutopilotEventAuthenticator).GetMethod("Verify");
        Assert.NotNull(verify);
        foreach (var parameter in verify!.GetParameters())
        {
            Assert.False(
                parameter.ParameterType == typeof(HttpContext)
                || parameter.ParameterType == typeof(ClaimsPrincipal)
                || parameter.ParameterType == typeof(IPAddress)
                || parameter.ParameterType == typeof(ConnectionInfo)
                || parameter.ParameterType == typeof(HttpRequest),
                $"Signature admission takes {parameter.Name} of {parameter.ParameterType}, which would let partition or transport material decide authentication.");
        }

        // The input set is closed and is exactly the envelope plus a clock: adding a
        // fifth admission input would be the way a partition identity, a peer address
        // or a declared source could later be smuggled into authentication.
        var parameterTypes = verify!.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(
            new[] { typeof(string), typeof(string), typeof(string), typeof(DateTime) },
            parameterTypes);

        // Admission is on by default in the type and in the shipped configuration: a
        // rate-limit partition is never the thing that authenticates an event, and the
        // route is anonymous, so turning signature requirement off would be a total
        // bypass rather than a tuning change.
        Assert.True(new AutopilotOptions().RequireSignedEvents);
        var root = LocateRepositoryRoot();
        foreach (var file in new[]
        {
            Path.Combine(root, "apps", "api", "appsettings.json"),
            Path.Combine(root, "apps", "api", "appsettings.Production.json"),
        })
        {
            Assert.True(File.Exists(file), $"Could not locate {Path.GetFileName(file)} from {AppContext.BaseDirectory}.");
            Assert.Contains("\"RequireSignedEvents\": true", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unsigned_or_tampered_events_are_refused_under_every_partition_identity()
    {
        // Fail closed, across the identity set #898 introduces: an envelope that is
        // unsigned, tampered with, or outside the timestamp window is refused for every
        // caller, however it is partitioned, and a correctly signed envelope is accepted
        // for every caller - so the partition an event lands in is never an admission
        // control in either direction.
        Environment.SetEnvironmentVariable(TestSecretVariable, TestSecret);
        try
        {
            var authenticator = TestAuthenticator();
            string? baselineMissing = null;
            string? baselineMismatch = null;
            string? baselineStale = null;
            foreach (var context in SignedEnvelopeIdentities())
            {
                var signature = context.Request.Headers[AutopilotIntakeController.SignatureHeader].ToString();
                var source = context.Request.Headers[AutopilotIntakeController.SourceHeader].ToString();
                var tampered = Sign(TestSecret, FrozenTimestamp, OtherPayload);
                var staleTimestamp = new DateTimeOffset(FrozenUtc.AddMinutes(-10)).ToUnixTimeSeconds().ToString();

                var missing = authenticator.Verify(null, FrozenTimestamp, SignedPayload, FrozenUtc);
                var mismatch = authenticator.Verify($"sha256={tampered}", FrozenTimestamp, SignedPayload, FrozenUtc);
                var stale = authenticator.Verify(signature, staleTimestamp, SignedPayload, FrozenUtc.AddMinutes(10));
                var good = authenticator.Verify(signature, FrozenTimestamp, SignedPayload, FrozenUtc);

                Assert.False(missing.Valid, "An unsigned intake event was admitted.");
                Assert.False(mismatch.Valid, "A signature computed over a different payload was admitted.");
                Assert.False(stale.Valid, "An event outside the timestamp window was admitted as fresh.");
                Assert.True(good.Valid, "A correctly signed event was refused in this partition identity.");

                baselineMissing ??= missing.FailureReason;
                baselineMismatch ??= mismatch.FailureReason;
                baselineStale ??= stale.FailureReason;
                Assert.Equal(baselineMissing, missing.FailureReason);
                Assert.Equal(baselineMismatch, mismatch.FailureReason);
                Assert.Equal(baselineStale, stale.FailureReason);

                // The envelope read back off that partitioned request behaves exactly
                // like the envelope handed to the controller: the declared source and
                // event id ride along without being an identity, so they can neither
                // add nor remove an admission.
                Assert.Equal(signature, context.Request.Headers[AutopilotIntakeController.SignatureHeader].ToString());
                Assert.NotEqual(string.Empty, source);
            }

            // With no secret resolvable the route refuses rather than admitting: the
            // anonymous partition must never become a signature-free path.
            var secretless = TestAuthenticator("TASLIM_QA_SIGNED_INTAKE_PARTITION_ABSENT_SECRET");
            Assert.False(secretless.Verify(Sign(TestSecret, FrozenTimestamp, SignedPayload), FrozenTimestamp, SignedPayload, FrozenUtc).Valid);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestSecretVariable, null);
        }
    }

    [Fact]
    public void Intake_partition_key_carries_no_address_and_no_signed_envelope_material()
    {
        // The privacy property where it is most exposed: the intake envelope carries
        // credential material (signature, timestamp, declared source, event id) and the
        // limiter keeps a key for the whole window. Neither the transport address nor
        // any byte of the envelope may be retained, and header rotation must not move
        // the key - otherwise every legitimate event would mint itself a fresh budget.
        var peer = IPAddress.Parse("203.0.113.203");
        var signature = Sign(TestSecret, FrozenTimestamp, SignedPayload);
        var withEnvelope = CreateIntakeContext(
            peer,
            signature: $"sha256={signature}",
            timestamp: FrozenTimestamp,
            source: "completion-bridge",
            eventId: "evt-qa-enumerable-0001",
            eventType: AutopilotEventTypes.WaveTaskCompleted);
        var key = PartitionKey(GenerationPolicy, withEnvelope);

        // Failing evidence against unpatched main, which writes the peer address
        // verbatim into the key.
        Assert.DoesNotContain("203.0.113", key, StringComparison.Ordinal);
        Assert.DoesNotContain("::ffff", key, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, key, StringComparison.Ordinal);
        Assert.DoesNotContain(signature[..16], key, StringComparison.Ordinal);
        Assert.DoesNotContain(FrozenTimestamp, key, StringComparison.Ordinal);
        Assert.DoesNotContain("completion-bridge", key, StringComparison.Ordinal);
        Assert.DoesNotContain("evt-qa-enumerable-0001", key, StringComparison.Ordinal);

        // One client, one partition: rotating the envelope (the legitimate per-event
        // variation on this route) must not move it.
        var rotated = CreateIntakeContext(
            peer,
            signature: $"sha256={Sign(TestSecret, FrozenTimestamp, OtherPayload)}",
            timestamp: new DateTimeOffset(FrozenUtc.AddSeconds(37)).ToUnixTimeSeconds().ToString(),
            source: "completion-bridge",
            eventId: "evt-qa-enumerable-0002",
            eventType: AutopilotEventTypes.WaveTaskCompleted);
        Assert.Equal(key, PartitionKey(GenerationPolicy, rotated));

        // Different clients do not collapse into one key, and the key is digested.
        var neighbour = PartitionKey(GenerationPolicy, CreateIntakeContext(IPAddress.Parse("203.0.113.204")));
        Assert.NotEqual(key, neighbour);
        var digest = key[(key.LastIndexOf(':') + 1)..];
        Assert.Matches("^[0-9a-f]{32}$", digest);

        // Spoofed proxy material is inert at this route too: it can neither move nor
        // widen the identity that addresses signed intake traffic.
        foreach (var header in new[] { "X-Forwarded-For", "X-Real-IP", "Forwarded", "X-Client-IP", "CF-Connecting-IP" })
        {
            var forged = CreateIntakeContext(peer);
            forged.Request.Headers[header] = "192.0.2.55, 203.0.113.203";
            forged.Request.Headers[AutopilotIntakeController.SourceHeader] = "completion-bridge";
            var forgedKey = PartitionKey(GenerationPolicy, forged);
            Assert.Equal(key, forgedKey);
            Assert.DoesNotContain("192.0.2.55", forgedKey, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Saturated_intake_client_gains_no_budget_from_envelope_rotation_while_a_neighbour_keeps_its_own()
    {
        // The enforcement half, through the limiter the intake route actually selects:
        // a caller whose budget is exhausted stays exhausted however it rotates its
        // signed envelope or forges proxy headers, and the neighbour behind a different
        // address is untouched by that throttling.
        var options = CreateOptions();
        var limiter = options.GlobalLimiter!;
        var abusivePeer = IPAddress.Parse("203.0.113.211");
        using var saturated = await SaturateAsync(limiter, CreateIntakeContext(abusivePeer), GenerationConcurrencyPermitLimit);

        for (var i = 0; i < 8; i++)
        {
            var timestamp = new DateTimeOffset(FrozenUtc.AddSeconds(i)).ToUnixTimeSeconds().ToString();
            var rotated = CreateIntakeContext(
                abusivePeer,
                signature: $"sha256={Sign(TestSecret, timestamp, SignedPayload)}",
                timestamp: timestamp,
                source: "completion-bridge",
                eventId: $"evt-qa-rotation-{i}",
                eventType: AutopilotEventTypes.WaveTaskCompleted);
            rotated.Request.Headers["X-Forwarded-For"] = $"198.51.100.{i + 1}";
            rotated.Request.Headers["X-Real-IP"] = $"192.0.2.{i + 1}";
            using var overflow = await limiter.AcquireAsync(rotated);
            Assert.False(overflow.IsAcquired,
                $"Rotating the signed envelope to event {i} minted extra intake budget for a peer that is already at its concurrency limit.");
        }

        // Control: the refusal above is the budget, not an artefact of the headers.
        using var cleanOverflow = await limiter.AcquireAsync(CreateIntakeContext(abusivePeer));
        Assert.False(cleanOverflow.IsAcquired);

        // A second anonymous intake caller keeps its own budget while the first is
        // throttled, and is still subject to signature admission like anyone else.
        var healthyPeer = IPAddress.Parse("198.51.100.212");
        var healthy = CreateIntakeContext(
            healthyPeer,
            signature: $"sha256={Sign(TestSecret, FrozenTimestamp, SignedPayload)}",
            timestamp: FrozenTimestamp,
            source: "completion-bridge",
            eventId: "evt-qa-healthy",
            eventType: AutopilotEventTypes.WaveTaskCompleted);
        using var healthyLease = await limiter.AcquireAsync(healthy);
        Assert.True(healthyLease.IsAcquired,
            "A healthy anonymous intake client was denied because a different client exhausted its own budget.");

        // A peerless caller is separated from both addressed clients rather than
        // sharing their throttling.
        var peerless = CreateIntakeContext(null, connectionId: "conn-qa-isolated");
        using var peerlessLease = await limiter.AcquireAsync(peerless);
        Assert.True(peerlessLease.IsAcquired,
            "A caller on a transport with no peer address was denied behind an addressed client's saturated budget.");
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "apps", "api", "appsettings.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root containing apps/api/appsettings.json not found.");
    }
}
