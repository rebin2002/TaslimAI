using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Normalization;

/// <summary>
/// Translates provider type tokens into <see cref="AcademicSourceKind"/>.
///
/// The vocabularies differ across the scholarly APIs - Crossref writes
/// <c>journal-article</c>, OpenAlex writes <c>article</c>, a repository may write
/// <c>Dissertation</c> - and a token that is not in the table returns null so the
/// normalizer records an unmapped token instead of guessing a form the work may
/// not have.
/// </summary>
public static class SourceKindMap
{
    private static readonly Dictionary<string, AcademicSourceKind> Tokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Crossref.
            ["journal-article"] = AcademicSourceKind.JournalArticle,
            ["journal-issue"] = AcademicSourceKind.Other,
            ["journal-review-article"] = AcademicSourceKind.ReviewArticle,
            ["book-chapter"] = AcademicSourceKind.BookChapter,
            ["book-section"] = AcademicSourceKind.BookChapter,
            ["monograph"] = AcademicSourceKind.Book,
            ["book"] = AcademicSourceKind.Book,
            ["book-set"] = AcademicSourceKind.EditedVolume,
            ["edited-book"] = AcademicSourceKind.EditedVolume,
            ["reference-book"] = AcademicSourceKind.EditedVolume,
            ["proceedings-article"] = AcademicSourceKind.ConferencePaper,
            ["proceedings-series-chapter"] = AcademicSourceKind.ConferencePaper,
            ["conference-paper"] = AcademicSourceKind.ConferencePaper,
            ["conference-abstract"] = AcademicSourceKind.ConferencePaper,
            ["dissertation"] = AcademicSourceKind.Thesis,
            ["report"] = AcademicSourceKind.Report,
            ["standard"] = AcademicSourceKind.Standard,
            ["dataset"] = AcademicSourceKind.Dataset,
            ["posted-content"] = AcademicSourceKind.Preprint,
            ["peer-review"] = AcademicSourceKind.ReviewArticle,
            ["reference-entry"] = AcademicSourceKind.BookChapter,
            ["component"] = AcademicSourceKind.Dataset,

            // OpenAlex.
            ["article"] = AcademicSourceKind.JournalArticle,
            ["biblio"] = AcademicSourceKind.Other,
            ["dissertation-article"] = AcademicSourceKind.Thesis,
            ["editorial"] = AcademicSourceKind.Other,
            ["library-of-congress-subject-headings"] = AcademicSourceKind.Dataset,
            ["letter"] = AcademicSourceKind.Other,
            ["news"] = AcademicSourceKind.Other,
            ["retraction"] = AcademicSourceKind.Other,
            ["erratum"] = AcademicSourceKind.Other,
            ["review"] = AcademicSourceKind.ReviewArticle,
            ["preprint"] = AcademicSourceKind.Preprint,
            ["registry"] = AcademicSourceKind.Dataset,

            // DOAJ, repositories and hand-entered records.
            ["research-article"] = AcademicSourceKind.JournalArticle,
            ["original-article"] = AcademicSourceKind.JournalArticle,
            ["thesis"] = AcademicSourceKind.Thesis,
            ["master thesis"] = AcademicSourceKind.Thesis,
            ["phd thesis"] = AcademicSourceKind.Thesis,
            ["clinical guideline"] = AcademicSourceKind.ClinicalGuideline,
            ["guideline"] = AcademicSourceKind.ClinicalGuideline,
            ["technical report"] = AcademicSourceKind.Report,
            ["working paper"] = AcademicSourceKind.Report,
            ["policy brief"] = AcademicSourceKind.Report,
            ["white paper"] = AcademicSourceKind.Report,
            ["standard document"] = AcademicSourceKind.Standard,
            ["web page"] = AcademicSourceKind.Other,
            ["website"] = AcademicSourceKind.Other,

            // Semantic Scholar / arXiv style.
            ["journal"] = AcademicSourceKind.JournalArticle,
            ["conference"] = AcademicSourceKind.ConferencePaper,
            ["review-article"] = AcademicSourceKind.ReviewArticle,
            ["case-report"] = AcademicSourceKind.JournalArticle,
            ["short survey"] = AcademicSourceKind.ReviewArticle,
            ["book section"] = AcademicSourceKind.BookChapter,
            ["book section (book-chapter)"] = AcademicSourceKind.BookChapter,
            ["chapter"] = AcademicSourceKind.BookChapter,
        };

    /// <summary>
    /// Map one token, or null when the token is empty or unknown. Surrounding
    /// punctuation and repository decorations are trimmed first.
    /// </summary>
    public static AcademicSourceKind? Map(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Providers are inconsistent about case ("Journal Article", "journal-article")
        // and about the separator, so both spellings of a hyphen are tried.
        var candidate = token.Trim()
            .Trim('.', ',', ';', ')', ']')
            .Replace('_', '-')
            .ToLowerInvariant();

        if (Tokens.TryGetValue(candidate, out var direct)) return direct;

        // Some repositories append a language or version decoration, for example
        // "article;abstract" or "Journal Article".
        var semicolon = candidate.IndexOf(';');
        if (semicolon > 0 && Tokens.TryGetValue(candidate[..semicolon].Trim(), out var trimmedKind)) return trimmedKind;

        var collapsed = candidate.Replace("-", " ");
        if (Tokens.TryGetValue(collapsed, out var spacedKind)) return spacedKind;
        return Tokens.TryGetValue(candidate.Replace(" ", "-"), out var hyphenKind) ? hyphenKind : null;
    }

    /// <summary>
    /// The default peer-review expectation for a kind. This is an expectation,
    /// not a claim about the work: it is only ever used to raise a flag when the
    /// provenance does not record the actual status.
    /// </summary>
    public static bool ExpectsPeerReview(AcademicSourceKind kind) => kind switch
    {
        AcademicSourceKind.JournalArticle => true,
        AcademicSourceKind.ReviewArticle => true,
        AcademicSourceKind.ClinicalGuideline => true,
        AcademicSourceKind.Thesis => false,
        AcademicSourceKind.Preprint => false,
        AcademicSourceKind.Report => false,
        _ => false,
    };

    /// <summary>True when a kind is a whole book rather than a part of one.</summary>
    public static bool IsBookLevel(AcademicSourceKind kind) => kind
        is AcademicSourceKind.Book
        or AcademicSourceKind.EditedVolume;
}
