using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionQualityControlTests
{
    private readonly MovieProductionQualityControlService qc = new(
        Options.Create(new MovieProductionQualityControlOptions()));

    [Fact]
    public void Exact_target_resolution_with_complete_evidence_is_accepted()
    {
        var decision = qc.Evaluate(Request(new(1920, 1080), cost: null));

        Assert.Equal(MovieProductionQualityControlService.ContractVersion, decision.ContractVersion);
        Assert.Equal(MovieProductionQcActions.Accept, decision.Action);
        Assert.True(decision.IsAccepted);
        Assert.False(decision.RequiresHumanReview);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.Accepted);
    }

    [Fact]
    public void Resolution_gap_within_source_and_scale_bounds_requests_upscale()
    {
        var decision = qc.Evaluate(Request(
            new(1280, 720),
            new MovieProductionQcEconomics(MaxAdditionalCostUsd: 1m, EstimatedUpscaleCostUsd: 0.12m)));

        Assert.Equal(MovieProductionQcActions.Upscale, decision.Action);
        Assert.False(decision.RequiresHumanReview);
        Assert.Equal(new MovieResolution(1920, 1080), decision.RecommendedSourceResolution);
        Assert.Equal(0.12m, decision.EstimatedAdditionalCostUsd);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.UpscaleWithinBounds);
    }

    [Fact]
    public void Small_source_that_exceeds_scale_bound_requests_higher_source_regeneration()
    {
        var decision = qc.Evaluate(Request(
            new(640, 360),
            new MovieProductionQcEconomics(MaxAdditionalCostUsd: 2m, EstimatedRegenerationCostUsd: 0.75m)));

        Assert.Equal(MovieProductionQcActions.RegenerateAtHigherSourceResolution, decision.Action);
        Assert.False(decision.RequiresHumanReview);
        Assert.Equal(new MovieResolution(1920, 1080), decision.RecommendedSourceResolution);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.SourceResolutionInsufficient);
    }

    [Fact]
    public void Missing_resolution_evidence_requires_review_instead_of_guessing()
    {
        var decision = qc.Evaluate(new MovieProductionQcRequest(
            Requirements(),
            new MovieProductionQcEvidence(null, 5, "continuity-v1"),
            new MovieProductionQcEconomics(EstimatedUpscaleCostUsd: 0.12m)));

        Assert.Equal(MovieProductionQcActions.RequireReview, decision.Action);
        Assert.True(decision.RequiresHumanReview);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.MeasurementMissing);
    }

    [Fact]
    public void Hard_continuity_conflict_requires_review_even_when_resolution_is_sufficient()
    {
        var decision = qc.Evaluate(new MovieProductionQcRequest(
            Requirements(),
            new MovieProductionQcEvidence(new(1920, 1080), 5, "continuity-v1", HardContinuityConflictCount: 1)));

        Assert.Equal(MovieProductionQcActions.RequireReview, decision.Action);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.HardContinuityConflict);
    }

    [Fact]
    public void Duration_outside_tolerance_requires_review_because_resolution_escalation_cannot_fix_it()
    {
        var decision = qc.Evaluate(new MovieProductionQcRequest(
            Requirements() with { DurationToleranceSeconds = 1 },
            new MovieProductionQcEvidence(new(1920, 1080), 8, "continuity-v1")));

        Assert.Equal(MovieProductionQcActions.RequireReview, decision.Action);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.DurationOutOfTolerance);
    }

    [Fact]
    public void Unknown_escalation_cost_is_not_silently_authorized()
    {
        var decision = qc.Evaluate(Request(new(1280, 720), cost: null));

        Assert.Equal(MovieProductionQcActions.RequireReview, decision.Action);
        Assert.True(decision.RequiresHumanReview);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.EscalationCostUnknown);
    }

    [Fact]
    public void Escalation_over_budget_is_routed_to_review_without_charging()
    {
        var decision = qc.Evaluate(Request(
            new(1280, 720),
            new MovieProductionQcEconomics(MaxAdditionalCostUsd: 0.10m, EstimatedUpscaleCostUsd: 0.12m)));

        Assert.Equal(MovieProductionQcActions.RequireReview, decision.Action);
        Assert.Equal(0.12m, decision.EstimatedAdditionalCostUsd);
        Assert.Contains(decision.Findings, item => item.ReasonCode == MovieProductionQcReasonCodes.EscalationCostLimitExceeded);
    }

    [Fact]
    public void Evidence_parser_accepts_only_bounded_provider_neutral_measurements()
    {
        var evidence = MovieProductionQcEvidenceParser.Parse("""
            {
              "width": 1280,
              "height": 720,
              "durationSeconds": 5,
              "continuitySnapshotHash": "continuity-v1",
              "continuityWarningCount": 0,
              "hardContinuityConflictCount": 0,
              "outputSha256": "abc123"
            }
            """);

        Assert.Equal(new MovieResolution(1280, 720), evidence.CurrentResolution);
        Assert.Equal(5, evidence.DurationSeconds);
        Assert.Equal("continuity-v1", evidence.ContinuitySnapshotHash);
        Assert.Equal("abc123", evidence.OutputSha256);
        Assert.Equal(new MovieProductionQcEvidence(null, null, null), MovieProductionQcEvidenceParser.Parse("not-json"));
    }

    private static MovieProductionQcRequest Request(
        MovieResolution current,
        MovieProductionQcEconomics? cost) => new(
        Requirements(),
        new MovieProductionQcEvidence(current, 5, "continuity-v1"),
        cost);

    private static MovieProductionQcRequirements Requirements() => new(
        new MovieResolution(1920, 1080),
        new MovieResolution(1280, 720),
        ExpectedDurationSeconds: 5,
        DurationToleranceSeconds: 1,
        MaxUpscaleFactor: 2m,
        AspectRatioTolerance: 0.01m,
        RequiredContinuitySnapshotHash: "continuity-v1",
        MaxContinuityWarnings: 0,
        RequireContinuityEvidence: true,
        RecommendedRegenerationSourceResolution: new MovieResolution(1920, 1080));
}
