using Taslim.Api.Documents;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentGenerationCostEstimatorTests
{
    [Fact]
    public void Negative_estimated_output_rate_is_rejected_before_preflight_can_treat_it_as_free()
    {
        var options = new DocumentGenerationOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.EstimatedOutputUsdPer1KTokens = -0.01m);
    }

    [Fact]
    public void Zero_estimated_output_rate_remains_valid()
    {
        var options = new DocumentGenerationOptions
        {
            EstimatedOutputUsdPer1KTokens = 0m,
        };

        Assert.Equal(0m, options.EstimatedOutputUsdPer1KTokens);
    }
}
