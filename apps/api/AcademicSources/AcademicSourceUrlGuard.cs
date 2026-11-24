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
    /// http(s), no embedded credentials, host present, host is an <em>exact</em> member of the
    /// allow list (case-insensitive, trailing dot stripped), and — for IP literals — the address
    /// is routable public unicast. Sub-domain breadth exists only for entries passed in
    /// <paramref name="subdomainExceptions"/>, which callers must source from explicit operator
    /// configuration: a configured <c>api.example.org</c> base URL no longer implies that
    /// <c>evil.api.example.org</c> or <c>redirects.api.example.org</c> may be read.
    /// Redirection is handled manually by the caller with this same check, so an allow-listed
    /// endpoint cannot bounce reads elsewhere.
    /// </summary>
    public static bool IsAllowedEndpoint(
        Uri? endpoint,
        IReadOnlyCollection<string> allowedHosts,
        IReadOnlyCollection<string>? subdomainExceptions = null)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri) return false;
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(endpoint.UserInfo)) return false;
        if (!string.IsNullOrEmpty(endpoint.Fragment)) return false;
        return IsAllowedEndpoint(endpoint.Host, allowedHosts, subdomainExceptions);
    }

    /// <summary>
    /// Host-only form of the same rule, used at the connection layer where only the hostname is
    /// known. Also rejects a bare userinfo-carrying or empty host, and rejects IPv6 literals that
    /// fail to parse rather than letting them fall through to hostname matching.
    /// </summary>
    public static bool IsAllowedEndpoint(
        string? host,
        IReadOnlyCollection<string> allowedHosts,
        IReadOnlyCollection<string>? subdomainExceptions = null)
    {
        var normalizedHost = AcademicSourceConnectionPolicy.NormalizeHost(host);
        if (normalizedHost.Length == 0) return false;

        if (TryParseIpLiteral(normalizedHost, out var literal))
        {
            // An IP literal is only ever acceptable when the operator explicitly allow-listed
            // that exact address, and never when it points at loopback, private, link-local
            // (cloud metadata), site-local or multicast space.
            if (!IsPubliclyRoutable(literal)) return false;
            return Contains(allowedHosts, normalizedHost) || Contains(subdomainExceptions, normalizedHost);
        }

        if (Contains(allowedHosts, normalizedHost)) return true;
        if (subdomainExceptions is not { Count: > 0 }) return false;

        // Explicitly configured exceptions only: the entry must itself be a usable hostname and
        // the candidate must be a strict sub-domain of it, never the entry's suffix look-alike.
        foreach (var exception in subdomainExceptions)
        {
            var normalizedException = AcademicSourceConnectionPolicy.NormalizeHost(exception);
            if (normalizedException.Length == 0) continue;
            if (TryParseIpLiteral(normalizedException, out _)) continue;
            if (normalizedHost.Length > normalizedException.Length + 1
                && normalizedHost.EndsWith("." + normalizedException, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Applies <see cref="IsAllowedEndpoint"/> to a string URL, returning the parsed URI when safe.
    /// </summary>
    public static bool TryAllowedEndpoint(
        string? url,
        IReadOnlyCollection<string> allowedHosts,
        out Uri? endpoint,
        IReadOnlyCollection<string>? subdomainExceptions = null)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!ResearchCitationUrlPolicy.IsSafe(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (!IsAllowedEndpoint(parsed, allowedHosts, subdomainExceptions)) return false;
        endpoint = parsed;
        return true;
    }

    /// <summary>
    /// True when <paramref name="host"/> parses as an IP address, including the bracketed IPv6
    /// form a <see cref="Uri"/> reports for <c>https://[::1]/</c>. Bracket stripping matters:
    /// without it <see cref="IPAddress.TryParse(string)"/> fails, the value is treated as a
    /// hostname, and the routability rules below are skipped entirely.
    /// </summary>
    public static bool TryParseIpLiteral(string? host, out IPAddress address)
    {
        var normalized = AcademicSourceConnectionPolicy.NormalizeHost(host);
        address = IPAddress.Any;
        if (normalized.Length == 0) return false;
        return IPAddress.TryParse(normalized, out address!);
    }

    /// <summary>
    /// True when a provider payload advertises a URL that may be persisted and later rendered to
    /// users: absolute http(s) with no credentials, per the same boundary the research pipeline
    /// enforces for citation URLs. The URL is stored as advertised; nothing is fetched here.
    /// </summary>
    public static bool IsProviderIssuedUrlSafe(string? value) => ResearchCitationUrlPolicy.IsSafe(value);

    /// <summary>
    /// Rejects loopback, private, link-local (including the cloud metadata address), site-local,
    /// multicast, reserved and unspecified addresses, including the IPv4-compatible ::/96 and
    /// IPv4-mapped spellings of those ranges. DNS-level rebinding of an allow-listed hostname is
    /// closed by <see cref="AcademicSourceConnectionPolicy"/>, which resolves the host and dials
    /// only an address that passes this classifier; a URL allow list alone is not sufficient.
    /// Native v6 special-use space is refused as its own set of rules rather than by hostname:
    /// fc00::/6 (unique local, the unicast link-local remainder and multicast/reserved),
    /// 2001::/23 (IETF Protocol Assignments, RFC 2928, which contains the PCP/TURN/DNS-SD anycast
    /// addresses, benchmarking, the deprecated ORCHID block and ORCHIDv2), 2001:db8::/32
    /// (documentation, RFC 3849), 3fff::/20 (the current documentation prefix, RFC 9637),
    /// 100::/64 (discard-only, RFC 6666), 2002::/16 (6to4, RFC 3056), 3ffe::/16 (legacy 6bone) and
    /// 5f00::/16 (SRv6 SIDs, RFC 9602, not globally reachable). The local-use IPv4-translatable
    /// prefix 64:ff9b:1::/48 (RFC 8215) is refused outright; the well-known NAT64 prefix
    /// 64:ff9b::/96 (RFC 6052) is the DNS64 spelling of an IPv4 destination and inherits the IPv4
    /// rules on its embedded address, so metadata and private space stay refused while public
    /// provider space stays reachable on IPv6-only networks.
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
            // Deprecated IPv4-compatible ::/96 encodes a v4 address in the low 32 bits; a stack
            // that still honours the spelling would route it as that v4 destination (for example
            // ::a9fe:a9fe is 169.254.169.254, the metadata address), so it inherits the v4 rules
            // rather than counting as native v6 public space. :: and ::1 land on 0.0.0.0/8 below.
            if (IsV4Compatible(bytes))
            {
                var embedded = new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] };
                return IsPubliclyRoutable(new IPAddress(embedded));
            }
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Teredo
                || IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address)) return false;
            // fc00::/6 is the whole special-use tail of the address space: unique local fc00::/7,
            // unicast link-local fe00::/9 — only fe80::/10 is flagged by IsIPv6LinkLocal, so the
            // rest of the range would otherwise look public — and multicast/reserved ff00::/8.
            // Documentation 2001:db8::/32 and discard-only 100::/64 are not publicly routable
            // provider space either. 6to4 (2002::/16) embeds a v4 address and is refused outright
            // rather than inheriting whatever the embedded v4 looks like.
            if (bytes[0] >= 0xfc) return false;
            // RFC 8215 reserves 64:ff9b:1::/48 for local use inside a single domain: a destination
            // written there only names something that domain's own translator can resolve, so it is
            // refused outright, like 6to4. The well-known prefix 64:ff9b::/96 (RFC 6052) is a
            // different case. On the IPv6-only networks that deploy DNS64 (RFC 6147) a lookup of a
            // public provider hostname legitimately answers 64:ff9b::<public v4>, so refusing the
            // whole /96 would break the guard's own purpose: the address is only reachable through
            // a translator, but the destination it names is ordinary IPv4 space. It therefore
            // inherits the IPv4 rules on its embedded last 32 bits, exactly as ::ffff:0:0/96 and
            // ::/96 do above: 64:ff9b::a9fe:a9fe is 169.254.169.254 and stays refused as the
            // metadata address, 64:ff9b::a00:1 stays refused as 10.0.0.1, while 64:ff9b::808:808
            // stays reachable as 8.8.8.8. The socket is still pinned to the original v6 address
            // that was classified, so the reduction cannot swap the destination mid-connection.
            if (IsNat64LocalUse(bytes)) return false;
            if (IsNat64WellKnown(bytes))
            {
                var translated = new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] };
                return IsPubliclyRoutable(new IPAddress(translated));
            }
            // 2001::/23 is the IETF Protocol Assignments block (RFC 2928): PCP anycast 2001:1::1,
            // TURN 2001:1::2, DNS-SD 2001:1::3, benchmarking 2001:2::/48, AMT 2001:3::/32,
            // AS112-v6 2001:4:112::/48, ORCHID 2001:10::/28, ORCHIDv2 2001:20::/28 and the
            // Drone Remote ID 2001:30::/28 all live inside it, and Teredo 2001:0::/32 is already
            // refused above. The registry marks the /23 not globally reachable unless a more
            // specific allocation says otherwise, and no more specific allocation inside it names
            // an academic content provider, so the block is refused conservatively as a whole.
            // The /23 stops short of the documentation row below and of real provider space such
            // as 2001:448::/32 or 2001:4860::/32, whose second byte is above 0x01.
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01) return false;
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return false;
            if (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes[2] == 0 && bytes[3] == 0) return false;
            if (bytes[0] == 0x20 && bytes[1] == 0x02) return false;
            if (bytes[0] == 0x3f && bytes[1] == 0xfe) return false;                      // v6-only loopback range
            if (bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0) return false; // 3fff::/20 docs
            // 5f00::/16 is the Segment Routing over IPv6 (SRv6) SID space (RFC 9602). It sits
            // inside globally routed 2000::/3 but the registry marks it not globally reachable, so
            // it is assignment space rather than provider space. 5f01::/16 and above stay public.
            if (bytes[0] == 0x5f && bytes[1] == 0x00) return false;
            return true;
        }

        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        if (IPAddress.IsLoopback(address)) return false;                                    // 127.0.0.0/8
        if (b[0] == 0) return false;                                                         // 0.0.0.0/8
        // 100.64.0.0/10 is CGNAT: 100.64.0.0 through 100.127.255.255, not just the third octet.
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        if (b[0] == 10) return false;                                                        // 10.0.0.0/8
        if (b[0] == 192 && b[1] == 168) return false;                                        // 192.168.0.0/16
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;                           // 172.16.0.0/12
        if (b[0] == 169 && b[1] == 254) return false;                                        // 169.254.0.0/16 link-local + metadata
        if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return false;              // 192.0.0.0/24, 192.0.2.0/24
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;                         // benchmarking
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;                          // documentation
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;                           // documentation
        if (b[0] == 192 && b[1] == 88 && b[2] == 99) return false;                           // 6to4 anycast relay
        if (b[0] >= 224) return false;                                                       // multicast + reserved
        return true;
    }

    private static bool Contains(IReadOnlyCollection<string>? hosts, string normalizedHost)
    {
        if (hosts is not { Count: > 0 }) return false;
        foreach (var candidate in hosts)
        {
            if (AcademicSourceConnectionPolicy.NormalizeHost(candidate) == normalizedHost) return true;
        }
        return false;
    }

    private static bool IsV4Mapped(byte[] bytes)
    {
        for (var i = 0; i < 10; i++) if (bytes[i] != 0) return false;
        return bytes[10] == 0xff && bytes[11] == 0xff;
    }

    private static bool IsV4Compatible(byte[] bytes)
    {
        for (var i = 0; i < 12; i++) if (bytes[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// True for the well-known NAT64 prefix 64:ff9b::/96 (RFC 6052, section 3.2.2). The first 96
    /// bits are the prefix and the remaining 32 bits carry the embedded IPv4 destination that a
    /// DNS64 deployment (RFC 6147) synthesises, which the caller classifies with the IPv4 rules
    /// instead of refusing the whole block.
    /// </summary>
    private static bool IsNat64WellKnown(byte[] bytes)
    {
        if (bytes[0] != 0x00 || bytes[1] != 0x64 || bytes[2] != 0xff || bytes[3] != 0x9b) return false;
        for (var i = 4; i < 12; i++) if (bytes[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// True for 64:ff9b:1::/48, the local-use IPv4-translatable prefix (RFC 8215). Its third group
    /// is 0001, so it is disjoint from the well-known /96 above, where bytes 4..11 are zero.
    /// </summary>
    private static bool IsNat64LocalUse(byte[] bytes)
        => bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes[4] == 0x00 && bytes[5] == 0x01;

}
