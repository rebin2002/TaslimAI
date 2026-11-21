using Microsoft.AspNetCore.Http;

namespace Taslim.Api.Assets;

/// <summary>
/// Cache policy for authenticated asset download responses.
/// </summary>
public static class AssetDownloadResponsePolicy
{
    /// <summary>
    /// The directive the download endpoint claims for its own responses. <c>no-store</c>
    /// removes the storage path for private and shared caches alike; <c>private</c> keeps the
    /// intent unambiguous for intermediaries that honour a cache-class directive but are
    /// configured to ignore or rewrite the rest.
    /// </summary>
    public const string PrivateNoStoreDirective = "private, no-store";

    /// <summary>
    /// Cache-key fence added to <c>Vary</c>. A download URL is scoped to the asset id only, so
    /// the credential is the single thing that separates one caller's answer from another's.
    /// Without it an intermediary that stores the response anyway has no second signal keeping
    /// two principals out of the same cache entry.
    /// </summary>
    public const string CredentialCacheKeyFence = "Cookie";

    /// <summary>
    /// Marks the current response private, non-storable, and partitioned by credential.
    /// </summary>
    /// <remarks>
    /// Every authenticated route in this API currently re-issues the session cookie on each
    /// request, and cookie issuance happens to write <c>Cache-Control: no-cache, no-store</c>.
    /// That makes the download responses look safe while the download endpoint itself claims
    /// nothing, so the guarantee is a side effect of an unrelated policy: change the stamp
    /// validation interval, the cookie provider, or the re-issue rules and private asset bytes
    /// silently become heuristically cacheable again. Claiming the directive here makes the
    /// fence a property of the download, and adds the credential partition that cookie
    /// issuance never provided.
    /// </remarks>
    public static void ApplyPrivateNoStore(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.CacheControl = PrivateNoStoreDirective;
        response.Headers.Pragma = "no-cache";

        var existingVary = response.Headers.Vary.ToString();
        if (!ContainsCredentialFence(existingVary))
        {
            response.Headers.Vary = string.IsNullOrWhiteSpace(existingVary)
                ? CredentialCacheKeyFence
                : $"{existingVary}, {CredentialCacheKeyFence}";
        }
    }

    private static bool ContainsCredentialFence(string? vary)
    {
        if (string.IsNullOrWhiteSpace(vary)) return false;
        return vary
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, CredentialCacheKeyFence, StringComparison.OrdinalIgnoreCase));
    }
}
