using Taslim.Api.Domain;

namespace Taslim.Api.Images;

public sealed class ImageGenerationOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "openai";
    public string Model { get; set; } = "gpt-image-2.5-sunburst";
    public int ProviderTimeoutSeconds { get; set; } = 150;
    public int MaxPromptCharacters { get; set; } = 4_000;
    public int MaxTitleCharacters { get; set; } = 160;
    public int MaxImagesPerJob { get; set; } = 1;
    public int MaxOutputBytes { get; set; } = 10 * 1_048_576;
    public string? PricingVersion { get; set; }
    public DateTime? PricingEffectiveDateUtc { get; set; }
    public string? PricingSource { get; set; }
    public string Currency { get; set; } = "USD";
    public ImagePricingOptions Pricing { get; set; } = new();
}

public sealed class ImagePricingOptions
{
    // GPT Image 2.5 Sunburst rates, USD per 1M tokens, documented 2026-09-08.
    public decimal? TextInputUsdPerMillion { get; set; }
    public decimal? CachedTextInputUsdPerMillion { get; set; }
    public decimal? ImageInputUsdPerMillion { get; set; }
    public decimal? CachedImageInputUsdPerMillion { get; set; }
    public decimal? ImageOutputUsdPerMillion { get; set; }

    public UsagePricingSnapshot? ToSnapshot(ImageGenerationOptions options) =>
        TextInputUsdPerMillion.HasValue && CachedTextInputUsdPerMillion.HasValue && ImageInputUsdPerMillion.HasValue && CachedImageInputUsdPerMillion.HasValue && ImageOutputUsdPerMillion.HasValue && options.PricingEffectiveDateUtc.HasValue && !string.IsNullOrWhiteSpace(options.PricingVersion) && !string.IsNullOrWhiteSpace(options.PricingSource)
        ? new(
        options.ProviderKey,
        options.Model,
        options.PricingVersion!,
        options.PricingEffectiveDateUtc.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(options.PricingEffectiveDateUtc.Value, DateTimeKind.Utc)
            : options.PricingEffectiveDateUtc.Value.ToUniversalTime(),
        $"{options.Currency} per 1M tokens",
        options.PricingSource!,
        new Dictionary<string, decimal>
        {
            ["textInput"] = TextInputUsdPerMillion.Value,
            ["cachedTextInput"] = CachedTextInputUsdPerMillion.Value,
            ["imageInput"] = ImageInputUsdPerMillion.Value,
            ["cachedImageInput"] = CachedImageInputUsdPerMillion.Value,
            ["imageOutput"] = ImageOutputUsdPerMillion.Value,
        }) : null;
}

public sealed class ImageProviderUnavailableException() : Exception("The configured image provider is unavailable.");
public sealed class ImageProviderTimeoutException() : Exception("The image provider timed out.");
public sealed class ImageProviderFailureException(string safeCode = "IMAGE_GENERATION_FAILED") : Exception("The image provider failed.")
{
    public string SafeCode { get; } = safeCode;
}
public sealed class ImageProviderSafetyException() : Exception("The image provider declined the request for safety reasons.");
public sealed class ImageOutputInvalidException() : Exception("The image provider returned an invalid image.");
