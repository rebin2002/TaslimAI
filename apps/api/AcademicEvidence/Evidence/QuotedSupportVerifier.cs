using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Evidence;

/// <summary>Why an evidence item passed or failed the content check.</summary>
public static class SupportVerificationCodes
{
    /// <summary>Quoted text was found in the stored content.</summary>
    public const string QuoteLocated = "SUPPORT_QUOTE_LOCATED";

    /// <summary>Quoted text was not found in the stored content.</summary>
    public const string QuoteNotFound = "SUPPORT_QUOTE_NOT_FOUND";

    /// <summary>Paraphrase shares enough recorded terminology with the stored content.</summary>
    public const string ParaphraseGrounded = "SUPPORT_PARAPHRASE_GROUNDED";

    /// <summary>Paraphrase has too little overlap with the stored content to be trusted.</summary>
    public const string ParaphraseUngrounded = "SUPPORT_PARAPHRASE_UNGROUNDED";

    /// <summary>Every number in the finding appears in the stored content.</summary>
    public const string NumbersLocated = "SUPPORT_NUMBERS_LOCATED";

    /// <summary>A reported figure is nowhere in the stored content.</summary>
    public const string NumberNotFound = "SUPPORT_NUMBER_NOT_FOUND";

    /// <summary>The access level cannot support this kind of claim.</summary>
    public const string AccessInsufficient = "SUPPORT_ACCESS_INSUFFICIENT";

    /// <summary>No content is asserted, so nothing was checked.</summary>
    public const string NoContentClaim = "SUPPORT_NO_CONTENT_CLAIM";
}

/// <summary>
/// Result of checking one evidence item's content against the source.
/// </summary>
public sealed record SupportVerification(
    EvidenceIntegrityStatus Status,
    IReadOnlyList<string> ReasonCodes,
    /// <summary>Share of checked tokens found, when a token check applied.</summary>
    double? Overlap,
    IReadOnlyList<EvidenceFlag> Flags);

