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

    // True when the address must never be a fetch target. Kept byte-identical to the proven
    // e9903fa8 predicate (special-use IPv4, the ::/96 and ::ffff:/96 embedded-IPv4 reductions,
    // IPv6 unique-local/link-local/site-local/multicast/documentation/discard/6to4/Teredo/6bone,
    // and a fail-closed family branch).
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
        return (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0)
            || (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00)
            || (bytes[0] == 0x20 && bytes[1] == 0x02)
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            || (bytes[0] == 0x3F && bytes[1] == 0xFE)
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
        var normalized = NormalizeHost(host);
        if (normalized.Length == 0) throw new InvalidDataException("Provider download URL is not safe.");

        if (IPAddress.TryParse(normalized, out var literal))
        {
            if (ProviderUrlPolicy.IsBlocked(literal)) throw new InvalidDataException("Provider download target is not public.");
            return literal;
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

        IPAddress? selected = null;
        foreach (var address in addresses)
        {
            // All-or-nothing: a mixed answer (public + rebinding candidate) refuses the target
            // entirely. This is the property an alternating resolver test asserts.
            if (address is null || ProviderUrlPolicy.IsBlocked(address))
                throw new InvalidDataException("Provider download target is not public.");
            if (selected is null
                || (selected.AddressFamily == AddressFamily.InterNetworkV6 && address.AddressFamily == AddressFamily.InterNetwork))
                selected = address;
        }

        return selected!;
    }

    /// <summary>
    /// Dials one connection: decide, then hand the decided address to the transport. Exposed
    /// separately from the handler so tests can assert the dialed endpoint equals the validated
    /// endpoint without a real network, using a stub transport.
    /// </summary>
    public static async Task<Stream> OpenConnectionAsync(
        string host,
        int port,
        IProviderHostResolver resolver,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> transport,
        CancellationToken cancellationToken)
    {
        var pinned = await SelectPinnedAddressAsync(host, resolver, cancellationToken).ConfigureAwait(false);
        return await transport(pinned, port, cancellationToken).ConfigureAwait(false);
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
