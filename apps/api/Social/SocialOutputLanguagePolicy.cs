namespace Taslim.Api.Social;

public static class SocialOutputLanguagePolicy
{
    private static readonly IReadOnlySet<string> SupportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "ar", "ku" };

    public static string Normalize(string? providerLanguage, string? requestedLanguage)
    {
        var requested = NormalizeValue(requestedLanguage);
        if (SupportedLanguages.Contains(requested)) return requested;

        var provider = NormalizeValue(providerLanguage);
        return SupportedLanguages.Contains(provider) ? provider : "en";
    }

    private static string NormalizeValue(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
