using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;

namespace Taslim.Api.Images;

public sealed class ImageGenerationJobHandler(
    IEnumerable<IImageGenerationProvider> providers,
    IImagePromptBuilder promptBuilder,
    IOptions<ImageGenerationOptions> options,
    ILogger<ImageGenerationJobHandler> logger) : IGenerationJobHandler
{
    private readonly ImageGenerationOptions settings = options.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        ImageGenerationInput request;
        try
        {
            request = JsonSerializer.Deserialize<ImageGenerationInput>(job.InputJson)
                ?? throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The image request is invalid.");
        }
        catch (JsonException)
        {
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The image request is invalid.");
        }

        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, settings.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null) throw new ImageProviderUnavailableException();
        var capabilities = (provider as IImageGenerationProviderCapabilities)?.Capabilities;
        ImageGenerationRequestValidator.Validate(request, settings, capabilities);
        if (capabilities is not null && settings.MaxImagesPerJob > capabilities.MaxImagesPerRequest)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The selected image configuration is unavailable.");

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(10);
        var requestWithJob = request with { GenerationJobId = job.Id };
        var prompt = promptBuilder.Build(requestWithJob);
        progress.Report(25);

        logger.LogInformation("Image generation started. JobId={JobId}; ProviderKey={ProviderKey}", job.Id, provider.Key);
        progress.Report(35);
        var generated = await provider.GenerateAsync(requestWithJob, prompt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(82);
        var imageInfo = ValidateOutput(generated, capabilities);

        var format = imageInfo.Format;
        var contentType = imageInfo.ContentType;
        var metadata = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Image,
            format,
            width = imageInfo.Width,
            height = imageInfo.Height,
            aspectRatio = prompt.NormalizedAspectRatio,
            quality = prompt.NormalizedQuality,
        });
        var artifact = new GeneratedFileArtifact($"image-{job.Id:N}.{format}", contentType, generated.Content, metadata);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Generated image" : request.Title.Trim();
        var asset = new GeneratedAssetDescriptor(title, "Generated with Taslim Image Studio.", AssetTypes.Image, metadata);
        var resultJson = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Image,
            format,
            width = imageInfo.Width,
            height = imageInfo.Height,
            aspectRatio = prompt.NormalizedAspectRatio,
            quality = prompt.NormalizedQuality,
        });
        progress.Report(90);
        logger.LogInformation("Image generation output validated. JobId={JobId}; ContentType={ContentType}; SizeBytes={SizeBytes}", job.Id, contentType, generated.Content.Length);
        var usage = new AiUsageMetadata(
            settings.ProviderKey,
            settings.Model,
            generated.Usage.InputTokens,
            null,
            generated.Usage.OutputTokens,
            generated.Usage.ActualCostUsd,
            generated.Usage.ActualCostUsd,
            generated.Usage.LatencyMs,
            generated.Usage.FinishReason,
            false,
            generated.Usage.ImageInputTokens,
            generated.Usage.ImageOutputTokens,
            settings.PricingVersion,
            settings.Pricing.ToSnapshot(settings).ToJson(),
            settings.Currency,
            generated.Usage.CostBasis);
        return new GenerationHandlerResult(resultJson, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadata, artifact, asset)], usage);
    }

    private ImageBinaryInfo ValidateOutput(ImageProviderResult generated, ImageGenerationCapabilities? capabilities)
    {
        var maxBytes = Math.Min(25 * 1_048_576, Math.Max(1, settings.MaxOutputBytes));
        if (generated.Content.Length == 0 || generated.Content.Length > maxBytes)
            throw new ImageOutputInvalidException();
        var imageInfo = ImageBinaryInspector.Read(generated.Content.Span);
        var supportedTypes = capabilities?.OutputContentTypes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/webp",
        };
        if (imageInfo is null || !supportedTypes.Contains(imageInfo.ContentType)
            || string.IsNullOrWhiteSpace(generated.ContentType)
            || !string.Equals(generated.ContentType, imageInfo.ContentType, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(generated.Format)
            || !string.Equals(generated.Format, imageInfo.Format, StringComparison.OrdinalIgnoreCase))
            throw new ImageOutputInvalidException();
        if (generated.Width.HasValue && generated.Width != imageInfo.Width || generated.Height.HasValue && generated.Height != imageInfo.Height)
            throw new ImageOutputInvalidException();
        if (!imageInfo.Width.HasValue || !imageInfo.Height.HasValue || imageInfo.Width.Value <= 0 || imageInfo.Height.Value <= 0)
            throw new ImageOutputInvalidException();
        var width = imageInfo.Width.Value;
        var height = imageInfo.Height.Value;
        var maxDimension = Math.Max(1, settings.MaxImageDimension);
        var maxPixels = Math.Max(1, settings.MaxImagePixels);
        if (width > maxDimension || height > maxDimension || (long)width * height > maxPixels)
            throw new ImageOutputInvalidException();
        return imageInfo;
    }
}
