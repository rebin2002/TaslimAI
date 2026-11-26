namespace Taslim.Api.AcademicEvidence.Domain;

/// <summary>
/// The bibliographic form of a scholarly source. The kind drives citation
/// rendering, the expected identifier type (DOI for articles, ISBN for books)
/// and the default peer-review expectation, so it is only ever set from a value
/// a metadata provider actually asserted.
/// </summary>
public enum AcademicSourceKind
{
    /// <summary>Provider did not classify the work, or the classification was unrecognised.</summary>
    Unknown = 0,
    JournalArticle,
    ReviewArticle,
    Book,
    BookChapter,
    EditedVolume,
    ConferencePaper,
    Thesis,
    Report,
    ClinicalGuideline,
    Standard,
    Dataset,
    Preprint,
    Other,
}

/// <summary>
/// How much of a work was actually obtained. This is the single most important
/// distinction in the academic lane: catalog metadata about a source is never
/// evidence that the source was read, so anything that quotes, pages or
/// paraphrases content requires <see cref="Abstract"/> or a full-text level.
/// </summary>
public enum SourceAccessLevel
{
    /// <summary>Nothing was retrieved; the level is not known.</summary>
    Unknown = 0,

    /// <summary>
    /// Only catalog/registry metadata is held (title, authors, identifiers).
    /// Bibliographic citation is permitted; quotation, content claims and page
    /// locators are not.
    /// </summary>
    MetadataOnly,

    /// <summary>
    /// A publisher- or provider-supplied abstract is held. Short quoted support
    /// may be admitted against that abstract, and only against it.
    /// </summary>
    Abstract,

    /// <summary>Full text was legitimately obtained (open access or licensed).</summary>
    FullText,

    /// <summary>
    /// Full text supplied by the member as a private upload (for example their
    /// own manuscript). Content claims are permitted against the stored text;
    /// the source is not treated as peer-reviewed or published.
    /// </summary>
    PrivateUpload,
}

/// <summary>How the content of a source reached the platform.</summary>
public enum ContentAcquisition
{
    None = 0,
    CatalogMetadata,
    ProviderAbstract,
    OpenAccessCopy,
    LicensedFullText,
    MemberUpload,
}

/// <summary>The kind of persistent identifier carried on a record.</summary>
public enum IdentifierKind
{
    Unknown = 0,
    Doi,
    Isbn,
    Issn,
    Orcid,
    Doaj,
    Pubmed,
    Pmcid,
    Arxiv,
    Url,
}

/// <summary>
/// How a syntactically well-formed identifier was obtained, which caps how far
/// provenance may be upgraded from it.
/// </summary>
public enum IdentifierOrigin
{
    /// <summary>Origin not recorded; provenance can never reach verified.</summary>
    Unknown = 0,

    /// <summary>Read back from a registry response (Crossref DOI record, ISBN registry).</summary>
    RegistryResponse,

    /// <summary>Present on the landing page of the work itself.</summary>
    WorkLandingPage,

    /// <summary>Supplied by the member.</summary>
    MemberProvided,

    /// <summary>Emitted by a generative provider and not checked against any registry.</summary>
    ProviderAssertion,

    /// <summary>Copied from a citing work's reference list.</summary>
    CitationList,
}

/// <summary>
/// The provenance verdict for a bibliographic record. Only two of these may
/// ever be presented to a member as "verified", and only when
/// <see cref="Provenance.ProvenanceVerdict"/> can be reconstructed from stored
/// field evidence.
/// </summary>
public enum ProvenanceVerdict
{
    /// <summary>No verification has been attempted.</summary>
    Unassessed = 0,

    /// <summary>
    /// Identifiers are malformed or absent, or the record conflicts with itself.
    /// </summary>
    Invalid,

    /// <summary>
    /// Structurally sound but not corroborated by two independent origins. This
    /// is the honest default for a single provider hit and must be displayed as
    /// unverified.
    /// </summary>
    Unverified,

    /// <summary>Two or more independent origins agree on the core identity.</summary>
    Verified,

    /// <summary>
    /// Independent origins disagree on a core field (title, year, kind,
    /// identifier), so no identity can be asserted until the conflict is
    /// resolved.
    /// </summary>
    Disputed,

    /// <summary>
    /// The record is still bibliographically accurate but the work has been
    /// withdrawn, corrected or flagged by a retraction notice.
    /// </summary>
    Withdrawn,
}

/// <summary>Origin of a locator (page, paragraph, section) attached to evidence.</summary>
public enum LocatorOrigin
{
    /// <summary>No origin recorded. Never admissible: this is the fabricated-page-number path.</summary>
    Unknown = 0,

    /// <summary>
    /// Read from registry metadata, for example the Crossref
    /// <c>page</c>/<c>article-number</c> deposit for the version of record.
    /// </summary>
    RegistryMetadata,

    /// <summary>Printed on the retrieved copy, captured by the member or an operator.</summary>
    RetrievedCopy,

    /// <summary>Derived from an anchored span inside the stored full text.</summary>
    AnchoredExtraction,

    /// <summary>Typed in by the member.</summary>
    MemberProvided,

    /// <summary>
    /// Guessed by a model. Rejected outright: page numbers cannot be inferred
    /// from text, only read from a paginated artifact.
    /// </summary>
    ModelInference,
}

