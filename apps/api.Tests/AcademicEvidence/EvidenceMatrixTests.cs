using AcademicDomain = Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Appraisal;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Evidence;
using Taslim.Api.AcademicEvidence.Matrix;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;
using Xunit;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// The evidence matrix: comparing findings across sources, naming contradictions
/// and stating what the review has not covered. The failure mode designed out here
/// is the confident synthesis, where two studies that never spoke about the same
/// thing are reported as a disagreement.
/// </summary>
public sealed class EvidenceMatrixTests
{
    private const string Question = "tender-centralisation-effect";

    private static EvidenceClaim Claim(
        string subject = "formulary retention",
        string predicate = "effect of centralised tendering",
        EffectDirection direction = EffectDirection.Increase,
        double? magnitude = null,
        string? unit = null,
        double? low = null,
        double? high = null,
        string population = "kurdistan region pharmacy market") => new()
    {
        SubjectKey = subject,
        PredicateKey = predicate,
        Direction = direction,
        Magnitude = magnitude,
        Unit = unit,
        IntervalLow = low,
        IntervalHigh = high,
        PopulationKey = population,
    };

    private static (NormalizedBibliographicRecord Record, SourceProvenance Provenance) Source(
        int index, string? abstractText = AcademicEvidenceFixtures.AbstractText)
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse($"eeeeeeee-0000-4000-8000-0000000000{index:D2}"), abstractText);
        return (pair.Record, AcademicEvidenceFixtures.ProvenanceOf(pair));
    }

    private static EvidenceItem Item(
        (NormalizedBibliographicRecord Record, SourceProvenance Provenance) source,
        EvidenceClaim? claim,
        string topic = Question,
        bool withLocator = true,
        string? content = "Retention of essential medicines on formulary improved by 18 percent")
    {
        var result = EvidenceRecordBuilder.Record(
            source.Record, source.Provenance, topic,
            supportKind: EvidenceSupportKind.QuantitativeFinding,
            content: content,
            structuredClaim: claim,
            locator: withLocator
                ? new SourceLocator
                {
                    Kind = LocatorKind.PageRange,
                    Origin = LocatorOrigin.RegistryMetadata,
                    Label = "412-419",
                }
                : null,
            recordedAtUtc: AcademicEvidenceFixtures.Clock);
        return result.Item;
    }

    private static SourceAssessment Assessment(
        (NormalizedBibliographicRecord Record, SourceProvenance Provenance) source,
        IReadOnlyList<EvidenceItem> items,
        ScreeningDecision? decision = null,
        QualityTier? forceTier = null)
    {
        var quality = SourceQualityScreener.Screen(
            source.Record, source.Provenance, PeerReviewStatus.VerifiedFromRegistry,
            screenedAtUtc: AcademicEvidenceFixtures.Clock);
        var inclusion = InclusionExclusionPolicy.Evaluate(
            source.Record, source.Provenance, quality,
            criteria: new InclusionCriteria(), decidedAtUtc: AcademicEvidenceFixtures.Clock);

        // A source keeps its row when it was screened in before its integrity
        // changed: silently dropping a retracted study would hide the very finding
        // the review cited, while showing it unflagged would cite it as sound.
        var recorded = decision is null
            ? inclusion.Decision
            : inclusion.Decision with { Decision = decision.Value };

        return new SourceAssessment(
            source.Record,
            source.Provenance,
            quality with { Tier = forceTier ?? quality.Tier },
            recorded,
            Evidence: items);
    }

    private static (NormalizedBibliographicRecord Record, SourceProvenance Provenance) Paired(
        NormalizedBibliographicRecord record, SourceProvenance provenance) => (record, provenance);

    private static AcademicDomain.EvidenceMatrix Build(params SourceAssessment[] assessments)
        => EvidenceMatrixBuilder.Build(
            AcademicEvidenceFixtures.Workspace,
            AcademicEvidenceFixtures.Project,
            [new MatrixQuestion { Key = Question, Text = "Does centralised tendering improve formulary retention?" }],
            assessments);

    // ------------------------------------------------------------- comparison

    [Fact]
    public void One_side_being_silent_is_a_coverage_gap_not_a_disagreement()
    {
        var comparison = ClaimStanceComparer.Compare(Claim(), null);

        Assert.Equal(ClaimRelation.NoEvidence, comparison.Relation);
        Assert.Contains(StanceReasonCodes.NoClaimOnOneSide, comparison.ReasonCodes);
    }

    [Fact]
    public void Findings_about_different_subjects_are_never_placed_against_each_other()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(subject: "formulary retention"),
            Claim(subject: "vaccine cold chain"));

        Assert.Equal(ClaimRelation.NotComparable, comparison.Relation);
        Assert.Contains(StanceReasonCodes.DifferentSubject, comparison.ReasonCodes);
    }

    [Fact]
    public void A_claim_key_written_differently_still_compares_as_the_same_claim()
    {
        // Two members coding the same finding by hand: capitalisation and trailing
        // punctuation must not split it into two rows of the matrix.
        var comparison = ClaimStanceComparer.Compare(
            Claim(subject: "Formulary Retention, KRI", predicate: "Effect of Tendering"),
            Claim(subject: "formulary retention kri", predicate: "effect of tendering"));

        Assert.Equal(ClaimRelation.Agreement, comparison.Relation);
        Assert.Equal(ClaimStanceComparer.BuildKey(" Formulary's Retention, 2024 "),
            ClaimStanceComparer.BuildKey("formulary's retention 2024"));
    }

    [Fact]
    public void Different_populations_can_both_be_true()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(population: "Erbil governorate"),
            Claim(population: "Sulaymaniyah governorate"));

        Assert.Equal(ClaimRelation.NotComparable, comparison.Relation);
        Assert.Contains(StanceReasonCodes.DifferentPopulation, comparison.ReasonCodes);
    }

    [Fact]
    public void Opposite_findings_on_the_same_question_are_a_contradiction()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(direction: EffectDirection.Increase, magnitude: 2.4, unit: "pp", low: 1.1, high: 3.6),
            Claim(direction: EffectDirection.Decrease, magnitude: 1.8, unit: "pp", low: 0.6, high: 2.9));

        Assert.Equal(ClaimRelation.Contradiction, comparison.Relation);
        Assert.Contains(StanceReasonCodes.DirectionOpposed, comparison.ReasonCodes);
    }

    [Fact]
    public void A_clash_one_study_measured_imprecisely_is_reported_as_undetermined()
    {
        // The odds ratio interval crosses 1, so "increased" was never established
        // and the studies may simply differ in power.
        var comparison = ClaimStanceComparer.Compare(
            Claim(direction: EffectDirection.Increase, unit: "OR", low: 0.8, high: 1.4),
            Claim(direction: EffectDirection.Decrease, unit: "OR", low: 0.4, high: 0.9));

        Assert.Equal(ClaimRelation.Indeterminate, comparison.Relation);
        Assert.Contains(StanceReasonCodes.SignificanceDiffers, comparison.ReasonCodes);
    }

    [Fact]
    public void Agreement_without_magnitudes_is_labelled_directional()
    {
        var comparison = ClaimStanceComparer.Compare(Claim(), Claim());

        Assert.Equal(ClaimRelation.Agreement, comparison.Relation);
        Assert.Contains(StanceReasonCodes.DirectionAligned, comparison.ReasonCodes);
        Assert.Contains(StanceReasonCodes.DirectionalOnly, comparison.ReasonCodes);
        Assert.True(comparison.ReasonCodes.Count == 2);
    }

    [Fact]
    public void The_same_direction_can_still_conflict_on_size()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(unit: "OR", low: 1.2, high: 1.6, magnitude: 1.4),
            Claim(unit: "OR", low: 2.4, high: 3.1, magnitude: 2.7));

        Assert.Equal(ClaimRelation.Contradiction, comparison.Relation);
        Assert.Contains(StanceReasonCodes.IntervalGap, comparison.ReasonCodes);
    }

    [Fact]
    public void Overlapping_intervals_of_the_same_direction_agree()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(unit: "OR", low: 1.2, high: 2.0, magnitude: 1.6),
            Claim(unit: "OR", low: 1.8, high: 3.0, magnitude: 2.2));

        Assert.Equal(ClaimRelation.Agreement, comparison.Relation);
        Assert.Contains(StanceReasonCodes.IntervalOverlap, comparison.ReasonCodes);
    }

    [Fact]
    public void Different_units_are_not_quietly_treated_as_comparable()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(magnitude: 18, unit: "percent"),
            Claim(magnitude: 0.18, unit: "fraction"));

        Assert.Equal(ClaimRelation.Indeterminate, comparison.Relation);
        Assert.Contains(StanceReasonCodes.UnitMismatch, comparison.ReasonCodes);
    }

    [Fact]
    public void Only_one_study_reporting_a_size_corroborates_the_finding_not_its_magnitude()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(magnitude: 18, unit: "pp", low: 10, high: 26),
            Claim());

        Assert.Equal(ClaimRelation.Indeterminate, comparison.Relation);
        Assert.Contains(StanceReasonCodes.DirectionalOnly, comparison.ReasonCodes);
    }

    [Fact]
    public void A_direction_nobody_recorded_stops_the_comparison()
    {
        var comparison = ClaimStanceComparer.Compare(
            Claim(direction: EffectDirection.Unknown),
            Claim(direction: EffectDirection.Increase));

        Assert.Equal(ClaimRelation.Indeterminate, comparison.Relation);
        Assert.Contains(StanceReasonCodes.DirectionUnknown, comparison.ReasonCodes);
    }

    [Theory]
    [InlineData(EffectDirection.Increase, EffectDirection.Decrease, true)]
    [InlineData(EffectDirection.Presence, EffectDirection.Absence, true)]
    [InlineData(EffectDirection.NoEffect, EffectDirection.Increase, true)]
    [InlineData(EffectDirection.Increase, EffectDirection.NoEffect, true)]
    [InlineData(EffectDirection.Increase, EffectDirection.Mixed, false)]
    [InlineData(EffectDirection.Mixed, EffectDirection.Mixed, false)]
    [InlineData(EffectDirection.Increase, EffectDirection.Increase, false)]
    public void Opposition_is_defined_only_for_directions_that_cannot_both_hold(
        EffectDirection left, EffectDirection right, bool opposed)
    {
        Assert.Equal(opposed, ClaimStanceComparer.AreOpposed(left, right));
    }

    [Fact]
    public void An_intervals_null_point_depends_on_what_the_unit_measures()
    {
        // A ratio is null at 1, a difference at 0, and anything unnamed is not
        // assessed at all rather than guessed.
        Assert.False(Claim(unit: "OR", low: 0.9, high: 1.4).ExcludesNull);
        Assert.True(Claim(unit: "OR", low: 1.1, high: 1.4).ExcludesNull);
        Assert.False(Claim(unit: "percentage points", low: -0.2, high: 0.5).ExcludesNull);
        Assert.True(Claim(unit: "pp", low: 0.2, high: 0.5).ExcludesNull);
        Assert.Null(Claim(unit: "index", low: 1, high: 2).ExcludesNull);
        Assert.Null(Claim(unit: "pp").ExcludesNull);
    }

    [Fact]
    public void A_sources_own_stance_distinguishes_absence_of_evidence_from_uncertainty()
    {
        Assert.Equal(SourceStance.NoEvidence, ClaimStanceComparer.ToStance([]));
        Assert.Equal(SourceStance.Indeterminate,
            ClaimStanceComparer.ToStance([Claim(direction: EffectDirection.Unknown)]));
        Assert.Equal(SourceStance.Supports, ClaimStanceComparer.ToStance([Claim()]));
        Assert.Equal(SourceStance.Contradicts,
            ClaimStanceComparer.ToStance([Claim(direction: EffectDirection.Decrease)]));
        Assert.Equal(SourceStance.Contradicts,
            ClaimStanceComparer.ToStance([Claim(direction: EffectDirection.NoEffect)]));
        Assert.Equal(SourceStance.Mixed, ClaimStanceComparer.ToStance(
            [Claim(), Claim(direction: EffectDirection.Decrease)]));
        Assert.Equal(SourceStance.Mixed,
            ClaimStanceComparer.ToStance([Claim(direction: EffectDirection.Mixed)]));
    }

    // ------------------------------------------------------------- the matrix

    [Fact]
    public void Sources_screened_out_of_the_review_never_appear_in_the_matrix()
    {
        var kept = Source(1);
        var dropped = Source(2);
        var matrix = Build(
            Assessment(kept, [Item(kept, Claim())]),
            Assessment(dropped, [Item(dropped, Claim(direction: EffectDirection.Decrease))],
                decision: ScreeningDecision.Excluded));

        Assert.Equal([kept.Record.SourceId], matrix.SourceIds);
        Assert.Single(matrix.Cells);
        Assert.DoesNotContain(matrix.Relationships, r => r.RightSourceId == dropped.Record.SourceId);
    }

    [Fact]
    public void Two_studies_that_agree_produce_no_contradiction_row()
    {
        var left = Source(3);
        var right = Source(4);
        var matrix = Build(
            Assessment(left, [Item(left, Claim(unit: "OR", low: 1.2, high: 2.0, magnitude: 1.6))]),
            Assessment(right, [Item(right, Claim(unit: "OR", low: 1.8, high: 3.0, magnitude: 2.2))]));

        var relationship = Assert.Single(matrix.Relationships);
        Assert.Equal(Question, relationship.QuestionKey);
        Assert.Equal(ClaimRelation.Agreement, relationship.Relation);
        Assert.Equal(left.Record.SourceId, relationship.LeftSourceId);
        Assert.Equal(right.Record.SourceId, relationship.RightSourceId);
        Assert.DoesNotContain(matrix.Gaps, g => g.Kind == ResearchGapKind.UnresolvedContradiction);
        Assert.All(matrix.Cells, c => Assert.Equal(SourceStance.Supports, c.Stance));
    }

    [Fact]
    public void Opposing_studies_are_recorded_as_a_contradiction_the_review_has_to_answer()
    {
        var left = Source(5);
        var right = Source(6);
        var matrix = Build(
            Assessment(left, [Item(left, Claim(direction: EffectDirection.Increase, unit: "pp",
                low: 1.1, high: 3.6, magnitude: 2.4))]),
            Assessment(right, [Item(right, Claim(direction: EffectDirection.Decrease, unit: "pp",
                low: 0.6, high: 2.9, magnitude: 1.8))]));

        var relationship = Assert.Single(matrix.Relationships);
        Assert.Equal(ClaimRelation.Contradiction, relationship.Relation);
        Assert.Contains(StanceReasonCodes.DirectionOpposed, relationship.ReasonCodes);
        Assert.NotNull(relationship.Explanation);

        var gap = Assert.Single(matrix.Gaps, g => g.Kind == ResearchGapKind.UnresolvedContradiction);
        Assert.Equal(Question, gap.QuestionKey);
        Assert.Equal([left.Record.SourceId, right.Record.SourceId], gap.SourceIds);
        Assert.NotNull(gap.SuggestedStep);
    }

    [Fact]
    public void A_question_no_source_answered_becomes_a_named_gap()
    {
        var source = Source(7);
        var matrix = EvidenceMatrixBuilder.Build(
            AcademicEvidenceFixtures.Workspace,
            AcademicEvidenceFixtures.Project,
            [
                new MatrixQuestion { Key = Question, Text = "Tendering effect" },
                new MatrixQuestion { Key = "cold-chain", Text = "Cold chain reliability" },
            ],
            [Assessment(source, [Item(source, Claim())])]);

        var coldChain = matrix.Find("cold-chain", source.Record.SourceId);
        Assert.NotNull(coldChain);
        Assert.Equal(SourceStance.NoEvidence, coldChain.Stance);

        var gap = Assert.Single(matrix.Gaps, g => g.Kind == ResearchGapKind.NoSourcesForQuestion);
        Assert.Equal("cold-chain", gap.QuestionKey);
        Assert.Equal(FindingSeverity.Blocking, gap.Severity);
        Assert.NotNull(gap.SuggestedStep);
    }

    [Fact]
    public void A_finding_from_one_study_alone_is_marked_as_uncorroborated()
    {
        var source = Source(8);
        var matrix = Build(Assessment(source, [Item(source, Claim(unit: "OR", low: 1.2, high: 2.0, magnitude: 1.6))]));

        var gap = Assert.Single(matrix.Gaps, g => g.Kind == ResearchGapKind.UncorroboratedSingleSource);
        Assert.Equal(Question, gap.QuestionKey);
        Assert.Equal([source.Record.SourceId], gap.SourceIds);
    }

    [Fact]
    public void Evidence_that_failed_the_integrity_check_does_not_move_the_stance()
    {
        var source = Source(9);
        // A quotation the stored text does not contain, recorded against the same
        // question: it must not be able to support a cell.
        var suspect = Item(source, Claim(direction: EffectDirection.Decrease),
            content: "the tender process reduced prices by a third");
        Assert.Equal(EvidenceIntegrityStatus.UnsupportedByStoredText, suspect.Integrity);

        var matrix = Build(Assessment(source, [suspect]));
        var cell = matrix.Find(Question, source.Record.SourceId)!;

        Assert.Equal(SourceStance.Indeterminate, cell.Stance);
        Assert.Empty(cell.EvidenceIds);
        Assert.Contains(cell.Flags, f => f.Code == EvidenceMatrixBuilder.CellNotVerifiedFlag);
    }

    [Fact]
    public void Evidence_with_no_locator_is_counted_but_reported_as_unlocatable()
    {
        var source = Source(10);
        var matrix = Build(Assessment(source, [Item(source, Claim(), withLocator: false)]));
        var cell = matrix.Find(Question, source.Record.SourceId)!;

        Assert.False(cell.HasVerifiableLocator);
        Assert.Contains(matrix.Gaps, g => g.Kind == ResearchGapKind.UnlocatableClaims);
    }

    [Fact]
    public void Readable_evidence_of_unread_sources_is_a_gap_in_coverage()
    {
        // Both studies are catalogue metadata only: the matrix can compare their
        // recorded findings but nobody has read either one.
        var left = Source(11, abstractText: null);
        var right = Source(12, abstractText: null);
        var items = new List<EvidenceItem>();
        foreach (var source in new[] { left, right })
        {
            var result = EvidenceRecordBuilder.Record(
                source.Record, source.Provenance, Question,
                EvidenceSupportKind.BibliographicOnly, null,
                structuredClaim: Claim(unit: "OR", low: 1.2, high: 2.0, magnitude: 1.6));
            items.Add(result.Item);
        }

        var matrix = Build(
            Assessment(left, [items[0]]),
            Assessment(right, [items[1]]));

        Assert.All(matrix.Cells, c => Assert.Equal(SourceAccessLevel.MetadataOnly, c.AccessLevel));
        Assert.Contains(matrix.Gaps, g => g.Kind == ResearchGapKind.NoFullTextCoverage);
    }

    [Fact]
    public void A_source_under_a_dispute_is_named_as_a_threat_to_the_whole_matrix()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("eeeeeeee-0000-4000-8000-000000000013"),
            [
                AcademicEvidenceFixtures.Origin("crossref", doi: AcademicEvidenceFixtures.TestDoi,
                    abstractText: AcademicEvidenceFixtures.AbstractText),
                AcademicEvidenceFixtures.Origin("openalex", doi: AcademicEvidenceFixtures.OtherTestDoi,
                    sourceKindToken: "article", abstractText: AcademicEvidenceFixtures.AbstractText),
            ],
            AcademicEvidenceFixtures.Clock);
        var pair = Paired(normalization.Record, ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock));
        Assert.Equal(ProvenanceVerdict.Disputed, pair.Provenance.Verdict);

        var matrix = Build(Assessment(pair, [Item(pair, Claim())], decision: ScreeningDecision.Included));
        var cell = matrix.Find(Question, pair.Record.SourceId)!;

        Assert.Contains(cell.Flags, f =>
            f.Code == "CELL_PROVENANCE_BLOCKS_CLAIMS" && f.Severity == FindingSeverity.Blocking);
        Assert.Contains(matrix.Gaps, g => g.Kind == ResearchGapKind.DisputedProvenance);
    }

    [Fact]
    public void A_retracted_study_is_kept_out_of_the_findings_it_once_supported()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse("eeeeeeee-0000-4000-8000-000000000014"));
        var withdrawn = ProvenanceVerifier.Verify(pair.Record, pair.Normalization, [new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            Origin = RetractionNoticeOrigin.Registry,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        }], AcademicEvidenceFixtures.Clock);
        var source = Paired(pair.Record, withdrawn);

        // Screened in before the notice arrived, which is how a retraction actually
        // reaches a review: the finding is already cited somewhere.
        var matrix = Build(Assessment(source, [Item(source, Claim())],
            decision: ScreeningDecision.Included));

        Assert.Contains(matrix.Cells.Single().Flags, f => f.Code == "CELL_PROVENANCE_BLOCKS_CLAIMS");
        Assert.Contains(matrix.Gaps, g => g.Kind == ResearchGapKind.DependsOnWithdrawnSource);
    }

    [Fact]
    public void Unstructured_evidence_engages_a_question_without_inventing_a_direction()
    {
        var source = Source(15);
        var result = EvidenceRecordBuilder.Record(
            source.Record, source.Provenance, Question,
            EvidenceSupportKind.DirectQuote,
            content: "Retention of essential medicines on formulary improved by 18 percent");
        Assert.Null(result.Item.StructuredClaim);

        var matrix = Build(Assessment(source, [result.Item]));
        var cell = matrix.Find(Question, source.Record.SourceId)!;

        Assert.Equal(SourceStance.Contextual, cell.Stance);
        Assert.Single(cell.EvidenceIds);
    }

    [Fact]
    public void The_matrix_does_not_depend_on_the_order_things_were_entered_in()
    {
        var left = Source(16);
        var right = Source(17);
        var leftAssessment = Assessment(left, [Item(left, Claim(unit: "pp", low: 1.1, high: 3.6, magnitude: 2.4))]);
        var rightAssessment = Assessment(right, [Item(right,
            Claim(direction: EffectDirection.Decrease, unit: "pp", low: 0.6, high: 2.9, magnitude: 1.8))]);

        var first = Build(leftAssessment, rightAssessment);
        var second = Build(rightAssessment, leftAssessment);

        Assert.Equal(first.SourceIds, second.SourceIds);
        Assert.Equal(first.Cells.Select(c => (c.SourceId, c.QuestionKey, c.Stance)),
            second.Cells.Select(c => (c.SourceId, c.QuestionKey, c.Stance)));
        Assert.Equal(first.Relationships.Select(r => (r.LeftSourceId, r.RightSourceId, r.Relation)),
            second.Relationships.Select(r => (r.LeftSourceId, r.RightSourceId, r.Relation)));
        Assert.Equal(first.Gaps.Select(g => (g.Kind, g.QuestionKey, g.SourceIds)),
            second.Gaps.Select(g => (g.Kind, g.QuestionKey, g.SourceIds)));
        Assert.Equal(
            EvidenceMatrixMarkdownRenderer.Render(first, id => id.ToString("N")),
            EvidenceMatrixMarkdownRenderer.Render(second, id => id.ToString("N")));
    }

    [Fact]
    public void Questions_and_sources_are_ordered_for_a_stable_table()
    {
        var a = Source(18);
        var b = Source(19);
        var matrix = EvidenceMatrixBuilder.Build(
            AcademicEvidenceFixtures.Workspace,
            AcademicEvidenceFixtures.Project,
            [
                new MatrixQuestion { Key = "zeta", Text = "Z" },
                new MatrixQuestion { Key = "alpha", Text = "A" },
            ],
            [Assessment(b, []), Assessment(a, [])]);

        Assert.Equal(["alpha", "zeta"], matrix.Questions.Select(q => q.Key));
        Assert.Equal([a.Record.SourceId, b.Record.SourceId], matrix.SourceIds);
        Assert.Equal(4, matrix.Cells.Count);
        Assert.Equal(2, matrix.CellsFor("alpha").Count);
        Assert.Null(matrix.Find("missing", a.Record.SourceId));
    }

    // ------------------------------------------------------------- rendering

    [Fact]
    public void The_rendered_table_names_provenance_and_never_a_page_number()
    {
        var source = Source(20);
        var matrix = Build(Assessment(source, [Item(source, Claim())]));
        var markdown = EvidenceMatrixMarkdownRenderer.Render(
            matrix, id => "Khoshnaw & Ahmed 2024");

        Assert.Contains("## Evidence matrix", markdown);
        Assert.Contains("Khoshnaw & Ahmed 2024", markdown);
        Assert.Contains("+ supports", markdown);
        Assert.Contains("verified", markdown);
        // The rendered review must not contain a pinpoint nobody read.
        Assert.DoesNotContain("p. ", markdown);
        Assert.DoesNotContain("412", markdown);
    }

    [Fact]
    public void Blocked_cells_are_marked_in_the_table()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(
            Guid.Parse("eeeeeeee-0000-4000-8000-000000000021"));
        var withdrawn = ProvenanceVerifier.Verify(pair.Record, pair.Normalization, [new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            Origin = RetractionNoticeOrigin.Registry,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        }], AcademicEvidenceFixtures.Clock);
        var source = Paired(pair.Record, withdrawn);

        var markdown = EvidenceMatrixMarkdownRenderer.Render(
            Build(Assessment(source, [Item(source, Claim())], decision: ScreeningDecision.Included)),
            _ => "Retracted study");

        Assert.Contains("[blocked]", markdown);
        Assert.Contains("retracted", markdown);
    }

    [Fact]
    public void An_empty_matrix_says_so_instead_of_rendering_a_header_row()
    {
        var empty = new AcademicDomain.EvidenceMatrix
        {
            WorkspaceId = AcademicEvidenceFixtures.Workspace,
            ProjectId = AcademicEvidenceFixtures.Project,
        };

        var markdown = EvidenceMatrixMarkdownRenderer.Render(empty, _ => "unused");

        Assert.Contains("No sources have been screened", markdown);
        Assert.DoesNotContain("| --- |", markdown);
    }

    [Theory]
    [InlineData(SourceStance.Supports, "+ supports")]
    [InlineData(SourceStance.Contradicts, "- contradicts")]
    [InlineData(SourceStance.Mixed, "~ mixed")]
    [InlineData(SourceStance.Contextual, "o contextual")]
    [InlineData(SourceStance.Indeterminate, "? indeterminate")]
    [InlineData(SourceStance.NoEvidence, "no evidence found")]
    [InlineData(SourceStance.NotEvaluated, "-")]
    public void Every_stance_has_its_own_mark(SourceStance stance, string symbol)
    {
        Assert.Equal(symbol, EvidenceMatrixMarkdownRenderer.Symbol(stance));
    }

    [Theory]
    [InlineData(ProvenanceVerdict.Verified, "verified")]
    [InlineData(ProvenanceVerdict.Unverified, "unverified")]
    [InlineData(ProvenanceVerdict.Disputed, "disputed")]
    [InlineData(ProvenanceVerdict.Withdrawn, "retracted")]
    [InlineData(ProvenanceVerdict.Invalid, "invalid")]
    [InlineData(ProvenanceVerdict.Unassessed, "unchecked")]
    public void Provenance_is_always_spoken_of_plainly(ProvenanceVerdict verdict, string label)
    {
        Assert.Equal(label, EvidenceMatrixMarkdownRenderer.ProvenanceLabel(verdict));
    }

    [Fact]
    public void Pipe_characters_in_a_source_label_cannot_break_the_table()
    {
        var source = Source(22);
        var markdown = EvidenceMatrixMarkdownRenderer.Render(
            Build(Assessment(source, [Item(source, Claim())])), _ => "Khoshnaw | Ahmed");

        Assert.Contains(@"Khoshnaw \| Ahmed", markdown);
        // The escaped pipe must not read as a column boundary: four cells, five
        // unescaped separators, so the table keeps its shape in any renderer.
        var row = markdown.Split('\n').First(l => l.StartsWith("| Khoshnaw", StringComparison.Ordinal));
        var columns = System.Text.RegularExpressions.Regex.Matches(row, @"(?<!\\)\|").Count;
        Assert.Equal(5, columns);
    }
}
