using System.Net;
using System.Net.Sockets;
using Taslim.Api.Contracts;

namespace Taslim.Api.Research;

/// <summary>
/// Server-side safety boundary for citation URLs accepted from research providers.
/// Provider URLs are untrusted output. They are persisted on research source rows, returned in the
/// report result, exported through the citation manifest and the DOCX/PDF source list, and rendered
/// as clickable source links, so a URL is only admitted when it points at the same host a reader
/// would infer from it and when that host can denote a public web source.
/// </summary>
public static class ResearchCitationUrlPolicy
{
    /// <summary>
    /// True when <paramref name="value"/> is an absolute http(s) URL whose authority carries no
    /// embedded credentials. A URL such as <c>https://trusted.example:token@host.example/report</c>
    /// resolves to <c>host.example</c> while the visible prefix advertises
    /// <c>trusted.example</c>, and the credential segment is persisted and exported verbatim, so
    /// such URLs are rejected instead of normalized. An <c>@</c> below the authority, for example
    /// in a path or a query value, remains a legitimate citation URL.
    /// </summary>
    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;
        if (uri.GetLeftPart(UriPartial.Authority).Contains('@')) return false;
        return AdmitsPublicHost(uri);
    }

    /// <summary>
    /// True when a source row's citation link may be persisted and exported. Uploaded sources carry
    /// no web link at all and stay admissible; a web source is admissible only while both its
    /// citation URL and its canonical form pass <see cref="IsSafe"/>, so a row can never advertise a
    /// public host in one field and an internal target in the other.
    /// </summary>
    public static bool AdmitsCitationLink(string? url, string? canonicalUrl) =>
        (url is null || IsSafe(url)) && (canonicalUrl is null || IsSafe(canonicalUrl));

    /// <summary>
    /// Applies the citation guard to a normalized source set. Server-assigned citation identifiers
    /// and ranks are preserved for the surviving rows, so the report prompt, the citation validator,
    /// the persisted source rows, the CSV manifest and the DOCX/PDF source list all describe exactly
    /// the same set of public sources; rows whose link cannot denote a public web source are dropped
    /// rather than rewritten.
    /// </summary>
    public static IReadOnlyList<ResearchSourceCandidate> FilterAdmissibleSourceLinks(IReadOnlyList<ResearchSourceCandidate> sources)
    {
        if (sources.Count == 0) return sources;
        var admissible = sources.Where(source => AdmitsCitationLink(source.Url, source.CanonicalUrl)).ToArray();
        return admissible.Length == sources.Count ? sources : admissible;
    }

    /// <summary>
    /// A citation is a source a reader can independently open on the public web, so its target must
    /// be a public host. Loopback, RFC1918, link-local (including the cloud identity-metadata
    /// endpoints at 169.254.169.254 and 169.254.170.2), shared-address, benchmarking, documentation,
    /// multicast and reserved addresses, <c>localhost</c> names, and single-label names that resolve
    /// only through a reader's private search domain are never citations. This mirrors the
    /// published-target rule that the provider download boundary already applies and is deliberately
    /// syntactic: no DNS lookup is performed, so RFC-reserved fixture hosts such as
    /// <c>example.gov</c> keep working and a provider cannot make the guard depend on network state.
    /// <see cref="Uri"/> canonicalizes alternate IP encodings (decimal, hexadecimal and abbreviated
    /// octet forms) before the host is inspected, so an obfuscated loopback spelling fails on its
    /// resolved form.
    /// </summary>
    private static bool AdmitsPublicHost(Uri uri)
    {
        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (IPAddress.TryParse(host, out var literal)) return IsPublicAddress(literal);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        // RFC 6761 reserves the .localhost suffix, which always resolves to loopback.
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        // A public citation host is always fully qualified.
        return host.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            // 0.0.0.0/8, RFC1918 private space, and loopback.
            if (first is 0 or 10 or 127) return false;
            if (first == 169 && second == 254) return false;
            if (first == 172 && second is >= 16 and <= 31) return false;
            if (first == 192 && second == 168) return false;
            // 100.64.0.0/10 shared address space (carrier-grade NAT).
            if (first == 100 && second is >= 64 and <= 127) return false;
            // 198.18.0.0/15 benchmarking and the RFC5737 documentation ranges.
            if (first == 198 && second is 18 or 19) return false;
            if (first == 192 && second == 0 && bytes[2] == 2) return false;
            if (first == 198 && second == 51 && bytes[2] == 100) return false;
            if (first == 203 && second == 0 && bytes[2] == 113) return false;
            // Multicast, reserved and broadcast.
            return first < 224;
        }
        // IPv6: the reserved 0000::/8 block (which also carries ::v4-compatible spellings),
        // fc00::/7 unique-local, fe80::/10 link-local, 2001:db8::/32 documentation and ff00::/8
        // multicast are not public citation targets.
        if (bytes[0] == 0) return false;
        if ((bytes[0] & 0xFE) == 0xFC) return false;
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false;
        return bytes[0] < 0xFF;
    }
}