/// <summary>The unit a locator points into.</summary>
public enum LocatorKind
{
    None = 0,
    Page,
    PageRange,
    ArticleNumber,
    Paragraph,
    Section,
    Clause,
    Timestamp,
}

/// <summary>How a locator's page numbering basis was established.</summary>
public enum PaginationBasis
{
    /// <summary>Not applicable, the locator does not reference pages.</summary>
    NotApplicable = 0,

    /// <summary>Printed pagination of the version of record.</summary>
    PublishedPagination,

    /// <summary>
    /// Pagination of the retrieved PDF, which may differ from the printed
    /// pages and must be labelled as such.
    /// </summary>
    FilePagination,
}

/// <summary>What kind of support an evidence item asserts over a source.</summary>
public enum EvidenceSupportKind
{
    /// <summary>The source exists with this identity; no content is asserted.</summary>
    BibliographicOnly = 0,

    /// <summary>Verbatim quotation, requires a present-in-text check.</summary>
    DirectQuote,

    /// <summary>Condensed restatement, requires full text or abstract content.</summary>
    Paraphrase,

    /// <summary>A reported magnitude, requires the number to be present in text.</summary>
    QuantitativeFinding,

    /// <summary>The source does not address the question, recorded as absence of evidence.</summary>
    AbsenceOfEvidence,
}

/// <summary>Verdict of the integrity check applied to an evidence item.</summary>
public enum EvidenceIntegrityStatus
{
    /// <summary>Not yet checked.</summary>
    Unchecked = 0,

    /// <summary>Supporting content was located in the stored text at the recorded access level.</summary>
    VerifiedAgainstSource,

    /// <summary>Supporting content was not found in the stored text.</summary>
    UnsupportedByStoredText,

    /// <summary>The access level cannot support this kind of claim at all.</summary>
    NotSupportedAtAccessLevel,

    /// <summary>Locator was rejected, so the item may not be exported.</summary>
    LocatorRejected,
}

/// <summary>Result of screening a source into or out of the review.</summary>
public enum ScreeningDecision
{
    /// <summary>Not screened yet.</summary>
    Unscheduled = 0,
    Included,
    Excluded,
    /// <summary>Cannot be decided from metadata alone; needs full text or member input.</summary>
    Undetermined,
}

/// <summary>Peer-review status of a work, and how that status is known.</summary>
public enum PeerReviewStatus
{
    /// <summary>Not asserted by any recorded origin.</summary>
    Unknown = 0,

    /// <summary>Provider claims the venue is peer reviewed; not independently checked.</summary>
    AssertedByProvider,

    /// <summary>Checked against a registry or publisher policy captured in provenance.</summary>
    VerifiedFromRegistry,

    /// <summary>Explicitly not peer reviewed (preprint, editorial, working paper).</summary>
    NotPeerReviewed,
}

/// <summary>Whether a bias/risk-of-bias domain can be judged at all.</summary>
public enum BiasJudgement
{
    /// <summary>No information recorded for this domain.</summary>
    NoInformation = 0,

    /// <summary>Concerning, low risk on the recorded criteria.</summary>
    LowConcern,

    /// <summary>Some cause for concern.</summary>
    SomeConcern,

    /// <summary>High risk on the recorded criteria.</summary>
    HighConcern,

    /// <summary>The domain does not apply to this study design.</summary>
    NotApplicable,
}

/// <summary>Relationship between two claims about the same subject.</summary>
public enum ClaimRelation
{
    /// <summary>Not comparable: different subject, predicate, population or unit.</summary>
    NotComparable = 0,

    /// <summary>The claims cannot be ordered without information that is missing.</summary>
    Indeterminate,

    /// <summary>The sources report the same direction and compatible magnitudes.</summary>
    Agreement,

    /// <summary>The sources report opposing findings on the same comparable question.</summary>
    Contradiction,

    /// <summary>
    /// One side simply has no claim. Absence of evidence is never reported as a
    /// contradiction.
    /// </summary>
    NoEvidence,
}

/// <summary>Direction of a reported effect.</summary>
public enum EffectDirection
{
    /// <summary>Direction not reported or not derivable.</summary>
    Unknown = 0,
    Increase,
    Decrease,
    NoEffect,
    Presence,
    Absence,
    Mixed,
}

/// <summary>Category of a research gap surfaced by the matrix.</summary>
public enum ResearchGapKind
{
    NoSourcesForQuestion = 0,
    NoFullTextCoverage,
    UncorroboratedSingleSource,
    UnresolvedContradiction,
    PopulationCoverageGap,
    TimeframeCoverageGap,
    QualityBelowThreshold,
    DependsOnWithdrawnSource,
    UnlocatableClaims,
    DisputedProvenance,
}

/// <summary>Severity of a gap or a flag.</summary>
public enum FindingSeverity
{
    /// <summary>Informational only.</summary>
    Note = 0,

    /// <summary>Must be disclosed to the member in the review.</summary>
    Warning,

    /// <summary>Blocks export of the affected content.</summary>
    Blocking,
}

/// <summary>Actor recorded on an audit event.</summary>
public enum AuditActorKind
{
    /// <summary>Platform code path, for example a scheduled retraction rescreen.</summary>
    System = 0,
    Member,
    ProviderMetadata,
}
