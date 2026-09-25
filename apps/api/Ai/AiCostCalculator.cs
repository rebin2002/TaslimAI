using Taslim.Api.Domain;

namespace Taslim.Api.Ai;

public interface IAiCostCalculator
{
    decimal? Calculate(AiUsageMetadata usage);
    UsagePricingSnapshot? GetPricingSnapshot(AiUsageMetadata usage);
}

public sealed class AiCostCalculator(AiModelCatalog catalog) : IAiCostCalculator
{
    public decimal? Calculate(AiUsageMetadata usage)
    {
        var model = catalog.Find(usage.ModelKey);
        if (usage.ActualCost.HasValue)
            return decimal.Round(Math.Max(0m, usage.ActualCost.Value), 8, MidpointRounding.AwayFromZero);
        if (model is null || (usage.InputTokens is null && usage.OutputTokens is null))
            return usage.EstimatedCost.HasValue ? decimal.Round(Math.Max(0m, usage.EstimatedCost.Value), 8, MidpointRounding.AwayFromZero) : null;

        var input = Math.Max(0, usage.InputTokens.GetValueOrDefault());
        var cached = Math.Min(input, Math.Max(0, usage.CachedInputTokens.GetValueOrDefault()));
        var output = Math.Max(0, usage.OutputTokens.GetValueOrDefault());
        var uncachedInput = input - cached;
        if (uncachedInput > 0 && !model.InputPricePerMillion.HasValue
            || cached > 0 && !model.CachedInputPricePerMillion.HasValue
            || output > 0 && !model.OutputPricePerMillion.HasValue)
            return null;
        var cost = uncachedInput * model.InputPricePerMillion.GetValueOrDefault() / 1_000_000m
            + cached * model.CachedInputPricePerMillion.GetValueOrDefault() / 1_000_000m
            + output * model.OutputPricePerMillion.GetValueOrDefault() / 1_000_000m;
        return decimal.Round(cost, 8, MidpointRounding.AwayFromZero);
    }

    public UsagePricingSnapshot? GetPricingSnapshot(AiUsageMetadata usage)
    {
        var model = catalog.Find(usage.ModelKey);
        if (model is null || model.InputPricePerMillion is null || model.CachedInputPricePerMillion is null || model.OutputPricePerMillion is null) return null;
        return new UsagePricingSnapshot(
            model.ProviderKey,
            model.ModelKey,
            model.PricingVersion ?? "unversioned",
            model.PricingEffectiveDateUtc ?? DateTime.UnixEpoch,
            "USD per 1M tokens",
            model.PricingSource ?? "configured",
            new Dictionary<string, decimal>
            {
                ["input"] = model.InputPricePerMillion.Value,
                ["cachedInput"] = model.CachedInputPricePerMillion.Value,
                ["output"] = model.OutputPricePerMillion.Value,
            });
    }
}
