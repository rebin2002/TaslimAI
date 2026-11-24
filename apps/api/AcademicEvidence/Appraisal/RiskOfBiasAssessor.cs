using System.Globalization;
using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Appraisal;

/// <summary>
/// Domain vocabulary for the supported appraisal frameworks, keyed by short code.
/// </summary>
public static class BiasFrameworks
{
    /// <summary>Cochrane RoB 2 domains for randomised trials.</summary>
    public static readonly IReadOnlyList<string> Rob2 =
    [
        "bias1_randomisation",
        "bias2_deviations",
        "bias3_missing_outcomes",
        "bias4_measurement",
        "bias5_selection_of_reported_result",
    ];

    /// <summary>ROBINS-I style domains for non-randomised studies.</summary>
    public static readonly IReadOnlyList<string> RobinsI =
    [
        "confounding",
        "selection_of_participants",
        "classification_of_interventions",
        "deviations_from_intended_interventions",
        "missing_data",
        "measurement_of_outcomes",
        "selection_of_reported_result",
    ];

    /// <summary>Common qualitative appraisal prompts, CASP-style.</summary>
    public static readonly IReadOnlyList<string> Qualitative =
    [
        "research_design_appropriateness",
        "recruitment_strategy",
        "data_collection_reflexivity",
        "relationship_with_participants",
        "ethical_considerations",
        "analysis_adequacy",
        "findings_credibility",
    ];

    /// <summary>Resolve a framework code to its domain list, or null when unknown.</summary>
    public static IReadOnlyList<string>? DomainsFor(string? framework) => framework?.ToLowerInvariant() switch
    {
        "rob2" or "rob-2" => Rob2,
        "robinsi" or "robins-i" => RobinsI,
        "casp-qual" or "qualitative" or "casp" => Qualitative,
        _ => null,
    };
}

