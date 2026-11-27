using Taslim.Api.AcademicEvidence.Appraisal;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Evidence;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Matrix;

/// <summary>
/// Everything the matrix needs about one source: its identity, its provenance,
/// its appraisal, and the evidence actually recorded against it.
/// </summary>
public sealed record SourceAssessment(
    NormalizedBibliographicRecord Record,
    SourceProvenance Provenance,
    QualityScreenResult? Quality = null,
    InclusionDecision? Decision = null,
    RiskOfBiasAssessment? Bias = null,
    IReadOnlyList<EvidenceItem>? Evidence = null)
{
    /// <summary>Evidence rows recorded for the source, never null.</summary>
    public IReadOnlyList<EvidenceItem> EvidenceItems => Evidence ?? [];
}

/// <summary>
/// Assembles the evidence matrix: sources against research questions, with the
/// pairwise claim relationships between them.
///
/// The builder reads only what was recorded. An item that failed the locator or
/// content check cannot move a cell to a stance, and a source that the protocol
/// excluded is left out of the comparison but stays in the library, because the
/// reason a study was excluded is part of the review. Ordering is fixed by source
/// and question keys, so rebuilding the same inputs produces identical output and a
/// diff of two runs means the evidence changed, not the sort.
/// </summary>
public static class EvidenceMatrixBuilder
{
    /// <summary>Flag raised when a cell rests on unverified evidence only.</summary>
    public const string CellNotVerifiedFlag = "CELL_EVIDENCE_NOT_VERIFIED";

    /// <summary>
    /// Build the matrix.
    /// </summary>
    /// <param name="workspaceId">Tenant scope.</param>
    /// <param name="projectId">Review the matrix belongs to.</param>
    /// <param name="questions">Research questions in protocol order.</param>
    /// <param name="assessments">Assessed sources.</param>
    public static Domain.EvidenceMatrix Build(
        Guid workspaceId,
        Guid projectId,
        IReadOnlyList<MatrixQuestion> questions,
        IReadOnlyList<SourceAssessment> assessments)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(assessments);

        var orderedQuestions = questions
            .OrderBy(q => q.Key, StringComparer.Ordinal)
            .ThenBy(q => q.Text, StringComparer.Ordinal)
            .ToList();

        var orderedSources = assessments
            .Where(a => a.Decision?.Decision != ScreeningDecision.Excluded)
            .OrderBy(a => a.Record.SourceId)
            .ToList();

        var cells = new List<EvidenceMatrixCell>();
        var relationships = new List<MatrixRelationship>();

        foreach (var question in orderedQuestions)
        {
            var claimsBySource = new List<(Guid SourceId, List<EvidenceClaim> Claims, List<EvidenceItem> Items)>();

            foreach (var assessment in orderedSources)
            {
                var forQuestion = assessment.EvidenceItems
                    .Where(e => string.Equals(e.Topic, question.Key, StringComparison.Ordinal))
                    .ToList();

                if (forQuestion.Count == 0)
                {
                    cells.Add(new EvidenceMatrixCell
                    {
                        QuestionKey = question.Key,
                        SourceId = assessment.Record.SourceId,
                        Stance = SourceStance.NoEvidence,
                        AccessLevel = assessment.Record.AccessLevel,
                        Provenance = assessment.Provenance.Verdict,
                        Quality = assessment.Quality?.Tier ?? QualityTier.InsufficientInformation,
                        Screening = assessment.Decision?.Decision ?? ScreeningDecision.Unscheduled,
                        Flags = [],
                    });
                    continue;
                }

                var verified = forQuestion.Where(e => e.IsExportable).ToList();
                var unverifiedCount = forQuestion.Count - verified.Count;

                var claims = verified
                    .Select(e => e.StructuredClaim)
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .ToList();

                var flags = new List<EvidenceFlag>();
                if (unverifiedCount > 0)
                {
                    flags.Add(new EvidenceFlag
                    {
                        Code = CellNotVerifiedFlag,
                        Severity = FindingSeverity.Warning,
                        Message = $"{unverifiedCount} recorded item(s) for this question did not pass the "
                            + "integrity check and were not counted in the stance.",
                    });
                }

                if (!RetractionScreen.MaySupportClaims(assessment.Provenance))
                {
                    flags.Add(new EvidenceFlag
                    {
                        Code = "CELL_PROVENANCE_BLOCKS_CLAIMS",
                        Severity = FindingSeverity.Blocking,
                        Message = "This source's provenance does not permit it to support a finding here.",
                    });
                }

                var stance = verified.Count == 0
                    ? SourceStance.Indeterminate
                    : ClaimStanceComparer.ToStance(claims);

                if (stance == SourceStance.NoEvidence && verified.Count > 0)
                {
                    // Evidence exists but was unstructured, so the cell records the
                    // source as engaged with the question without inventing a stance.
                    stance = SourceStance.Contextual;
                }

                cells.Add(new EvidenceMatrixCell
                {
                    QuestionKey = question.Key,
                    SourceId = assessment.Record.SourceId,
                    Stance = stance,
                    EvidenceIds = [.. verified.Select(e => e.EvidenceId).OrderBy(id => id)],
                    HasVerifiableLocator = verified.Any(e => e.Locator is { IsEmpty: false }),
                    AccessLevel = LowestAccess(verified.Count > 0 ? verified : forQuestion),
                    Provenance = assessment.Provenance.Verdict,
                    Quality = assessment.Quality?.Tier ?? QualityTier.InsufficientInformation,
                    Screening = assessment.Decision?.Decision ?? ScreeningDecision.Unscheduled,
                    Flags = flags,
                });

                claimsBySource.Add((assessment.Record.SourceId, claims, verified));
            }

            relationships.AddRange(BuildRelationships(question.Key, claimsBySource));
        }

