using System.Text;

namespace Taslim.Api.Assets;

/// <summary>
/// Turns a Media Library search term into the contains-pattern the deployed
/// PostgreSQL provider matches with <c>ILIKE ... ESCAPE</c>.
///
/// A search term is data, not pattern syntax. Left raw, <c>%</c> would widen to "any
/// run of characters", <c>_</c> to "any single character", and <c>\</c> - PostgreSQL's
/// default LIKE escape character - would silently escape whichever character follows it
/// and, when typed last, would swallow the closing wildcard. Members would then be
/// shown assets that do not contain their term (and lose assets that do), and the
/// <c>totalCount</c>/<c>totalPages</c> pair the pager walks would be wrong for the same
/// filter. Escaping the metacharacters keeps the filtered page contents, its cursor and
/// its totals in agreement with the literal text that was typed.
/// </summary>
public static class AssetSearchPattern
{
    /// <summary>
    /// Use a non-backslash escape so SQL ESCAPE '!' is valid regardless of PostgreSQL
    /// standard_conforming_strings mode. Backslashes remain literal search text.
    /// </summary>
    public const string EscapeCharacter = "!";

    /// <summary>
    /// Builds the <c>ILIKE</c> pattern that matches assets whose text contains
    /// <paramref name="term"/> literally.
    /// </summary>
    public static string Contains(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        var builder = new StringBuilder(term.Length + 4).Append('%');
        foreach (var character in term)
        {
            if (character is '%' or '_' or '!') builder.Append(EscapeCharacter);
            builder.Append(character);
        }

        return builder.Append('%').ToString();
    }
}
