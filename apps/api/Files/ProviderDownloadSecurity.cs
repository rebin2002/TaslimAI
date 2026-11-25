using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Taslim.Api.Files;

public interface IProviderUrlPolicy
{
    Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a hostname to the addresses the operating system would connect to. Extracted as an
/// interface so endpoint admission can be tested without depending on live DNS: the production
/// implementation is <see cref="SystemProviderHostResolver"/>, and tests inject a fixed table.
/// Mirrors <c>IAcademicHostResolver</c> from the landed academic hardening (#1271) so the
/// repository has one resolver seam pattern rather than two.
/// </summary>
public interface IProviderHostResolver
{
    /// <summary>
    /// All addresses for <paramref name="host"/>. An empty result means "nothing to connect to"
    /// and is treated as a rejection, never as an approval.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Resolver backed by the system resolver, used by the composed HTTP handler.</summary>
public sealed class SystemProviderHostResolver : IProviderHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses;
    }
}

public sealed class ProviderUrlPolicy : IProviderUrlPolicy
{
    private readonly IProviderHostResolver hostResolver;

    /// <param name="hostResolver">
    /// Injection seam for tests. Production composition registers
    /// <see cref="SystemProviderHostResolver"/>, so there is deliberately no hostname that skips
    /// resolution: an unconditional test-name bypass would let a private DNS answer for a
    /// reserved name decide the fetch target in production.
    /// </param>
    public ProviderUrlPolicy(IProviderHostResolver? hostResolver = null)
        => this.hostResolver = hostResolver ?? new SystemProviderHostResolver();

    public async Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Port is not (-1 or 443)
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidDataException("Provider download URL is not safe.");

