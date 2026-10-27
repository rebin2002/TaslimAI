using System.Globalization;
using Microsoft.Extensions.Configuration;
using Taslim.Api.Ai;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AiCostCalculatorSafetyTests
{
    [Fact]
    public void Extreme_catalog_pricing_fails_closed_as_unknown_cost()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Models:extreme:ProviderKey"] = "synthetic",
            ["Ai:Models:extreme:InputPricePerMillion"] = decimal.MaxValue.ToString(CultureInfo.InvariantCulture),
        }).Build();
        var calculator = new AiCostCalculator(new AiModelCatalog(configuration));
        var usage = new AiUsageMetadata("synthetic", "extreme", int.MaxValue, null, null, null, null, 0, "completed", false);

        var cost = calculator.Calculate(usage);

        Assert.Null(cost);
    }

    [Fact]
    public void Normal_catalog_pricing_still_returns_a_rounded_cost()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Models:normal:ProviderKey"] = "synthetic",
            ["Ai:Models:normal:InputPricePerMillion"] = "2",
        }).Build();
        var calculator = new AiCostCalculator(new AiModelCatalog(configuration));
        var usage = new AiUsageMetadata("synthetic", "normal", 1_000, null, null, null, null, 0, "completed", false);

        var cost = calculator.Calculate(usage);

        Assert.Equal(0.002m, cost);
    }
}
