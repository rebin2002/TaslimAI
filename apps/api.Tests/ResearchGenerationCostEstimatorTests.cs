using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchGenerationCostEstimatorTests
{
    [Fact]
    public void Negative_output_pricing_is_rejected_before_research_cost_estimation()
    {
        var options = new ResearchGenerationOptions { MaxOutputTokens = 6_000 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            options.EstimatedOutputUsdPer1KTokens = -0.01m);

        Assert.Equal(nameof(ResearchGenerationOptions.EstimatedOutputUsdPer1KTokens), exception.ParamName);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.01, 0.06)]
    public void Nonnegative_output_pricing_remains_estimable(double rate, double expected)
    {
        var options = new ResearchGenerationOptions
        {
            MaxOutputTokens = 6_000,
            EstimatedOutputUsdPer1KTokens = (decimal)rate,
        };

        Assert.Equal((decimal)expected, ResearchGenerationCostEstimator.Estimate(options));
    }
}
