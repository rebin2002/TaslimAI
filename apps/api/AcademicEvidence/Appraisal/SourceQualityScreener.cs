using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Appraisal;

/// <summary>Criteria the screener weights. A missing signal is never a free pass.</summary>
public static class QualityCriterionCodes
{
    /// <summary>Peer review evidenced rather than merely asserted.</summary>
    public const string PeerReviewed = "QUAL_PEER_REVIEWED";

    /// <summary>Source form carries scholarly weight, such as an article or monograph.</summary>
    public const string ScholarlyForm = "QUAL_SCHOLARLY_FORM";

    /// <summary>A persistent identifier resolved to a real record.</summary>
    public const string Identifiable = "QUAL_PERSISTENT_IDENTIFIER";

    /// <summary>Provenance is verified rather than unverified or disputed.</summary>
    public const string ProvenanceVerified = "QUAL_PROVENANCE_VERIFIED";

    /// <summary>Full text was actually read, not just catalogued.</summary>
    public const string FullTextReviewed = "QUAL_FULL_TEXT_REVIEWED";

    /// <summary>Publication is recent enough for the configured window.</summary>
    public const string Recency = "QUAL_RECENCY";

    /// <summary>A risk-of-bias appraisal exists for the work.</summary>
    public const string AppraisalRecorded = "QUAL_RISK_OF_BIAS_RECORDED";

    /// <summary>The study design suits the question it is used for.</summary>
    public const string DesignFitness = "QUAL_DESIGN_FITNESS";

    /// <summary>No publication-integrity notice applies.</summary>
    public const string IntegrityClear = "QUAL_INTEGRITY_CLEAR";
}

/// <summary>Flags raised by the screener.</summary>
public static class QualityFlagCodes
{
    /// <summary>Peer-review status was only asserted by a provider.</summary>
    public const string PeerReviewUnconfirmed = "FLAG_PEER_REVIEW_ASSERTED";

    /// <summary>Only catalog metadata is held.</summary>
    public const string MetadataOnly = "FLAG_METADATA_ONLY";

    /// <summary>The source carries an admissible retraction or concern notice.</summary>
    public const string IntegrityNotice = "FLAG_INTEGRITY_NOTICE";

    /// <summary>A model asserted a retraction that has not been confirmed.</summary>
    public const string IntegrityUnconfirmed = "FLAG_INTEGRITY_UNCONFIRMED";

    /// <summary>Provenance is disputed, so identity itself is in question.</summary>
    public const string ProvenanceDisputed = "FLAG_PROVENANCE_DISPUTED";

    /// <summary>Too few signals were recorded to place the source honestly.</summary>
    public const string InsufficientSignals = "FLAG_INSUFFICIENT_SIGNALS";
}

/// <summary>
/// Settings for one screening pass. The recency window and design expectations
/// belong to the review protocol, so they are supplied rather than assumed.
/// </summary>
public sealed record QualityScreenOptions(
    /// <summary>Year the review treats as the start of the current literature.</summary>
    int? RecencyWindowStartYear = null,

    /// <summary>Study designs that answer the question well, as normalised tokens.</summary>
    IReadOnlyList<string>? PreferredDesigns = null,

    /// <summary>Weights per criterion code; anything omitted uses the default table.</summary>
    IReadOnlyDictionary<string, double>? Weights = null);