        var gaps = ResearchGapAnalyzer.Analyze(orderedQuestions, orderedSources, cells, relationships);

        return new Domain.EvidenceMatrix
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Questions = orderedQuestions,
            SourceIds = [.. orderedSources.Select(a => a.Record.SourceId)],
            Cells = cells,
            Relationships = relationships,
            Gaps = gaps,
        };
    }

    /// <summary>
    /// Compare every claim pair inside one question. Pairs are emitted in a fixed
    /// order so the same inputs always produce the same relationship list.
    /// </summary>
    private static List<MatrixRelationship> BuildRelationships(
        string questionKey,
        List<(Guid SourceId, List<EvidenceClaim> Claims, List<EvidenceItem> Items)> claimsBySource)
    {
        var relationships = new List<MatrixRelationship>();

        for (var i = 0; i < claimsBySource.Count; i++)
        {
            for (var j = i + 1; j < claimsBySource.Count; j++)
            {
                var left = claimsBySource[i];
                var right = claimsBySource[j];

                StanceComparison? best = null;
                foreach (var leftClaim in left.Claims)
                {
                    foreach (var rightClaim in right.Claims)
                    {
                        var comparison = ClaimStanceComparer.Compare(leftClaim, rightClaim);

                        // Contradiction is the finding a review must not miss, so it
                        // wins over any softer relation the other pairs reported.
                        if (best is null || RanksAbove(comparison.Relation, best.Relation)) best = comparison;
                    }
                }

                // Either side engaged the question without a structured claim: that
                // is not a comparison, so no row is emitted.
                if (best is not { } decided) continue;

                if (decided.Relation == ClaimRelation.NoEvidence) continue;

                relationships.Add(new MatrixRelationship
                {
                    QuestionKey = questionKey,
                    LeftSourceId = left.SourceId,
                    RightSourceId = right.SourceId,
                    Relation = decided.Relation,
                    ReasonCodes = decided.ReasonCodes,
                    Explanation = decided.Explanation,
                });
            }
        }

        return relationships;
    }

    private static bool RanksAbove(ClaimRelation candidate, ClaimRelation current) =>
        Rank(candidate) > Rank(current);

    private static int Rank(ClaimRelation relation) => relation switch
    {
        ClaimRelation.Contradiction => 4,
        ClaimRelation.Agreement => 3,
        ClaimRelation.Indeterminate => 2,
        ClaimRelation.NoEvidence => 1,
        _ => 0,
    };

    private static SourceAccessLevel LowestAccess(IReadOnlyList<EvidenceItem> items)
    {
        if (items.Count == 0) return SourceAccessLevel.Unknown;
        var lowest = items[0].AccessLevelAtRecord;
        foreach (var item in items)
        {
            if (item.AccessLevelAtRecord < lowest) lowest = item.AccessLevelAtRecord;
        }

        return lowest;
    }
}
