using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorTests
{
    [Fact]
    public void Auto_director_selects_studio_for_important_complex_shot()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(2m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 100, Complexity: 100, BudgetSensitivity: 0, DurationSeconds: 10,
            RequiresContinuity: true, RequestedQuality: DirectorQualityLevels.Auto));

        Assert.Equal(DirectorQualityLevels.Studio, recommendation.QualityLevel);
        Assert.Equal(DirectorQualityLevels.Auto, recommendation.SelectionMode);
        Assert.Contains("high_story_importance", recommendation.Reasons);
        Assert.Contains("high_shot_complexity", recommendation.Reasons);
    }

    [Fact]
    public void Auto_director_selects_fast_for_low_importance_budget_sensitive_shot()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(1m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 0, Complexity: 0, BudgetSensitivity: 100, DurationSeconds: 10,
            RequiresContinuity: false, RequestedQuality: DirectorQualityLevels.Auto));

        Assert.Equal(DirectorQualityLevels.Fast, recommendation.QualityLevel);
        Assert.True(recommendation.BudgetSensitivityScore > 0.99m);
    }

    [Fact]
    public void Budget_limit_downgrades_auto_recommendation_without_spending()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(12m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 100, Complexity: 100, BudgetSensitivity: 0, DurationSeconds: 10,
            RequiresContinuity: true, RequestedQuality: DirectorQualityLevels.Auto, BudgetLimitUsd: 5m));

        Assert.Equal(DirectorQualityLevels.Fast, recommendation.QualityLevel);
        Assert.True(recommendation.BudgetConstrained);
        Assert.Contains("budget_limit_selected_lower_quality", recommendation.Reasons);
    }

    [Fact]
    public void Explicit_quality_remains_a_user_choice()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(4m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 0, Complexity: 0, BudgetSensitivity: 100, DurationSeconds: 10,
            RequiresContinuity: false, RequestedQuality: DirectorQualityLevels.Cinematic));

        Assert.Equal(DirectorQualityLevels.Cinematic, recommendation.QualityLevel);
        Assert.Equal(DirectorQualityLevels.Cinematic, recommendation.SelectionMode);
    }

    private sealed class FixedCostEstimator(decimal amount) : IDirectorCostEstimator
    {
        public DirectorCostEstimate Estimate(DirectorCostRequest request) => new(true, amount, "USD", null);
    }
}
