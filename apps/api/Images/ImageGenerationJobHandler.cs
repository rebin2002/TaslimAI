using System.Text;
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

        ImageGenerationRequestValidator.Validate(request, settings);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(10);
        var prompt = promptBuilder.Build(request);
        progress.Report(25);
        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, settings.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null) throw new ImageProviderUnavailableException();

        logger.LogInformation("Image generation started. JobId={JobId}; ProviderKey={ProviderKey}", job.Id, provider.Key);
        progress.Report(35);
        var generated = await provider.GenerateAsync(request, prompt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(82);
        if (generated.Content.Length == 0 || generated.Content.Length > Math.Min(10 * 1_048_576, Math.Max(1, settings.MaxOutputBytes)))
            throw new ImageOutputInvalidException();
        var imageInfo = ImageBinaryInspector.Read(generated.Content.Span);
        if (imageInfo is null || !imageInfo.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ImageOutputInvalidException();

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

}
