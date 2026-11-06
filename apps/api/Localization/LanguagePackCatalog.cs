namespace Taslim.Api.Localization;

/// <summary>Interface language-pack codes accepted by account/auth endpoints.</summary>
public static class LanguagePackCatalog
{
    public const string English = "en";
    public const string Arabic = "ar";
    public const string Kurdish = "ku";
    public const string SimplifiedChinese = "zh-CN";

    public static readonly IReadOnlySet<string> InterfaceLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        English, Arabic, Kurdish, SimplifiedChinese,
    };

    public static bool IsInterfaceLanguage(string? value) =>
        !string.IsNullOrWhiteSpace(value) && InterfaceLanguages.Contains(value.Trim());

    public static string NormalizeInterfaceLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value) ? English : value.Trim() switch
        {
            var language when language.Equals(SimplifiedChinese, StringComparison.OrdinalIgnoreCase) => SimplifiedChinese,
            var language when language.Equals(Arabic, StringComparison.OrdinalIgnoreCase) => Arabic,
            var language when language.Equals(Kurdish, StringComparison.OrdinalIgnoreCase) => Kurdish,
            _ => English,
        };
}
