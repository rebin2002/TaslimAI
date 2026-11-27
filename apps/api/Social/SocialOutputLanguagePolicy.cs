namespace Taslim.Api.Social;

/// <summary>
/// Provider-neutral output-boundary policy for the Social Studio language request.
/// An explicit user request (en/ar/ku) always wins over the provider-selected draft
/// language so provider drift cannot silently replace the requested output language.
/// Only <c>auto</c> (or an unsupported request value) defers to a valid provider
/// language, and anything else fails closed to English.
/// </summary>
public static class SocialOutputLanguagePolicy
{
    public const string Auto = "auto";
    public const string Fallback = "en";

    private static readonly IReadOnlySet<string> ExplicitLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "ar", "ku" };

    public static string Normalize(string? providerLanguage, string? requestedLanguage)
    {
        var requested = NormalizeValue(requestedLanguage);
        if (ExplicitLanguages.Contains(requested)) return requested;

        var provider = NormalizeValue(providerLanguage);
        return ExplicitLanguages.Contains(provider) ? provider : Fallback;
    }

    private static string NormalizeValue(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