        // Admission and dialing share one decision function (ProviderPinnedConnections), so a URL
        // cannot be judged safe at check time and then connected somewhere else at use time.
        // IP literals are classified directly with zero resolver calls; hostnames are resolved and
        // refused when any answer sits in non-public space.
        await ProviderPinnedConnections.SelectPinnedAddressAsync(uri.DnsSafeHost, hostResolver, cancellationToken)
            .ConfigureAwait(false);
    }

    // True when the address must never be a fetch target. Semantically aligned with the
    // independently reviewed, deployed academic guard (AcademicSourceUrlGuard.IsPubliclyRoutable
    // as landed by PR #1273): special-use IPv4, the ::/96 and ::ffff:0:0/96 embedded-IPv4
    // reductions, the well-known NAT64 prefix 64:ff9b::/96 (RFC 6052) reduced the same way so
    // DNS64 answers for public IPv4 stay fetchable while embedded private/metadata space stays
    // refused, the local-use IPv4-translatable prefix 64:ff9b:1::/48 (RFC 8215) refused outright,
    // the IETF Protocol Assignments block 2001::/23, the current documentation prefix 3fff::/20
    // (RFC 9637), the SRv6 SID space 5f00::/16 (RFC 9602), unique-local/link-local/site-local/
    // multicast/documentation/discard/6to4/Teredo/6bone IPv6, and a fail-closed family branch.
    // The reduction never swaps the dialed destination: callers pin the ORIGINAL validated
    // address (the v6 literal or resolver answer), the embedded IPv4 only decides admission.
    internal static bool IsBlocked(IPAddress address)
    {
        // Fail closed: only a plain v4 or v6 endpoint can ever be a fetch target.
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return true;

        // ::ffff:0:0/96 (mapped) and the deprecated ::/96 (compatible) literals both embed an
        // IPv4 destination. Reduce to the v4 rules so an embedded address cannot inherit the v6
        // checks below: without this, [::7f00:1] or [::a9fe:a9fe] is neither link-local nor
        // unique-local and would be treated as a public target. :: and ::1 reduce to 0.0.0.0 and
        // 0.0.0.1, which the v4 rules refuse as unusable.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        else
        {
            var candidate = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && HasEmbeddedIPv4(candidate))
                address = new IPAddress(new[] { candidate[12], candidate[13], candidate[14], candidate[15] });
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            var third = bytes[2];
            return first == 0
                || first == 10
                || first == 127
                || (first == 100 && second is >= 64 and <= 127)
                || (first == 169 && second == 254)
                || (first == 172 && second is >= 16 and <= 31)
                || (first == 192 && second == 0 && third == 0)
                || (first == 192 && second == 0 && third == 2)
                || (first == 192 && second == 88 && third == 99)
                || (first == 192 && second == 168)
                || (first == 198 && second is >= 18 and <= 19)
                || (first == 198 && second == 51 && third == 100)
                || (first == 203 && second == 0 && third == 113)
                || first >= 224;
        }

        // IPv6 unique-local (fc00::/7), link-local/site-local (fe80::/10 and
        // fec0::/10), multicast (ff00::/8), documentation (2001:db8::/32), discard-only
        // (100::/64), 6to4 (2002::/16), Teredo (2001::/32) and legacy 6bone (3ffe::/16) are
        // not fetch targets. 6to4 carries an attacker-chosen v4 destination in bytes 2-5 and
        // is refused outright rather than reduced to whatever that embedded v4 looks like.
        // NAT64 (aligned with #1273): the local-use prefix names only whatever the LOCAL
        // domain's own translator resolves, so it is refused outright like 6to4. The
        // well-known /96 is the DNS64 spelling of an IPv4 destination on IPv6-only networks:
        // refusing it wholesale would break legitimate provider lookups, so admission
        // inherits the IPv4 rules on the embedded last 32 bits (64:ff9b::a9fe:a9fe is the
        // metadata address and stays refused; 64:ff9b::808:808 is public 8.8.8.8 and stays
        // allowed). Socket pinning is unaffected — the original v6 address is what is dialed.
        if (IsNat64LocalUse(bytes)) return true;
        if (IsNat64WellKnown(bytes))
            return IsBlocked(new IPAddress(new[] { bytes[12], bytes[13], bytes[14], bytes[15] }));
        return (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0)
            || (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00)
            || (bytes[0] == 0x20 && bytes[1] == 0x02)
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01)
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            || (bytes[0] == 0x3F && bytes[1] == 0xFE)
            || (bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0)
            || (bytes[0] == 0x5F && bytes[1] == 0x00)
            || bytes[0] >= 0xFC;
    }

    // True for an address in ::/96, the deprecated IPv4-compatible form that carries a v4
    // destination in the low four bytes. Only called with a 16-byte v6 address.
    private static bool HasEmbeddedIPv4(byte[] bytes)
    {
        if (bytes.Length != 16) return false;
        for (var i = 0; i < 12; i++)
        {
            if (bytes[i] != 0) return false;
        }

        return true;
    }

    // True for the well-known NAT64 prefix 64:ff9b::/96 (RFC 6052): first 96 bits fixed,
    // remaining 32 bits carry the DNS64-synthesised IPv4 destination. Only called with a
    // 16-byte v6 address.
    private static bool IsNat64WellKnown(byte[] bytes)
    {
        if (bytes[0] != 0x00 || bytes[1] != 0x64 || bytes[2] != 0xff || bytes[3] != 0x9b) return false;
        for (var i = 4; i < 12; i++)
        {
            if (bytes[i] != 0) return false;
        }

        return true;
    }

    // True for 64:ff9b:1::/48, the local-use IPv4-translatable prefix (RFC 8215). Disjoint
    // from the well-known /96 above (its third group is 0001, where bytes 4..11 are zero).
    private static bool IsNat64LocalUse(byte[] bytes)
        => bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes[4] == 0x00 && bytes[5] == 0x01;
}

