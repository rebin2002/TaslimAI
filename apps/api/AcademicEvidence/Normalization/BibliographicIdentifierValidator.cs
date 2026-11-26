using System.Text.RegularExpressions;
using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Normalization;

/// <summary>
/// Outcome of validating one identifier: the canonical form to store, and the
/// reason when the value cannot be stored at all.
/// </summary>
/// <param name="IsValid">True only when syntax and any check digit passed.</param>
/// <param name="Normalized">Canonical value, null when invalid.</param>
/// <param name="Reason">Stable reason code when invalid.</param>
/// <param name="RegistrantPrefix">DOI prefix or ISBN registration group when derivable.</param>
public readonly record struct IdentifierValidationResult(
    bool IsValid,
    string? Normalized,
    string? Reason,
    string? RegistrantPrefix);

/// <summary>
/// Syntactic validation and canonicalisation of bibliographic identifiers.
/// Everything here is offline arithmetic and pattern matching: a valid result
/// means "well-formed and self-consistent", never "this DOI resolves to that
/// paper", which is what <c>ProvenanceVerifier</c> still has to establish from
/// independent origins.
/// </summary>
public static partial class BibliographicIdentifierValidator
{
    private const string DoiSchemePrefixPattern = @"(?:https?://)?(?:dx\.)?doi\.org/";