/// <summary>
/// Checks that quoted or reported content is actually present in the stored text.
///
/// The check runs against the content held at the source's access level and nothing
/// else: for a metadata-only record there is no text, so no quotation can pass, and
/// for an abstract-level record a sentence from the unread body of the paper fails.
/// Quantitative findings are checked number by number, because a fabricated
/// percentage is the most damaging invention a student's review can carry and the
/// easiest one to detect mechanically.
/// </summary>
public static partial class QuotedSupportVerifier
{
    /// <summary>Token overlap a paraphrase must reach to be called grounded.</summary>
    public const double ParaphraseGroundingThreshold = 0.45;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "that", "this", "with", "were", "was", "are", "has", "have", "had",
        "not", "but", "from", "they", "their", "there", "which", "about", "into", "over", "than",
        "been", "being", "can", "could", "would", "should", "may", "might", "will", "all", "any",
        "such", "also", "more", "most", "other", "some", "these", "those", "then", "when", "where",
        "whether", "while", "because", "between", "during", "after", "before", "each", "per",
    };

    [GeneratedRegex(@"\d+(?:[.,]\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Verify one evidence item's content against the source.
    /// </summary>
    public static SupportVerification Verify(
        NormalizedBibliographicRecord record,
        EvidenceSupportKind kind,
        string? content,
        string? claim)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (kind is EvidenceSupportKind.BibliographicOnly or EvidenceSupportKind.AbsenceOfEvidence)
        {
            return new SupportVerification(EvidenceIntegrityStatus.Unchecked, [SupportVerificationCodes.NoContentClaim],
                null, []);
        }

        var searchable = TextNormalizer.Normalize(AccessLevelPolicy.GetSearchableText(record));
        if (searchable.Length == 0)
        {
            return new SupportVerification(EvidenceIntegrityStatus.NotSupportedAtAccessLevel,
                [SupportVerificationCodes.AccessInsufficient], null,
                [new EvidenceFlag
                {
                    Code = SupportVerificationCodes.AccessInsufficient,
                    Severity = FindingSeverity.Blocking,
                    Message = $"No text is stored for this source at its {record.AccessLevel} access level, so the "
                        + "claim cannot be checked against it and will not be exported.",
                }]);
        }

        var candidate = TextNormalizer.Normalize(
            string.IsNullOrWhiteSpace(content) ? claim : content + " " + claim);
        if (candidate.Length == 0)
        {
            return new SupportVerification(EvidenceIntegrityStatus.UnsupportedByStoredText,
                [SupportVerificationCodes.QuoteNotFound], null,
                [new EvidenceFlag
                {
                    Code = SupportVerificationCodes.QuoteNotFound,
                    Severity = FindingSeverity.Blocking,
                    Message = "The evidence item carries no text to check.",
                }]);
        }

        return kind switch
        {
            EvidenceSupportKind.DirectQuote => VerifyQuote(candidate, searchable),
            EvidenceSupportKind.QuantitativeFinding => VerifyNumbers(candidate, searchable),
            _ => VerifyParaphrase(candidate, searchable),
        };
    }

    /// <summary>
    /// Locate a quote in the stored text and return the span it occupies, which is
    /// what makes an <see cref="LocatorOrigin.AnchoredExtraction"/> honest: the span
    /// is measured, not asserted.
    /// </summary>
    /// <returns>Zero-based start and end in the original text, or null when absent.</returns>
    public static (int Start, int End)? FindQuoteSpan(
        NormalizedBibliographicRecord record,
        string quote,
        out bool matched)
    {
        matched = false;
        var haystack = AccessLevelPolicy.GetSearchableText(record);
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrWhiteSpace(quote)) return null;

        // Compare in a normalized space but return offsets into the stored text by
        // re-anchoring on the first and last normalized tokens.
        var normalizedHay = TextNormalizer.Normalize(haystack);
        var normalizedQuote = TextNormalizer.Normalize(quote);
        if (normalizedQuote.Length == 0) return null;

        var index = normalizedHay.IndexOf(normalizedQuote, StringComparison.Ordinal);
        if (index < 0) return null;

        matched = true;
        var firstToken = TokenRegex().Match(quote);
        var lastToken = LastMatch(TokenRegex(), quote);
        if (!firstToken.Success || lastToken is not { } tail) return null;

        var start = haystack.IndexOf(firstToken.Value, 0, StringComparison.OrdinalIgnoreCase);
        var end = haystack.IndexOf(tail.Value, start, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0) return null;

        return (start, end + tail.Value.Length);
    }

    private static SupportVerification VerifyQuote(string candidate, string searchable)
    {
        var found = searchable.Contains(candidate, StringComparison.Ordinal);
        return found
            ? new SupportVerification(EvidenceIntegrityStatus.VerifiedAgainstSource,
                [SupportVerificationCodes.QuoteLocated], 1, [])
            : new SupportVerification(EvidenceIntegrityStatus.UnsupportedByStoredText,
                [SupportVerificationCodes.QuoteNotFound], 0,
                [new EvidenceFlag
                {
                    Code = SupportVerificationCodes.QuoteNotFound,
                    Severity = FindingSeverity.Blocking,
                    Message = "The quotation does not appear in the text held for this source. It was not "
                        + "exported: either the full text is needed, or the quotation is not from this work.",
                }]);
    }

    private static SupportVerification VerifyParaphrase(string candidate, string searchable)
    {
        var tokens = SignificantTokens(candidate);
        if (tokens.Count == 0)
        {
            return new SupportVerification(EvidenceIntegrityStatus.UnsupportedByStoredText,
                [SupportVerificationCodes.ParaphraseUngrounded], 0,
                [new EvidenceFlag
                {
                    Code = SupportVerificationCodes.ParaphraseUngrounded,
                    Severity = FindingSeverity.Warning,
                    Message = "The restatement carries no checkable terms, so it is kept as an author's own "
                        + "wording rather than as sourced evidence.",
                }]);
        }

        var hits = tokens.Count(token => searchable.Contains(token, StringComparison.Ordinal));
        var overlap = (double)hits / tokens.Count;

        if (overlap >= ParaphraseGroundingThreshold)
        {
            return new SupportVerification(EvidenceIntegrityStatus.VerifiedAgainstSource,
                [SupportVerificationCodes.ParaphraseGrounded], Math.Round(overlap, 4), []);
        }

        return new SupportVerification(EvidenceIntegrityStatus.UnsupportedByStoredText,
            [SupportVerificationCodes.ParaphraseUngrounded], Math.Round(overlap, 4),
            [new EvidenceFlag
            {
                Code = SupportVerificationCodes.ParaphraseUngrounded,
                Severity = FindingSeverity.Blocking,
                Message = $"Only {Percent(overlap)} of the terms in this restatement appear in the text held for "
                    + "the source, so it is not treated as evidence from it.",
            }]);
    }

    private static SupportVerification VerifyNumbers(string candidate, string searchable)
    {
        var numbers = NumberRegex().Matches(candidate)
            .Select(m => m.Value)
            .Where(v => v.Length > 1 || v is not ("0" or "1"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (numbers.Count == 0)
        {
            // A finding with no figure is checked as a paraphrase instead, since
            // "there was no reported difference" is a claim about words, not numbers.
            return VerifyParaphrase(candidate, searchable);
        }

        var searchableNumbers = NumberRegex().Matches(searchable)
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = numbers.Where(n => !searchableNumbers.Contains(n) && !searchableNumbers.Contains(WithoutSeparator(n)))
            .ToList();

        if (missing.Count == 0)
        {
            return new SupportVerification(EvidenceIntegrityStatus.VerifiedAgainstSource,
                [SupportVerificationCodes.NumbersLocated], 1, []);
        }

        return new SupportVerification(EvidenceIntegrityStatus.UnsupportedByStoredText,
            [SupportVerificationCodes.NumberNotFound], 0,
            [new EvidenceFlag
            {
                Code = SupportVerificationCodes.NumberNotFound,
                Severity = FindingSeverity.Blocking,
                Message = "The reported figure(s) " + string.Join(", ", missing)
                    + " do not appear anywhere in the text held for this source, so the finding was not attributed to it.",
            }]);
    }

    private static HashSet<string> SignificantTokens(string normalizedCandidate)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TokenRegex().Matches(normalizedCandidate))
        {
            var token = match.Value;
            if (token.Length < 4) continue;
            if (StopWords.Contains(token)) continue;
            tokens.Add(token);
        }

        return tokens;
    }

    private static string WithoutSeparator(string number) => number.Replace(",", "");

    private static string Percent(double value) =>
        Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + "%";

    private static Match? LastMatch(Regex regex, string value)
    {
        Match? last = null;
        foreach (Match match in regex.Matches(value)) last = match;
        return last;
    }
}

/// <summary>
/// Text folding used for every content comparison in this lane: case, whitespace,
/// quote and dash variants and punctuation are removed, but digits, letters and
/// diacritics are preserved, so a Kurdish quotation is matched as written.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Fold text for substring comparison.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var folded = character switch
            {
                '\u2018' or '\u2019' or '\u201B' => '\'',
                '\u201C' or '\u201D' or '\u00AB' or '\u00BB' => '"',
                '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                '\u00A0' or '\u2002' or '\u2003' or '\u2009' or '\u202F' => ' ',
                '\t' or '\n' or '\r' => ' ',
                _ => character,
            };

            if (folded == ' ')
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }

            if (char.IsPunctuation(folded) || char.IsSymbol(folded))
            {
                if (folded is '\'' or '"' or '-' or '/' or '%')
                {
                    builder.Append(char.ToLowerInvariant(folded));
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(folded));
        }

        return builder.ToString().Trim();
    }
}