/// <summary>
/// Connection-layer SSRF boundary for generated-media provider downloads. The URL policy answers
/// "may this target be fetched"; this class answers "which address may the socket actually be
/// dialed at". One resolver call per connection, all-or-nothing address admission, and a pinned
/// <see cref="SocketsHttpHandler"/> mirror the landed AcademicSourceConnectionPolicy (#1271):
/// the hostname that was classified is the hostname that is connected, closing the DNS-rebinding
/// window where a first lookup returns a public address and the connect-time lookup returns
/// 127.0.0.1 or 169.254.169.254.
/// </summary>
public static class ProviderPinnedConnections
{
    /// <summary>
    /// Pool lifetime for pinned provider connections. Reuse is safe (a pooled entry can only
    /// exist for an endpoint that passed admission when it was created); the bound is here so a
    /// long-lived process re-runs the admission decision regularly rather than forever.
    /// </summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Chooses the single address a connection to <paramref name="host"/> may use. IP literals
    /// are classified directly (no resolver call, so bracketed forms such as <c>[::1]</c> reach
    /// the routability rules). Hostnames are resolved once and every returned address must be
    /// public: one non-public answer refuses the whole target rather than quietly dialing a
    /// sibling address, and an empty answer refuses. IPv4 is preferred when the provider is
    /// dual-stack so the pinned family stays stable for one operation.
    /// </summary>
    public static async Task<IPAddress> SelectPinnedAddressAsync(
        string? host,
        IProviderHostResolver resolver,
        CancellationToken cancellationToken)
    {
        var candidates = await SelectPinnedAddressesAsync(host, resolver, cancellationToken).ConfigureAwait(false);
        return candidates[0];
    }

    /// <summary>
    /// The full ordered candidate list one connection may try: every address must be public or
    /// the whole target is refused, IPv4 sorts first (dual-stack stability, mirroring #1271),
    /// and the remainder of the SAME single resolution answer follows so a provider CDN with
    /// several healthy A records keeps the platform's connect-time fallback without ever
    /// re-resolving or dialing an unvalidated address.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> SelectPinnedAddressesAsync(
        string? host,
        IProviderHostResolver resolver,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeHost(host);
        if (normalized.Length == 0) throw new InvalidDataException("Provider download URL is not safe.");

        if (IPAddress.TryParse(normalized, out var literal))
        {
            if (ProviderUrlPolicy.IsBlocked(literal)) throw new InvalidDataException("Provider download target is not public.");
            return new[] { literal };
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await resolver.ResolveAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new InvalidDataException("Provider download host could not be resolved safely.", exception);
        }

        if (addresses is null || addresses.Count == 0)
            throw new InvalidDataException("Provider download target is not public.");

        IPAddress? firstV4 = null;
        var validated = new List<IPAddress>(addresses.Count);
        foreach (var address in addresses)
        {
            // All-or-nothing: a mixed answer (public + rebinding candidate) refuses the target
            // entirely. This is the property an alternating resolver test asserts.
            if (address is null || ProviderUrlPolicy.IsBlocked(address))
                throw new InvalidDataException("Provider download target is not public.");
            if (firstV4 is null && address.AddressFamily == AddressFamily.InterNetwork) firstV4 = address;
            validated.Add(address);
        }

        if (firstV4 is null) return validated;
        var ordered = new List<IPAddress>(validated.Count) { firstV4 };
        foreach (var address in validated)
        {
            if (!address.Equals(firstV4)) ordered.Add(address);
        }