    [GeneratedRegex(@"^10\.\d{4,9}/[!-;=?-~]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DoiShapeRegex();

    [GeneratedRegex(@"^https?://pmc\.ncbi\.nlm\.nih\.gov/articles/(PMC\d{1,10})/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PmcUrlRegex();

    /// <summary>Validate and canonicalise a DOI.</summary>
    public static IdentifierValidationResult ValidateDoi(string? value)
    {
        var candidate = StripDecorations(value, "doi", DoiSchemePrefixPattern);
        if (candidate is null || candidate.Length == 0) return Invalid("DOI_MISSING");

        // A handle containing control characters cannot have been registered.
        if (candidate.Any(char.IsControl)) return Invalid("DOI_CONTROL_CHARS");

        // The Handle system treats DOIs as case-insensitive, so storage is
        // lower-cased to keep one identity for the same article.
        candidate = candidate.ToLowerInvariant();

        if (!DoiShapeRegex().IsMatch(candidate))
        {
            // Name the part that is wrong: a caller reporting "the DOI is
            // malformed" about a value with no registrant is misleading, and the
            // reason codes are what a member sees in the review UI.
            if (!candidate.StartsWith("10.", StringComparison.Ordinal)) return Invalid("DOI_MISSING_PREFIX");
            var handleSlash = candidate.IndexOf('/');
            var registrant = handleSlash > 0 ? candidate[3..handleSlash] : candidate[3..];
            if (registrant.Length is < 4 or > 9 || !registrant.All(char.IsAsciiDigit))
            {
                return Invalid("DOI_MALFORMED_REGISTRANT");
            }
            return Invalid("DOI_MALFORMED_SUFFIX");
        }

        var slash = candidate.IndexOf('/');
        return new IdentifierValidationResult(true, candidate, null, candidate[..slash]);
    }

    /// <summary>Validate an ISBN-10 or ISBN-13, including its check digit.</summary>
    public static IdentifierValidationResult ValidateIsbn(string? value)
    {
        var candidate = StripDecorations(value, "isbn");
        if (candidate is null) return Invalid("ISBN_MISSING");

        candidate = candidate.Replace("-", "").Replace(" ", "").ToUpperInvariant();

        return candidate.Length switch
        {
            10 => ValidateIsbn10(candidate),
            13 => ValidateIsbn13(candidate),
            _ => Invalid("ISBN_LENGTH"),
        };
    }

    /// <summary>
    /// Canonical ISBN-13 used for dedupe, converting a valid ISBN-10 into its
    /// 978-prefixed equivalent so the same book is not counted twice.
    /// </summary>
    public static string? ToIsbn13(IdentifierValidationResult isbn)
    {
        if (!isbn.IsValid || isbn.Normalized is not { Length: > 0 } normalized) return null;
        if (normalized.Length == 13) return normalized;
        if (normalized.Length != 10) return null;

        var body = "978" + normalized[..9];
        return body + ComputeIsbn13CheckDigit(body);
    }

    /// <summary>Validate an ORCID iD, including its ISO 7064 MOD 11-2 check digit.</summary>
    public static IdentifierValidationResult ValidateOrcid(string? value)
    {
        var candidate = StripDecorations(value, "orcid", @"(?:https?://)?orcid\.org/");
        if (candidate is null) return Invalid("ORCID_MISSING");

        candidate = candidate.ToUpperInvariant();
        if (candidate.Length != 19 || candidate[4] != '-' || candidate[9] != '-' || candidate[14] != '-')
        {
            return Invalid("ORCID_SHAPE");
        }

        var digits = string.Concat(candidate[..4], candidate[5..9], candidate[10..14], candidate[15..18]);
        if (digits.Length != 15 || !digits.All(char.IsAsciiDigit)) return Invalid("ORCID_DIGITS");

        var total = 0;
        foreach (var character in digits) total = (total + (character - '0')) * 2;
        var remainder = total % 11;
        var result = (12 - remainder) % 11;
        var expected = result == 10 ? "X" : result.ToString();

        return candidate[18].ToString() == expected
            ? new IdentifierValidationResult(true, candidate, null, candidate[..9])
            : Invalid("ORCID_CHECK_DIGIT");
    }

    /// <summary>Validate an ISSN, including its check digit.</summary>
    public static IdentifierValidationResult ValidateIssn(string? value)
    {
        var candidate = StripDecorations(value, "issn");
        if (candidate is null) return Invalid("ISSN_MISSING");

        candidate = candidate.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (candidate.Length != 8) return Invalid("ISSN_LENGTH");
        if (!candidate[..7].All(char.IsAsciiDigit)) return Invalid("ISSN_DIGITS");
        if (!(char.IsAsciiDigit(candidate[7]) || candidate[7] == 'X')) return Invalid("ISSN_LAST");

        var sum = 0;
        for (var i = 0; i < 7; i++) sum += (candidate[i] - '0') * (8 - i);
        var check = (11 - (sum % 11)) % 11;
        var expected = check == 10 ? "X" : check.ToString();

        return candidate[7].ToString() == expected
            ? new IdentifierValidationResult(true, candidate[..4] + "-" + candidate[4..], null, candidate[..4])
            : Invalid("ISSN_CHECK_DIGIT");
    }

    /// <summary>Validate a PubMed identifier.</summary>
    public static IdentifierValidationResult ValidatePubmed(string? value)
    {
        var candidate = StripDecorations(value, "pmid", @"(?:https?://)?pubmed\.ncbi\.nlm\.nih\.gov/");
        if (candidate is null) return Invalid("PMID_MISSING");

        candidate = candidate.TrimStart('0');
        if (candidate.Length is 0 or > 8 || !candidate.All(char.IsAsciiDigit)) return Invalid("PMID_SHAPE");

        // PubMed has no check digit, so this proves shape only.
        return new IdentifierValidationResult(true, candidate, null, null);
    }

    /// <summary>Validate a PMC identifier, accepting the article URL form.</summary>
    public static IdentifierValidationResult ValidatePmcid(string? value)
    {
        var candidate = StripDecorations(value, "pmc");
        if (candidate is null) return Invalid("PMCID_MISSING");

        var fromUrl = PmcUrlRegex().Match(candidate);
        if (fromUrl.Success) candidate = fromUrl.Groups[1].Value;

        candidate = candidate.ToUpperInvariant();
        if (!candidate.StartsWith("PMC", StringComparison.Ordinal)) return Invalid("PMCID_SHAPE");

        var digits = candidate[3..].TrimStart('0');
        if (digits.Length is 0 or > 10 || !digits.All(char.IsAsciiDigit)) return Invalid("PMCID_SHAPE");

        return new IdentifierValidationResult(true, "PMC" + digits, null, null);
    }

    /// <summary>Validate an arXiv identifier, both the modern and the legacy form.</summary>
    public static IdentifierValidationResult ValidateArxiv(string? value)
    {
        var candidate = StripDecorations(value, "arxiv", @"(?:https?://)?arxiv\.org/(?:abs|pdf)/");
        if (candidate is null) return Invalid("ARXIV_MISSING");

        candidate = candidate.ToLowerInvariant();
        if (candidate.EndsWith(".pdf", StringComparison.Ordinal)) candidate = candidate[..^4];

        var body = candidate;
        var versionSuffix = string.Empty;
        var versionMarker = body.LastIndexOf('v');
        if (versionMarker > 0 && body[(versionMarker + 1)..].All(char.IsAsciiDigit)
            && versionMarker + 1 < body.Length)
        {
            versionSuffix = body[versionMarker..];
            body = body[..versionMarker];
        }

        var modern = body.Length is 9 or 10 && body[4] == '.'
            && body[..4].All(char.IsAsciiDigit) && body[5..].All(char.IsAsciiDigit);

        // Legacy form: an archive name, optionally with a subject subcode, then
        // a 7- or 8-digit sequential identifier, for example hep-th/9901001.
        var legacy = false;
        var archiveSlash = body.IndexOf('/');
        if (archiveSlash > 0 && archiveSlash < body.Length - 1)
        {
            var archive = body[..archiveSlash];
            var serial = body[(archiveSlash + 1)..];
            legacy = archive.Length is >= 2 and <= 12
                && char.IsAsciiLetter(archive[0])
                && archive.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
                && serial.Length is 7 or 8
                && serial.All(char.IsAsciiDigit);
        }

        if (!modern && !legacy) return Invalid("ARXIV_SHAPE");
        return new IdentifierValidationResult(true, body + versionSuffix, null, null);
    }

    /// <summary>Validate an identifier of any supported kind by dispatching to the right rule.</summary>
    public static IdentifierValidationResult Validate(IdentifierKind kind, string? value) => kind switch
    {
        IdentifierKind.Doi => ValidateDoi(value),
        IdentifierKind.Isbn => ValidateIsbn(value),
        IdentifierKind.Issn => ValidateIssn(value),
        IdentifierKind.Orcid => ValidateOrcid(value),
        IdentifierKind.Pubmed => ValidatePubmed(value),
        IdentifierKind.Pmcid => ValidatePmcid(value),
        IdentifierKind.Arxiv => ValidateArxiv(value),
        IdentifierKind.Url => ValidateUrl(value),
        _ => Invalid("IDENTIFIER_KIND_UNSUPPORTED"),
    };

    /// <summary>Validate a resolvable web address used as an identifier or locator.
    public static IdentifierValidationResult ValidateUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Invalid("URL_MISSING");
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return Invalid("URL_SHAPE");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("URL_SCHEME");
        }

        // Credentials hidden in the authority make a link look like a different
        // host than the one it resolves to, the same trap the existing research
        // citation policy rejects.
        if (uri.GetLeftPart(UriPartial.Authority).Contains('@', StringComparison.Ordinal)) return Invalid("URL_USERINFO");

        return new IdentifierValidationResult(true, uri.ToString(), null, uri.Host);
    }

    private static IdentifierValidationResult ValidateIsbn10(string candidate)
    {
        if (!candidate[..9].All(char.IsAsciiDigit)) return Invalid("ISBN_DIGITS");
        if (!(char.IsAsciiDigit(candidate[9]) || candidate[9] == 'X')) return Invalid("ISBN_LAST");

        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (candidate[i] - '0') * (10 - i);
        sum += candidate[9] == 'X' ? 10 : candidate[9] - '0';

        return sum % 11 == 0
            ? new IdentifierValidationResult(true, candidate, null, null)
            : Invalid("ISBN_CHECK_DIGIT");
    }

    private static IdentifierValidationResult ValidateIsbn13(string candidate)
    {
        if (!candidate.All(char.IsAsciiDigit)) return Invalid("ISBN_DIGITS");
        if (candidate[..3] is not ("978" or "979")) return Invalid("ISBN_PREFIX");

        return candidate[12].ToString() == ComputeIsbn13CheckDigit(candidate[..12])
            ? new IdentifierValidationResult(true, candidate, null, candidate[..3])
            : Invalid("ISBN_CHECK_DIGIT");
    }

    private static string ComputeIsbn13CheckDigit(string firstTwelve)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (firstTwelve[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return ((10 - (sum % 10)) % 10).ToString();
    }

    private static IdentifierValidationResult Invalid(string reason) =>
        new(false, null, reason, null);

    /// <summary>
    /// Trim label prefixes, resolver URLs and sentence punctuation. Decorative
    /// wrappers are stripped because a reference list writes "doi:10.x/y," with a
    /// trailing comma, and storing that verbatim makes the identifier unresolvable.
    /// </summary>
    private static string? StripDecorations(string? value, params string[] decorations)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var candidate = value.Trim();

        foreach (var decoration in decorations)
        {
            if (decoration.Contains("//", StringComparison.Ordinal) || decoration.Contains('\\', StringComparison.Ordinal))
            {
                candidate = Regex.Replace(candidate, decoration, "", RegexOptions.IgnoreCase);
                continue;
            }

            if (candidate.StartsWith(decoration + ":", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[(decoration.Length + 1)..];
            }
            else if (candidate.StartsWith(decoration + " ", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[decoration.Length..];
            }
        }

        candidate = candidate.Trim();

        // A DOI suffix legitimately contains punctuation, so only sentence
        // terminators at the very end are removed.
        candidate = candidate.TrimEnd('.', ',', ';', ')', ']', '/', ' ');
        if (candidate.StartsWith("(", StringComparison.Ordinal) && candidate.EndsWith(")", StringComparison.Ordinal))
        {
            candidate = candidate[1..^1];
        }

        candidate = candidate.Trim();
        return candidate.Length == 0 ? null : candidate;
    }
}
