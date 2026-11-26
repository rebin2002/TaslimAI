using Taslim.Api.Contracts;

namespace Taslim.Api.Research;

/// <summary>
/// Isolated, test-only ordering contract for the Research export citation
/// reference list (the "Sources" section). It pins how citation references
/// are ordered when several sources share a title: same-title sources are
/// grouped together by a normalized (whitespace-collapsed, case-insensitive)
/// title key and then sequenced by an ordinal, culture-independent citation
/// identifier, so the rendered reference order is byte-stable regardless of
/// provider discovery order, ranks, or retrieval timestamps.
///
/// This helper is deliberately NOT wired into <see cref="ResearchDocumentMapper"/>;
/// production wiring is a separate review-owned change. Citation URLs are
/// emitted exactly as stored — no canonicalization or escaping is performed
/// here (that behaviour is owned by the citation URL canonicalization lane).
/// </summary>
internal static class ResearchExportReferenceOrdering
{
    /// <summary>Heading of the export section that carries the citation reference list.</summary>
    internal const string SourcesHeading = "Sources";

    /// <summary>Literal locator suffix used for sources without a URL (uploaded files).</summary>
    internal const string UploadedSourceLocator = "Uploaded source";

    /// <summary>
    /// Orders the citation reference list for export. The sequence is a total
    /// order over (normalized title, ordinal citation id): two runs with the
    /// same source set produce the same reference sequence no matter what
    /// order the sources arrived in.
    /// </summary>
    public static IReadOnlyList<ResearchSourceCandidate> OrderForExport(IReadOnlyList<ResearchSourceCandidate> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources
            .OrderBy(source => NormalizeTitle(source.Title), StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.CitationId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Formats one citation reference line. The layout matches the existing
    /// Sources-section renderer ([citation id] title — locator); the locator
    /// is the stored URL verbatim, or <see cref="UploadedSourceLocator"/>
    /// when the source has no URL.
    /// </summary>
    public static string FormatReference(ResearchSourceCandidate source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return $"[{source.CitationId}] {source.Title} — {source.Url ?? UploadedSourceLocator}";
    }

    /// <summary>
    /// The fully ordered, rendered reference list as one string per source.
    /// Joining these lines is the byte-stable export contract pinned by tests.
    /// </summary>
    public static IReadOnlyList<string> RenderReferences(IReadOnlyList<ResearchSourceCandidate> sources) =>
        OrderForExport(sources).Select(FormatReference).ToArray();

    /// <summary>
    /// Grouping key for titles: trims, collapses runs of internal whitespace
    /// to a single space, and compares case-insensitively (ordinal). Only
    /// casing and whitespace variants count as the "same title"; distinct
    /// wording stays distinct.
    /// </summary>
    internal static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        Span<char> buffer = stackalloc char[title.Length];
        int written = 0;
        bool pendingSpace = false;
        foreach (char c in title.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = written > 0;
                continue;
            }
            if (pendingSpace)
            {
                buffer[written++] = ' ';
                pendingSpace = false;
            }
            buffer[written++] = c;
        }
        return new string(buffer[..written]);
    }
}
