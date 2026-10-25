using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationReferenceBoundaryTests
{
    [Fact]
    public async Task Deferred_reference_id_is_rejected_before_a_reference_capable_provider_is_called()
    {
        var provider = new ReferenceCapableImageProvider();
        var handler = new ImageGenerationJobHandler(
            [provider],
            new TaslimImagePromptBuilder(),
            Options.Create(new ImageGenerationOptions
            {
                Enabled = true,
                ProviderKey = provider.Key,
            }),
            NullLogger<ImageGenerationJobHandler>.Instance);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.ImageGenerate,
            InputJson = JsonSerializer.Serialize(new ImageGenerationInput(
                "Create a product image using the selected reference.",
                ImageGenerationValues.Product,
                ImageGenerationValues.Square,
                ImageGenerationValues.Standard,
                null,
                null,
                null,
                null,
                null,
                Guid.NewGuid())),
        };

        var exception = await Assert.ThrowsAsync<ImageRequestValidationException>(() =>
            handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.ImageReferenceNotSupported, exception.Code);
        Assert.Equal(0, provider.CallCount);
    }

    private sealed class ReferenceCapableImageProvider :
        IImageGenerationProvider,
        IImageGenerationProviderCapabilities,
        IImageGenerationProviderReadiness
    {
        public string Key => "synthetic-reference-provider";
        public bool IsAvailable => true;
        public int CallCount { get; private set; }
        public ImageGenerationCapabilities Capabilities => new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ImageGenerationValues.Square },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ImageGenerationValues.Standard },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" },
            SupportsReferenceImages: true,
            MaxImagesPerRequest: 1);

        public Task<ImageProviderResult> GenerateAsync(
            ImageGenerationInput request,
            ImagePromptBuildResult prompt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("The provider must not receive an unresolved reference ID.");
        }
    }
}
