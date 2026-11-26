using Taslim.Api.AcademicEvidence.Appraisal;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Matrix;

/// <summary>
/// Reads the finished matrix and states what the evidence base does not contain.
///
/// A gap is a statement about the search, never a finding. Each one names the
/// question it applies to, the sources involved and a next step phrased as an
/// action ("search the regional repositories for full text") rather than a promise
/// ("there is no literature on this"), because the absence of a retrieved study is
/// not evidence that none exists.
/// </summary>
public static class ResearchGapAnalyzer
{
    /// <summary>
    /// Analyse one matrix.
    /// </summary>
    public static IReadOnlyList<ResearchGap> Analyze(
        IReadOnlyList<MatrixQuestion> questions,
        IReadOnlyList<SourceAssessment> assessments,
        IReadOnlyList<EvidenceMatrixCell> cells,
        IReadOnlyList<MatrixRelationship> relationships)
    {
        var gaps = new List<ResearchGap>();
        var assessmentsBySource = assessments.ToDictionary(a => a.Record.SourceId);

        foreach (var question in questions.OrderBy(q => q.Key, StringComparer.Ordinal))
        {
            var questionCells = cells
                .Where(c => c.QuestionKey == question.Key)
                .OrderBy(c => c.SourceId)
                .ToList();

            var engaged = questionCells
                .Where(c => c.Stance != SourceStance.NoEvidence)
                .ToList();

            // 1. Nothing at all spoke to the question.
            if (engaged.Count == 0)
            {
                gaps.Add(new ResearchGap
                {
                    Kind = ResearchGapKind.NoSourcesForQuestion,
                    QuestionKey = question.Key,
                    Severity = FindingSeverity.Blocking,
                    Description = $"No included source reports a finding on \"{question.Text}\".",
                    SuggestedStep = "Broaden the search terms, add a regional or grey-literature source, or "
                        + "record the question as out of scope in the protocol.",
                });
                continue;
            }

            // 2. Only one source spoke, so the review has an example, not evidence.
            if (engaged.Count == 1)
            {
                gaps.Add(new ResearchGap
                {
                    Kind = ResearchGapKind.UncorroboratedSingleSource,
                    QuestionKey = question.Key,
                    Severity = FindingSeverity.Warning,
                    SourceIds = [engaged[0].SourceId],
                    Description = $"\"{question.Text}\" rests on a single source, so the finding cannot be "
                        + "presented as a pattern in the literature.",
                    SuggestedStep = "Seek at least one independent study of the same question before treating "
                        + "it as an established finding.",
                });
            }

            // 3. Every claim is metadata-only, meaning nobody read the work.
            var contentBearing = engaged
                .Where(c => c.Stance is SourceStance.Supports or SourceStance.Contradicts
                    or SourceStance.Mixed or SourceStance.Contextual)
                .ToList();
            if (contentBearing.Count > 0
                && contentBearing.All(c => !AccessLevelPolicy.SupportsContentClaims(c.AccessLevel)))
            {
                gaps.Add(new ResearchGap
                {
                    Kind = ResearchGapKind.NoFullTextCoverage,
                    QuestionKey = question.Key,
                    Severity = FindingSeverity.Blocking,
                    SourceIds = [.. contentBearing.Select(c => c.SourceId)],
                    Description = $"Every source cited for \"{question.Text}\" is held as catalog metadata only, "
                        + "so no content was actually read for this question.",
                    SuggestedStep = "Retrieve the full text or the publisher abstract for these works, or remove "
                        + "the content claims built on them.",
                });
            }

            // 4. Contradictions that the review has not resolved.
            var contradictions = relationships
                .Where(r => r.QuestionKey == question.Key && r.Relation == ClaimRelation.Contradiction)
                .ToList();
            if (contradictions.Count > 0)
            {
                gaps.Add(new ResearchGap
                {
                    Kind = ResearchGapKind.UnresolvedContradiction,
                    QuestionKey = question.Key,
                    Severity = FindingSeverity.Warning,
                    SourceIds = [.. contradictions
                        .SelectMany(r => new[] { r.LeftSourceId, r.RightSourceId })
                        .Distinct()
                        .Order()],
                    Description = $"{contradictions.Count} pair(s) of sources report opposing findings on "
                        + $"\"{question.Text}\".",
                    SuggestedStep = "Compare populations, timeframes and measurement units before choosing a "
                        + "position; report both findings if the difference cannot be reconciled.",
                });
            }

            // 5. Claims without pinpoints, which cannot be checked by a reviewer.
            var withoutLocators = engaged
                .Where(c => !c.HasVerifiableLocator
                    && c.Stance is SourceStance.Supports or SourceStance.Contradicts or SourceStance.Mixed)
                .ToList();
            if (withoutLocators.Count > 0)
            {
                gaps.Add(new ResearchGap
                {
                    Kind = ResearchGapKind.UnlocatableClaims,
                    QuestionKey = question.Key,
                    Severity = FindingSeverity.Warning,
                    SourceIds = [.. withoutLocators.Select(c => c.SourceId).Order()],
                    Description = $"{withoutLocators.Count} source(s) support a finding on \"{question.Text}\" "
                        + "without a pinpoint a reviewer could check.",
                    SuggestedStep = "Record a locator read from the retrieved copy, or present the finding "
                        + "without a page reference.",
                });
            }

            // 6. Population and timeframe holes inside one question.
            gaps.AddRange(BuildCoverageGaps(question, engaged, assessmentsBySource));
        }

        // Corpus-wide gaps do not belong to one question.
        var disputed = assessments
            .Where(a => a.Provenance.Verdict is ProvenanceVerdict.Disputed or ProvenanceVerdict.Invalid)
            .Select(a => a.Record.SourceId)
            .Order()
            .ToList();
        if (disputed.Count > 0)
        {
            gaps.Add(new ResearchGap
            {
                Kind = ResearchGapKind.DisputedProvenance,
                Severity = FindingSeverity.Blocking,
                SourceIds = disputed,
                Description = $"{disputed.Count} source(s) in the library have a disputed or invalid identity.",
                SuggestedStep = "Resolve which record is correct against a registry before citing any of them.",
            });
        }

        var withdrawn = assessments
            .Where(a => a.Provenance.Verdict == ProvenanceVerdict.Withdrawn)
            .Select(a => a.Record.SourceId)
            .Order()
            .ToList();
        if (withdrawn.Count > 0)
        {
            gaps.Add(new ResearchGap
            {
                Kind = ResearchGapKind.DependsOnWithdrawnSource,
                Severity = FindingSeverity.Blocking,
                SourceIds = withdrawn,
                Description = $"{withdrawn.Count} retracted or partially retracted work(s) remain in the "
                    + "library and must not support any finding.",
                SuggestedStep = "Replace them with non-retracted literature or cite the retraction notice itself.",
            });
        }

        var belowThreshold = assessments
            .Where(a => a.Quality?.Tier is QualityTier.Low or QualityTier.InsufficientInformation
                && a.Decision?.Decision == ScreeningDecision.Included)
            .Select(a => a.Record.SourceId)
            .Order()
            .ToList();
        if (belowThreshold.Count > 0)
        {
            gaps.Add(new ResearchGap
            {
                Kind = ResearchGapKind.QualityBelowThreshold,
                Severity = FindingSeverity.Warning,
                SourceIds = belowThreshold,
                Description = $"{belowThreshold.Count} included source(s) carry too few recorded quality signals "
                    + "for the review to state anything about their reliability.",
                SuggestedStep = "Record peer-review status and an appraisal, or exclude them from the synthesis.",
            });
        }

        return gaps;
    }

