using System.Net;
using Taslim.Api.Research;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Server-side SSRF boundary for reading provider endpoints and for admitting provider-issued
/// URLs into persisted records. Endpoint reads are constrained to the configured provider hosts;
/// URLs a provider advertises inside its payloads are checked with the same citation-URL safety
/// policy the research pipeline uses before they are stored, exported or rendered, and any
/// literal IP address is rejected when it resolves into a non-public range.
/// </summary>
public static class AcademicSourceUrlGuard
{
    /// <summary>
    /// True when <paramref name="endpoint"/> may be read server-side. Requirements: absolute
    /// http(s), no embedded credentials, host present, host equals or is a sub-domain of an
    /// allow-listed entry (case-insensitive, trailing dot stripped), and — for IP literals — the
    /// address is routable public unicast. Redirection is handled manually by the caller with
    /// this same check, so an allow-listed endpoint cannot bounce reads elsewhere.
    /// </summary>
    public static bool IsAllowedEndpoint(Uri? endpoint, IReadOnlyCollection<string> allowedHosts)
    {
        if (endpoint is null) return false;
        if (!endpoint.IsAbsoluteUri) return false;
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(endpoint.UserInfo)) return false;
        if (!string.IsNullOrEmpty(endpoint.Fragment)) return false;
        var host = endpoint.Host;
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (IPAddress.TryParse(TrimTrailingDot(host), out var literal))
        {
            // An IP literal is only ever acceptable when the operator explicitly allow-listed
            // that exact address, and never when it points at loopback, private, link-local
            // (cloud metadata), site-local or multicast space.
            if (!IsPubliclyRoutable(literal)) return false;
            return allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        }

        if (allowedHosts.Count == 0) return false;
        var normalizedHost = TrimTrailingDot(host).ToLowerInvariant();
        foreach (var allowed in allowedHosts)
        {
            var normalizedAllowed = TrimTrailingDot(allowed).ToLowerInvariant();
            if (normalizedHost == normalizedAllowed) return true;
            if (normalizedHost.EndsWith("." + normalizedAllowed, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Applies <see cref="IsAllowedEndpoint"/> to a string URL, returning the parsed URI when safe.
    /// </summary>
    public static bool TryAllowedEndpoint(string? url, IReadOnlyCollection<string> allowedHosts, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!ResearchCitationUrlPolicy.IsSafe(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (!IsAllowedEndpoint(parsed, allowedHosts)) return false;
        endpoint = parsed;
        return true;
    }

    /// <summary>
    /// True when a provider payload advertises a URL that may be persisted and later rendered to
    /// users: absolute http(s) with no credentials, per the same boundary the research pipeline
    /// enforces for citation URLs. The URL is stored as advertised; nothing is fetched here.
    /// </summary>
    public static bool IsProviderIssuedUrlSafe(string? value) => ResearchCitationUrlPolicy.IsSafe(value);

    /// <summary>
    /// Rejects loopback, private, link-local (including the cloud metadata address), site-local,
    /// multicast and unspecified addresses. DNS-level rebinding of an allow-listed hostname is
    /// mitigated by the host allow list plus manual redirect revalidation; a defense-in-depth
    /// connect-callback pin remains available for the hosting layer.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // v4-mapped addresses inherit the v4 rules.
            if (IsV4Mapped(bytes))
            {
                var mapped = new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] };
                return IsPubliclyRoutable(new IPAddress(mapped));
            }
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Teredo
                || IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address)) return false;
            // Unique local fc00::/7, documentation 2001:db8::/32 and discard-only 100::/64 are
            // not publicly routable provider space. Further unallocated space is not
            // prefix-classifiable here; the host allow list already constrains every read.
            if (bytes[0] is 0xfc or 0xfd) return false;
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return false;
            if (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0 && bytes[3] == 0) return false;
            return true;
        }

        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        if (IPAddress.IsLoopback(address)) return false;                    // 127.0.0.0/8
        if (b[0] == 0) return false;                                         // 0.0.0.0/8
        if (b[0] == 100 && b[1] == 64) return false;                         // 100.64.0.0/10 CGNAT
        if (b[0] == 10) return false;                                        // 10.0.0.0/8
        if (b[0] == 192 && b[1] == 168) return false;                        // 192.168.0.0/16
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;           // 172.16.0.0/12
        if (b[0] == 169 && b[1] == 254) return false;                        // 169.254.0.0/16 link-local + metadata
        if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return false; // 192.0.0.0/24, 192.0.2.0/24
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;         // benchmarking
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;          // documentation
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;           // documentation
        if (b[0] >= 224) return false;                                       // multicast + reserved
        return true;
    }

    private static bool IsV4Mapped(byte[] bytes)
    {
        for (var i = 0; i < 10; i++) if (bytes[i] != 0) return false;
        return bytes[10] == 0xff && bytes[11] == 0xff;
    }

    private static string TrimTrailingDot(string host) => host.Length > 0 && host[^1] == '.' ? host[..^1] : host;
}
