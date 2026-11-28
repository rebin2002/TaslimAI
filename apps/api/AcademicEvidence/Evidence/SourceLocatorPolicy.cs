using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Evidence;

/// <summary>Codes explaining why a pinpoint was kept or thrown away.</summary>
public static class LocatorDecisionCodes
{
    /// <summary>No pinpoint was supplied, which is a legal and honest state.</summary>
    public const string NoneSupplied = "LOCATOR_NONE";

    /// <summary>Admitted as read from an artifact or a registry.</summary>
    public const string Admitted = "LOCATOR_ADMITTED";

    /// <summary>The origin cannot have observed a page number.</summary>
    public const string OriginUnsourced = "LOCATOR_ORIGIN_UNSOURCED";

    /// <summary>No paginated artifact is held, so no page may be claimed.</summary>
    public const string NoArtifact = "LOCATOR_NO_ARTIFACT";

    /// <summary>The anchored span no longer matches the stored text.</summary>
    public const string AnchorMismatch = "LOCATOR_ANCHOR_MISMATCH";

    /// <summary>The label is empty or unreadable.</summary>
    public const string MalformedLabel = "LOCATOR_MALFORMED";

    /// <summary>Accepted, but the basis is a member's own reading of a print copy.</summary>
    public const string MemberAsserted = "LOCATOR_MEMBER_ASSERTED";

    /// <summary>Accepted, and must be printed as the PDF's page rather than the journal's.</summary>
    public const string FilePagination = "LOCATOR_FILE_PAGINATION";
}

/// <summary>
/// Result of admitting a locator: either the locator to store, or the reason it
/// was discarded. A discarded locator never degrades into a blank page number, it
/// stays absent, which is what a supervisor should see.
/// </summary>
public sealed record LocatorDecision(
    bool Admitted,
    string Code,
    SourceLocator? Locator,
    IReadOnlyList<EvidenceFlag> Flags);

/// <summary>
/// Decides whether a pinpoint reference may be attached to evidence.
///
/// This is the strict no-fabricated-page-number rule. A page number can only come
/// from somewhere that had pages in front of it: a registry deposit, the retrieved
/// copy, an anchored span inside the stored text, or the member's own reading. A
/// locator with no recorded origin, or one whose origin is a model's inference, is
/// discarded, and it is discarded even when the number happens to be right - the
/// platform cannot know that it is, and a bibliography that prints unverifiable
/// pinpoints teaches members that invented ones are acceptable.
/// </summary>
public static class SourceLocatorPolicy
{
    /// <summary>
    /// Evaluate a candidate locator against the source that would carry it.
    /// </summary>
    /// <param name="locator">Candidate, may be null.</param>
    /// <param name="record">Source the locator is attached to.</param>
    /// <param name="storedText">Text actually held, used to validate anchors.</param>
    /// <param name="assertsContent">True when the evidence item makes a claim about content.</param>
    public static LocatorDecision Admit(
        SourceLocator? locator,
        NormalizedBibliographicRecord record,
        string? storedText,
        bool assertsContent)
    {
        if (locator is null || locator.IsEmpty)
        {
            return new LocatorDecision(true, LocatorDecisionCodes.NoneSupplied, null, []);
        }

        var flags = new List<EvidenceFlag>();
        var level = record.AccessLevel;

        // 1. Origin gate. This is the rule that blocks invented pinpoints.
        if (locator.Origin is LocatorOrigin.Unknown)
        {
            return Reject(LocatorDecisionCodes.OriginUnsourced,
                "A page or paragraph pinpoint was supplied without stating where it was read. "
                    + "It has been removed; the citation will show no pinpoint.");
        }

        if (locator.Origin == LocatorOrigin.ModelInference)
        {
            return Reject(LocatorDecisionCodes.OriginUnsourced,
                "A pinpoint produced by a model cannot be trusted to correspond to any paginated "
                    + "artifact, so it is never stored, even if the number looks plausible.");
        }

        // 2. The source must actually hold something with pages.
        var printedPage = locator.Kind is LocatorKind.Page or LocatorKind.PageRange;
        if (printedPage && !AccessLevelPolicy.SupportsPrintedPagination(level)
            && locator.Origin != LocatorOrigin.RegistryMetadata)
        {
            return Reject(LocatorDecisionCodes.NoArtifact,
                $"The source is held at {level} access, which has no paginated artifact to read a page "
                    + "number from, so the pinpoint was removed.");
        }

        // 3. Content-bearing locators need content.
        if (assertsContent && !AccessLevelPolicy.SupportsContentClaims(level))
        {
            return Reject(LocatorDecisionCodes.NoArtifact,
                "Metadata alone cannot be quoted or pinpointed, so no location was recorded.");
        }

        // 4. An anchored extraction must still anchor. A span that does not match
        // the stored text means the quote was edited after it was taken.
        if (locator.Origin == LocatorOrigin.AnchoredExtraction)
        {
            if (string.IsNullOrEmpty(storedText)
                || locator.AnchorStart is not { } start
                || locator.AnchorEnd is not { } end
                || start < 0 || end <= start || end > storedText.Length)
            {
                return Reject(LocatorDecisionCodes.AnchorMismatch,
                    "The stored passage no longer matches the position it was taken from, so the locator "
                        + "was discarded rather than cited against the wrong span.");
            }

            if (locator.Kind is LocatorKind.Page or LocatorKind.PageRange
                && locator.PaginationBasis != PaginationBasis.FilePagination)
            {
                // An anchored span proves where text sits in the retrieved file, not
                // which printed page it is on, so a span cannot certify published
                // pagination either. Downgrade rather than invent.
                return Reject(LocatorDecisionCodes.NoArtifact,
                    "The passage was located by position in the retrieved file, which does not establish a "
                        + "printed page number.");
            }
        }

        if (locator.Kind is LocatorKind.Page or LocatorKind.PageRange && string.IsNullOrWhiteSpace(locator.Label))
        {
            return Reject(LocatorDecisionCodes.MalformedLabel,
                "A page locator arrived without a usable page label.");
        }

        // 5. Accepted, with any basis it must be labelled by.
        var normalized = locator with
        {
            Label = NormalizeLabel(locator),
            PaginationBasis = locator.Kind is LocatorKind.Page or LocatorKind.PageRange
                ? locator.PaginationBasis == PaginationBasis.NotApplicable
                    // A page range deposited by a registry is the publisher's own
                    // pagination; a number read off a retrieved file is only that
                    // file's page order, and the citation has to say which.
                    ? locator.Origin == LocatorOrigin.RegistryMetadata
                        ? PaginationBasis.PublishedPagination
                        : PaginationBasis.FilePagination
                    : locator.PaginationBasis
                : PaginationBasis.NotApplicable,
        };

        if (normalized.PaginationBasis == PaginationBasis.FilePagination)
        {
            flags.Add(new EvidenceFlag
            {
                Code = LocatorDecisionCodes.FilePagination,
                Severity = FindingSeverity.Warning,
                Message = "This page number is from the page order of the retrieved file, which can differ from "
                    + "the printed journal pagination.",
            });
        }

        if (locator.Origin == LocatorOrigin.MemberProvided)
        {
            flags.Add(new EvidenceFlag
            {
                Code = LocatorDecisionCodes.MemberAsserted,
                Severity = FindingSeverity.Note,
                Message = "The pinpoint was read by the member from a copy in hand and has not been matched "
                    + "against a stored file.",
            });
        }

        return new LocatorDecision(true, LocatorDecisionCodes.Admitted, normalized, flags);
    }