    private static IEnumerable<ResearchGap> BuildCoverageGaps(
        MatrixQuestion question,
        IReadOnlyList<EvidenceMatrixCell> engaged,
        IReadOnlyDictionary<Guid, SourceAssessment> assessmentsBySource)
    {
        var populations = engaged
            .SelectMany(c => ClaimsFor(c, assessmentsBySource))
            .Select(ClaimStanceComparer.BuildKey)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (populations.Count > 1)
        {
            yield return new ResearchGap
            {
                Kind = ResearchGapKind.PopulationCoverageGap,
                QuestionKey = question.Key,
                Severity = FindingSeverity.Note,
                Description = $"Findings on \"{question.Text}\" come from {populations.Count} different "
                    + "populations, so they are not directly comparable.",
                SuggestedStep = "Look for a study of the population the review is actually about, or present "
                    + "the findings separately by population.",
            };
        }

        var timeframes = engaged
            .Select(c => c.SourceId)
            .Where(assessmentsBySource.ContainsKey)
            .Select(id => assessmentsBySource[id].Record.PublicationYear)
            .Where(y => y is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (timeframes.Count > 1)
        {
            var span = int.Parse(timeframes.Max()!, System.Globalization.CultureInfo.InvariantCulture)
                - int.Parse(timeframes.Min()!, System.Globalization.CultureInfo.InvariantCulture);
            if (span >= 10)
            {
                yield return new ResearchGap
                {
                    Kind = ResearchGapKind.TimeframeCoverageGap,
                    QuestionKey = question.Key,
                    Severity = FindingSeverity.Note,
                    Description = $"Sources for \"{question.Text}\" span {span.ToString(System.Globalization.CultureInfo.InvariantCulture)} years, "
                        + "which may mean the reported situation changed between them.",
                    SuggestedStep = "Check for more recent work, or state the period each finding describes.",
                };
            }
        }
    }

    private static IEnumerable<string> ClaimsFor(
        EvidenceMatrixCell cell,
        IReadOnlyDictionary<Guid, SourceAssessment> assessmentsBySource)
    {
        if (!assessmentsBySource.TryGetValue(cell.SourceId, out var assessment)) yield break;

        foreach (var item in assessment.EvidenceItems)
        {
            if (item.StructuredClaim is { } claim) yield return claim.PopulationKey;
        }
    }
}
