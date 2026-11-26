using Taslim.Api.Images;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImagePricingValidationTests
{
    public static IEnumerable<object[]> PricingRateNames() =>
    [
        [nameof(ImagePricingOptions.TextInputUsdPerMillion)],
        [nameof(ImagePricingOptions.CachedTextInputUsdPerMillion)],
        [nameof(ImagePricingOptions.ImageInputUsdPerMillion)],
        [nameof(ImagePricingOptions.CachedImageInputUsdPerMillion)],
        [nameof(ImagePricingOptions.ImageOutputUsdPerMillion)],
    ];

    [Theory]
    [MemberData(nameof(PricingRateNames))]
    public void Negative_pricing_rate_is_rejected(string propertyName)
    {
        var pricing = new ImagePricingOptions();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => SetRate(pricing, propertyName, -0.01m));

        Assert.Equal(propertyName, exception.ParamName);
    }

    [Fact]
    public void Zero_pricing_rates_are_valid_boundary_values()
    {
        var pricing = new ImagePricingOptions
        {
            TextInputUsdPerMillion = 0m,
            CachedTextInputUsdPerMillion = 0m,
            ImageInputUsdPerMillion = 0m,
            CachedImageInputUsdPerMillion = 0m,
            ImageOutputUsdPerMillion = 0m,
        };

        Assert.Equal(0m, pricing.TextInputUsdPerMillion);
        Assert.Equal(0m, pricing.CachedTextInputUsdPerMillion);
        Assert.Equal(0m, pricing.ImageInputUsdPerMillion);
        Assert.Equal(0m, pricing.CachedImageInputUsdPerMillion);
        Assert.Equal(0m, pricing.ImageOutputUsdPerMillion);
    }

    private static void SetRate(ImagePricingOptions pricing, string propertyName, decimal value)
    {
        switch (propertyName)
        {
            case nameof(ImagePricingOptions.TextInputUsdPerMillion):
                pricing.TextInputUsdPerMillion = value;
                break;
            case nameof(ImagePricingOptions.CachedTextInputUsdPerMillion):
                pricing.CachedTextInputUsdPerMillion = value;
                break;
            case nameof(ImagePricingOptions.ImageInputUsdPerMillion):
                pricing.ImageInputUsdPerMillion = value;
                break;
            case nameof(ImagePricingOptions.CachedImageInputUsdPerMillion):
                pricing.CachedImageInputUsdPerMillion = value;
                break;
            case nameof(ImagePricingOptions.ImageOutputUsdPerMillion):
                pricing.ImageOutputUsdPerMillion = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unknown Image pricing property.");
        }
    }
}
