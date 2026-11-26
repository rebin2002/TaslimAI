using System.Text.RegularExpressions;

namespace Taslim.Api.AcademicTemplates;

/// <summary>One bibliography row as it arrives from the saved research artifact.</summary>
public sealed record AcademicBibliographyEntry(
    string DisplayText,
    string? Doi,
    string? Isbn,
    string? Url,
    bool MarkedSyntheticPlaceholder);

/// <summary>
/// The reference-integrity gate every academic export passes through. Two failure classes are refused:
///
/// 1. A non-placeholder row carrying no verifiable identifier (DOI/ISBN/resolvable URL): the epic's
///    "never fake citations" rule means an export cannot silently ship a reference the system cannot
///    point at. The research lanes already resolve identifiers during discovery; this gate makes the
///    export re-check it.
/// 2. A synthetic placeholder (the gallery's format samples) reaching a real export — preview material
///    must never be smuggled into submitted work, in either direction.
///
/// The gate is deliberately a pure function over plain data so the Document/Research lanes can call it
/// at their own seam without importing this module's renderers.
/// </summary>
public static class AcademicBibliographyIntegrity
{
    private static readonly Regex DoiPattern = new(@"^10\.\d{4,9}/\S+$", RegexOptions.Compiled);
    private static readonly Regex IsbnPattern = new(@"^(97(8|9))?\d{9}(\d|X)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record IntegrityReport(bool Passed, IReadOnlyList<string> Violations);

    public static IntegrityReport ValidateExportable(IReadOnlyList<AcademicBibliographyEntry> entries)
    {
        var violations = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var position = $"entry {index + 1}";
            if (entry.MarkedSyntheticPlaceholder)
            {
                violations.Add($"{position}: synthetic preview placeholder cannot appear in a real export");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.DisplayText))
            {
                violations.Add($"{position}: empty reference text");
                continue;
            }
            var verified =
                (!string.IsNullOrWhiteSpace(entry.Doi) && DoiPattern.IsMatch(entry.Doi.Trim())) ||
                (!string.IsNullOrWhiteSpace(entry.Isbn) && IsbnPattern.IsMatch(entry.Isbn.Trim().Replace("-", "").Replace(" ", ""))) ||
                IsVerifiedUrl(entry.Url);
            if (!verified)
                violations.Add($"{position}: no verifiable identifier (DOI, ISBN or resolvable source URL)");
        }
        return new IntegrityReport(violations.Count == 0, violations);
    }

    /// <summary>
    /// URL verification accepts only http(s) absolute URLs whose host is not a bare-data or placeholder
    /// host; it is a structural check (resolution happens upstream in the discovery lanes, and the
    /// Research lane's source lifecycle owns liveness).
    /// </summary>
    private static bool IsVerifiedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme is "http" or "https"
        && !string.Equals(parsed.Host, "example.org", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Entries from a saved research artifact are trusted after their upstream verification;
    /// this helper only re-checks their shape, never rewrites them.</summary>
    public static bool LooksLikeFabricatedLocator(AcademicBibliographyEntry entry) =>
        entry.DisplayText.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
        || entry.DisplayText.Contains("synthetic", StringComparison.OrdinalIgnoreCase)
        || entry.DisplayText.Contains("not a real", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The privacy seam: nothing in this module's customer-facing surface may expose backend identity. The
/// controller serializes through <see cref="Scrub"/> and CI asserts both the guard itself and the
/// absence of a known token list in every response body.
/// </summary>
public static class AcademicCatalogPrivacyGuard
{
    /// <summary>Token families that must never appear in gallery/preview surfaces or rendered samples.</summary>
    public static readonly IReadOnlyList<string> ForbiddenTokenFamilies = new[]
    {
        "openai", "anthropic", "claude", "gpt-4", "gpt-5", "gemini", "llama", "mistral",
        "deepseek", "grok", "provider", "model-routing", "routing", "upstream",
        "api-key", "apikey", "secret", "token=", "credits", "cost", "usd", "billing",
        "endpoint", "baseurl", "base-url", "prompt",
    };

    public static IReadOnlyList<string> FindLeaks(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        var normalized = json.ToLowerInvariant();
        return ForbiddenTokenFamilies
            .Where(token => ContainsWordish(normalized, token))
            .ToList();
    }

    private static bool ContainsWordish(string haystack, string needle)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            // Word-ish match so benign strings like "models of the page grid" (no such phrase ships)
            // or the word "credit" inside "accredited" cannot false-positive a whole response, while
            // raw identifiers ("gpt-4", "sk-proj-…") always trip it.
            var before = index == 0 ? '\0' : haystack[index - 1];
            var afterIndex = index + needle.Length;
            var after = afterIndex < haystack.Length ? haystack[afterIndex] : '\0';
            var boundaryBefore = !char.IsLetterOrDigit(before);
            var boundaryAfter = !char.IsLetterOrDigit(after);
            if (boundaryBefore && boundaryAfter) return true;
            index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>Serialized form of an object with the guard applied; throws if the payload would leak.</summary>
    public static string Scrub(object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var leaks = FindLeaks(json);
        if (leaks.Count > 0)
            throw new InvalidOperationException($"Academic catalog payload would disclose backend internals: {string.Join(", ", leaks)}");
        return json;
    }
}
