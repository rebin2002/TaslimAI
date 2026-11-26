using System.Text;
using System.Text.RegularExpressions;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Deterministic identifier normalization used for dedup and for rejecting malformed identifiers
/// before they ever reach a network call. Only check-digit-verified ISBNs and syntactically valid
/// DOIs produce normalized forms; anything else returns null instead of a guessed value.
/// </summary>
public static partial class AcademicSourceIdentity
{
    [GeneratedRegex(@"^(?:https?://(?:dx\.)?doi\.org/|info:doi/|doi:|doi/)?(10\.\d{4,9}(?:\.\d+)*/[^\s\x00-\x1f\x7f]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoiRegex();

    /// <summary>
    /// Normalizes a DOI to lowercase registrable form (<c>10.xxxx/rest</c>), or null when the
    /// input is not a syntactically valid DOI. Recognized prefixes (<c>https://doi.org/</c>,
    /// <c>https://dx.doi.org/</c>, <c>doi:</c>, <c>info:doi/</c>) are stripped. No network
    /// lookup happens here; registration is the job of <see cref="IAcademicDoiValidator"/>.
    /// </summary>
    public static string? NormalizeDoi(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().TrimEnd('/', '.');
        var match = DoiRegex().Match(candidate);
        if (!match.Success) return null;
        var doi = match.Groups[1].Value;
        var slash = doi.IndexOf('/');
        return slash > 2 && slash < doi.Length - 1 ? doi.ToLowerInvariant() : null;
    }

    public static bool IsValidDoi(string? value) => NormalizeDoi(value) is not null;

    /// <summary>
    /// Escapes a DOI for embedding in a REST path segment. Query and fragment delimiters
    /// (<c>?</c>, <c>#</c>) and other unsafe printable characters are percent-encoded so a hostile
    /// suffix cannot escape the intended endpoint; the path separator itself and already-encoded
    /// <c>%</c> sequences pass through, which is what the DOI REST APIs accept.
    /// </summary>
    public static string? ToPathSafeDoi(string? normalizedDoi)
    {
        if (string.IsNullOrEmpty(normalizedDoi)) return null;
        if (normalizedDoi.Contains("..", StringComparison.Ordinal)) return null; // traversal is not DOI syntax
        var builder = new StringBuilder(normalizedDoi.Length + 8);
        foreach (var c in normalizedDoi)
        {
            if (char.IsControl(c)) return null;
            if (IsPathPassthrough(c)) { builder.Append(c); continue; }
            if (c <= 0x7e)
            {
                builder.Append('%').Append(((int)c).ToString("x2"));
                continue;
            }
            foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                builder.Append('%').Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    private static bool IsPathPassthrough(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '-' or '.' or '_' or '~'
        || c is '/' or '%' or '!' or '$' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or '@' or ':' or '&';

    /// <summary>Strips hyphens, spaces and surrounding noise, validates the check digit, and returns digits only.</summary>
    public static string? NormalizeIsbn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (c is '-' or ' ' or '\u2010' or '\u2011') continue;
            if (char.IsAsciiDigit(c)) { digits.Append(c); continue; }
            if ((c is 'x' or 'X') && digits.Length == 9) { digits.Append('X'); continue; }
            return null;
        }
        var isbn = digits.ToString();
        return isbn.Length switch
        {
            10 when IsValidIsbn10(isbn) => isbn,
            13 when IsValidIsbn13(isbn) => isbn,
            _ => null,
        };
    }

    /// <summary>Returns the verified ISBN-13 for any ISBN input, or null when invalid or underivable.</summary>
    public static string? ToIsbn13(string? value)
    {
        var isbn = NormalizeIsbn(value);
        if (isbn is null) return null;
        if (isbn.Length == 13) return isbn.StartsWith("978", StringComparison.Ordinal) || isbn.StartsWith("979", StringComparison.Ordinal) ? isbn : null;
        return ConvertIsbn10To13(isbn);
    }

    /// <summary>Exact ISBN-13 to ISBN-10 conversion; only valid for the 978 prefix group.</summary>
    public static string? ToIsbn10(string? value)
    {
        var isbn = NormalizeIsbn(value);
        if (isbn is null) return null;
        if (isbn.Length == 10) return isbn;
        if (!isbn.StartsWith("978", StringComparison.Ordinal)) return null; // ISBN-10 is undefined for 979 groups.
        var body = isbn[3..12];
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (body[i] - '0') * (10 - i);
        var check = (11 - sum % 11) % 11;
        return body + (check == 10 ? "X" : check.ToString());
    }

    private static string? ConvertIsbn10To13(string isbn10)
    {
        var body = "978" + isbn10[..9];
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var check = (10 - sum % 10) % 10;
        return body + check.ToString();
    }

    private static bool IsValidIsbn10(string isbn)
    {
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            if (!char.IsAsciiDigit(isbn[i])) return false;
            sum += (isbn[i] - '0') * (10 - i);
        }
        var check = isbn[9] is 'X' or 'x' ? 10 : isbn[9] - '0';
        return (sum + check) % 11 == 0;
    }

    private static bool IsValidIsbn13(string isbn)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            if (!char.IsAsciiDigit(isbn[i])) return false;
            sum += (isbn[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }
        if (!char.IsAsciiDigit(isbn[12])) return false;
        return (sum + (isbn[12] - '0')) % 10 == 0;
    }

    /// <summary>
    /// Stable cross-provider dedup key: DOI first, then ISBN-13, then provider-scoped identity.
    /// Records with no verified identifier never collapse into each other across providers.
    /// </summary>
    public static string BuildDedupKey(AcademicSourceRecord record)
    {
        var doi = NormalizeDoi(record.Doi);
        if (doi is not null) return $"doi:{doi}";
        var isbn = ToIsbn13(record.Isbn13 ?? record.Isbn10);
        if (isbn is not null) return $"isbn:{isbn}";
        return $"{record.ProviderKey}:{record.ProviderWorkId}";
    }
}
