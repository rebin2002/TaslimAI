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

    [Theory]
    [InlineData("InputPricePerMillion")]
    [InlineData("CachedInputPricePerMillion")]
    [InlineData("OutputPricePerMillion")]
    public void Negative_catalog_rates_fail_closed_for_cost_and_pricing_snapshot(string rateKey)
    {
        var rates = new Dictionary<string, string?>
        {
            ["Ai:Models:invalid:ProviderKey"] = "synthetic",
            ["Ai:Models:invalid:InputPricePerMillion"] = "2",
            ["Ai:Models:invalid:CachedInputPricePerMillion"] = "1",
            ["Ai:Models:invalid:OutputPricePerMillion"] = "3",
        };
        rates[$"Ai:Models:invalid:{rateKey}"] = "-1";
        var calculator = new AiCostCalculator(new AiModelCatalog(
            new ConfigurationBuilder().AddInMemoryCollection(rates).Build()));
        var usage = new AiUsageMetadata("synthetic", "invalid", 1_000, 100, 100, null, null, 0, "completed", false);

        Assert.Null(calculator.Calculate(usage));
        Assert.Null(calculator.GetPricingSnapshot(usage));
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
