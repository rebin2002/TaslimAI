namespace Taslim.Api.AcademicEvidence.Domain;

/// <summary>
/// Thresholds that both the domain model and the verification services must
/// agree on, kept here so the entities never have to reference the service layer
/// to explain their own invariants.
/// </summary>
public static class AcademicEvidenceLimits
{
    /// <summary>
    /// Independent origins required before a record may be presented as a
    /// verified citation. One provider hit is, honestly, an unverified claim.
    /// </summary>
    public const int MinimumCorroboratingOrigins = 2;

    /// <summary>
    /// Minimum share of applicable risk-of-bias domains that must be answered
    /// before an overall judgement may be published.
    /// </summary>
    public const double MinimumBiasAnsweredCoverage = 0.5;
}

/// <summary>
/// Which year was actually read, because the same work can carry several: the
/// printed year, an online-first year, a copyright year on a book, or the year
/// of a revised edition. Storing the basis keeps a copyright line from being
/// cited as a publication year.
/// </summary>
public enum PublicationYearBasis
{
    /// <summary>No year was asserted by any origin.</summary>
    Unknown = 0,

    /// <summary>Printed publication year of the version of record.</summary>
    PrintedPublicationYear,

    /// <summary>Year the work first appeared online, ahead of the issue.</summary>
    OnlineFirstYear,

    /// <summary>Year recorded by a repository deposit, which may be a revision.</summary>
    RepositoryDepositYear,

    /// <summary>Copyright year, which is not the same as the publication year.</summary>
    CopyrightYear,

    /// <summary>Supplied by the member and not read from a registry.</summary>
    MemberProvidedYear,
}

/// <summary>
/// An author or editor of a work, already normalised. Every field here was read
/// from a metadata origin: a name that a provider did not return stays absent
/// rather than being guessed, and <see cref="GivenNames"/> is only populated
/// when the origin actually expanded the initials.
/// </summary>
public sealed record BibliographicAuthor
{
    /// <summary>Name exactly as the origin printed it, after whitespace normalisation.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Family name, case-folded for matching but preserving diacritics.</summary>
    public required string FamilyName { get; init; }

    /// <summary>Given names, when the origin expanded them; otherwise null.</summary>
    public string? GivenNames { get; init; }

    /// <summary>Validated ORCID iD, or null when absent or malformed.</summary>
    public string? Orcid { get; init; }

    /// <summary>Affiliation string as recorded, never inferred from the name.</summary>
    public string? Affiliation { get; init; }

    /// <summary>Whether this person is an author or an editor of the work.</summary>
    public AuthorRole Role { get; init; } = AuthorRole.Author;

    /// <summary>
    /// Stable key used to recognise the same person across metadata origins.
    /// Produced by <c>AuthorNameNormalizer</c> and persisted so dedupe stays
    /// reproducible after a code change.
    /// </summary>
    public string MatchKey { get; init; } = string.Empty;
}

/// <summary>Position of a person relative to the work.</summary>
public enum AuthorRole
{
    Author = 0,
    Editor,
    Translator,
    Contributor,
}

/// <summary>
/// A persistent identifier attached to a source, paired with the origin that
/// asserted it. A well-formed identifier is not the same thing as a correct
/// one, so the origin caps what provenance may be concluded from it.
/// </summary>
public sealed record BibliographicIdentifier
{
    public required IdentifierKind Kind { get; init; }

    /// <summary>Value exactly as received, after trimming decorative prefixes.</summary>
    public required string RawValue { get; init; }

    /// <summary>Canonical form used for equality and lookups; null when malformed.</summary>
    public string? NormalizedValue { get; init; }

    /// <summary>True when the value passed the syntactic and check-digit rules.</summary>
    public bool IsWellFormed { get; init; }

    /// <summary>Machine-readable reason when <see cref="IsWellFormed"/> is false.</summary>
    public string? MalformedReason { get; init; }

    public IdentifierOrigin Origin { get; init; } = IdentifierOrigin.Unknown;

    /// <summary>Registry that minted the identifier, for example a Crossref DOI prefix.</summary>
    public string? RegistrantPrefix { get; init; }

    /// <summary>When the identifier was observed, in UTC.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>
/// A source whose metadata has been put through normalisation. This is the
/// bibliographic identity of a work and deliberately carries no claim that any
/// content was read: <see cref="AccessLevel"/> and
/// <see cref="ContentAcquisition"/> say what was actually obtained.
/// </summary>
public sealed record NormalizedBibliographicRecord
{
    public required Guid SourceId { get; init; }

    /// <summary>Tenant scope. Every read and write filter on this.</summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>
    /// Title of the work in the language it was catalogued in, normalised for
    /// whitespace and punctuation only. Null when no origin supplied one, which
    /// makes the record uncitable rather than inventing a heading.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>Subtitle when the origin separated one.</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// Title in its original script, for example a Sorani Kurdish thesis title,
    /// kept alongside the transliterated form rather than replacing it.
    /// </summary>
    public string? OriginalScriptTitle { get; init; }