/// <summary>
/// Aggregates answered risk-of-bias domains into an overall judgement.
///
/// The rule that matters for integrity is the incomplete case: a source with no
/// appraisal recorded comes out <see cref="OverallBiasRisk.Unclear"/>, and an
/// appraisal answered only by a model draft is held at unclear too. Automated
/// appraisal defaults to "low risk" when left blank, which quietly upgrades every
/// unread paper in a review, so the assessor refuses to do that.
/// </summary>
public static class RiskOfBiasAssessor
{
    /// <summary>Build an assessment from recorded domain answers.</summary>
    /// <param name="record">Source being appraised, used for the study design.</param>
    /// <param name="framework">Framework code such as <c>rob2</c>.</param>
    /// <param name="findings">Answers supplied by a reviewer or a deposit.</param>
    /// <param name="assessedAtUtc">Timestamp for the record.</param>
    public static RiskOfBiasAssessment Assess(
        NormalizedBibliographicRecord record,
        string framework,
        IReadOnlyList<BiasDomainFinding> findings,
        DateTimeOffset assessedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(findings);

        var domains = BiasFrameworks.DomainsFor(framework)
            ?? throw new ArgumentException($"Unknown risk-of-bias framework '{framework}'.", nameof(framework));

        var flags = new List<EvidenceFlag>();

        // Only reviewer- or registry-answered domains count toward the judgement.
        // A model draft is kept visible so the member can confirm it.
        var reviewerAnswered = findings
            .Where(f => f.Source is BiasJudgementSource.Reviewer or BiasJudgementSource.Registry)
            .ToList();

        var drafts = findings
            .Where(f => f.Source == BiasJudgementSource.ModelDraft)
            .ToList();

        var answeredKeys = reviewerAnswered
            .Select(f => f.DomainCode)
            .ToHashSet(StringComparer.Ordinal);

        var covered = domains.Count(d => answeredKeys.Contains(d));
        var coverage = domains.Count == 0 ? 0 : (double)covered / domains.Count;

        var unknownDomains = findings
            .Where(f => !domains.Contains(f.DomainCode, StringComparer.Ordinal))
            .ToList();
        if (unknownDomains.Count > 0)
        {
            flags.Add(new EvidenceFlag
            {
                Code = "BIAS_DOMAIN_UNKNOWN",
                Severity = FindingSeverity.Warning,
                Message = $"{unknownDomains.Count} appraisal answer(s) name a domain outside the {framework} "
                    + "framework and were not counted.",
            });
        }

        var judgedDomains = reviewerAnswered.Where(f => domains.Contains(f.DomainCode, StringComparer.Ordinal)).ToList();

        var overall = Judge(judgedDomains, coverage);

        if (coverage < AcademicEvidenceLimits.MinimumBiasAnsweredCoverage)
        {
            flags.Add(new EvidenceFlag
            {
                Code = "BIAS_INCOMPLETE",
                Severity = FindingSeverity.Warning,
                Message = $"Only {Percent(coverage)} of the {framework} domains were answered, so the overall "
                    + "risk of bias is unclear rather than low.",
            });
        }

        else if (coverage < 1)
        {
            // Past the conclusive threshold, but not everything was answered: the
            // judgement rests on the domains that were, and the review must be able
            // to say which ones those were.
            var unanswered = domains.Where(d => !answeredKeys.Contains(d)).ToList();
            flags.Add(new EvidenceFlag
            {
                Code = "BIAS_PARTIALLY_ANSWERED",
                Severity = FindingSeverity.Note,
                Message = $"{unanswered.Count} of the {framework} domains ({string.Join(", ", unanswered)}) have no "
                    + "recorded answer, so the overall judgement rests on the rest.",
            });
        }
        if (drafts.Count > 0 && reviewerAnswered.Count == 0)
        {
            flags.Add(new EvidenceFlag
            {
                Code = "BIAS_MODEL_DRAFT_ONLY",
                Severity = FindingSeverity.Warning,
                Message = "These risk-of-bias answers were drafted from the text and have not been confirmed by a "
                    + "reviewer, so they carry no judgement yet.",
            });
        }

        return new RiskOfBiasAssessment
        {
            SourceId = record.SourceId,
            WorkspaceId = record.WorkspaceId,
            Framework = framework,
            StudyDesign = record.StudyDesign ?? "unspecified",
            Domains =
            [
                .. reviewerAnswered,
                .. drafts.Select(d => d with
                {
                    Rationale = string.IsNullOrWhiteSpace(d.Rationale)
                        ? "Draft answer, unconfirmed."
                        : d.Rationale + " (draft, unconfirmed)",
                }),
            ],
            OverallRisk = overall,
            AnsweredCoverage = Math.Round(Math.Clamp(coverage, 0, 1), 4),
            Flags = flags,
            AssessedAtUtc = assessedAtUtc,
        };
    }

    /// <summary>
    /// True when the appraisal is safe to treat as an independent quality signal
    /// when weighting evidence in the matrix.
    /// </summary>
    public static bool IsConclusive(RiskOfBiasAssessment assessment) =>
        assessment.OverallRisk != OverallBiasRisk.Unclear
        && assessment.AnsweredCoverage >= AcademicEvidenceLimits.MinimumBiasAnsweredCoverage;

    private static OverallBiasRisk Judge(IReadOnlyList<BiasDomainFinding> answered, double coverage)
    {
        if (answered.Count == 0 || coverage < AcademicEvidenceLimits.MinimumBiasAnsweredCoverage)
        {
            return OverallBiasRisk.Unclear;
        }

        var applicable = answered
            .Where(f => f.Judgement != BiasJudgement.NotApplicable)
            .ToList();

        if (applicable.Count == 0) return OverallBiasRisk.Unclear;

        var high = applicable.Count(f => f.Judgement == BiasJudgement.HighConcern);
        var some = applicable.Count(f => f.Judgement == BiasJudgement.SomeConcern);

        // One high-concern domain is enough to hold the whole study at high risk,
        // which is how the framework itself is specified.
        if (high > 0) return OverallBiasRisk.HighRisk;
        if (some > 0) return OverallBiasRisk.SomeConcerns;
        return OverallBiasRisk.LowRisk;
    }

    private static string Percent(double value) =>
        Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + "%";
}
