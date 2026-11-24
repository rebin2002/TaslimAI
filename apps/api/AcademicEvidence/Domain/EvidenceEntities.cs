namespace Taslim.Api.AcademicEvidence.Domain;

/// <summary>
/// A pinpoint reference attached to evidence. A locator is only admissible when
/// an origin that can actually observe pagination supplied it, which is the
/// mechanism behind the no-fabricated-page-number rule: see
/// <c>SourceLocatorPolicy</c>.
/// </summary>
public sealed record SourceLocator
{
    public LocatorKind Kind { get; init; } = LocatorKind.None;

    /// <summary>Where the locator came from.</summary>
    public LocatorOrigin Origin { get; init; } = LocatorOrigin.Unknown;

    /// <summary>
    /// Printed label exactly as read, for example <c>"412"</c> or
    /// <c>"412-419"</c> or <c>"e0261147"</c> for an article number.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>Which page numbering the label refers to.</summary>
    public PaginationBasis PaginationBasis { get; init; } = PaginationBasis.NotApplicable;

    /// <summary>
    /// Character span inside the stored text that the quote was taken from, used
    /// to keep an <see cref="LocatorOrigin.AnchoredExtraction"/> honest. A span
    /// that no longer matches the stored text invalidates the locator.
    /// </summary>
    public int? AnchorStart { get; init; }

    public int? AnchorEnd { get; init; }

    /// <summary>True when the locator points at nothing, which is a legal state.</summary>
    public bool IsEmpty => Kind == LocatorKind.None || string.IsNullOrWhiteSpace(Label);
}

/// <summary>
/// One extractable unit of evidence: what the source is said to support, the
/// content that supports it, and the integrity verdict of the check that was run
/// against the stored text.
/// </summary>
public sealed record EvidenceItem
{
    public required Guid EvidenceId { get; init; }
    public required Guid WorkspaceId { get; init; }

    /// <summary>The source this evidence is attributed to.</summary>
    public required Guid SourceId { get; init; }

    /// <summary>Research question or theme this evidence answers.</summary>
    public required string Topic { get; init; }

    public EvidenceSupportKind SupportKind { get; init; } = EvidenceSupportKind.BibliographicOnly;

    /// <summary>
    /// Verbatim text quoted from the stored content, or a condensed restatement.
    /// Direct quotes must appear in the stored text; see
    /// <c>QuotedSupportVerifier</c>.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>The member's or analyst's claim that the content supports.</summary>
    public string? Claim { get; init; }

    /// <summary>Structured form of the claim, used by the matrix comparer.</summary>
    public EvidenceClaim? StructuredClaim { get; init; }

    public SourceLocator? Locator { get; init; }

    /// <summary>Access level the source held when this evidence was recorded.</summary>
    public SourceAccessLevel AccessLevelAtRecord { get; init; } = SourceAccessLevel.Unknown;

    public EvidenceIntegrityStatus Integrity { get; init; } = EvidenceIntegrityStatus.Unchecked;

    /// <summary>Reason codes behind <see cref="Integrity"/>.</summary>
    public IReadOnlyList<string> IntegrityReasons { get; init; } = [];

    /// <summary>Flags carried on the item, for export disclosure.</summary>
    public IReadOnlyList<EvidenceFlag> Flags { get; init; } = [];

    /// <summary>
    /// True when the item may be exported into a chapter: content-bearing items
    /// must have passed the integrity check, while bibliographic-only and
    /// absence-of-evidence rows make no claim about unread content. A locator
    /// with an unrecorded origin also blocks export, because the citation would
    /// print a pinpoint that no artifact was read from.
    /// </summary>
    public bool IsExportable
    {
        get
        {
            if (Locator is { IsEmpty: false } locator && locator.Origin == LocatorOrigin.Unknown) return false;
            return SupportKind switch
            {
                // Neither of these asserts anything about unread content: one
                // cites the source's existence, the other reports a search
                // result. Both stay exportable before integrity checking.
                EvidenceSupportKind.BibliographicOnly => true,
                EvidenceSupportKind.AbsenceOfEvidence => true,
                EvidenceSupportKind.QuantitativeFinding
                    or EvidenceSupportKind.DirectQuote
                    or EvidenceSupportKind.Paraphrase
                    => Integrity == EvidenceIntegrityStatus.VerifiedAgainstSource,
                _ => false,
            };
        }
    }

    public DateTimeOffset RecordedAtUtc { get; init; }
}

/// <summary>
/// A comparable finding reported by a source. Claims are structured so agreement
/// and contradiction are decided by declared subject, predicate and magnitude
/// rather than by text similarity, which cannot tell "increased" from
/// "decreased" reliably across paraphrase.
/// </summary>
public sealed record EvidenceClaim
{
    /// <summary>Normalised key of what is being studied.</summary>
    public required string SubjectKey { get; init; }

    /// <summary>Normalised key of the relationship asserted.</summary>
    public required string PredicateKey { get; init; }

    public EffectDirection Direction { get; init; } = EffectDirection.Unknown;

    /// <summary>Reported point estimate, when the source gives one.</summary>
    public double? Magnitude { get; init; }

    /// <summary>Unit exactly as reported, required for any magnitude comparison.</summary>
    public string? Unit { get; init; }

    /// <summary>Lower bound of the reported interval, when one exists.</summary>
    public double? IntervalLow { get; init; }

    /// <summary>Upper bound of the reported interval, when one exists.</summary>
    public double? IntervalHigh { get; init; }

    /// <summary>Normalised population key, for example <c>kurdistan-iraq:pharma</c>.</summary>
    public string PopulationKey { get; init; } = string.Empty;

    /// <summary>Timeframe label the finding covers.</summary>
    public string? Timeframe { get; init; }

    /// <summary>True when both bounds are present.</summary>
    public bool HasInterval => IntervalLow.HasValue && IntervalHigh.HasValue;

    /// <summary>True when the interval excludes a null effect, if one can be judged.</summary>
    public bool? ExcludesNull
    {
        get
        {
            if (!HasInterval) return null;
            var nullPoint = NullPointFor(Unit);
            if (nullPoint is null) return null;
            return IntervalLow!.Value > nullPoint.Value || IntervalHigh!.Value < nullPoint.Value;
        }
    }

    /// <summary>
    /// The value that represents "no effect" for a unit. Unknown units return
    /// null so the comparer reports <see cref="ClaimRelation.Indeterminate"/>
    /// instead of assuming a ratio scale.
    /// </summary>
    private static double? NullPointFor(string? unit) => unit?.Trim().ToLowerInvariant() switch
    {
        "or" or "odds ratio" or "hr" or "hazard ratio" or "rr" or "risk ratio" or "ratio" => 1.0,
        "pp" or "percentage points" or "difference" or "mean difference" or "md" or "smd" => 0.0,
        _ => null,
    };
}
