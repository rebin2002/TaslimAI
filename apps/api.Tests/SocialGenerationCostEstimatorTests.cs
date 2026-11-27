using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialGenerationCostEstimatorTests
{
    [Fact]
    public void Negative_estimated_output_rate_is_rejected_before_preflight_can_treat_it_as_free()
    {
        var options = new SocialGenerationOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.EstimatedOutputUsdPer1KTokens = -0.01m);
    }

    [Fact]
    public void Zero_estimated_output_rate_remains_valid()
    {
        var options = new SocialGenerationOptions
        {
            EstimatedOutputUsdPer1KTokens = 0m,
        };

        Assert.Equal(0m, options.EstimatedOutputUsdPer1KTokens);
    }
}