        return ordered;
    }

    /// <summary>
    /// Dials one connection: decide once, then hand the validated addresses to the transport in
    /// order until one connects. Fallback stays inside the single admitted answer (never a fresh
    /// resolution), so the DNS-rebinding closure is preserved while a dead sibling address no
    /// longer fails the whole fetch. Exposed with an injectable transport so tests can assert the
    /// dialed endpoints equal the validated endpoints without a real network.
    /// </summary>
    public static async Task<Stream> OpenConnectionAsync(
        string host,
        int port,
        IProviderHostResolver resolver,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> transport,
        CancellationToken cancellationToken)
    {
        var candidates = await SelectPinnedAddressesAsync(host, resolver, cancellationToken).ConfigureAwait(false);
        Exception? lastFailure = null;
        foreach (var address in candidates)
        {
            try
            {
                return await transport(address, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                // One validated address being down must not strand the connection while other
                // validated addresses from the same answer remain; cancellation still propagates.
                lastFailure = exception;
            }
        }

        throw lastFailure!;
    }

    /// <summary>
    /// Builds the primary handler for every policy-guarded provider client. The connect callback
    /// returns a plain, already-pinned stream; <see cref="SocketsHttpHandler"/> then performs TLS
    /// on top of it using the <em>original request hostname</em> for SNI and certificate-name
    /// validation, so pinning the IP does not weaken the identity check (same guarantee the
    /// landed academic handler documents). Automatic redirects stay off: a redirect destination
    /// is provider response data and is only safe once <see cref="IProviderUrlPolicy"/> has
    /// classified it, and each manual hop re-enters this callback. <see cref="HttpMessageHandler"/>
    /// proxying is disabled outright, because a proxy would resolve the hostname itself and dial
    /// wherever it likes — bypassing the pin — so no connection can leave through one.
    /// </summary>
    public static SocketsHttpHandler CreatePinnedHandler(IProviderHostResolver resolver)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = PooledConnectionLifetime,
        };
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            // No proxy hop is possible by construction: UseProxy=false above is the only knob the
            // pipeline has for proxying, SocketsHttpConnectionContext exposes no proxy state on
            // net8, and every connect therefore dials through the admission logic below. If this
            // handler is ever cloned with proxying re-enabled, the reviewed invariant breaks and
            // ProviderDownloadRedirectHardeningTests fails on UseProxy.
            return await OpenConnectionAsync(
                context.DnsEndPoint.Host,
                context.DnsEndPoint.Port,
                resolver,
                ConnectSocketAsync,
                cancellationToken).ConfigureAwait(false);
        };
        return handler;
    }

    /// <summary>
    /// Normalizes a connection hostname: lowercase, trailing dot removed, IPv6 brackets removed,
    /// and scoped link-local forms (<c>fe80::1%eth0</c>) rejected rather than silently dialled.
    /// </summary>
    public static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        var value = host.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var close = value.IndexOf(']');
            if (close < 0) return string.Empty;
            value = value[1..close];
        }

        if (value.Length == 0 || value.IndexOf('%') >= 0) return string.Empty;
        if (value[^1] == '.') value = value[..^1];
        return value.ToLowerInvariant();
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class ProviderDownloadSecurity(IProviderUrlPolicy urlPolicy)
{
    public const int MaxRedirects = 3;

    public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) =>
        urlPolicy.EnsureSafeAsync(uri, cancellationToken);
}

