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
    public string PricingVersion { get; set; } = "gpt-image-2.5-sunburst-2026-09-08";
    public DateTime PricingEffectiveDateUtc { get; set; } = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
    public string PricingSource { get; set; } = "https://developers.openai.com/api/docs/models/gpt-image-2.5-sunburst";
    public string Currency { get; set; } = "USD";
    public ImagePricingOptions Pricing { get; set; } = new();
}

public sealed class ImagePricingOptions
{
    // GPT Image 2.5 Sunburst rates, USD per 1M tokens, documented 2026-09-08.
    public decimal TextInputUsdPerMillion { get; set; } = 5m;
    public decimal CachedTextInputUsdPerMillion { get; set; } = 1.25m;
    public decimal ImageInputUsdPerMillion { get; set; } = 8m;
    public decimal CachedImageInputUsdPerMillion { get; set; } = 2m;
    public decimal ImageOutputUsdPerMillion { get; set; } = 30m;

    public UsagePricingSnapshot ToSnapshot(ImageGenerationOptions options) => new(
        options.ProviderKey,
        options.Model,
        options.PricingVersion,
        options.PricingEffectiveDateUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(options.PricingEffectiveDateUtc, DateTimeKind.Utc)
            : options.PricingEffectiveDateUtc.ToUniversalTime(),
        $"{options.Currency} per 1M tokens",
        options.PricingSource,
        new Dictionary<string, decimal>
        {
            ["textInput"] = TextInputUsdPerMillion,
            ["cachedTextInput"] = CachedTextInputUsdPerMillion,
            ["imageInput"] = ImageInputUsdPerMillion,
            ["cachedImageInput"] = CachedImageInputUsdPerMillion,
            ["imageOutput"] = ImageOutputUsdPerMillion,
        });
}

public sealed class ImageProviderUnavailableException() : Exception("The configured image provider is unavailable.");
public sealed class ImageProviderTimeoutException() : Exception("The image provider timed out.");
public sealed class ImageProviderFailureException(string safeCode = "IMAGE_GENERATION_FAILED") : Exception("The image provider failed.")
{
    public string SafeCode { get; } = safeCode;
}
public sealed class ImageProviderSafetyException() : Exception("The image provider declined the request for safety reasons.");
public sealed class ImageOutputInvalidException() : Exception("The image provider returned an invalid image.");
