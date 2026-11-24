using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Normalization;

/// <summary>
/// Turns a name token from a metadata origin into a
/// <see cref="BibliographicAuthor"/>. Metadata sources write the same person as
/// "Rebin Khoshnaw", "Khoshnaw, R." and "R. Ahmad Khoshnaw", so the value kept
/// for display is the origin's own string while the <c>MatchKey</c> is the
/// derived identity used for dedupe. No part of a name is ever expanded or
/// invented: an origin that supplied only initials yields an author with only
/// initials.
/// </summary>
public static partial class AuthorNameNormalizer
{
    /// <summary>
    /// Lowercase surname particles. A particle only joins the surname when it is
    /// followed by another token, which keeps a lone "De" from becoming a name.
    /// Covers the Dutch/German/French forms plus the Arabic and Kurdish
    /// patronymic articles common in the region's institutional repositories.
    /// </summary>
    private static readonly HashSet<string> SurnameParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "van", "von", "de", "del", "della", "der", "den", "di", "da", "dos", "du",
        "la", "le", "el", "al", "abu", "ibn", "bin", "binte", "ben", "ter", "ten", "af",
    };

    private static readonly HashSet<string> GenerationalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jr", "jr.", "sr", "sr.", "ii", "iii", "iv", "phd", "md",
    };

    [GeneratedRegex(@"^([^-–—])(?:[^-–—]|-[^\s])*[.‑]$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleInitialWithDotRegex();

    /// <summary>
    /// Parse one author token. Returns null only when the token carries no usable
    /// name at all, which is recorded as a warning rather than a fabricated author.
    /// </summary>
    public static BibliographicAuthor? Parse(
        string? token,
        AuthorRole role,
        string? orcid = null,
        string? affiliation = null,
        ICollection<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var display = CollapseWhitespace(token);
        var parts = display.Split([','], 2, StringSplitOptions.TrimEntries);

        string family;
        string? given;

        if (parts.Length == 2 && parts[1].Length > 0)
        {
            // Catalogue order: "Family, Given".
            family = parts[0];
            given = parts[1];
        }
        else
        {
            var nameParts = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nameParts.Length == 0) return null;

            if (nameParts.Length == 1)
            {
                family = nameParts[0];
                given = null;
            }
            else
            {
                // Monographic order, taking the trailing particle run into the
                // surname so "Ahmad van der Berg" does not become "Berg".
                var surnameStart = nameParts.Length - 1;
                while (surnameStart > 0 && IsParticle(nameParts[surnameStart - 1])) surnameStart--;
                if (nameParts.Length - surnameStart > 4) surnameStart = nameParts.Length - 1;

                family = string.Join(' ', nameParts[surnameStart..]);
                given = string.Join(' ', nameParts[..surnameStart]);

                // "Jr." belongs to neither name component.
                var lastGiven = given.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (lastGiven.Length > 0 && GenerationalSuffixes.Contains(lastGiven[^1]))
                {
                    family = family + ", " + lastGiven[^1];
                    given = string.Join(' ', lastGiven[..^1]);
                    if (given.Length == 0) given = null;
                }
            }
        }

        family = StripTrailingPunctuation(family);
        if (family.Length == 0) return null;

        var validatedOrcid = ValidateOrcidField(orcid, warnings);

        return new BibliographicAuthor
        {
            DisplayName = CollapseWhitespace(display),
            FamilyName = FoldForMatching(family),
            GivenNames = string.IsNullOrWhiteSpace(given) ? null : StripTrailingPunctuation(given),
            Orcid = validatedOrcid,
            Affiliation = string.IsNullOrWhiteSpace(affiliation) ? null : CollapseWhitespace(affiliation),
            Role = role,
            MatchKey = BuildMatchKey(family, given, validatedOrcid),
        };
    }

    /// <summary>
    /// Identity key: ORCID first, because it is the only author identifier that
    /// is globally unique; otherwise the folded surname plus the leading initial
    /// of every given name, so "Khoshnaw, R." and "Khoshnaw, Rebin" collide while
    /// "Khoshnaw, A." does not.
    /// </summary>
    public static string BuildMatchKey(string familyName, string? givenNames, string? orcid)
    {
        if (!string.IsNullOrWhiteSpace(orcid)) return "orcid:" + orcid;

        var family = FoldForMatching(familyName);
        if (string.IsNullOrWhiteSpace(givenNames)) return family;

        var initials = new StringBuilder(family.Length + 4);
        initials.Append(family).Append('|');
        foreach (var token in givenNames.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var folded = FoldForMatching(token);
            if (folded.Length == 0) continue;
            initials.Append(folded[0]);
        }

        return initials.ToString();
    }

    /// <summary>
    /// Case-fold, strip diacritics and drop punctuation so "Kurdî", "Kurdi" and
    /// "KURDI-" compare equal. Deliberately not a transliteration: a name written
    /// in Sorani script keeps its own characters, because folding scripts would
    /// merge distinct people.
    /// </summary>
    public static string FoldForMatching(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsPunctuation(character) || char.IsSymbol(character))
            {
                // Word-internal hyphens are part of a surname, so only spacing
                // punctuation and edge hyphens are dropped.
                if (character is not ('-' or '–' or '—')) continue;
                builder.Append('-');
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        var folded = builder.ToString().Replace("--", "-").Trim(' ', '-');
        return Regex.Replace(folded, @"\s+", " ");
    }

    /// <summary>True when the token looks like a bare initial such as "J.".</summary>
    public static bool IsInitialToken(string token) =>
        SingleInitialWithDotRegex().IsMatch(token.Trim());

    private static bool IsParticle(string token)
    {
        // Particles are only particles when written lowercase in the origin.
        if (token.Length == 0 || !char.IsLower(token[0])) return false;
        return SurnameParticles.Contains(token.TrimEnd('.'));
    }

    private static string? ValidateOrcidField(string? orcid, ICollection<string>? warnings)
    {
        if (string.IsNullOrWhiteSpace(orcid)) return null;

        var result = BibliographicIdentifierValidator.ValidateOrcid(orcid);
        if (result.IsValid) return result.Normalized;

        // A malformed ORCID is dropped instead of stored: an unusable iD would
        // become a false identity key shared with unrelated authors.
        warnings?.Add("ORCID_REJECTED:" + orcid.Trim());
        return null;
    }

    private static string StripTrailingPunctuation(string value) => value.Trim().TrimEnd('.', ',', ';');

    private static string CollapseWhitespace(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
}