/// <summary>
/// Bounded, revalidated redirect follower for provider download clients whose fetch code has no
/// manual redirect loop (Manus Movie, Runway Movie, DirectVideo). The pinned primary handler runs
/// with <c>AllowAutoRedirect=false</c> — the only safe setting while every hop must pass endpoint
/// admission — which would otherwise turn legitimate CDN 301/302/303/307/308 output hops into hard
/// failures. This handler restores that compatibility while re-running the full URL policy (HTTPS,
/// port 443, no userinfo, public destination) against every <c>Location</c> before it is dialed,
/// so a redirect can never land on a private destination.
///
/// Semantics deliberately mirror the platform auto-redirect handler: relative targets resolve
/// against the hop URI, GET/HEAD methods are preserved end-to-end, and credential-like headers
/// (Authorization, Proxy-Authorization, Cookie,
/// and any header whose name contains key/token/secret/signature) are dropped when a hop changes
/// origin, so a cross-origin CDN never receives provider API credentials. A refused hop surfaces
/// as an <see cref="HttpRequestException"/> wrapping the policy's stable
/// <see cref="InvalidDataException"/> so existing provider failure mapping keeps classifying
/// failures unchanged; no URL material is echoed into messages. When the hop budget is exhausted
/// the final 3xx is returned to the caller rather than chased further, and a malformed
/// <c>Location</c> is likewise surfaced, never followed. Following is limited to bodyless GET/HEAD
/// artifact fetches: a 3xx on any mutating method (the paid generation POST submissions included,
/// which the create paths deliberately never retry) is surfaced to the provider unchanged — a
/// provider-supplied 307/308 must never resend a prompt/request body (another origin's public
/// endpoint could run a fresh billable generation), and even a same-origin POST->GET rewrite would
/// silently reissue provider-controlled requests the submissions opted out of.
/// </summary>
public sealed class ProviderRedirectResolver : DelegatingHandler
{
    private static readonly HashSet<string> ExplicitCredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
    };

    private readonly IProviderUrlPolicy urlPolicy;
    private readonly int maxRedirects;

    public ProviderRedirectResolver(IProviderUrlPolicy urlPolicy, int maxRedirects = ProviderDownloadSecurity.MaxRedirects)
    {
        this.urlPolicy = urlPolicy ?? throw new ArgumentNullException(nameof(urlPolicy));
        this.maxRedirects = maxRedirects < 0 ? 0 : maxRedirects;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        var currentOwned = false;
        var redirects = 0;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (currentOwned) current.Dispose();
                throw;
            }

            if (!IsRedirectStatus(response.StatusCode) || response.Headers.Location is null)
                return response;

            if (!IsBodylessRead(current.Method))
                return response; // Mutating submissions (POST etc.) never follow, replay, or rewrite.

            var target = ResolveTarget(current.RequestUri!, response.Headers.Location);
            if (target is null)
                return response; // Malformed Location: surface the 3xx like the platform handler.

            if (redirects >= maxRedirects)
                return response; // Budget exhausted; the caller maps the remaining 3xx to its failure.

            try
            {
                await urlPolicy.EnsureSafeAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                response.Dispose();
                if (currentOwned) current.Dispose();
                // The policy's stable message stays reachable as the inner exception for logs,
                // while HttpRequestException keeps every provider's existing failure mapping.
                throw new HttpRequestException("Provider download redirect was refused.", exception);
            }

            var next = BuildNextRequest(current, target);
            redirects++;
            response.Dispose();
            if (currentOwned) current.Dispose();
            current = next;
            currentOwned = true;
        }
    }

    private static bool IsBodylessRead(HttpMethod method) =>
        HttpMethod.Get.Equals(method) || HttpMethod.Head.Equals(method);

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static Uri? ResolveTarget(Uri from, Uri location)
    {
        if (location.IsAbsoluteUri) return location;
        return Uri.TryCreate(from, location, out var combined) && combined.IsAbsoluteUri ? combined : null;
    }

    private static HttpRequestMessage BuildNextRequest(HttpRequestMessage previous, Uri target)
    {
        // Only bodyless reads reach here (SendAsync gates the methods), so the hop preserves
        // GET/HEAD as-is and carries no content to replay.
        var next = new HttpRequestMessage(previous.Method, target)
        {
            Version = previous.Version,
            VersionPolicy = previous.VersionPolicy,
        };

        CopyHeaders(previous, next, crossOrigin: !IsSameOrigin(previous.RequestUri!, target));
        return next;
    }

    private static void CopyHeaders(HttpRequestMessage previous, HttpRequestMessage next, bool crossOrigin)
    {
        foreach (var header in previous.Headers)
        {
            if (crossOrigin && IsCredentialHeader(header.Key)) continue;
            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static bool IsCredentialHeader(string name) =>
        ExplicitCredentialHeaders.Contains(name)
        || name.Contains("key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("signature", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}
