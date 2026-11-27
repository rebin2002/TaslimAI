using System.Net;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Admission for URLs that a scholarly catalog advertised, before this service hands them to a
/// browser as clickable links.
///
/// The shared research boundary (<c>ResearchCitationUrlPolicy</c>, also used by
/// <see cref="AcademicSourceUrlGuard.IsProviderIssuedUrlSafe"/>) answers a different question: it
/// keeps credentials and non-http(s) schemes out of persisted citation text. It deliberately permits
/// <c>http://127.0.0.1:8080/</c>, <c>http://localhost/</c> and <c>http://[::1]/</c>, because a
/// research pipeline that stores a URL is not the thing that opens it. This class answers the
/// question that matters one hop later: should the studio put this address in front of a reader as a
/// link? A student who clicks a provider-supplied <c>http://localhost:9200/</c> sends a request from
/// their own machine to whatever is listening there, and nothing that runs on this server can stop
/// that after the link has been rendered — validating the address here is the only place it can be
/// stopped. It is deliberately not described as protection against a hostname that later rebinds:
/// server-side DNS checks do not pin what a browser resolves when the link is clicked.
///
/// Scope is additive by design. The shared research policy and the endpoint guard are untouched, so
/// this cannot widen or narrow what any other feature persists or dials; it only filters what this
/// feature displays, and it reuses the guard's address classifier rather than inventing a second one.
/// </summary>
public static class AcademicProviderLinkPolicy
{
    /// <summary>
    /// Hostnames that never identify a publisher. Matching is exact-or-suffix on the normalized
    /// label, so <c>localhost</c>, <c>api.localhost</c> and <c>db.internal</c> are refused while
    /// <c>localhost-records.example.org</c> (a real host that happens to contain the word) is kept.
    /// </summary>
    private static readonly string[] LocalOnlySuffixes =
    [
        "localhost",
        "local",
        "localdomain",
        "internal",
        "intranet",
        "lan",
        "home",
        "test",
        "invalid",
    ];

    /// <summary>
    /// True when a provider-advertised address may be shown as a link: absolute http(s), no embedded
    /// credentials, a host that is not a machine-local name, and — when the host is written as an IP
    /// literal, in any of the mapped or encoded spellings <see cref="AcademicSourceUrlGuard"/>
    /// understands — an address the guard classifies as publicly routable. Unusual or unclassifiable
    /// host spellings are refused rather than guessed at, because the cost of a wrong guess here is a
    /// link a reader clicks.
    /// </summary>
    public static bool IsDisplayable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (!parsed.IsAbsoluteUri) return false;
        if (!(parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
              || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))) return false;
        // "https://publisher.example@attacker.invalid/" renders as the publisher and resolves to the
        // attacker: userinfo in a displayed link is a spoofing vector, not an address.
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;

        var host = AcademicSourceConnectionPolicy.NormalizeHost(parsed.IdnHost.Length > 0 ? parsed.IdnHost : parsed.Host);
        if (host.Length == 0) return false;

        if (AcademicSourceUrlGuard.TryParseIpLiteral(host, out var literal))
            return AcademicSourceUrlGuard.IsPubliclyRoutable(literal);

        // A numeric shape that did not parse as an address is decimal / octal / hex-encoded IPv4
        // (http://2130706433/ is 127.0.0.1 to a browser). Nothing legitimate looks like this, so it
        // is refused instead of being treated as a name.
        if (LooksLikeEncodedAddress(host)) return false;

        // Single-label names are machine-local or search-domain-relative (NetBIOS, mDNS); no
        // publisher is reached by one.
        if (host.IndexOf('.') < 0) return false;
        foreach (var suffix in LocalOnlySuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            if (host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Projects a provider URL onto the DTO: the address when it may be displayed, otherwise <c>null</c>.</summary>
    public static string? Project(string? url) => IsDisplayable(url) ? url!.Trim() : null;

    private static bool LooksLikeEncodedAddress(string host)
    {
        // Percent-escaped, bracketed or zone-scoped spellings are refused outright: the guard's own
        // normalization rejects IPv6 zones, and a browser would not.
        if (host.IndexOf('%') >= 0 || host.IndexOf('[') >= 0 || host.IndexOf(']') >= 0 || host.IndexOf('@') >= 0)
            return true;
        // What a browser still resolves to a machine is the family of spellings where every label is
        // numeric: decimal (2130706433), short-form dotted (127.1), octal (0177.0.0.1) and hex
        // (0x7f.0.0.1). A real hostname is allowed to contain digits — "arxiv1.org" must survive — so
        // a label only counts toward this rule while every one of its characters is a digit, or the
        // label carries an explicit 0x prefix.
        var sawDigit = false;
        foreach (var label in host.Split('.'))
        {
            if (label.Length == 0) return true;                                   // leading, trailing or doubled dot
            if (label.StartsWith("0x", StringComparison.Ordinal))
            {
                if (label.Length == 2) return false;
                foreach (var c in label.AsSpan(2))
                {
                    if (!char.IsAsciiHexDigit(c)) return false;                   // 0xquiz not an address
                }
                sawDigit = true;
                continue;
            }
            foreach (var c in label)
            {
                if (char.IsAsciiDigit(c)) { sawDigit = true; continue; }
                return false;                                                     // an ordinary name
            }
        }
        return sawDigit;
    }
}
