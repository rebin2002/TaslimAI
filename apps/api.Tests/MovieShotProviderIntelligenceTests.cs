using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotProviderIntelligenceTests
{
    private readonly MovieShotBenchmarkScorer scorer = new();

    [Fact]
    public void Empirical_scores_preserve_evidence_and_weighted_confidence()
    {
        var result = scorer.Rank(
            new MovieShotBenchmarkRequest(
                ["candidate-a"],
                [
                    new(MovieShotBenchmarkTypes.CloseUpFace, 80),
                    new(MovieShotBenchmarkTypes.Motion, 20),
                ]),
            [
                Evidence("candidate-a", MovieShotBenchmarkTypes.CloseUpFace, 0.90m, 25, 0.88m, "bench-2026-09", "close-up-study"),
                Evidence("candidate-a", MovieShotBenchmarkTypes.Motion, 0.40m, 10, 0.70m, "bench-2026-09", "motion-study"),
            ]);

        var candidate = Assert.Single(result.RankedCandidates);
        Assert.Equal(MovieShotBenchmarkEvidenceKind.Empirical, candidate.EvidenceKind);
        Assert.Equal(0.80m, candidate.OverallSuitabilityScore);
        Assert.Equal(1m, candidate.EvidenceCoverage);
        Assert.Equal(0.844m, candidate.Confidence);
        Assert.Equal(2, candidate.EmpiricalDimensionCount);
        Assert.All(candidate.Dimensions, item => Assert.Equal(MovieShotBenchmarkEvidenceKind.Empirical, item.EvidenceKind));
        Assert.Equal("close-up-study", candidate.Dimensions[0].EvidenceReference);
        Assert.Equal(25, candidate.Dimensions[0].SampleCount);
    }

    [Fact]
    public void Missing_empirical_data_is_unknown_not_zero_and_reduces_coverage()
    {
        var result = scorer.Rank(
            new MovieShotBenchmarkRequest(
                ["candidate-a"],
                [
                    new(MovieShotBenchmarkTypes.CloseUpFace, 50),
                    new(MovieShotBenchmarkTypes.TextSignage, 50),
                ]),
            [Evidence("candidate-a", MovieShotBenchmarkTypes.CloseUpFace, 0.80m, 12, 0.90m, "bench-1", "face")]);

        var candidate = Assert.Single(result.RankedCandidates);
        Assert.Equal(MovieShotBenchmarkEvidenceKind.Mixed, candidate.EvidenceKind);
        Assert.Equal(0.80m, candidate.OverallSuitabilityScore);
        Assert.Equal(0.5m, candidate.EvidenceCoverage);
        Assert.Equal(0.45m, candidate.Confidence);
        Assert.Equal(1, candidate.EmpiricalDimensionCount);
        Assert.Equal(0, candidate.FallbackDimensionCount);
        Assert.Equal(1, candidate.UnknownDimensionCount);
        var unknown = candidate.Dimensions.Single(item => item.ShotType == MovieShotBenchmarkTypes.TextSignage);
        Assert.Null(unknown.SuitabilityScore);
        Assert.Equal(0m, unknown.Confidence);
        Assert.Equal("No empirical or fallback evidence is available.", unknown.Reason);
    }

    [Fact]
    public void Explicit_fallback_is_labeled_and_has_no_empirical_provenance()
    {
        var result = scorer.Rank(
            new MovieShotBenchmarkRequest(
                ["candidate-a"],
                [new(MovieShotBenchmarkTypes.LipSync, 100)],
                [new(MovieShotBenchmarkTypes.LipSync, 0.55m, 0.20m, "Cold-start policy; not historical evidence.")]),
            []);

        var dimension = Assert.Single(Assert.Single(result.RankedCandidates).Dimensions);
        Assert.Equal(MovieShotBenchmarkEvidenceKind.Fallback, dimension.EvidenceKind);
        Assert.Equal(0.55m, dimension.SuitabilityScore);
        Assert.Equal(0.20m, dimension.Confidence);
        Assert.Equal(0, dimension.SampleCount);
        Assert.Null(dimension.EvidenceReference);
        Assert.Null(dimension.BenchmarkVersion);
        Assert.Equal("Cold-start policy; not historical evidence.", dimension.Reason);
    }

    [Fact]
    public void Fully_unknown_candidate_is_not_rankable_and_sorts_after_known_candidates()
    {
        var result = scorer.Rank(
            new MovieShotBenchmarkRequest(
                ["unknown", "empirical"],
                [new(MovieShotBenchmarkTypes.Environment, 100)]),
            [Evidence("empirical", MovieShotBenchmarkTypes.Environment, 0.10m, 3, 0.30m, "bench-1", "environment")]);

        Assert.Equal(["empirical", "unknown"], result.RankedCandidates.Select(item => item.CandidateKey));
        var unknown = result.RankedCandidates[1];
        Assert.Equal(MovieShotBenchmarkEvidenceKind.Unknown, unknown.EvidenceKind);
        Assert.Null(unknown.OverallSuitabilityScore);
        Assert.Equal(0m, unknown.Confidence);
        Assert.Equal(0m, unknown.EvidenceCoverage);
    }

    [Fact]
    public void Empirical_candidates_are_preferred_over_fallback_candidates_before_score_tie_breaking()
    {
        var result = scorer.Rank(
            new MovieShotBenchmarkRequest(
                ["fallback", "empirical"],
                [new(MovieShotBenchmarkTypes.Motion, 100)],
                [new(MovieShotBenchmarkTypes.Motion, 0.99m, 0.20m, "Cold-start")]),
            [Evidence("empirical", MovieShotBenchmarkTypes.Motion, 0.60m, 4, 0.60m, "bench-1", "motion")]);

        Assert.Equal("empirical", result.RankedCandidates[0].CandidateKey);
        Assert.Equal(MovieShotBenchmarkEvidenceKind.Empirical, result.RankedCandidates[0].EvidenceKind);
        Assert.Equal("fallback", result.RankedCandidates[1].CandidateKey);
        Assert.Equal(MovieShotBenchmarkEvidenceKind.Fallback, result.RankedCandidates[1].EvidenceKind);
    }

    [Fact]
    public void Validation_rejects_duplicate_dimensions_unknown_references_and_invalid_scores()
    {
        var request = new MovieShotBenchmarkRequest(
            ["candidate-a"],
            [
                new(MovieShotBenchmarkTypes.Motion, 50),
                new(MovieShotBenchmarkTypes.Motion, 50),
            ]);
        var errors = MovieShotBenchmarkScorer.Validate(
            request,
            [Evidence("not-requested", MovieShotBenchmarkTypes.Motion, 1.2m, 0, -0.1m, "", "")]);

        Assert.Contains(errors, item => item.Code == "duplicate" && item.Field.Contains("ShotType", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Code == "unknown_reference" && item.Field.Contains("CandidateKey", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Code == "out_of_range" && item.Field.Contains("SuitabilityScore", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Code == "out_of_range" && item.Field.Contains("Confidence", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Code == "out_of_range" && item.Field.Contains("SampleCount", StringComparison.Ordinal));
    }

    [Fact]
    public void Serialized_result_has_no_provider_model_prompt_or_cost_fields()
    {
        var result = scorer.Rank(
            new MovieShotBenchmarkRequest(
                ["opaque-candidate"],
                [new(MovieShotBenchmarkTypes.CloseUpFace, 100)]),
            [Evidence("opaque-candidate", MovieShotBenchmarkTypes.CloseUpFace, 0.75m, 8, 0.66m, "bench-1", "face")]);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("overallSuitabilityScore", json, StringComparison.Ordinal);
        Assert.Contains("evidenceKind", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MovieShotBenchmarkEvidence Evidence(
        string candidateKey,
        string shotType,
        decimal score,
        int sampleCount,
        decimal confidence,
        string version,
        string reference) => new(
            candidateKey,
            shotType,
            score,
            sampleCount,
            confidence,
            new DateTime(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc),
            version,
            reference);
}
