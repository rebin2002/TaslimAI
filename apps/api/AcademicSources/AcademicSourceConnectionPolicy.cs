using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Resolves a hostname to the addresses the operating system would connect to. Extracted as an
/// interface so endpoint admission can be tested without depending on live DNS: the production
/// implementation is <see cref="SystemAcademicHostResolver"/>, and tests inject a fixed table.
/// </summary>
public interface IAcademicHostResolver
{
    /// <summary>
    /// All addresses for <paramref name="host"/>. An empty result means "nothing to connect to"
    /// and is treated as a rejection, never as an approval.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Resolver backed by the system resolver, used by the composed HTTP handler.</summary>
public sealed class SystemAcademicHostResolver : IAcademicHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses;
    }
}

/// <summary>Why an endpoint was admitted or refused at the connection layer.</summary>
public enum AcademicConnectDecisionStatus
{
    /// <summary>Admitted: the listed address may be dialed for this hostname.</summary>
    Pinned,

    /// <summary>Hostname is not an exact member of the endpoint allow list (or is an excluded sub-domain).</summary>
    HostNotAllowed,

    /// <summary>Resolution produced no usable address at all.</summary>
    NoAddress,

    /// <summary>At least one candidate address sits in non-public space.</summary>
    NonPublicAddress,

    /// <summary>Host is syntactically unusable (empty, scoped IPv6 zone, or a bare bracket form).</summary>
    InvalidHost,
}

/// <summary>
/// Outcome of admitting one connection target. <see cref="Address"/> is the only address the
/// socket may be dialed at; a refused decision never carries one.
/// </summary>
public sealed record AcademicConnectDecision(AcademicConnectDecisionStatus Status, IPAddress? Address, string? Host)
{
    public bool IsPinned => Status == AcademicConnectDecisionStatus.Pinned && Address is not null;

    /// <summary>Stable, non-secret label used in failure categories and logs.</summary>
    public string Reason => Status switch
    {
        AcademicConnectDecisionStatus.Pinned => "pinned",
        AcademicConnectDecisionStatus.HostNotAllowed => "host_not_allowed",
        AcademicConnectDecisionStatus.NoAddress => "no_address",
        AcademicConnectDecisionStatus.NonPublicAddress => "non_public_address",
        _ => "invalid_host",
    };
}

/// <summary>
/// Connection-layer SSRF boundary for scholarly provider reads. The URL allow list answers
/// "may this host be talked to at all"; this class answers "which address may the socket be
/// dialed at, and is that address actually public". Two separate questions, because a hostname
/// can be allow-listed and still resolve to 169.254.169.254 or 127.0.0.1 — the DNS-rebinding and
/// time-of-check/time-of-use case that a URL check alone cannot close.
/// </summary>
public static class AcademicSourceConnectionPolicy
{
    /// <summary>
    /// Decides the dial target for <paramref name="host"/>. IP literals are classified directly
    /// (no resolver call, so bracketed forms such as <c>[::1]</c> reach the routability rules).
    /// Hostnames are resolved and every returned address must be public: one non-public answer
    /// refuses the whole target rather than quietly dialing a sibling address, and an empty
    /// answer refuses rather than falling through.
    /// </summary>
    public static async Task<AcademicConnectDecision> DecideAsync(
        string? host,
        IAcademicHostResolver resolver,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeHost(host);
        if (normalized.Length == 0) return new AcademicConnectDecision(AcademicConnectDecisionStatus.InvalidHost, null, host);
        if (AcademicSourceUrlGuard.TryParseIpLiteral(normalized, out var literal))
        {
            // Bracketed IPv6 reaches this branch instead of being mistaken for a hostname.
            return AcademicSourceUrlGuard.IsPubliclyRoutable(literal)
                ? new AcademicConnectDecision(AcademicConnectDecisionStatus.Pinned, literal, normalized)
                : new AcademicConnectDecision(AcademicConnectDecisionStatus.NonPublicAddress, null, normalized);
        }

        if (resolver is null) return new AcademicConnectDecision(AcademicConnectDecisionStatus.NoAddress, null, normalized);

        var addresses = await resolver.ResolveAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (addresses is null || addresses.Count == 0) return new AcademicConnectDecision(AcademicConnectDecisionStatus.NoAddress, null, normalized);

        IPAddress? selected = null;
        foreach (var address in addresses)
        {
            if (address is null || !AcademicSourceUrlGuard.IsPubliclyRoutable(address))
                return new AcademicConnectDecision(AcademicConnectDecisionStatus.NonPublicAddress, null, normalized);
            // Prefer IPv4 when the provider is dual-stack: it keeps the pinned family stable for
            // one operation instead of drifting between families per attempt.
            if (selected is null || (selected.AddressFamily == AddressFamily.InterNetworkV6 && address.AddressFamily == AddressFamily.InterNetwork))
                selected = address;
        }

        return selected is null
            ? new AcademicConnectDecision(AcademicConnectDecisionStatus.NoAddress, null, normalized)
            : new AcademicConnectDecision(AcademicConnectDecisionStatus.Pinned, selected, normalized);
    }

