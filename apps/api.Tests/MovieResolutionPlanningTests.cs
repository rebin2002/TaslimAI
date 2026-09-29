using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieResolutionPlanningTests
{
    private static readonly DateTime BenchmarkTime = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Catalog_contains_the_five_supported_delivery_tiers()
    {
        Assert.Equal(["480p", "720p", "1080p", "1440p", "2160p"], MovieResolutionTiers.All.Select(item => item.Code));
        Assert.Equal("1440p / 2K", MovieResolutionTiers.All.Single(item => item.Code == MovieResolutionTiers.P1440).DisplayLabel);
        Assert.Equal("2160p / 4K", MovieResolutionTiers.All.Single(item => item.Code == MovieResolutionTiers.P2160).DisplayLabel);
    }

    [Fact]
    public void Planner_selects_the_lowest_cost_evidenced_path_that_meets_quality()
    {
        var planner = new MovieResolutionPlanner(new MovieResolutionPlannerOptions());
        var result = planner.Plan(
            new(
                MovieResolutionTiers.P1080,
                MovieResolutionTiers.P720,
                0.85m,
                [
                    Evidence(MovieResolutionPathKinds.Native, MovieResolutionTiers.P1080, null, 0.60m, 0m, 0.96m),
                    Evidence(MovieResolutionPathKinds.SourceToMaster, MovieResolutionTiers.P1080, MovieResolutionTiers.P720, 0.20m, 0.08m, 0.90m),
                ]),
            BenchmarkTime);

        Assert.Equal(MovieResolutionPlanStatuses.Recommended, result.Status);
        Assert.NotNull(result.SelectedPath);
        Assert.Equal(MovieResolutionPathKinds.SourceToMaster, result.SelectedPath!.PathKind);
        Assert.Equal(0.28m, result.SelectedPath.EstimatedCostUsd);
        Assert.Contains(MovieResolutionPlanReasonCodes.NativeEscalation, result.EscalationTriggers);
        Assert.Equal(2, result.Alternatives.Count);
    }

    [Fact]
    public void Planner_rejects_a_cheaper_path_when_predicted_quality_is_below_requirement()
    {
        var planner = new MovieResolutionPlanner();
        var result = planner.Plan(
            new(
                MovieResolutionTiers.P2160,
                MovieResolutionTiers.P1080,
                0.90m,
                [
                    Evidence(MovieResolutionPathKinds.Native, MovieResolutionTiers.P2160, null, 2.00m, 0m, 0.95m),
                    Evidence(MovieResolutionPathKinds.SourceToMaster, MovieResolutionTiers.P2160, MovieResolutionTiers.P1080, 0.40m, 0.10m, 0.82m),
                ]),
            BenchmarkTime);

        Assert.Equal(MovieResolutionPathKinds.Native, result.SelectedPath?.PathKind);
        var upscale = Assert.Single(result.Alternatives, item => item.PathKind == MovieResolutionPathKinds.SourceToMaster);
        Assert.False(upscale.IsEligible);
        Assert.Equal(MovieResolutionPlanReasonCodes.QualityBelowRequirement, upscale.ReasonCode);
        Assert.Equal(0.82m, upscale.PredictedQualityScore);
    }

    [Fact]
    public void Planner_fails_honestly_when_quality_or_cost_evidence_is_missing()
    {
        var planner = new MovieResolutionPlanner();
        var result = planner.Plan(
            new(
                MovieResolutionTiers.P1440,
                MovieResolutionTiers.P720,
                0.80m,
                [
                    Evidence(MovieResolutionPathKinds.Native, MovieResolutionTiers.P1440, null, null, null, null),
                    Evidence(MovieResolutionPathKinds.SourceToMaster, MovieResolutionTiers.P1440, MovieResolutionTiers.P720, 0.30m, 0.10m, null),
                ]),
            BenchmarkTime);

        Assert.Equal(MovieResolutionPlanStatuses.InsufficientEvidence, result.Status);
        Assert.Null(result.SelectedPath);
        Assert.Contains(MovieResolutionPlanReasonCodes.BenchmarkRefreshRequired, result.EscalationTriggers);
        Assert.Contains(result.Alternatives, item => item.ReasonCode == MovieResolutionPlanReasonCodes.CostUnknown);
        Assert.Contains(result.Alternatives, item => item.ReasonCode == MovieResolutionPlanReasonCodes.QualityEvidenceUnknown);
    }

    [Fact]
    public void Planner_does_not_use_stale_benchmarks()
    {
        var planner = new MovieResolutionPlanner(new MovieResolutionPlannerOptions { MaximumBenchmarkAgeDays = 30, MinimumBenchmarkSamples = 5 });
        var result = planner.Plan(
            new(
                MovieResolutionTiers.P720,
                null,
                0.70m,
                [Evidence(MovieResolutionPathKinds.Native, MovieResolutionTiers.P720, null, 0.10m, 0m, 0.95m, samples: 5, benchmarkedAt: BenchmarkTime.AddDays(-31))]),
            BenchmarkTime);

        Assert.Equal(MovieResolutionPlanStatuses.InsufficientEvidence, result.Status);
        Assert.Equal(MovieResolutionPlanReasonCodes.EvidenceStale, Assert.Single(result.Alternatives).ReasonCode);
    }

    [Fact]
    public void Planner_rejects_unknown_source_tiers_and_never_exposes_provider_model_or_prompt_fields()
    {
        var planner = new MovieResolutionPlanner();
        var invalid = planner.Plan(new(MovieResolutionTiers.P1080, "999p", null, []), BenchmarkTime);
        Assert.Equal(MovieResolutionPlanStatuses.InvalidRequest, invalid.Status);
        Assert.Equal(MovieResolutionPlanReasonCodes.SourceResolutionUnknown, invalid.FailureReasonCode);

        var safe = planner.Plan(new(MovieResolutionTiers.P1080, null, 0.80m, []), BenchmarkTime);
        var serialized = JsonSerializer.Serialize(safe);
        Assert.DoesNotContain("provider", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static MovieResolutionEvidence Evidence(
        string pathKind,
        string target,
        string? source,
        decimal? generationCost,
        decimal? masteringCost,
        decimal? quality,
        int samples = 10,
        DateTime? benchmarkedAt = null) =>
        new(pathKind, target, source, true, generationCost, masteringCost, quality, samples, benchmarkedAt ?? BenchmarkTime);
}
