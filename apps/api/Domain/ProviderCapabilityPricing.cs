namespace Taslim.Api.Domain;

/// <summary>
/// Server-owned capability and pricing metadata for provider-neutral generation routing.
/// Provider and model keys are operational data and must never be copied into normal-user DTOs.
/// </summary>
public sealed class ProviderCapabilityPricing
{
    public Guid Id { get; set; }
    public string CapabilityKey { get; set; } = "movie.video";
    public string ProviderKey { get; set; } = string.Empty;
    public string ModelKey { get; set; } = string.Empty;
    public string SourceResolution { get; set; } = string.Empty;
    public string TargetResolution { get; set; } = string.Empty;
    public string QualityTier { get; set; } = string.Empty;
    public string ProcessingPath { get; set; } = string.Empty;
    public bool SupportsUpscaling { get; set; }
    public int MaxDurationSeconds { get; set; } = 3_600;
    public int MaxRetryAttempts { get; set; } = 8;
    public int MaxUpscalePasses { get; set; } = 3;
    public decimal? BasePriceUsdPerSecondMin { get; set; }
    public decimal? BasePriceUsdPerSecondMax { get; set; }
    public decimal? BasePriceUsdFixedMin { get; set; }
    public decimal? BasePriceUsdFixedMax { get; set; }
    public decimal? UpscalePriceUsdPerSecondMin { get; set; }
    public decimal? UpscalePriceUsdPerSecondMax { get; set; }
    public decimal? UpscalePriceUsdFixedMin { get; set; }
    public decimal? UpscalePriceUsdFixedMax { get; set; }
    public string Currency { get; set; } = UsageCurrencies.Usd;
    public string PricingVersion { get; set; } = string.Empty;
    public DateTime EffectiveAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