    /// <summary>
    /// Dials the socket for one connection: re-decide, then connect to exactly the decided
    /// address. Exposed separately from the handler so tests can assert the dialed endpoint
    /// without a real network, using a stub transport.
    /// </summary>
    public static async Task<Stream> OpenConnectionAsync(
        string host,
        int port,
        IAcademicHostResolver resolver,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> transport,
        CancellationToken cancellationToken)
    {
        var decision = await DecideAsync(host, resolver, cancellationToken).ConfigureAwait(false);
        if (!decision.IsPinned) throw Reject(host, decision.Reason);
        return await transport(decision.Address!, port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the transport handler used by every provider client. The connect callback returns a
    /// plain, already-pinned stream; <see cref="SocketsHttpHandler"/> then performs TLS on top of
    /// it using the <em>original request hostname</em> for SNI and certificate-name validation, so
    /// pinning the IP does not weaken the identity check. No certificate callback is set anywhere
    /// in this module and automatic redirects stay off (the gateway revalidates each hop).
    /// The handler also dials directly: an ambient proxy would replace the destination in
    /// <c>context.DnsEndPoint</c>, so the allow-list check below would be judging the proxy's
    /// hostname instead of the provider's.
    /// </summary>
    public static SocketsHttpHandler CreatePinnedHandler(IAcademicHostResolver resolver, AcademicSourceDiscoveryOptions options)
    {
        var allowedHosts = options.AllowedEndpointHosts();
        var exceptions = options.EndpointHostExceptions;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            // Direct dialing is part of the admission model, not an optimization. The runtime
            // default is UseProxy=true, which makes a proxy from HTTPS_PROXY/HTTP_PROXY (or an
            // explicit Proxy below) the endpoint handed to ConnectCallback: the guard then
            // classifies the proxy hostname, the resolver pins the proxy address, and the
            // provider's address never passes through this method. Either the proxy host is not
            // allow-listed and every provider call fails, or it happens to be allow-listed and the
            // destination is chosen by a third party outside the pinned-dial rules. No scholarly
            // route is meant to leave through a proxy — the only proxy configuration this service
            // has is the inbound ForwardedHeaders:KnownProxies list for client-IP attribution — so
            // ambient proxy settings are ignored here. TLS is untouched: the request hostname still
            // drives SNI and certificate-name validation on the returned stream.
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromSeconds(options.PooledConnectionLifetimeSeconds),
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
        };
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            // A pooled connection is keyed by host+port, but revalidate anyway: the entry could
            // have been established before an allow-list change, and the cost is one string
            // comparison plus, for a fresh connection, one resolver call.
            if (!AcademicSourceUrlGuard.IsAllowedEndpoint(host, allowedHosts, exceptions))
                throw Reject(host, "host_not_allowed");
            return await OpenConnectionAsync(host, port, resolver, ConnectAsync, cancellationToken).ConfigureAwait(false);
        };
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
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
        return TrimTrailingDot(value).ToLowerInvariant();
    }

    private static string TrimTrailingDot(string value) => value.Length > 0 && value[^1] == '.' ? value[..^1] : value;

    private static AcademicSourceRejectedException Reject(string? host, string reason)
    {
        // Never echo provider payloads or full URLs; the category and a normalized host label are
        // enough for an operator and stay safe to persist.
        var details = new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.BlockedEndpoint);
        return new AcademicSourceRejectedException(details);
    }
}
