namespace Taslim.Api.AcademicEvidence.Domain;

/// <summary>
/// Outcome of screening one source for the literature review. Scores are only
/// ever produced from recorded signals: a missing signal contributes nothing and
/// is reported as a flag rather than silently counted for or against the source.
/// </summary>
public sealed record QualityScreenResult
{
    public required Guid SourceId { get; init; }
    public required Guid WorkspaceId { get; init; }

    public QualityTier Tier { get; init; } = QualityTier.InsufficientInformation;

    /// <summary>
    /// Sum of the criterion weights that were actually evidenced, between 0 and
    /// 1. Deliberately paired with <see cref="EvidenceCoverage"/> so a source
    /// scored on two signals can never look as solid as one scored on six.
    /// </summary>
    public double Score { get; init; }

    /// <summary>Share of the applicable criteria that had a recorded signal.</summary>
    public double EvidenceCoverage { get; init; }

    public IReadOnlyList<QualityCriterionFinding> Criteria { get; init; } = [];

    /// <summary>Disclosure flags such as missing peer-review evidence or a withdrawn notice.</summary>
    public IReadOnlyList<EvidenceFlag> Flags { get; init; } = [];

    /// <summary>Provenance verdict at the time of screening, kept so quality never outranks provenance.</summary>
    public ProvenanceVerdict ProvenanceVerdictAtScreen { get; init; } = ProvenanceVerdict.Unassessed;

    public DateTimeOffset ScreenedAtUtc { get; init; }
}

/// <summary>Overall quality tier assigned by the screener.</summary>
public enum QualityTier
{
    /// <summary>Not enough recorded signals to place the source.</summary>
    InsufficientInformation = 0,
    Low,
    Moderate,
    High,

    /// <summary>Retracted or partially retracted; excluded from synthesis.</summary>
    Withdrawn,
}

/// <summary>One criterion evaluated by the screener.</summary>
public sealed record QualityCriterionFinding
{
    public required string CriterionCode { get; init; }
    public required CriterionOutcome Outcome { get; init; }
    public double Weight { get; init; }
    public double Earned { get; init; }

    /// <summary>Origin key that supplied the signal, so a reviewer can trace it.</summary>
    public string? SignalOrigin { get; init; }

    public string? Note { get; init; }
}

/// <summary>How a screening criterion resolved.</summary>
public enum CriterionOutcome
{
    /// <summary>No recorded signal; contributes neither score nor penalty.</summary>
    NoSignal = 0,
    Satisfied,
    PartiallySatisfied,
    Failed,
    NotApplicable,
}

/// <summary>A disclosure flag raised by appraisal or evidence assembly.</summary>
public sealed record EvidenceFlag
{
    /// <summary>Stable machine code, for example <c>RETRACTION_FULL</c>.</summary>
    public required string Code { get; init; }

    public FindingSeverity Severity { get; init; } = FindingSeverity.Warning;

    /// <summary>Member-facing sentence, generated from the code so wording stays consistent.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// A risk-of-bias appraisal over the appraisal domains that were actually
/// answered. Unanswered domains stay <see cref="BiasJudgement.NoInformation"/>
/// and hold the overall judgement at "unclear" rather than defaulting to low
/// risk, which is the common way automated appraisals overstate quality.
/// </summary>
public sealed record RiskOfBiasAssessment
{
    public required Guid SourceId { get; init; }
    public required Guid WorkspaceId { get; init; }

    /// <summary>Framework the domains come from, for example <c>rob2</c> or <c>casp-qual</c>.</summary>
    public required string Framework { get; init; }

    public required string StudyDesign { get; init; }

    public IReadOnlyList<BiasDomainFinding> Domains { get; init; } = [];

    public OverallBiasRisk OverallRisk { get; init; } = OverallBiasRisk.Unclear;

    /// <summary>Share of domains that received an answer, surfaced next to the judgement.</summary>
    public double AnsweredCoverage { get; init; }

    /// <summary>True when the appraisal is too incomplete to publish a judgement.</summary>
    public IReadOnlyList<EvidenceFlag> Flags { get; init; } = [];

    public DateTimeOffset AssessedAtUtc { get; init; }
}

/// <summary>One domain of a risk-of-bias tool.</summary>
public sealed record BiasDomainFinding
{
    public required string DomainCode { get; init; }
    public required BiasJudgement Judgement { get; init; }

    /// <summary>Recorded rationale, only from the member or a captured appraisal form.</summary>
    public string? Rationale { get; init; }

    /// <summary>Who answered it, so a model guess is distinguishable from a reviewer.</summary>
    public BiasJudgementSource Source { get; init; } = BiasJudgementSource.Unanswered;
}

/// <summary>Who produced a bias judgement.</summary>
public enum BiasJudgementSource
{
    /// <summary>No answer recorded.</summary>
    Unanswered = 0,

    /// <summary>Member or reviewer answered from the methods section.</summary>
    Reviewer,

    /// <summary>Read from a structured appraisal deposit.</summary>
    Registry,

    /// <summary>
    /// Suggested by a model from the text. Surfaced as a draft the member must
    /// confirm, and excluded from <see cref="RiskOfBiasAssessment.OverallRisk"/>.
    /// </summary>
    ModelDraft,
}

/// <summary>Aggregated risk-of-bias judgement.</summary>
public enum OverallBiasRisk
{
    /// <summary>Too few answered domains to judge.</summary>
    Unclear = 0,
    LowRisk,
    SomeConcerns,
    HighRisk,
}

/// <summary>
/// A recorded decision to include or exclude a source, with the reason codes and
/// the criteria revision they came from, so the review's PRISMA-style counts are
/// reproducible.
/// </summary>
public sealed record InclusionDecision
{
    public required Guid SourceId { get; init; }
    public required Guid WorkspaceId { get; init; }

    public ScreeningDecision Decision { get; init; } = ScreeningDecision.Unscheduled;

    /// <summary>Inclusion reason codes when <see cref="ScreeningDecision.Included"/>.</summary>
    public IReadOnlyList<string> InclusionReasons { get; init; } = [];

    /// <summary>Exclusion reason codes when <see cref="ScreeningDecision.Excluded"/>.</summary>
    public IReadOnlyList<string> ExclusionReasons { get; init; } = [];
    /// <summary>
    /// Checks that could not be evaluated, which is what a member has to act on
    /// before a source can move out of the undetermined pile.
    /// </summary>
    public IReadOnlyList<string> UndeterminedReasons { get; init; } = [];

    /// <summary>Criteria revision this decision was evaluated against.</summary>
    public string CriteriaVersion { get; init; } = string.Empty;

    /// <summary>Free-text note recorded by the member.</summary>
    public string? MemberNote { get; init; }

    public AuditActorKind DecidedBy { get; init; } = AuditActorKind.System;

    public DateTimeOffset DecidedAtUtc { get; init; }
}
