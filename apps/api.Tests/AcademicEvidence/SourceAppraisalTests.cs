using Taslim.Api.AcademicEvidence.Appraisal;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;
using Xunit;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// Literature appraisal: quality screening, risk of bias and inclusion. The
/// property under test is not "high quality sources score high" but that the
/// pipeline declines to invent quality it was never given: an unread abstract is
/// not a reviewed full text, a provider's claim of peer review is not peer review,
/// and a blank appraisal form is not a low-risk study.
/// </summary>
public sealed class SourceAppraisalTests
{
    private static (NormalizedBibliographicRecord Record, SourceProvenance Provenance) VerifiedAbstractSource(
        Guid? sourceId = null)
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(sourceId);
        return (pair.Record, AcademicEvidenceFixtures.ProvenanceOf(pair));
    }

    private static List<RawBibliographicMetadata> OriginsWithAbstract()
        => AcademicEvidenceFixtures.CorroboratedJournalArticle()
            .Select(o => o with { AbstractText = AcademicEvidenceFixtures.AbstractText })
            .ToList();

    private static List<BiasDomainFinding> AllLowConcern(string framework)
        => BiasFrameworks.DomainsFor(framework)!
            .Select(d => new BiasDomainFinding
            {
                DomainCode = d,
                Judgement = BiasJudgement.LowConcern,
                Rationale = "Reported in the methods section.",
                Source = BiasJudgementSource.Reviewer,
            })
            .ToList();

    [Fact]
    public void A_verified_article_with_a_recorded_appraisal_reaches_the_high_tier()
    {
        var (record, provenance) = VerifiedAbstractSource();
        var bias = RiskOfBiasAssessor.Assess(
            record, "rob2", AllLowConcern("rob2"), AcademicEvidenceFixtures.Clock);

        var screen = SourceQualityScreener.Screen(
            record, provenance, PeerReviewStatus.VerifiedFromRegistry, bias,
            screenedAtUtc: AcademicEvidenceFixtures.Clock);

        Assert.Equal(QualityTier.High, screen.Tier);
        Assert.InRange(screen.Score, SourceQualityScreener.HighTierThreshold, 1.0);
        Assert.True(screen.EvidenceCoverage >= 0.8);
        Assert.Equal(ProvenanceVerdict.Verified, screen.ProvenanceVerdictAtScreen);
        Assert.Empty(screen.Flags);
        Assert.Equal(0.20, screen.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.PeerReviewed).Weight);
    }

    [Fact]
    public void A_provider_claiming_peer_review_scores_partly_and_says_so()
    {
        var (record, provenance) = VerifiedAbstractSource();
        var screen = SourceQualityScreener.Screen(
            record, provenance, PeerReviewStatus.AssertedByProvider, screenedAtUtc: AcademicEvidenceFixtures.Clock);

        var criterion = screen.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.PeerReviewed);
        Assert.Equal(CriterionOutcome.PartiallySatisfied, criterion.Outcome);
        Assert.Contains(screen.Flags, f => f.Code == QualityFlagCodes.PeerReviewUnconfirmed);
    }

    [Fact]
    public void Abstract_only_never_counts_as_a_read_full_text()
    {
        var (record, provenance) = VerifiedAbstractSource();
        Assert.Equal(SourceAccessLevel.Abstract, record.AccessLevel);

        var screen = SourceQualityScreener.Screen(record, provenance, screenedAtUtc: AcademicEvidenceFixtures.Clock);
        var criterion = screen.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.FullTextReviewed);

        Assert.Equal(CriterionOutcome.PartiallySatisfied, criterion.Outcome);
        Assert.DoesNotContain(screen.Flags, f => f.Code == QualityFlagCodes.MetadataOnly);
    }

    [Fact]
    public void Catalogue_metadata_alone_is_flagged_as_unreadable()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse("cccccccc-0000-4000-8000-000000000001"), abstractText: null);
        Assert.Equal(SourceAccessLevel.MetadataOnly, pair.Record.AccessLevel);

        var screen = SourceQualityScreener.Screen(
            pair.Record, AcademicEvidenceFixtures.ProvenanceOf(pair), screenedAtUtc: AcademicEvidenceFixtures.Clock);

        Assert.Contains(screen.Flags, f =>
            f.Code == QualityFlagCodes.MetadataOnly && f.Severity == FindingSeverity.Warning);
        Assert.Equal(CriterionOutcome.Failed,
            screen.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.FullTextReviewed).Outcome);
    }

    [Fact]
    public void A_source_nothing_was_recorded_about_is_insufficient_information_not_low_quality()
    {
        // A hand-built record with no peer-review status, no appraisal and no
        // readable text: the honest answer is "we do not know".
        var record = new NormalizedBibliographicRecord
        {
            SourceId = Guid.Parse("cccccccc-0000-4000-8000-000000000002"),
            WorkspaceId = AcademicEvidenceFixtures.Workspace,
            Title = "Untitled pile",
            SourceKind = AcademicSourceKind.Unknown,
            AccessLevel = SourceAccessLevel.Unknown,
            Identifiers = [],
        };
        var provenance = new SourceProvenance
        {
            SourceId = record.SourceId,
            WorkspaceId = record.WorkspaceId,
            Verdict = ProvenanceVerdict.Unassessed,
        };

        var screen = SourceQualityScreener.Screen(record, provenance, screenedAtUtc: AcademicEvidenceFixtures.Clock);

        Assert.Equal(QualityTier.InsufficientInformation, screen.Tier);
        Assert.Contains(screen.Flags, f => f.Code == QualityFlagCodes.InsufficientSignals);
        Assert.True(screen.EvidenceCoverage < 0.34);
    }

    [Fact]
    public void Retracted_source_is_tiered_withdrawn_and_cannot_be_included_even_when_selected()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse("cccccccc-0000-4000-8000-000000000003"));
        var notice = new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            Origin = RetractionNoticeOrigin.Registry,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        };
        var provenance = AcademicEvidenceFixtures.ProvenanceOf(pair, [notice]);
        Assert.Equal(ProvenanceVerdict.Withdrawn, provenance.Verdict);

        var screen = SourceQualityScreener.Screen(
            pair.Record, provenance, PeerReviewStatus.VerifiedFromRegistry, screenedAtUtc: AcademicEvidenceFixtures.Clock);

        Assert.Equal(QualityTier.Withdrawn, screen.Tier);
        Assert.Contains(screen.Flags, f =>
            f.Code == QualityFlagCodes.IntegrityNotice && f.Severity == FindingSeverity.Blocking);

        var evaluation = InclusionExclusionPolicy.Evaluate(
            pair.Record, provenance, screen, new InclusionCriteria(Version: "protocol-3"), isMemberSelected: true);

        Assert.Equal(ScreeningDecision.Excluded, evaluation.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.Retracted, evaluation.Decision.ExclusionReasons);
        Assert.False(evaluation.MemberOverride);
        Assert.Equal("protocol-3", evaluation.Decision.CriteriaVersion);
        Assert.Equal(AcademicEvidenceFixtures.Clock, evaluation.Decision.DecidedAtUtc);
    }

    [Fact]
    public void A_retraction_only_a_provider_asserted_flags_rather_than_excludes()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse("cccccccc-0000-4000-8000-000000000004"));
        var lead = new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            Origin = RetractionNoticeOrigin.ProviderAssertion,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        };
        var provenance = AcademicEvidenceFixtures.ProvenanceOf(pair, [lead]);
        var screen = SourceQualityScreener.Screen(
            pair.Record, provenance, PeerReviewStatus.VerifiedFromRegistry, screenedAtUtc: AcademicEvidenceFixtures.Clock);
        var evaluation = InclusionExclusionPolicy.Evaluate(pair.Record, provenance, screen);

        Assert.NotEqual(QualityTier.Withdrawn, screen.Tier);
        Assert.Contains(screen.Flags, f => f.Code == QualityFlagCodes.IntegrityUnconfirmed);
        Assert.Equal(ScreeningDecision.Included, evaluation.Decision.Decision);
        Assert.DoesNotContain(InclusionReasonCodes.Retracted, evaluation.Decision.ExclusionReasons);
    }

    [Fact]
    public void Disputed_identity_is_a_blocking_flag_and_an_exclusion()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("cccccccc-0000-4000-8000-000000000005"),
            [
                AcademicEvidenceFixtures.Origin("crossref", doi: AcademicEvidenceFixtures.TestDoi,
                    abstractText: AcademicEvidenceFixtures.AbstractText),
                AcademicEvidenceFixtures.Origin("openalex", doi: AcademicEvidenceFixtures.OtherTestDoi,
                    sourceKindToken: "article", abstractText: AcademicEvidenceFixtures.AbstractText),
            ],
            AcademicEvidenceFixtures.Clock);
        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        var screen = SourceQualityScreener.Screen(
            normalization.Record, provenance, screenedAtUtc: AcademicEvidenceFixtures.Clock);
        var evaluation = InclusionExclusionPolicy.Evaluate(
            normalization.Record, provenance, screen, isMemberSelected: true);

        Assert.Contains(screen.Flags, f =>
            f.Code == QualityFlagCodes.ProvenanceDisputed && f.Severity == FindingSeverity.Blocking);
        Assert.Equal(ScreeningDecision.Excluded, evaluation.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.ProvenanceDisputed, evaluation.Decision.ExclusionReasons);
    }

    [Fact]
    public void Recency_window_only_applies_when_the_protocol_sets_one()
    {
        var (record, provenance) = VerifiedAbstractSource();

        var noWindow = SourceQualityScreener.Screen(record, provenance, options: new QualityScreenOptions());
        var staleWindow = SourceQualityScreener.Screen(record, provenance,
            options: new QualityScreenOptions(RecencyWindowStartYear: 2015), screenedAtUtc: AcademicEvidenceFixtures.Clock);
        var tightWindow = SourceQualityScreener.Screen(record, provenance,
            options: new QualityScreenOptions(RecencyWindowStartYear: 2030), screenedAtUtc: AcademicEvidenceFixtures.Clock);

        Assert.Equal(CriterionOutcome.NotApplicable,
            noWindow.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.Recency).Outcome);
        Assert.Equal(CriterionOutcome.Satisfied,
            staleWindow.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.Recency).Outcome);
        Assert.Equal(CriterionOutcome.Failed,
            tightWindow.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.Recency).Outcome);
    }

    [Fact]
    public void Weights_are_explainable_and_rescaled_per_criterion()
    {
        var (record, provenance) = VerifiedAbstractSource();
        // A protocol that cares only about provenance: peer review is unscored, so
        // the tier must come from the criteria that carry weight.
        var weights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [QualityCriterionCodes.ProvenanceVerified] = 1.0,
        };

        var screen = SourceQualityScreener.Screen(record, provenance,
            options: new QualityScreenOptions(Weights: weights), screenedAtUtc: AcademicEvidenceFixtures.Clock);

        Assert.Equal(1.0, screen.Score, 6);
        Assert.Equal(QualityTier.High, screen.Tier);
        // Every criterion is still reported, unwighted rather than hidden, so the
        // reviewer can see what the protocol chose not to care about.
        var unscored = screen.Criteria.Single(c => c.CriterionCode == QualityCriterionCodes.ScholarlyForm);
        Assert.Equal(0, unscored.Weight);
        Assert.Equal(0, unscored.Earned);
        Assert.Equal(CriterionOutcome.Satisfied, unscored.Outcome);
        // Coverage asks how much of the weighted protocol had a signal to score:
        // the one criterion that carries weight here does have one.
        Assert.Equal(1, screen.EvidenceCoverage, 4);
    }

    [Fact]
    public void An_empty_appraisal_form_is_unclear_rather_than_low_risk()
    {
        var (record, _) = VerifiedAbstractSource();
        var bias = RiskOfBiasAssessor.Assess(record, "rob2", [], AcademicEvidenceFixtures.Clock);

        Assert.Equal(OverallBiasRisk.Unclear, bias.OverallRisk);
        Assert.Equal(0, bias.AnsweredCoverage);
        Assert.Contains(bias.Flags, f => f.Code == "BIAS_INCOMPLETE");
        Assert.False(RiskOfBiasAssessor.IsConclusive(bias));
    }

    [Fact]
    public void Model_drafts_are_visible_but_never_become_the_judgement()
    {
        var (record, _) = VerifiedAbstractSource();
        var drafts = BiasFrameworks.DomainsFor("rob2")!
            .Select(d => new BiasDomainFinding
            {
                DomainCode = d,
                Judgement = BiasJudgement.LowConcern,
                Rationale = "Drafted from the abstract by a model.",
                Source = BiasJudgementSource.ModelDraft,
            })
            .ToList();

        var bias = RiskOfBiasAssessor.Assess(record, "rob2", drafts, AcademicEvidenceFixtures.Clock);

        Assert.Equal(OverallBiasRisk.Unclear, bias.OverallRisk);
        Assert.Equal(0, bias.AnsweredCoverage);
        Assert.Contains(bias.Flags, f => f.Code == "BIAS_MODEL_DRAFT_ONLY");
        Assert.Equal(5, bias.Domains.Count);
    }

    [Fact]
    public void One_high_concern_domain_holds_the_whole_study_at_high_risk()
    {
        var (record, _) = VerifiedAbstractSource();
        var findings = AllLowConcern("rob2");
        findings[2] = findings[2] with
        {
            Judgement = BiasJudgement.HighConcern,
            Source = BiasJudgementSource.Reviewer,
        };

        var bias = RiskOfBiasAssessor.Assess(record, "rob2", findings, AcademicEvidenceFixtures.Clock);

        Assert.Equal(OverallBiasRisk.HighRisk, bias.OverallRisk);
        Assert.True(RiskOfBiasAssessor.IsConclusive(bias));
    }

    [Fact]
    public void Half_answered_domains_are_enough_to_be_conclusive_but_not_to_be_clean()
    {
        var (record, _) = VerifiedAbstractSource();
        var domains = BiasFrameworks.DomainsFor("rob2")!;
        var findings = domains.Take(3)
            .Select(d => new BiasDomainFinding
            {
                DomainCode = d,
                Judgement = BiasJudgement.SomeConcern,
                Source = BiasJudgementSource.Reviewer,
            })
            .ToList();

        var bias = RiskOfBiasAssessor.Assess(record, "rob2", findings, AcademicEvidenceFixtures.Clock);

        Assert.Equal(0.6, bias.AnsweredCoverage, 4);
        Assert.Equal(OverallBiasRisk.SomeConcerns, bias.OverallRisk);
        // Past the conclusive threshold, so it is no longer "incomplete", but the
        // two blank domains are still named for the reviewer.
        Assert.DoesNotContain(bias.Flags, f => f.Code == "BIAS_INCOMPLETE");
        var partial = Assert.Single(bias.Flags, f => f.Code == "BIAS_PARTIALLY_ANSWERED");
        Assert.Equal(FindingSeverity.Note, partial.Severity);
        Assert.Contains("bias4_measurement", partial.Message);
    }

    [Fact]
    public void Domains_outside_the_framework_are_flagged_and_not_scored()
    {
        var (record, _) = VerifiedAbstractSource();
        var findings = AllLowConcern("rob2");
        findings.Add(new BiasDomainFinding
        {
            DomainCode = "bias9_invented_domain",
            Judgement = BiasJudgement.LowConcern,
            Source = BiasJudgementSource.Reviewer,
        });

        var bias = RiskOfBiasAssessor.Assess(record, "rob2", findings, AcademicEvidenceFixtures.Clock);

        Assert.Contains(bias.Flags, f => f.Code == "BIAS_DOMAIN_UNKNOWN");
        Assert.Equal(5, bias.Domains.Count(f => f.DomainCode != "bias9_invented_domain"));
    }

    [Fact]
    public void An_unknown_framework_is_rejected_rather_than_silently_defaulted()
    {
        var (record, _) = VerifiedAbstractSource();
        Assert.Throws<ArgumentException>(() => RiskOfBiasAssessor.Assess(
            record, "rob-9000", [], AcademicEvidenceFixtures.Clock));
        Assert.Null(BiasFrameworks.DomainsFor("rob-9000"));
        Assert.NotNull(BiasFrameworks.DomainsFor("ROB2"));
        Assert.Equal(7, BiasFrameworks.DomainsFor("robins-i")!.Count);
        Assert.Equal(7, BiasFrameworks.DomainsFor("qualitative")!.Count);
    }

    [Fact]
    public void Not_applicable_domains_do_not_count_against_the_judgement()
    {
        var (record, _) = VerifiedAbstractSource();
        var domains = BiasFrameworks.DomainsFor("rob2")!;
        var findings = domains.Select((d, i) => new BiasDomainFinding
        {
            DomainCode = d,
            Judgement = i == 0 ? BiasJudgement.NotApplicable : BiasJudgement.LowConcern,
            Source = BiasJudgementSource.Reviewer,
        }).ToList();

        var bias = RiskOfBiasAssessor.Assess(record, "rob2", findings, AcademicEvidenceFixtures.Clock);

        Assert.Equal(OverallBiasRisk.LowRisk, bias.OverallRisk);
        Assert.Equal(1.0, bias.AnsweredCoverage, 4);
    }

    [Fact]
    public void Non_scholarly_forms_are_excluded_by_the_protocol()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse("cccccccc-0000-4000-8000-000000000006"),
            abstractText: AcademicEvidenceFixtures.AbstractText);
        var news = pair.Record with { SourceKind = AcademicSourceKind.Other };

        var evaluation = InclusionExclusionPolicy.Evaluate(
            news, AcademicEvidenceFixtures.ProvenanceOf(pair),
            criteria: new InclusionCriteria(AcceptedKinds: [AcademicSourceKind.JournalArticle]));

        Assert.Equal(ScreeningDecision.Excluded, evaluation.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.NotScholarlyForm, evaluation.Decision.ExclusionReasons);
    }

    [Fact]
    public void Publication_year_outside_the_window_is_excluded_on_the_stated_reason()
    {
        var (record, provenance) = VerifiedAbstractSource();

        var outside = InclusionExclusionPolicy.Evaluate(record, provenance,
            criteria: new InclusionCriteria(YearFrom: 2025, YearTo: 2026));
        var inside = InclusionExclusionPolicy.Evaluate(record, provenance,
            criteria: new InclusionCriteria(YearFrom: 2020, YearTo: 2024,
                Languages: [record.Language ?? "en"]));

        Assert.Contains(InclusionReasonCodes.OutsideDateWindow, outside.Decision.ExclusionReasons);
        Assert.Equal(ScreeningDecision.Excluded, outside.Decision.Decision);
        Assert.Equal(ScreeningDecision.Included, inside.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.WithinDateWindow, inside.Decision.InclusionReasons);
    }

    [Fact]
    public void A_source_in_another_language_is_excluded_rather_than_translated()
    {
        var (record, provenance) = VerifiedAbstractSource();
        var arabic = record with { Language = "ar" };

        var evaluation = InclusionExclusionPolicy.Evaluate(arabic, provenance,
            criteria: new InclusionCriteria(Languages: ["en", "ckb"]));

        Assert.Contains(InclusionReasonCodes.WrongLanguage, evaluation.Decision.ExclusionReasons);
    }

    [Fact]
    public void A_duplicate_is_excluded_against_the_record_that_survived()
    {
        var (record, provenance) = VerifiedAbstractSource();
        var keeper = Guid.Parse("cccccccc-0000-4000-8000-0000000000ff");

        var evaluation = InclusionExclusionPolicy.Evaluate(record, provenance, duplicateOf: keeper);

        Assert.Equal(ScreeningDecision.Excluded, evaluation.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.DuplicateOfKeptRecord, evaluation.Decision.ExclusionReasons);
    }

    [Fact]
    public void Nothing_assessed_about_relevance_leaves_the_decision_undetermined()
    {
        // The pile a member has not read yet must not silently become "included".
        var (record, provenance) = VerifiedAbstractSource();
        var evaluation = InclusionExclusionPolicy.Evaluate(record, provenance,
            criteria: new InclusionCriteria(ScopeTerms: ["primary healthcare"]));

        Assert.Equal(ScreeningDecision.Undetermined, evaluation.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.TopicRelevanceUnassessed, evaluation.Decision.UndeterminedReasons);
    }

    [Fact]
    public void Assessed_irrelevance_excludes_and_a_member_may_still_select_the_source()
    {
        var (record, provenance) = VerifiedAbstractSource();

        var irrelevant = InclusionExclusionPolicy.Evaluate(record, provenance,
            topicRelevance: 0.1, criteria: new InclusionCriteria(ScopeTerms: ["tender"]));
        var selected = InclusionExclusionPolicy.Evaluate(record, provenance,
            isMemberSelected: true, topicRelevance: 0.1, criteria: new InclusionCriteria(ScopeTerms: ["tender"]));

        Assert.Equal(ScreeningDecision.Excluded, irrelevant.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.OutOfScopeTopic, irrelevant.Decision.ExclusionReasons);
        Assert.Equal(ScreeningDecision.Included, selected.Decision.Decision);
        Assert.True(selected.MemberOverride || selected.Decision.InclusionReasons.Contains(InclusionReasonCodes.MemberSelected));
        Assert.DoesNotContain(InclusionReasonCodes.OutOfScopeTopic, selected.Decision.ExclusionReasons);
    }

    [Fact]
    public void Requiring_verified_provenance_holds_an_unverified_source_back()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("cccccccc-0000-4000-8000-000000000007"),
            [AcademicEvidenceFixtures.Origin(abstractText: AcademicEvidenceFixtures.AbstractText)],
            AcademicEvidenceFixtures.Clock);
        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        var evaluation = InclusionExclusionPolicy.Evaluate(normalization.Record, provenance,
            criteria: new InclusionCriteria(RequireVerifiedProvenance: true));

        Assert.Equal(ProvenanceVerdict.Unverified, provenance.Verdict);
        Assert.Equal(ScreeningDecision.Undetermined, evaluation.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.ProvenanceUnverified, evaluation.Decision.UndeterminedReasons);
    }

    [Fact]
    public void Requiring_full_text_excludes_a_metadata_only_record()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse("cccccccc-0000-4000-8000-000000000008"), abstractText: null);

        var evaluation = InclusionExclusionPolicy.Evaluate(
            pair.Record, AcademicEvidenceFixtures.ProvenanceOf(pair),
            criteria: new InclusionCriteria(RequireFullText: true));

        Assert.Equal(ScreeningDecision.Excluded, evaluation.Decision.Decision);
        Assert.Contains(InclusionReasonCodes.FullTextRequiredButAbsent, evaluation.Decision.ExclusionReasons);
    }

    [Fact]
    public void Summarize_counts_decisions_and_reasons_for_the_prisma_figure()
    {
        var (record, provenance) = VerifiedAbstractSource();
        var decisions = new List<InclusionDecision>
        {
            InclusionExclusionPolicy.Evaluate(record, provenance).Decision,
            InclusionExclusionPolicy.Evaluate(record with { SourceKind = AcademicSourceKind.Other }, provenance,
                criteria: new InclusionCriteria(AcceptedKinds: [AcademicSourceKind.JournalArticle])).Decision,
            InclusionExclusionPolicy.Evaluate(record, provenance,
                criteria: new InclusionCriteria(ScopeTerms: ["nothing matches this"])).Decision,
        };

        var summary = InclusionExclusionPolicy.Summarize(decisions);

        Assert.Equal(1, summary[nameof(ScreeningDecision.Included)]);
        Assert.Equal(1, summary[nameof(ScreeningDecision.Excluded)]);
        Assert.Equal(1, summary[nameof(ScreeningDecision.Undetermined)]);
        Assert.Equal(1, summary["exclusion:" + InclusionReasonCodes.NotScholarlyForm]);
    }

    [Fact]
    public void The_pipeline_keeps_every_step_of_one_source_together()
    {
        var appraiser = new LiteratureAppraiser();
        var sourceId = Guid.Parse("cccccccc-0000-4000-8000-000000000009");

        var result = appraiser.Appraise(new AppraisalRequest(
            WorkspaceId: AcademicEvidenceFixtures.Workspace,
            SourceId: sourceId,
            Origins: OriginsWithAbstract(),
            Notices: null,
            Criteria: new InclusionCriteria(Version: "protocol-1", AcceptedKinds: [AcademicSourceKind.JournalArticle]),
            BiasFramework: "rob2",
            BiasFindings: AllLowConcern("rob2"),
            PeerReviewStatus: PeerReviewStatus.VerifiedFromRegistry,
            TopicRelevance: 0.9));

        Assert.Equal(ProvenanceVerdict.Verified, result.Provenance.Verdict);
        Assert.Equal(RetractionScreenOutcome.Clear, result.Retraction.Outcome);
        Assert.Equal(QualityTier.High, result.Quality.Tier);
        Assert.Equal(OverallBiasRisk.LowRisk, result.Bias!.OverallRisk);
        Assert.Equal(ScreeningDecision.Included, result.Decision.Decision);
        Assert.Equal(sourceId, result.Record.SourceId);
        Assert.Contains(InclusionReasonCodes.TopicMatched, result.Decision.InclusionReasons);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void The_pipeline_refuses_to_upgrade_a_source_that_fell_at_provenance()
    {
        var appraiser = new LiteratureAppraiser();
        var result = appraiser.Appraise(new AppraisalRequest(
            WorkspaceId: AcademicEvidenceFixtures.Workspace,
            SourceId: Guid.Parse("cccccccc-0000-4000-8000-00000000000a"),
            Origins: [AcademicEvidenceFixtures.Origin(abstractText: AcademicEvidenceFixtures.AbstractText)],
            Notices: null,
            Criteria: null,
            PeerReviewStatus: PeerReviewStatus.AssertedByProvider));

        Assert.Equal(ProvenanceVerdict.Unverified, result.Provenance.Verdict);
        Assert.False(result.Provenance.MayPresentAsVerified);
        Assert.True(result.Quality.Tier is QualityTier.Moderate or QualityTier.Low or QualityTier.InsufficientInformation);
        Assert.NotEqual(QualityTier.High, result.Quality.Tier);
    }

    [Fact]
    public void Audit_writer_records_the_verdicts_a_reviewer_will_be_asked_about()
    {
        var appraiser = new LiteratureAppraiser();
        var chain = new ProvenanceAuditChain();
        var result = appraiser.Appraise(new AppraisalRequest(
            WorkspaceId: AcademicEvidenceFixtures.Workspace,
            SourceId: Guid.Parse("cccccccc-0000-4000-8000-00000000000b"),
            Origins: OriginsWithAbstract(),
            Criteria: new InclusionCriteria(Version: "protocol-1"),
            BiasFramework: "rob2",
            BiasFindings: AllLowConcern("rob2"),
            PeerReviewStatus: PeerReviewStatus.VerifiedFromRegistry,
            TopicRelevance: 0.9));

        var written = AppraisalAuditWriter.Write(chain, result, AcademicEvidenceFixtures.Clock);

        // Normalization, verdict and screening decision; no notice event to write.
        Assert.Equal(3, written);
        Assert.Equal([
            ProvenanceAuditKinds.RecordNormalized,
            ProvenanceAuditKinds.VerdictComputed,
            ProvenanceAuditKinds.ScreeningDecided,
        ], chain.Events.Select(e => e.EventKind).Distinct().ToArray());
        Assert.Contains("Verified", chain.Events.Single(e => e.EventKind == ProvenanceAuditKinds.VerdictComputed).PayloadCanonical);
        Assert.True(chain.IsValid(out _));

        // A source under a retraction notice gains the notice event; the chain
        // still validates as the trail grows.
        var retracted = appraiser.Appraise(new AppraisalRequest(
            WorkspaceId: AcademicEvidenceFixtures.Workspace,
            SourceId: result.Record.SourceId,
            Origins: OriginsWithAbstract(),
            Notices: [new RetractionNotice
            {
                Kind = RetractionKind.FullRetraction,
                Origin = RetractionNoticeOrigin.Registry,
                NoticedAtUtc = AcademicEvidenceFixtures.Clock,
            }],
            PeerReviewStatus: PeerReviewStatus.VerifiedFromRegistry));

        var extra = AppraisalAuditWriter.Write(
            chain, retracted, AcademicEvidenceFixtures.Clock.AddMinutes(5), AuditActorKind.System);

        // Four this time: the retraction notice earns its own event.
        Assert.Equal(4, extra);
        Assert.Equal(7, chain.Events.Count);
        Assert.Contains(chain.Events, e => e.EventKind == ProvenanceAuditKinds.RetractionNoticed);
        Assert.Equal(ProvenanceVerdict.Withdrawn, retracted.Provenance.Verdict);
        Assert.True(chain.IsValid(out _));
    }
}
