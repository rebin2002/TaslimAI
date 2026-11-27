using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Appraisal;

/// <summary>
/// Stable reason codes for inclusion and exclusion. Every reason a member sees in
/// the source library traces to one of these, so the PRISMA-style counts in the
/// exported review are reproducible rather than a paragraph of prose.
/// </summary>
public static class InclusionReasonCodes
{
    // Inclusion.
    public const string ScholarlyForm = "INCL_SCHOLARLY_FORM";
    public const string PeerReviewConfirmed = "INCL_PEER_REVIEW_CONFIRMED";
    public const string WithinDateWindow = "INCL_WITHIN_DATE_WINDOW";
    public const string TopicMatched = "INCL_TOPIC_MATCHED";
    public const string FullTextAvailable = "INCL_FULL_TEXT_AVAILABLE";
    public const string MemberSelected = "INCL_MEMBER_SELECTED";

    // Exclusion.
    public const string NoTitle = "EXCL_NO_TITLE";
    public const string Retracted = "EXCL_RETRACTED";
    public const string ProvenanceDisputed = "EXCL_PROVENANCE_DISPUTED";
    public const string NotScholarlyForm = "EXCL_NOT_SCHOLARLY_FORM";
    public const string OutsideDateWindow = "EXCL_OUTSIDE_DATE_WINDOW";
    public const string WrongLanguage = "EXCL_WRONG_LANGUAGE";
    public const string DuplicateOfKeptRecord = "EXCL_DUPLICATE";
    public const string FullTextRequiredButAbsent = "EXCL_FULL_TEXT_REQUIRED";
    public const string Unlocatable = "EXCL_UNLOCATABLE";
    public const string OutOfScopeTopic = "EXCL_OUT_OF_SCOPE";

    // Undetermined.
    public const string TopicRelevanceUnassessed = "UNDET_TOPIC_UNASSESSED";
    public const string AccessLevelUnknown = "UNDET_ACCESS_UNKNOWN";
    public const string ProvenanceUnverified = "UNDET_PROVENANCE_UNVERIFIED";
}

/// <summary>
/// The protocol a review screens against. Everything here is a choice the member
/// or the university's protocol states; nothing is defaulted by the platform,
/// because an unstated date window silently deletes real literature.
/// </summary>
public sealed record InclusionCriteria(
    /// <summary>Identifier for the protocol revision, recorded on each decision.</summary>
    string Version = "protocol-1",

    /// <summary>Inclusive lower bound publication year.</summary>
    int? YearFrom = null,

    /// <summary>Inclusive upper bound publication year.</summary>
    int? YearTo = null,

    /// <summary>Accepted language tags, for example <c>en</c> or <c>ckb</c>.</summary>
    IReadOnlyList<string>? Languages = null,

    /// <summary>Forms the protocol accepts as scholarly literature.</summary>
    IReadOnlyList<AcademicSourceKind>? AcceptedKinds = null,

    /// <summary>Forms excluded even if otherwise relevant.</summary>
    IReadOnlyList<AcademicSourceKind>? RejectedKinds = null,

    /// <summary>True when the protocol only admits sources whose text was read.</summary>
    bool RequireFullText = false,

    /// <summary>True when only a verified citation may be included.</summary>
    bool RequireVerifiedProvenance = false,

    /// <summary>Terms that mark a source in scope, matched on title, venue and abstract.</summary>
    IReadOnlyList<string>? ScopeTerms = null,

    /// <summary>Terms that mark a source out of scope, matched the same way.</summary>
    IReadOnlyList<string>? ExclusionTerms = null);

/// <summary>Outcome of evaluating one source against the protocol.</summary>
public sealed record InclusionEvaluation(
    InclusionDecision Decision,
    /// <summary>True when the member explicitly selected the source, which outranks screening.</summary>
    bool MemberOverride);

