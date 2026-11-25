namespace Taslim.Api.AcademicEvidence.Persistence;

/// <summary>
/// Persisted normalized bibliographic record, one row per source.
///
/// The table stores the normalized record and nothing else: provenance, screening
/// and extracted evidence live in their own rows so a retraction rescreen cannot
/// rewrite bibliographic fields, and a full text re-ingest cannot quietly change a
/// verdict. <see cref="ContentSha256"/> is the digest of the stored text, which lets
/// a later run detect that the passage an evidence row was anchored to has moved.
/// </summary>
public sealed class AcademicSourceEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? OriginalScriptTitle { get; set; }
    public string SourceKind { get; set; } = "Unknown";
    public string? PublicationYear { get; set; }
    public string YearBasis { get; set; } = "Unknown";
    public DateTime? PublicationDateUtc { get; set; }
    public string? Publisher { get; set; }
    public string? EditionLabel { get; set; }
    public int? EditionNumber { get; set; }
    public string? Volume { get; set; }
    public string? Issue { get; set; }
    public string? PageRange { get; set; }
    public string? PageRangeOrigin { get; set; }
    public string? Venue { get; set; }
    public string AccessLevel { get; set; } = "Unknown";
    public string ContentAcquisition { get; set; } = "None";
    public string? LandingPageUrl { get; set; }
    public string? Language { get; set; }
    public string? Discipline { get; set; }
    public string? StudyDesign { get; set; }
    public string? CorpusId { get; set; }

    /// <summary>Normalized authors, canonical JSON.</summary>
    public string AuthorsJson { get; set; } = "[]";

    /// <summary>Normalized editors, canonical JSON.</summary>
    public string EditorsJson { get; set; } = "[]";

    /// <summary>Identifiers with their validation outcome, canonical JSON.</summary>
    public string IdentifiersJson { get; set; } = "[]";

    /// <summary>Publisher or provider abstract, when held.</summary>
    public string? AbstractText { get; set; }

    /// <summary>
    /// Stored full text. Nullable because most catalog records never obtain one,
    /// and its presence is what separates a read source from a listed one.
    /// </summary>
    public string? FullText { get; set; }

    /// <summary>Lowercased SHA-256 of <see cref="FullText"/>, empty when absent.</summary>
    public string ContentSha256 { get; set; } = string.Empty;

    /// <summary>Fingerprint used to dedupe against the rest of the library.</summary>
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>Source this row duplicates, when merged rather than dropped.</summary>
    public Guid? DuplicateOfSourceId { get; set; }

    /// <summary>Identity of the review protocol the row was screened under.</summary>
    public string? ProtocolId { get; set; }

    public DateTime NormalizedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Persisted provenance verdict, one row per source, replaced as a unit when a
/// rescreen runs.
/// </summary>
public sealed class AcademicProvenanceEntity
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Guid WorkspaceId { get; set; }

    public string Verdict { get; set; } = "Unassessed";
    public int CorroboratingOriginCount { get; set; }

    /// <summary>Reason codes, canonical JSON array.</summary>
    public string ReasonCodesJson { get; set; } = "[]";

    /// <summary>Field-level evidence, canonical JSON array.</summary>
    public string FieldEvidenceJson { get; set; } = "[]";

    /// <summary>Publication-integrity notices, canonical JSON array.</summary>
    public string RetractionNoticesJson { get; set; } = "[]";

    /// <summary>
    /// Denormalized so a listing query can show the badge without recomputing it;
    /// always written from <see cref="Domain.SourceProvenance.MayPresentAsVerified"/>.
    /// </summary>
    public bool MayPresentAsVerified { get; set; }

    public DateTime AssessedAtUtc { get; set; }
}