    /// <summary>
    /// Render a locator for a citation, always labelled by its basis so a file page
    /// can never be read as a printed page. Returns null when there is nothing to
    /// print.
    /// </summary>
    public static string? FormatForCitation(SourceLocator? locator)
    {
        if (locator is null || locator.IsEmpty) return null;

        return locator.Kind switch
        {
            LocatorKind.Page => FormatPages(locator, isRange: false),
            LocatorKind.PageRange => FormatPages(locator, isRange: true),
            LocatorKind.ArticleNumber => $"art. {locator.Label}",
            LocatorKind.Paragraph => $"para. {locator.Label}",
            LocatorKind.Section => $"§ {locator.Label}",
            LocatorKind.Clause => $"cl. {locator.Label}",
            LocatorKind.Timestamp => locator.Label,
            _ => null,
        };
    }

    /// <summary>
    /// Print a page pinpoint. A label holding a range is pluralised even when the
    /// caller recorded it as a single page, so "412-419" never reads as one page.
    /// </summary>
    private static string FormatPages(SourceLocator locator, bool isRange)
    {
        var range = isRange || locator.Label?.Contains('-', StringComparison.Ordinal) == true;
        var prefix = range ? "pp. " : "p. ";
        var suffix = locator.PaginationBasis == PaginationBasis.FilePagination ? " [file pagination]" : string.Empty;
        return prefix + locator.Label + suffix;
    }

    private static string NormalizeLabel(SourceLocator locator)
    {
        var label = locator.Label?.Trim() ?? string.Empty;

        // Registry deposits arrive as "412–419" with an en dash and as "412-419"
        // with a hyphen; print one form.
        label = label.Replace('–', '-').Replace('—', '-');

        // A label that states its own unit is stripped, since the unit is added back
        // by the formatter with the correct basis.
        foreach (var prefix in new[] { "pp.", "p.", "page", "pages" })
        {
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                label = label[prefix.Length..].TrimStart(' ', ':', '.');
            }
        }

        return label.Trim();
    }

    private static LocatorDecision Reject(string code, string message) => new(false, code, null,
        [new EvidenceFlag
        {
            Code = code,
            Severity = FindingSeverity.Warning,
            Message = message,
        }]);
}
