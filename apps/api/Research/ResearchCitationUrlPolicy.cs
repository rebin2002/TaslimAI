namespace Taslim.Api.Research;

/// <summary>
/// Server-side safety boundary for citation URLs accepted from research providers.
/// Provider URLs are untrusted output. They are persisted on research source rows, returned in the
/// report result, exported through the citation manifest and the DOCX/PDF source list, and rendered
/// as clickable source links, so a URL is only admitted when it points at the same host a reader
/// would infer from it.
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
        return !uri.GetLeftPart(UriPartial.Authority).Contains('@');
    }
}