    public AcademicSourceKind SourceKind { get; init; } = AcademicSourceKind.Unknown;

    /// <summary>Host work: journal name, book title, repository or conference.</summary>
    public string? Venue { get; init; }

    public string? PublicationYear { get; init; }

    public PublicationYearBasis YearBasis { get; init; } = PublicationYearBasis.Unknown;

    /// <summary>Full publication date when the origin supplied one, UTC midnight.</summary>
    public DateTimeOffset? PublicationDateUtc { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Edition label as printed, for example "3rd edition".</summary>
    public string? EditionLabel { get; init; }

    /// <summary>Ordinal edition number when the label was unambiguous.</summary>
    public int? EditionNumber { get; init; }

    public string? Volume { get; init; }

    public string? Issue { get; init; }

    /// <summary>
    /// Page range exactly as read from registry metadata or the retrieved copy.
    /// Null unless an admissible origin supplied it; model-derived pagination is
    /// dropped by <c>SourceLocatorPolicy</c> and never stored here.
    /// </summary>
    public string? PageRange { get; init; }

    /// <summary>Where <see cref="PageRange"/> came from.</summary>
    public LocatorOrigin PageRangeOrigin { get; init; } = LocatorOrigin.Unknown;

    public IReadOnlyList<BibliographicAuthor> Authors { get; init; } = [];

    public IReadOnlyList<BibliographicAuthor> Editors { get; init; } = [];

    public IReadOnlyList<BibliographicIdentifier> Identifiers { get; init; } = [];

    public SourceAccessLevel AccessLevel { get; init; } = SourceAccessLevel.Unknown;

    public ContentAcquisition ContentAcquisition { get; init; } = ContentAcquisition.None;

    /// <summary>Landing page for the work, as recorded.</summary>
    public string? LandingPageUrl { get; init; }

    /// <summary>Language tag of the work when the origin recorded one.</summary>
    public string? Language { get; init; }

    /// <summary>Discipline label supplied by the metadata origin.</summary>
    public string? Discipline { get; init; }

    /// <summary>Study design when the origin or the member recorded one.</summary>
    public string? StudyDesign { get; init; }

    /// <summary>Abstract text, present only when <see cref="AccessLevel"/> reaches <see cref="SourceAccessLevel.Abstract"/>.</summary>
    public string? AbstractText { get; init; }

    /// <summary>Stored full text, present only at a full-text access level.</summary>
    public string? FullText { get; init; }

    /// <summary>Provider-agnostic corpus key (for example an OpenAlex work id).</summary>
    public string? CorpusId { get; init; }

    /// <summary>
    /// True when the record has enough identity to be cited: a title plus
    /// either a well-formed persistent identifier or a publisher.
    /// </summary>
    public bool IsCitable =>
        !string.IsNullOrWhiteSpace(Title)
        && (YearBasis != PublicationYearBasis.Unknown || !string.IsNullOrWhiteSpace(Publisher));

    /// <summary>First author surname, used by citation rendering and dedupe.</summary>
    public string? FirstAuthorFamilyName => Authors.Count > 0 ? Authors[0].FamilyName : null;

    /// <summary>The first well-formed identifier of a kind, or null.</summary>
    public string? NormalizedIdentifier(IdentifierKind kind)
    {
        foreach (var identifier in Identifiers)
        {
            if (identifier.Kind == kind && identifier.IsWellFormed
                && !string.IsNullOrWhiteSpace(identifier.NormalizedValue))
            {
                return identifier.NormalizedValue;
            }
        }

        return null;
    }
}

/// <summary>
/// Provenance recorded for one field of a record: which origin asserted the
/// value, when, and whether independent origins agree. Field-level rather than
/// record-level so a verified DOI cannot launder an unverified year.
/// </summary>
public sealed record FieldProvenance
{
    /// <summary>Name of the field on <see cref="NormalizedBibliographicRecord"/>.</summary>
    public required string FieldName { get; init; }

    /// <summary>Value that was recorded for the field, as normalised text.</summary>
    public string? Value { get; init; }

    /// <summary>Distinct origin keys that asserted this value.</summary>
    public IReadOnlyList<string> Origins { get; init; } = [];

    /// <summary>Origin keys that asserted a different value for the same field.</summary>
    public IReadOnlyList<string> ConflictingOrigins { get; init; } = [];

    /// <summary>Identifier origin when the field is an identifier.</summary>
    public IdentifierOrigin IdentifierOrigin { get; init; } = IdentifierOrigin.Unknown;

    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>A retraction, correction or expression-of-concern notice.</summary>
public sealed record RetractionNotice
{
    /// <summary>Whether the work was retracted, corrected or flagged.</summary>
    public required RetractionKind Kind { get; init; }

    /// <summary>Notice DOI, when the notice itself has one.</summary>
    public string? NoticeDoi { get; init; }

    /// <summary>Landing page of the notice.</summary>
    public string? NoticeUrl { get; init; }

    /// <summary>Who published the notice, for example the journal or a registry.</summary>
    public string? NotifiedBy { get; init; }

