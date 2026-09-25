using System.Text.Json;
using Taslim.Api.Domain;

namespace Taslim.Api.Usage;

public enum GenerationCostDimension
{
    TextInputTokens,
    TextCachedInputTokens,
    TextOutputTokens,
    ImageInputTokens,
    ImageOutputTokens,
    ImageGeneration,
    VoiceDurationSeconds,
    VoiceCharacters,
    MusicDurationSeconds,
    VideoDurationSeconds,
    ProviderFixed,
}

public sealed record GenerationCostComponent(
    GenerationCostDimension Dimension,
    decimal Quantity,
    string Unit,
    decimal? UnitPriceUsd,
    decimal? AmountUsd,
    string? RateKey = null);

public sealed record GenerationCostEstimate(
    bool IsKnown,
    decimal? AmountUsd,
    string Currency,
    string? PricingVersion,
    DateTime? PricingEffectiveAtUtc,
    string? PricingSource,
    IReadOnlyList<GenerationCostComponent> Components,
    string? UnknownReason = null)
{
    public static GenerationCostEstimate Unknown(string reason, string currency = UsageCurrencies.Usd) =>
        new(false, null, currency, null, null, null, [], reason);

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>
/// Internal, provider-neutral inputs used to estimate a generation request. Null means
/// that the provider did not supply that usage dimension; it is never interpreted as zero.
/// </summary>
public sealed record GenerationCostEstimationRequest(
    string ProviderKey,
    string? ModelKey = null,
    int? InputTokens = null,
    int? CachedInputTokens = null,
    int? OutputTokens = null,
    int? ImageInputTokens = null,
    int? ImageOutputTokens = null,
    int? ImageGenerations = null,
    decimal? VoiceDurationSeconds = null,
    int? VoiceCharacters = null,
    decimal? MusicDurationSeconds = null,
    decimal? VideoDurationSeconds = null);

public sealed class GenerationCostPricingOptions
{
    public string Currency { get; set; } = UsageCurrencies.Usd;
    public string? Version { get; set; }
    public DateTime? EffectiveAtUtc { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, GenerationProviderPricingOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GenerationProviderPricingOptions
{
    public string? ModelKey { get; set; }
    public decimal? FixedUsd { get; set; }
    public decimal? TextInputUsdPerMillionTokens { get; set; }
    public decimal? CachedTextInputUsdPerMillionTokens { get; set; }
    public decimal? TextOutputUsdPerMillionTokens { get; set; }
    public decimal? ImageInputUsdPerMillionTokens { get; set; }
    public decimal? ImageOutputUsdPerMillionTokens { get; set; }
    public decimal? ImageGenerationUsdPerUnit { get; set; }
    public decimal? VoiceUsdPerSecond { get; set; }
    public decimal? VoiceUsdPerCharacter { get; set; }
    public decimal? MusicUsdPerSecond { get; set; }
    public decimal? VideoUsdPerSecond { get; set; }
}

public interface IGenerationCostEstimator
{
    GenerationCostEstimate Estimate(GenerationCostEstimationRequest request);
}

public sealed class GenerationCostEstimator(Microsoft.Extensions.Options.IOptions<GenerationCostPricingOptions> options) : IGenerationCostEstimator
{
    private readonly GenerationCostPricingOptions settings = options.Value;

    public GenerationCostEstimate Estimate(GenerationCostEstimationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderKey)) return GenerationCostEstimate.Unknown("provider_missing", settings.Currency);
        var pricing = FindPricing(request.ProviderKey, request.ModelKey);
        if (pricing is null) return GenerationCostEstimate.Unknown("pricing_rule_missing", settings.Currency);

        var components = new List<GenerationCostComponent>();
        var unknown = false;
        Add(GenerationCostDimension.ProviderFixed, pricing.FixedUsd.HasValue ? 1m : null, "request", pricing.FixedUsd, "fixed");
        var cachedInputTokens = request.InputTokens.HasValue && request.CachedInputTokens.HasValue
            ? Math.Min(Math.Max(0, request.InputTokens.Value), Math.Max(0, request.CachedInputTokens.Value))
            : request.CachedInputTokens;
        var uncachedInputTokens = request.InputTokens.HasValue
            ? Math.Max(0, request.InputTokens.Value) - cachedInputTokens.GetValueOrDefault()
            : request.InputTokens;
        Add(GenerationCostDimension.TextInputTokens, uncachedInputTokens, "tokens", pricing.TextInputUsdPerMillionTokens, "textInputPerMillionTokens");
        Add(GenerationCostDimension.TextCachedInputTokens, cachedInputTokens, "tokens", pricing.CachedTextInputUsdPerMillionTokens, "cachedTextInputPerMillionTokens");
        Add(GenerationCostDimension.TextOutputTokens, request.OutputTokens, "tokens", pricing.TextOutputUsdPerMillionTokens, "textOutputPerMillionTokens");
        Add(GenerationCostDimension.ImageInputTokens, request.ImageInputTokens, "tokens", pricing.ImageInputUsdPerMillionTokens, "imageInputPerMillionTokens");
        Add(GenerationCostDimension.ImageOutputTokens, request.ImageOutputTokens, "tokens", pricing.ImageOutputUsdPerMillionTokens, "imageOutputPerMillionTokens");
        Add(GenerationCostDimension.ImageGeneration, request.ImageGenerations, "generation", pricing.ImageGenerationUsdPerUnit, "imageGenerationPerUnit");
        Add(GenerationCostDimension.VoiceDurationSeconds, request.VoiceDurationSeconds, "seconds", pricing.VoiceUsdPerSecond, "voicePerSecond");
        Add(GenerationCostDimension.VoiceCharacters, request.VoiceCharacters, "characters", pricing.VoiceUsdPerCharacter, "voicePerCharacter");
        Add(GenerationCostDimension.MusicDurationSeconds, request.MusicDurationSeconds, "seconds", pricing.MusicUsdPerSecond, "musicPerSecond");
        Add(GenerationCostDimension.VideoDurationSeconds, request.VideoDurationSeconds, "seconds", pricing.VideoUsdPerSecond, "videoPerSecond");

        var amount = components.Count == 0 || unknown ? null : decimal.Round(components.Sum(item => item.AmountUsd!.Value), 8, MidpointRounding.AwayFromZero);
        return new GenerationCostEstimate(
            !unknown && amount.HasValue,
            amount,
            string.IsNullOrWhiteSpace(settings.Currency) ? UsageCurrencies.Usd : settings.Currency.Trim().ToUpperInvariant(),
            settings.Version,
            settings.EffectiveAtUtc,
            settings.Source,
            components,
            unknown ? "rate_missing_for_usage_dimension" : components.Count == 0 ? "usage_dimensions_missing" : null);

        void Add(GenerationCostDimension dimension, decimal? quantity, string unit, decimal? unitPrice, string rateKey)
        {
            if (!quantity.HasValue) return;
            var normalizedQuantity = Math.Max(0m, quantity.Value);
            if (!unitPrice.HasValue)
            {
                if (normalizedQuantity > 0m) unknown = true;
                return;
            }
            var divisor = unit is "tokens" ? 1_000_000m : 1m;
            var componentAmount = decimal.Round(normalizedQuantity * unitPrice.Value / divisor, 8, MidpointRounding.AwayFromZero);
            components.Add(new GenerationCostComponent(dimension, normalizedQuantity, unit, unitPrice, componentAmount, rateKey));
        }
    }

    private GenerationProviderPricingOptions? FindPricing(string providerKey, string? modelKey)
    {
        if (!string.IsNullOrWhiteSpace(modelKey) && settings.Providers.TryGetValue($"{providerKey}:{modelKey}", out var modelPricing)) return modelPricing;
        return settings.Providers.TryGetValue(providerKey, out var providerPricing) ? providerPricing : null;
    }
}
