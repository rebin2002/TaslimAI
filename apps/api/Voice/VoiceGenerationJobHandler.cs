using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;

namespace Taslim.Api.Voice;

public sealed class VoiceGenerationJobHandler(
    IEnumerable<IVoiceGenerationProvider> providers,
    IOptions<VoiceGenerationOptions> options,
    ILogger<VoiceGenerationJobHandler> logger) : IGenerationJobHandler
{
    private readonly VoiceGenerationOptions settings = options.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.VoiceGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (!VoiceGenerationContractMapper.TryDeserializeInput(job.InputJson, out var request) || request is null)
            throw new VoiceRequestValidationException(GenerationJobErrorCodes.VoiceRequestInvalid, "The voice request is invalid.");

        VoiceGenerationRequestValidator.Validate(request, settings);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(10);

        if (!settings.Enabled)
            throw new VoiceProviderUnavailableException();

        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, settings.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw new VoiceProviderUnavailableException();

        logger.LogInformation("Voice generation started. JobId={JobId}; ProviderKey={ProviderKey}", job.Id, provider.Key);
        progress.Report(25);
        var stopwatch = Stopwatch.StartNew();
        var generated = await provider.GenerateAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(82);

        ValidateOutput(generated);
        var format = generated.Format.Trim().ToLowerInvariant();
        var metadata = new VoiceOutputMetadata(
            AssetTypes.Audio,
            generated.ContentType.Trim().ToLowerInvariant(),
            format,
            request.Language,
            request.VoiceStyle,
            request.SpeakingStyle,
            generated.Content.Length,
            generated.DurationMilliseconds,
            generated.SampleRateHz);
        var metadataJson = JsonSerializer.Serialize(metadata);
        var artifact = new GeneratedFileArtifact($"voice-{job.Id:N}.{format}", metadata.ContentType, generated.Content, metadataJson);
        var title = string.IsNullOrWhiteSpace(request.Title) ? BuildDefaultTitle(request.Text) : request.Title.Trim();
        var asset = new GeneratedAssetDescriptor(title, "Generated speech from Voice Studio.", AssetTypes.Audio, metadataJson);
        var resultJson = JsonSerializer.Serialize(metadata);
        progress.Report(90);

        var usage = new AiUsageMetadata(
            provider.Key,
            generated.Usage.ModelKey,
            generated.Usage.InputCharacters,
            null,
            generated.Usage.OutputBytes,
            generated.Usage.EstimatedCostUsd,
            generated.Usage.ActualCostUsd,
            Math.Max(1, generated.Usage.LatencyMs > 0 ? generated.Usage.LatencyMs : (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds)),
            generated.Usage.FinishReason,
            false,
            PricingVersion: generated.Usage.PricingVersion,
            PricingSnapshotJson: generated.Usage.PricingSnapshotJson,
            Currency: generated.Usage.Currency,
            CostBasis: generated.Usage.CostBasis,
            SafeMetadataJson: generated.Usage.SafeMetadataJson);

        logger.LogInformation("Voice generation output validated. JobId={JobId}; ContentType={ContentType}; SizeBytes={SizeBytes}", job.Id, metadata.ContentType, metadata.SizeBytes);
        return new GenerationHandlerResult(resultJson, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadataJson, artifact, asset)], usage);
    }

    private void ValidateOutput(VoiceProviderResult generated)
    {
        if (generated.Content.Length <= 0 || generated.Content.Length > Math.Min(10 * 1_048_576, Math.Max(1, settings.MaxOutputBytes)))
            throw new VoiceOutputInvalidException();
        if (string.IsNullOrWhiteSpace(generated.ContentType) || !generated.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            throw new VoiceOutputInvalidException();
        if (!VoiceGenerationValues.Formats.Contains(generated.Format.Trim()))
            throw new VoiceOutputInvalidException();
    }

    private static string BuildDefaultTitle(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 80) return normalized;
        return normalized[..77].TrimEnd() + "...";
    }
}