    public DateTimeOffset NoticedAtUtc { get; init; }

    /// <summary>
    /// How the notice was learned about. A model assertion is not an admissible
    /// origin: a retraction flag must come from a registry or a retrieved copy.
    /// </summary>
    public RetractionNoticeOrigin Origin { get; init; } = RetractionNoticeOrigin.Unknown;

    /// <summary>Verbatim notice text when captured.</summary>
    public string? NoticeText { get; init; }
}

/// <summary>Strength of a publication-integrity notice.</summary>
public enum RetractionKind
{
    /// <summary>Expression of concern or editorial note; the work stays citable but flagged.</summary>
    ExpressionOfConcern = 0,

    /// <summary>Correction, erratum or republication.</summary>
    Correction,

    /// <summary>Partial retraction of some findings.</summary>
    PartialRetraction,

    /// <summary>Full retraction.</summary>
    FullRetraction,
}

/// <summary>Where a publication-integrity notice came from.</summary>
public enum RetractionNoticeOrigin
{
    /// <summary>Unknown, and therefore inadmissible: never flag a work from a guess.</summary>
    Unknown = 0,

    /// <summary>Registry record (for example a retraction database or the publisher's notice).</summary>
    Registry,

    /// <summary>Read from the notice text in a retrieved copy.</summary>
    RetrievedCopy,

    /// <summary>Member supplied the notice.</summary>
    MemberProvided,

    /// <summary>
    /// Asserted by a generative provider. Recorded as a lead to check, never as
    /// a confirmed retraction.
    /// </summary>
    ProviderAssertion,
}

/// <summary>
/// The verification result for one source: the verdict, the field-level
/// evidence behind it, and any publication-integrity notices. Reconstructed
/// from stored evidence so a claim of "verified" always has a basis.
/// </summary>
public sealed record SourceProvenance
{
    public required Guid SourceId { get; init; }

    public required Guid WorkspaceId { get; init; }

    public ProvenanceVerdict Verdict { get; init; } = ProvenanceVerdict.Unassessed;

    /// <summary>Machine-readable reason codes explaining the verdict.</summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    /// <summary>Per-field evidence, one entry per recorded field.</summary>
    public IReadOnlyList<FieldProvenance> Fields { get; init; } = [];

    /// <summary>Count of distinct origins that corroborate the record identity.</summary>
    public int CorroboratingOriginCount { get; init; }

    /// <summary>Publication-integrity notices attached to the work.</summary>
    public IReadOnlyList<RetractionNotice> RetractionNotices { get; init; } = [];

    /// <summary>Strongest notice on the work, when any exists.</summary>
    public RetractionKind? StrongestNotice
    {
        get
        {
            RetractionKind? strongest = null;
            foreach (var notice in AdmissibleNotices)
            {
                if (strongest is null || notice.Kind > strongest.Value) strongest = notice.Kind;
            }

            return strongest;
        }
    }

    /// <summary>
    /// Notices with an admissible origin. Provider assertions are excluded, so a
    /// hallucinated retraction cannot silently remove a real source.
    /// </summary>
    public IReadOnlyList<RetractionNotice> AdmissibleNotices =>
        [.. RetractionNotices.Where(n => n.Origin != RetractionNoticeOrigin.Unknown
            && n.Origin != RetractionNoticeOrigin.ProviderAssertion)];

    /// <summary>Provider assertions recorded as leads that still need checking.</summary>
    public IReadOnlyList<RetractionNotice> UnconfirmedNoticeLeads =>
        [.. RetractionNotices.Where(n => n.Origin == RetractionNoticeOrigin.ProviderAssertion)];

    /// <summary>
    /// Only true for a verdict that two independent origins support. Presentation
    /// layers must use this rather than comparing against <see cref="ProvenanceVerdict.Verified"/>
    /// directly, so an unassessed or provider-only record can never render a
    /// "verified citation" badge.
    /// </summary>
    public bool MayPresentAsVerified =>
        Verdict == ProvenanceVerdict.Verified
        && CorroboratingOriginCount >= AcademicEvidenceLimits.MinimumCorroboratingOrigins
        && AdmissibleNotices.All(n => n.Kind == RetractionKind.ExpressionOfConcern
            || n.Kind == RetractionKind.Correction);
}

/// <summary>One append-only entry of the provenance audit trail.</summary>
public sealed record ProvenanceAuditEvent
{
    public required long Sequence { get; init; }
    public required Guid SourceId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string EventKind { get; init; }
    public AuditActorKind ActorKind { get; init; } = AuditActorKind.System;
    public Guid? ActorId { get; init; }

    /// <summary>Canonical payload, sorted keys, no volatile formatting.</summary>
    public required string PayloadCanonical { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Hash of the previous entry, empty for the first.</summary>
    public string PreviousHash { get; init; } = string.Empty;

    /// <summary>SHA-256 over the entry and its predecessor.</summary>
    public string EntryHash { get; init; } = string.Empty;
}
