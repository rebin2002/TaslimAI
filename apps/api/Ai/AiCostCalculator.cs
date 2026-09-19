namespace Taslim.Api.Ai;

public interface IAiCostCalculator
{
    decimal Calculate(AiUsageMetadata usage);
}

public sealed class AiCostCalculator(AiModelCatalog catalog) : IAiCostCalculator
{
    public decimal Calculate(AiUsageMetadata usage)
    {
        var model = catalog.Find(usage.ModelKey);
        if (model is null || (usage.InputTokens is null && usage.OutputTokens is null))
            return decimal.Round(usage.ActualCost ?? usage.EstimatedCost ?? 0m, 8, MidpointRounding.AwayFromZero);

        var input = Math.Max(0, usage.InputTokens.GetValueOrDefault());
        var cached = Math.Min(input, Math.Max(0, usage.CachedInputTokens.GetValueOrDefault()));
        var output = Math.Max(0, usage.OutputTokens.GetValueOrDefault());
        var uncachedInput = input - cached;
        var cost = uncachedInput * model.InputPricePerMillion / 1_000_000m
            + cached * model.CachedInputPricePerMillion / 1_000_000m
            + output * model.OutputPricePerMillion / 1_000_000m;
        return decimal.Round(cost, 8, MidpointRounding.AwayFromZero);
    }
}
