namespace Taslim.Api.AcademicEvidence.Domain;

/// <summary>
/// What one source does about one research question. The distinction between
/// <see cref="NoEvidence"/> and <see cref="Contradicts"/> is the point of the
/// whole matrix: a source that never addresses a question is a coverage gap, not
/// a dissenting result.
/// </summary>
public enum SourceStance
{
    /// <summary>Not evaluated, the cell is empty.</summary>
    NotEvaluated = 0,

    /// <summary>Source is silent on the question: absence of evidence.</summary>
    NoEvidence,

    /// <summary>Reported findings point the same way as the question's hypothesis.</summary>
    Supports,

    /// <summary>Reported findings oppose it on a comparable claim.</summary>
    Contradicts,

    /// <summary>The source reports both directions, or results differ by subgroup.</summary>
    Mixed,

    /// <summary>Provides background or definition rather than a testable finding.</summary>
    Contextual,

    /// <summary>A claim exists but cannot be ordered against the others.</summary>
    Indeterminate,
}

/// <summary>A research question, objective or sub-question the matrix is built over.</summary>
public sealed record MatrixQuestion
{
    /// <summary>Stable key used by cells and gaps.</summary>
    public required string Key { get; init; }

    public required string Text { get; init; }

    /// <summary>Optional hypothesis the sources are tested against.</summary>
    public string? Hypothesis { get; init; }

    /// <summary>Discipline or method label recorded for the question.</summary>
    public string? Discipline { get; init; }
}

/// <summary>One question-by-source intersection.</summary>
public sealed record EvidenceMatrixCell
{
    public required string QuestionKey { get; init; }
    public required Guid SourceId { get; init; }

    public SourceStance Stance { get; init; } = SourceStance.NotEvaluated;

    /// <summary>Evidence items that produced the stance.</summary>
    public IReadOnlyList<Guid> EvidenceIds { get; init; } = [];

    /// <summary>
    /// True when at least one contributing item has a content-verified locator,
    /// so a chapter sentence can be cited with a pinpoint. Metadata-only cells
    /// are always false.
    /// </summary>
    public bool HasVerifiableLocator { get; init; }

    /// <summary>Lowest access level among contributing items.</summary>
    public SourceAccessLevel AccessLevel { get; init; } = SourceAccessLevel.Unknown;

    /// <summary>Provenance verdict of the source when the cell was built.</summary>
    public ProvenanceVerdict Provenance { get; init; } = ProvenanceVerdict.Unassessed;

    /// <summary>Quality tier of the source when the cell was built.</summary>
    public QualityTier Quality { get; init; } = QualityTier.InsufficientInformation;

    /// <summary>Screening decision recorded for the source.</summary>
    public ScreeningDecision Screening { get; init; } = ScreeningDecision.Unscheduled;

    public IReadOnlyList<EvidenceFlag> Flags { get; init; } = [];
}

/// <summary>A pairwise relationship between two sources' claims.</summary>
public sealed record MatrixRelationship
{
    public required string QuestionKey { get; init; }
    public required Guid LeftSourceId { get; init; }
    public required Guid RightSourceId { get; init; }

    public ClaimRelation Relation { get; init; } = ClaimRelation.NotComparable;

    /// <summary>Why the relation was decided, as reason codes.</summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    /// <summary>
    /// Human-readable explanation, only populated for a decided relation; an
    /// indeterminate pair states what is missing instead of inventing a verdict.
    /// </summary>
    public string? Explanation { get; init; }
}

/// <summary>
/// The evidence matrix: sources on one axis, research questions on the other,
/// with the pairwise claim relationships and the gaps that fall out of it. Built
/// deterministically from recorded evidence so a rebuild reproduces the same
/// ordering and the same findings.
/// </summary>
public sealed record EvidenceMatrix
{
    public required Guid WorkspaceId { get; init; }
    public required Guid ProjectId { get; init; }

    public IReadOnlyList<MatrixQuestion> Questions { get; init; } = [];
    public IReadOnlyList<Guid> SourceIds { get; init; } = [];
    public IReadOnlyList<EvidenceMatrixCell> Cells { get; init; } = [];
    public IReadOnlyList<MatrixRelationship> Relationships { get; init; } = [];
    public IReadOnlyList<ResearchGap> Gaps { get; init; } = [];

    /// <summary>Look up a cell; absent cells mean the source was silent.</summary>
    public EvidenceMatrixCell? Find(string questionKey, Guid sourceId)
    {
        foreach (var cell in Cells)
        {
            if (cell.QuestionKey == questionKey && cell.SourceId == sourceId) return cell;
        }

        return null;
    }

    /// <summary>Sources that take a comparable position on a question.</summary>
    public IReadOnlyList<EvidenceMatrixCell> CellsFor(string questionKey) =>
        [.. Cells.Where(c => c.QuestionKey == questionKey)];
}

/// <summary>
/// A weakness in the reviewed evidence base, phrased as what is missing rather
/// than as a finding, so a gap can never be cited as a result.
/// </summary>
public sealed record ResearchGap
{
    public required ResearchGapKind Kind { get; init; }

    /// <summary>Question the gap applies to; null for corpus-wide gaps.</summary>
    public string? QuestionKey { get; init; }

    public FindingSeverity Severity { get; init; } = FindingSeverity.Warning;

    /// <summary>Sources involved, when the gap is about specific ones.</summary>
    public IReadOnlyList<Guid> SourceIds { get; init; } = [];

    /// <summary>Member-facing description of the missing evidence.</summary>
    public required string Description { get; init; }

    /// <summary>Suggested next search or acquisition step, without promising a result exists.</summary>
    public string? SuggestedStep { get; init; }
}
