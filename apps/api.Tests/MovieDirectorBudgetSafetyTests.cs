using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorBudgetSafetyTests
{
    [Fact]
    public void Budget_limited_routing_does_not_treat_unknown_lower_tier_as_affordable()
    {
        var planner = new DirectorCreativeQualityPlanner(new TieredCostEstimator());

        var recommendation = planner.Recommend(new DirectorCreativeRoutingRequest(
            DirectorCreativeTaskTypes.CreateOrRefineTreatment,
            Importance: 100,
            Complexity: 100,
            BudgetSensitivity: 0,
            RequestedQuality: DirectorQualityLevels.Auto,
            BudgetLimitUsd: 0.05m,
            InputTokens: 1_000,
            OutputTokens: 1_000));

        Assert.Equal(DirectorQualityLevels.Standard, recommendation.QualityLevel);
        Assert.Equal(0.05m, recommendation.EstimatedCostUsd);
        Assert.True(recommendation.BudgetConstrained);
    }

    [Fact]
    public void Budget_limited_routing_rejects_when_no_tier_has_a_verified_affordable_cost()
    {
        var planner = new DirectorCreativeQualityPlanner(new NoAffordableTierEstimator());

        var exception = Assert.Throws<DirectorValidationException>(() => planner.Recommend(new DirectorCreativeRoutingRequest(
            DirectorCreativeTaskTypes.CreateOrRefineTreatment,
            Importance: 100,
            Complexity: 100,
            BudgetSensitivity: 0,
            RequestedQuality: DirectorQualityLevels.Auto,
            BudgetLimitUsd: 0.05m,
            InputTokens: 1_000,
            OutputTokens: 1_000)));

        Assert.Contains("verified cost", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TieredCostEstimator : IDirectorCreativeCostEstimator
    {
        public DirectorCostEstimate Estimate(DirectorCreativeCostRequest request) => request.QualityLevel switch
        {
            DirectorQualityLevels.Studio => new(true, 0.20m, "USD", null),
            DirectorQualityLevels.Cinematic => new(false, null, "USD", "pricing_unavailable"),
            DirectorQualityLevels.Standard => new(true, 0.05m, "USD", null),
            _ => new(true, 0.01m, "USD", null),
        };
    }

    private sealed class NoAffordableTierEstimator : IDirectorCreativeCostEstimator
    {
        public DirectorCostEstimate Estimate(DirectorCreativeCostRequest request) => request.QualityLevel == DirectorQualityLevels.Studio
            ? new(true, 0.20m, "USD", null)
            : new(false, null, "USD", "pricing_unavailable");
    }
}
