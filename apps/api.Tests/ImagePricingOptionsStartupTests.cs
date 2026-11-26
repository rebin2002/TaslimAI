using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class NegativeImagePricingApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ImageGeneration:Pricing:TextInputUsdPerMillion", "-0.01");
    }
}

public sealed class ImagePricingOptionsStartupTests : IClassFixture<NegativeImagePricingApiFactory>
{
    private readonly NegativeImagePricingApiFactory factory;

    public ImagePricingOptionsStartupTests(NegativeImagePricingApiFactory factory) => this.factory = factory;

    [Fact]
    public void Negative_pricing_configuration_fails_during_host_startup()
    {
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(nameof(Taslim.Api.Images.ImagePricingOptions.TextInputUsdPerMillion), exception.ToString());
    }
}