/// <summary>
/// Persisted screening outcome: the quality tier, the risk-of-bias appraisal and the
/// inclusion decision for one source under one protocol.
/// </summary>
public sealed class AcademicScreeningEntity
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string ProtocolId { get; set; } = string.Empty;

    public string QualityTier { get; set; } = "InsufficientInformation";
    public double QualityScore { get; set; }
    public double QualityCoverage { get; set; }

    /// <summary>Per-criterion findings, canonical JSON array.</summary>
    public string CriteriaJson { get; set; } = "[]";

    /// <summary>Disclosure flags, canonical JSON array.</summary>
    public string FlagsJson { get; set; } = "[]";

    /// <summary>Risk-of-bias appraisal, null when none was recorded.</summary>
    public string? BiasJson { get; set; }

    public string Decision { get; set; } = "Unscheduled";
    public string InclusionReasonsJson { get; set; } = "[]";
    public string ExclusionReasonsJson { get; set; } = "[]";
    public string UndeterminedReasonsJson { get; set; } = "[]";
    public string DecidedBy { get; set; } = "System";
    public string ProvenanceVerdict { get; set; } = "Unassessed";
    public string? MemberNote { get; set; }

    public DateTime ScreenedAtUtc { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}

/// <summary>
/// Persisted evidence item. The locator columns are separated from the content so a
/// rejected locator is visible as a distinct state: <see cref="LocatorOrigin"/>
/// empty with a reason code recorded means the pinpoint was discarded, not that no
/// pinpoint was ever offered.
/// </summary>
public sealed class AcademicEvidenceItemEntity
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string SupportKind { get; set; } = "BibliographicOnly";

    public string? Content { get; set; }
    public string? Claim { get; set; }

    /// <summary>Structured claim for the matrix comparer, canonical JSON.</summary>
    public string? ClaimJson { get; set; }

    public string? LocatorKind { get; set; }
    public string? LocatorOrigin { get; set; }
    public string? LocatorLabel { get; set; }
    public string? LocatorPaginationBasis { get; set; }
    public int? AnchorStart { get; set; }
    public int? AnchorEnd { get; set; }

    /// <summary>Reason a locator was rejected, when it was.</summary>
    public string? LocatorRejectionCode { get; set; }

    public string Integrity { get; set; } = "Unchecked";
    public string IntegrityReasonsJson { get; set; } = "[]";
    public string FlagsJson { get; set; } = "[]";
    public string AccessLevelAtRecord { get; set; } = "Unknown";

    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>Persisted question-by-source cell of an evidence matrix.</summary>
public sealed class AcademicMatrixCellEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public string QuestionKey { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public string Stance { get; set; } = "NotEvaluated";

    /// <summary>Evidence ids that produced the stance, canonical JSON array.</summary>
    public string EvidenceIdsJson { get; set; } = "[]";

    public bool HasVerifiableLocator { get; set; }
    public string AccessLevel { get; set; } = "Unknown";
    public string Provenance { get; set; } = "Unassessed";
    public string Quality { get; set; } = "InsufficientInformation";
    public string Screening { get; set; } = "Unscheduled";
    public string FlagsJson { get; set; } = "[]";

    public DateTime BuiltAtUtc { get; set; }
}

/// <summary>Persisted relationship or gap surfaced by the matrix.</summary>
public sealed class AcademicMatrixFindingEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>Either <c>relationship</c> or <c>gap</c>.</summary>
    public string FindingType { get; set; } = "gap";

    public string QuestionKey { get; set; } = string.Empty;

    /// <summary>Relation for a pair, or gap kind.</summary>
    public string Code { get; set; } = string.Empty;

    public Guid? LeftSourceId { get; set; }
    public Guid? RightSourceId { get; set; }

    public string Severity { get; set; } = "Note";
    public string Explanation { get; set; } = string.Empty;

    /// <summary>Additional source ids for a gap, canonical JSON array.</summary>
    public string SourceIdsJson { get; set; } = "[]";

    /// <summary>Reason codes, canonical JSON array.</summary>
    public string ReasonCodesJson { get; set; } = "[]";

    /// <summary>Suggested next step for a gap.</summary>
    public string? SuggestedStep { get; set; }

    public DateTime BuiltAtUtc { get; set; }
}
