using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Provenance;

/// <summary>
/// What a source's access level permits. The academic lane's core honesty rule is
/// that catalog metadata and read content are different states, so this table is
/// the single place that decides whether a quotation, a paraphrase, a page number
/// or a reported number may be attached to a source at all.
/// </summary>
/// <param name="AccessLevel">Level the source actually reached.</param>
/// <param name="MayCite">The work may appear in a reference list.</param>
/// <param name="MayBibliograph">
/// Descriptive fields such as edition and page range may be printed, because they
/// came from the catalog record rather than from the text.
/// </param>
/// <param name="MayQuote">Verbatim quotation may be recorded.</param>
/// <param name="MayParaphrase">Content may be restated.</param>
/// <param name="MayAssertPageNumbers">A printed page pinpoint may be recorded.</param>
/// <param name="MayAssertQuantitativeFinding">A reported number may be attributed.</param>
/// <param name="BlockingReasons">Why the strongest capabilities are off.</param>
public readonly record struct AccessLevelCapabilities(
    SourceAccessLevel AccessLevel,
    bool MayCite,
    bool MayBibliograph,
    bool MayQuote,
    bool MayParaphrase,
    bool MayAssertPageNumbers,
    bool MayAssertQuantitativeFinding,
    IReadOnlyList<string> BlockingReasons);

/// <summary>Evaluates an access level against the capability table.</summary>
public static class AccessLevelPolicy
{
    /// <summary>Reason code for the metadata-versus-content boundary.</summary>
    public const string MetadataOnlyReason = "ACCESS_METADATA_ONLY";

    /// <summary>Reason code when only an abstract is held.</summary>
    public const string AbstractOnlyReason = "ACCESS_ABSTRACT_ONLY";

    /// <summary>Reason code when nothing was retrieved.</summary>
    public const string NothingHeldReason = "ACCESS_NOTHING_HELD";

    /// <summary>Reason code for the page-number boundary.</summary>
    public const string NoPaginationReason = "ACCESS_NO_PAGINATION_BASIS";

    /// <summary>
    /// Resolve the capabilities for one level.
    /// </summary>
    public static AccessLevelCapabilities Evaluate(SourceAccessLevel level)
    {
        var reasons = new List<string>();

        return level switch
        {
            SourceAccessLevel.Unknown => new AccessLevelCapabilities(
                level, MayCite: false, MayBibliograph: false, MayQuote: false, MayParaphrase: false,
                MayAssertPageNumbers: false, MayAssertQuantitativeFinding: false,
                [NothingHeldReason]),

            // A catalog record proves the work exists and how to cite it. It does
            // not prove anything was read, so no content capability is granted.
            SourceAccessLevel.MetadataOnly => new AccessLevelCapabilities(
                level, MayCite: true, MayBibliograph: true, MayQuote: false, MayParaphrase: false,
                MayAssertPageNumbers: false, MayAssertQuantitativeFinding: false,
                [MetadataOnlyReason, NoPaginationReason]),

            // A publisher abstract is content, but only the abstract: quotations
            // are allowed against it, printed pages are not, and a number that
            // appears in it is labelled as abstract-level support.
            SourceAccessLevel.Abstract => new AccessLevelCapabilities(
                level, MayCite: true, MayBibliograph: true, MayQuote: true, MayParaphrase: true,
                MayAssertPageNumbers: false, MayAssertQuantitativeFinding: true,
                [AbstractOnlyReason, NoPaginationReason]),

            SourceAccessLevel.FullText => new AccessLevelCapabilities(
                level, MayCite: true, MayBibliograph: true, MayQuote: true, MayParaphrase: true,
                MayAssertPageNumbers: true, MayAssertQuantitativeFinding: true, reasons),

            // The member's own manuscript is readable but unpublished: content
            // capabilities apply, and quality screening keeps it out of the
            // peer-reviewed tally.
            SourceAccessLevel.PrivateUpload => new AccessLevelCapabilities(
                level, MayCite: true, MayBibliograph: true, MayQuote: true, MayParaphrase: true,
                MayAssertPageNumbers: true, MayAssertQuantitativeFinding: true,
                ["ACCESS_PRIVATE_UPLOAD_NOT_PUBLISHED"]),

            _ => new AccessLevelCapabilities(
                level, false, false, false, false, false, false, [NothingHeldReason]),
        };
    }

    /// <summary>
    /// True when the level is strong enough to support a claim about content.
    /// Metadata-only records always fail this, which is what stops a catalog hit
    /// from becoming a quotation.
    /// </summary>
    public static bool SupportsContentClaims(SourceAccessLevel level) =>
        level is SourceAccessLevel.Abstract
            or SourceAccessLevel.FullText
            or SourceAccessLevel.PrivateUpload;

    /// <summary>
    /// True when printed pagination can honestly be claimed: an abstract has no
    /// pages of its own, and an unknown level has no artifact at all.
    /// </summary>
    public static bool SupportsPrintedPagination(SourceAccessLevel level) =>
        level is SourceAccessLevel.FullText or SourceAccessLevel.PrivateUpload;

    /// <summary>
    /// The text a content check should search. Abstract-level sources are checked
    /// against the abstract only, so a sentence that exists in the unread full
    /// text cannot pass.
    /// </summary>
    public static string? GetSearchableText(NormalizedBibliographicRecord record) => record.AccessLevel switch
    {
        SourceAccessLevel.FullText or SourceAccessLevel.PrivateUpload
            => string.IsNullOrWhiteSpace(record.FullText) ? record.AbstractText : record.FullText,
        SourceAccessLevel.Abstract => record.AbstractText,
        _ => null,
    };

    /// <summary>
    /// Downgrade a claimed level to what the stored content supports. Used when a
    /// provider advertises open access but no copy was actually retrieved.
    /// </summary>
    public static SourceAccessLevel Reconcile(
        SourceAccessLevel claimed,
        NormalizedBibliographicRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.FullText))
        {
            return claimed == SourceAccessLevel.PrivateUpload ? SourceAccessLevel.PrivateUpload : SourceAccessLevel.FullText;
        }

        if (!string.IsNullOrWhiteSpace(record.AbstractText)) return SourceAccessLevel.Abstract;
        return SourceAccessLevel.MetadataOnly;
    }
}
