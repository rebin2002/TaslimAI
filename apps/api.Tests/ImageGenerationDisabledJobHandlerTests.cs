using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationDisabledJobHandlerTests
{
    [Fact]
    public async Task Disabled_studio_fails_a_queued_job_before_invoking_the_provider()
    {
        var provider = new CountingImageProvider();
        var handler = new ImageGenerationJobHandler(
            [provider],
            new TaslimImagePromptBuilder(),
            Options.Create(new ImageGenerationOptions
            {
                Enabled = false,
                ProviderKey = provider.Key,
            }),
            NullLogger<ImageGenerationJobHandler>.Instance);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.ImageGenerate,
            InputJson = JsonSerializer.Serialize(new ImageGenerationInput(
                "A product image",
                ImageGenerationValues.Product,
                ImageGenerationValues.Square,
                ImageGenerationValues.Standard,
                null,
                null,
                null,
                null,
                null,
                null)),
        };

        await Assert.ThrowsAsync<ImageProviderUnavailableException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(0, provider.CallCount);
    }

    private sealed class CountingImageProvider : IImageGenerationProvider
    {
        public string Key => "test-image";
        public int CallCount { get; private set; }

        public Task<ImageProviderResult> GenerateAsync(
            ImageGenerationInput request,
            ImagePromptBuildResult prompt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("The provider must not be called while Image Studio is disabled.");
        }
    }
}