/// <summary>
/// Assigns an explainable quality tier to a source.
///
/// The screener is a scoring of recorded signals, not a claim about a journal. It
/// never infers peer review from a publisher name and never raises a tier because
/// a model judged the work impressive: each criterion states which origin supplied
/// the signal, and the <see cref="QualityScreenResult.EvidenceCoverage"/> figure
/// travels with the score so a reviewer can see a high tier built on one signal.
/// </summary>
public static class SourceQualityScreener
{
    /// <summary>Default criterion weights; they sum to 1.0.</summary>
    public static readonly IReadOnlyDictionary<string, double> DefaultWeights =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [QualityCriterionCodes.PeerReviewed] = 0.20,
            [QualityCriterionCodes.ScholarlyForm] = 0.10,
            [QualityCriterionCodes.Identifiable] = 0.10,
            [QualityCriterionCodes.ProvenanceVerified] = 0.15,
            [QualityCriterionCodes.FullTextReviewed] = 0.15,
            [QualityCriterionCodes.Recency] = 0.10,
            [QualityCriterionCodes.AppraisalRecorded] = 0.05,
            [QualityCriterionCodes.DesignFitness] = 0.10,
            [QualityCriterionCodes.IntegrityClear] = 0.05,
        };

    /// <summary>Score at which a source is placed in the high tier.</summary>
    public const double HighTierThreshold = 0.70;

    /// <summary>Score at which a source is placed in the moderate tier.</summary>
    public const double ModerateTierThreshold = 0.40;

    /// <summary>
    /// Screen one source.
    /// </summary>
    /// <param name="record">Normalized bibliographic record.</param>
    /// <param name="provenance">Verified provenance for the same source.</param>
    /// <param name="peerReviewStatus">
    /// Status as recorded, from <c>asserted by provider</c> through to
    /// <c>verified from registry</c>; passing none leaves the criterion unscored.
    /// </param>
    /// <param name="biasAssessment">Risk-of-bias appraisal when one exists.</param>
    /// <param name="options">Protocol settings.</param>
    /// <param name="screenedAtUtc">Timestamp recorded on the result.</param>
    public static QualityScreenResult Screen(
        NormalizedBibliographicRecord record,
        SourceProvenance provenance,
        PeerReviewStatus? peerReviewStatus = null,
        RiskOfBiasAssessment? biasAssessment = null,
        QualityScreenOptions? options = null,
        DateTimeOffset? screenedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(provenance);

        var settings = options ?? new QualityScreenOptions();
        var weights = settings.Weights ?? DefaultWeights;
        var criteria = new List<QualityCriterionFinding>();
        var flags = new List<EvidenceFlag>();

        var effectivePeerReview = peerReviewStatus ?? InferAssertedStatus(record);

        Add(criteria, QualityCriterionCodes.PeerReviewed, weights,
            effectivePeerReview switch
            {
                PeerReviewStatus.VerifiedFromRegistry => CriterionOutcome.Satisfied,
                PeerReviewStatus.AssertedByProvider => CriterionOutcome.PartiallySatisfied,
                PeerReviewStatus.NotPeerReviewed => CriterionOutcome.Failed,
                _ => CriterionOutcome.NoSignal,
            });

        if (effectivePeerReview == PeerReviewStatus.AssertedByProvider)
        {
            flags.Add(Flag(QualityFlagCodes.PeerReviewUnconfirmed, FindingSeverity.Warning,
                "Peer review was asserted by a provider but not confirmed against the journal's own policy."));
        }

        Add(criteria, QualityCriterionCodes.ScholarlyForm, weights,
            record.SourceKind switch
            {
                AcademicSourceKind.JournalArticle
                    or AcademicSourceKind.ReviewArticle
                    or AcademicSourceKind.Book
                    or AcademicSourceKind.Thesis
                    or AcademicSourceKind.ClinicalGuideline
                    or AcademicSourceKind.ConferencePaper => CriterionOutcome.Satisfied,
                AcademicSourceKind.BookChapter
                    or AcademicSourceKind.EditedVolume
                    or AcademicSourceKind.Report
                    or AcademicSourceKind.Standard => CriterionOutcome.PartiallySatisfied,
                AcademicSourceKind.Preprint
                    or AcademicSourceKind.Dataset
                    or AcademicSourceKind.Other => CriterionOutcome.Failed,
                _ => CriterionOutcome.NoSignal,
            });

        Add(criteria, QualityCriterionCodes.Identifiable, weights,
            provenance.Verdict == ProvenanceVerdict.Invalid ? CriterionOutcome.Failed
                : record.NormalizedIdentifier(IdentifierKind.Doi) is not null
                    || record.NormalizedIdentifier(IdentifierKind.Isbn) is not null
                ? CriterionOutcome.Satisfied
                : CriterionOutcome.Failed);

        Add(criteria, QualityCriterionCodes.ProvenanceVerified, weights,
            provenance.Verdict switch
            {
                ProvenanceVerdict.Verified => CriterionOutcome.Satisfied,
                ProvenanceVerdict.Unverified => CriterionOutcome.PartiallySatisfied,
                ProvenanceVerdict.Disputed or ProvenanceVerdict.Invalid => CriterionOutcome.Failed,
                _ => CriterionOutcome.NoSignal,
            });

        Add(criteria, QualityCriterionCodes.FullTextReviewed, weights,
            record.AccessLevel switch
            {
                SourceAccessLevel.FullText => CriterionOutcome.Satisfied,
                SourceAccessLevel.PrivateUpload => CriterionOutcome.Satisfied,
                SourceAccessLevel.Abstract => CriterionOutcome.PartiallySatisfied,
                SourceAccessLevel.MetadataOnly => CriterionOutcome.Failed,
                _ => CriterionOutcome.NoSignal,
            });

        if (record.AccessLevel is SourceAccessLevel.MetadataOnly or SourceAccessLevel.Unknown)
        {
            flags.Add(Flag(QualityFlagCodes.MetadataOnly, FindingSeverity.Warning,
                "Only catalogue metadata is held for this source, so nothing in a review may quote or paraphrase it."));
        }
        Add(criteria, QualityCriterionCodes.Recency, weights,
            EvaluateRecency(record, settings));

        Add(criteria, QualityCriterionCodes.AppraisalRecorded, weights,
            biasAssessment is null
                ? CriterionOutcome.NoSignal
                : biasAssessment.OverallRisk switch
                {
                    OverallBiasRisk.LowRisk => CriterionOutcome.Satisfied,
                    OverallBiasRisk.SomeConcerns => CriterionOutcome.PartiallySatisfied,
                    OverallBiasRisk.HighRisk => CriterionOutcome.Failed,
                    _ => CriterionOutcome.NoSignal,
                });

        Add(criteria, QualityCriterionCodes.DesignFitness, weights,
            EvaluateDesignFitness(record, settings));

        var admissibleNotices = provenance.AdmissibleNotices;
        Add(criteria, QualityCriterionCodes.IntegrityClear, weights,
            admissibleNotices.Count == 0 ? CriterionOutcome.Satisfied
                : admissibleNotices.Any(n => n.Kind is RetractionKind.FullRetraction or RetractionKind.PartialRetraction)
                    ? CriterionOutcome.Failed
                    : CriterionOutcome.PartiallySatisfied);

        if (admissibleNotices.Count > 0)
        {
            flags.Add(Flag(QualityFlagCodes.IntegrityNotice, FindingSeverity.Blocking,
                "This work carries a retraction or correction notice recorded from a registry or the publisher."));
        }

        if (provenance.UnconfirmedNoticeLeads.Count > 0)
        {
            flags.Add(Flag(QualityFlagCodes.IntegrityUnconfirmed, FindingSeverity.Warning,
                "A provider suggested this work may have been retracted. That has not been confirmed, so the "
                    + "source is flagged rather than removed."));
        }

        if (provenance.Verdict == ProvenanceVerdict.Disputed)
        {
            flags.Add(Flag(QualityFlagCodes.ProvenanceDisputed, FindingSeverity.Blocking,
                "Metadata sources disagree about this work's identity, so it cannot be cited until one is chosen."));
        }

        var scored = criteria.Where(c => c.Outcome is not (CriterionOutcome.NoSignal or CriterionOutcome.NotApplicable)).ToList();
        var applicableWeight = criteria
            .Where(c => c.Outcome != CriterionOutcome.NotApplicable)
            .Sum(c => c.Weight);
        var earned = scored.Sum(c => c.Earned);
        var coverage = applicableWeight <= 0 ? 0 : scored.Sum(c => c.Weight) / applicableWeight;

        var score = applicableWeight <= 0 ? 0 : earned / applicableWeight;

        var withdrawn = admissibleNotices.Any(n => n.Kind is RetractionKind.FullRetraction or RetractionKind.PartialRetraction)
            || provenance.Verdict == ProvenanceVerdict.Withdrawn;

        QualityTier tier;
        if (withdrawn)
        {
            tier = QualityTier.Withdrawn;
        }
        else if (coverage < 0.34)
        {
            tier = QualityTier.InsufficientInformation;
            flags.Add(Flag(QualityFlagCodes.InsufficientSignals, FindingSeverity.Warning,
                "Only part of the screening criteria had recorded signals, so no quality claim is made about this source."));
        }
        else if (score >= HighTierThreshold)
        {
            tier = QualityTier.High;
        }
        else if (score >= ModerateTierThreshold)
        {
            tier = QualityTier.Moderate;
        }
        else
        {
            tier = QualityTier.Low;
        }

        return new QualityScreenResult
        {
            SourceId = record.SourceId,
            WorkspaceId = record.WorkspaceId,
            Tier = tier,
            // A withdrawn work carries no quality credit at all, whatever its
            // other signals said before the notice arrived.
            Score = withdrawn ? 0 : Math.Round(Math.Clamp(score, 0, 1), 4),
            EvidenceCoverage = Math.Round(Math.Clamp(coverage, 0, 1), 4),
            Criteria = criteria,
            Flags = flags,
            ProvenanceVerdictAtScreen = provenance.Verdict,
            ScreenedAtUtc = screenedAtUtc ?? provenance.RetractionNotices
                .Select(n => n.NoticedAtUtc)
                .DefaultIfEmpty(DateTimeOffset.UnixEpoch)
                .Max(),
        };
    }

    private static CriterionOutcome EvaluateRecency(
        NormalizedBibliographicRecord record,
        QualityScreenOptions settings)
    {
        if (settings.RecencyWindowStartYear is null) return CriterionOutcome.NotApplicable;
        if (!int.TryParse(record.PublicationYear, out var year)) return CriterionOutcome.NoSignal;

        // A history or legal-review question may have no recency window at all,
        // which the caller expresses by leaving the window unset.
        return year >= settings.RecencyWindowStartYear.Value
            ? CriterionOutcome.Satisfied
            : CriterionOutcome.Failed;
    }

    private static CriterionOutcome EvaluateDesignFitness(
        NormalizedBibliographicRecord record,
        QualityScreenOptions settings)
    {
        if (settings.PreferredDesigns is null || settings.PreferredDesigns.Count == 0)
        {
            return CriterionOutcome.NotApplicable;
        }

        if (string.IsNullOrWhiteSpace(record.StudyDesign)) return CriterionOutcome.NoSignal;

        var design = AuthorNameNormalizer.FoldForMatching(record.StudyDesign);
        return settings.PreferredDesigns.Any(p =>
            string.Equals(AuthorNameNormalizer.FoldForMatching(p), design, StringComparison.Ordinal))
            ? CriterionOutcome.Satisfied
            : CriterionOutcome.PartiallySatisfied;
    }

    /// <summary>
    /// Peer-review status that may be inferred from the record itself, which is an
    /// assertion, never a verification. Preprints and uploads are known not to be
    /// peer reviewed; everything else stays unknown.
    /// </summary>
    private static PeerReviewStatus InferAssertedStatus(NormalizedBibliographicRecord record) => record.SourceKind switch
    {
        AcademicSourceKind.Preprint => PeerReviewStatus.NotPeerReviewed,
        _ when record.AccessLevel == SourceAccessLevel.PrivateUpload => PeerReviewStatus.NotPeerReviewed,
        _ => PeerReviewStatus.Unknown,
    };

    private static void Add(
        List<QualityCriterionFinding> criteria,
        string code,
        IReadOnlyDictionary<string, double> weights,
        CriterionOutcome outcome)
    {
        var weight = weights.TryGetValue(code, out var configured) ? configured : 0;
        criteria.Add(new QualityCriterionFinding
        {
            CriterionCode = code,
            Outcome = outcome,
            Weight = weight,
            Earned = outcome switch
            {
                CriterionOutcome.Satisfied => weight,
                CriterionOutcome.PartiallySatisfied => weight / 2,
                _ => 0,
            },
        });
    }

    private static EvidenceFlag Flag(string code, FindingSeverity severity, string message) => new()
    {
        Code = code,
        Severity = severity,
        Message = message,
    };
}