/// <summary>
/// Applies inclusion and exclusion criteria to a source.
///
/// The order of checks is the point. Integrity exclusions (a retraction, a
/// disputed identity) run before relevance, because a retracted study about the
/// right topic is still a study that must not be cited. A source whose relevance
/// was never assessed comes out <see cref="ScreeningDecision.Undetermined"/> rather
/// than included, so an unreviewed pile cannot masquerade as a screened set.
/// </summary>
public static class InclusionExclusionPolicy
{
    /// <summary>Evaluate one source.</summary>
    /// <param name="record">Normalized record.</param>
    /// <param name="provenance">Verified provenance, retraction-screened.</param>
    /// <param name="quality">Screening result for the same source.</param>
    /// <param name="criteria">The protocol.</param>
    /// <param name="isMemberSelected">The member chose this source for the review.</param>
    /// <param name="duplicateOf">Source id this record duplicates, when known.</param>
    /// <param name="topicRelevance">
    /// Assessed relevance from 0 to 1, or null when nobody has assessed it. A model
    /// score may be supplied here, but a null keeps the decision undetermined.
    /// </param>
    /// <param name="decidedAtUtc">Timestamp for the decision row.</param>
    public static InclusionEvaluation Evaluate(
        NormalizedBibliographicRecord record,
        SourceProvenance provenance,
        QualityScreenResult? quality = null,
        InclusionCriteria? criteria = null,
        bool isMemberSelected = false,
        Guid? duplicateOf = null,
        double? topicRelevance = null,
        DateTimeOffset? decidedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(provenance);

        var protocol = criteria ?? new InclusionCriteria();
        var exclusions = new List<string>();
        var inclusions = new List<string>();
        var undetermined = new List<string>();
        var atUtc = decidedAtUtc ?? provenance.RetractionNotices
            .Select(n => n.NoticedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.UnixEpoch)
            .Max();

        // 1. Identity and integrity gates, which no amount of relevance undoes.
        if (string.IsNullOrWhiteSpace(record.Title)) exclusions.Add(InclusionReasonCodes.NoTitle);
        if (provenance.Verdict == ProvenanceVerdict.Withdrawn) exclusions.Add(InclusionReasonCodes.Retracted);
        if (provenance.Verdict == ProvenanceVerdict.Disputed) exclusions.Add(InclusionReasonCodes.ProvenanceDisputed);

        if (duplicateOf is Guid keeper && keeper != Guid.Empty)
        {
            exclusions.Add(InclusionReasonCodes.DuplicateOfKeptRecord);
        }

        // 2. Form and locatability. A journal article with no DOI and no URL
        // cannot be checked by a supervisor, so it is not citable scholarship.
        if (protocol.RejectedKinds is { Count: > 0 } rejected && rejected.Contains(record.SourceKind))
        {
            exclusions.Add(InclusionReasonCodes.NotScholarlyForm);
        }

        if (protocol.AcceptedKinds is { Count: > 0 } accepted)
        {
            if (!accepted.Contains(record.SourceKind)) exclusions.Add(InclusionReasonCodes.NotScholarlyForm);
            else inclusions.Add(InclusionReasonCodes.ScholarlyForm);
        }
        else if (record.SourceKind is AcademicSourceKind.JournalArticle
                 or AcademicSourceKind.ReviewArticle
                 or AcademicSourceKind.Book
                 or AcademicSourceKind.BookChapter
                 or AcademicSourceKind.Thesis
                 or AcademicSourceKind.ConferencePaper
                 or AcademicSourceKind.Report
                 or AcademicSourceKind.ClinicalGuideline
                 or AcademicSourceKind.Standard)
        {
            inclusions.Add(InclusionReasonCodes.ScholarlyForm);
        }
        else if (record.SourceKind == AcademicSourceKind.Unknown)
        {
            undetermined.Add(InclusionReasonCodes.AccessLevelUnknown);
        }
        else
        {
            exclusions.Add(InclusionReasonCodes.NotScholarlyForm);
        }

        if (record.NormalizedIdentifier(IdentifierKind.Doi) is null
            && record.NormalizedIdentifier(IdentifierKind.Isbn) is null
            && record.NormalizedIdentifier(IdentifierKind.Url) is null
            && string.IsNullOrWhiteSpace(record.LandingPageUrl))
        {
            exclusions.Add(InclusionReasonCodes.Unlocatable);
        }

        // 3. Protocol windows.
        if (protocol.YearFrom is int from || protocol.YearTo is int to)
        {
            if (!int.TryParse(record.PublicationYear, out var year))
            {
                undetermined.Add(InclusionReasonCodes.AccessLevelUnknown);
            }
            else if ((protocol.YearFrom is int lower && year < lower)
                     || (protocol.YearTo is int upper && year > upper))
            {
                exclusions.Add(InclusionReasonCodes.OutsideDateWindow);
            }
            else
            {
                inclusions.Add(InclusionReasonCodes.WithinDateWindow);
            }
        }

        if (protocol.Languages is { Count: > 0 } languages)
        {
            if (string.IsNullOrWhiteSpace(record.Language)) undetermined.Add(InclusionReasonCodes.AccessLevelUnknown);
            else if (!languages.Any(l => string.Equals(l, record.Language, StringComparison.OrdinalIgnoreCase)))
            {
                exclusions.Add(InclusionReasonCodes.WrongLanguage);
            }
        }

        if (protocol.RequireFullText)
        {
            if (!AccessLevelPolicy.SupportsContentClaims(record.AccessLevel))
            {
                exclusions.Add(InclusionReasonCodes.FullTextRequiredButAbsent);
            }
            else if (record.AccessLevel is SourceAccessLevel.FullText or SourceAccessLevel.PrivateUpload)
            {
                inclusions.Add(InclusionReasonCodes.FullTextAvailable);
            }
        }

        if (protocol.RequireVerifiedProvenance && provenance.Verdict != ProvenanceVerdict.Verified)
        {
            undetermined.Add(InclusionReasonCodes.ProvenanceUnverified);
        }

        // 4. Topic relevance, only ever from an assessment that exists.
        var haystack = string.Join(' ',
            [record.Title, record.Subtitle, record.Venue, record.AbstractText, record.Discipline])
            .ToLowerInvariant();

        if (protocol.ExclusionTerms is { Count: > 0 } exclusionTerms
            && exclusionTerms.Any(t => haystack.Contains(t.ToLowerInvariant())))
        {
            exclusions.Add(InclusionReasonCodes.OutOfScopeTopic);
        }

        var scopeMatched = protocol.ScopeTerms is { Count: > 0 } scopeTerms
            && scopeTerms.Any(t => haystack.Contains(t.ToLowerInvariant()));

        if (exclusions.Count == 0)
        {
            if (scopeMatched) inclusions.Add(InclusionReasonCodes.TopicMatched);
            if (topicRelevance is double relevance)
            {
                if (relevance >= 0.5) inclusions.Add(InclusionReasonCodes.TopicMatched);
                else exclusions.Add(InclusionReasonCodes.OutOfScopeTopic);
            }
            else if (protocol.ScopeTerms is { Count: > 0 })
            {
                undetermined.Add(InclusionReasonCodes.TopicRelevanceUnassessed);
            }

            if (quality?.Tier == QualityTier.InsufficientInformation)
            {
                undetermined.Add(InclusionReasonCodes.AccessLevelUnknown);
            }
        }

        if (isMemberSelected && exclusions.Count == 0) inclusions.Add(InclusionReasonCodes.MemberSelected);

        // A member's explicit choice is respected for relevance, never for
        // integrity: a retracted or disputed source stays excluded and the reason
        // is shown instead of being overridden.
        var memberOverride = isMemberSelected
            && exclusions.Any(code => code != InclusionReasonCodes.OutOfScopeTopic
                && code != InclusionReasonCodes.Retracted
                && code != InclusionReasonCodes.ProvenanceDisputed
                && code != InclusionReasonCodes.NoTitle);

        var blockingIntegrity = exclusions.Any(code => code is InclusionReasonCodes.Retracted
            or InclusionReasonCodes.ProvenanceDisputed
            or InclusionReasonCodes.NoTitle);

        ScreeningDecision decision;
        if (isMemberSelected && !blockingIntegrity)
        {
            decision = ScreeningDecision.Included;
            if (exclusions.Count > 0) inclusions.Add(InclusionReasonCodes.MemberSelected);
            exclusions.RemoveAll(code => code == InclusionReasonCodes.OutOfScopeTopic);
        }
        else if (exclusions.Count > 0)
        {
            decision = ScreeningDecision.Excluded;
        }
        else if (undetermined.Count > 0)
        {
            decision = ScreeningDecision.Undetermined;
        }
        else if (inclusions.Count > 0)
        {
            decision = ScreeningDecision.Included;
        }
        else
        {
            decision = ScreeningDecision.Undetermined;
            undetermined.Add(InclusionReasonCodes.TopicRelevanceUnassessed);
        }

        var evaluation = new InclusionEvaluation(
            new InclusionDecision
            {
                SourceId = record.SourceId,
                WorkspaceId = record.WorkspaceId,
                Decision = decision,
                InclusionReasons = [.. inclusions.Distinct(StringComparer.Ordinal)],
                ExclusionReasons = [.. exclusions.Distinct(StringComparer.Ordinal)],
                UndeterminedReasons = [.. undetermined.Distinct(StringComparer.Ordinal)],
                CriteriaVersion = protocol.Version,
                DecidedBy = isMemberSelected ? AuditActorKind.Member : AuditActorKind.System,
                DecidedAtUtc = atUtc,
            },
            memberOverride);

        return evaluation;
    }

    /// <summary>
    /// Count decisions by outcome for the review's screening summary. Keys are the
    /// reason codes and the pseudo-key <c>__decision__</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Summarize(IEnumerable<InclusionDecision> decisions)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            counts[decision.Decision.ToString()] = counts.GetValueOrDefault(decision.Decision.ToString()) + 1;
            foreach (var reason in decision.ExclusionReasons.Distinct(StringComparer.Ordinal))
            {
                counts["exclusion:" + reason] = counts.GetValueOrDefault("exclusion:" + reason) + 1;
            }
        }

        return counts;
    }
}
