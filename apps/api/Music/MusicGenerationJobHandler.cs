using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;

namespace Taslim.Api.Music;

public interface IMusicGenerationProvider
{
    string Key { get; }

    Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request,
        CancellationToken cancellationToken = default);
}

public sealed class MusicGenerationJobHandler(
    IEnumerable<IMusicGenerationProvider> providers,
    IOptions<MusicGenerationOptions> options,
    ILogger<MusicGenerationJobHandler> logger) : IGenerationJobHandler
{
    private readonly MusicGenerationOptions settings = options.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.MusicGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        MusicGenerationInput request;
        try
        {
            request = JsonSerializer.Deserialize<MusicGenerationInput>(job.InputJson)
                ?? throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The music request is invalid.");
        }
        catch (JsonException)
        {
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The music request is invalid.");
        }

        MusicGenerationRequestValidator.Validate(request, settings);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(10);

        if (!settings.Enabled) throw new MusicProviderUnavailableException();
        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, settings.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null) throw new MusicProviderUnavailableException();

        logger.LogInformation("Music generation started. JobId={JobId}; ProviderKey={ProviderKey}", job.Id, provider.Key);
        progress.Report(25);
        var generated = await provider.GenerateAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(82);

        var outputInfo = MusicOutputInspector.Validate(generated, settings.MaxOutputBytes);
        var durationSeconds = generated.DurationSeconds ?? request.DurationSeconds;
        var metadata = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Music,
            format = outputInfo.Format,
            durationSeconds,
            genre = request.Genre,
            mood = request.Mood,
            vocalPreference = request.VocalPreference,
            language = request.Language,
        });
        var artifact = new GeneratedFileArtifact($"music-{job.Id:N}.{outputInfo.Format}", outputInfo.ContentType, generated.Content, metadata);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Generated music" : request.Title.Trim();
        var asset = new GeneratedAssetDescriptor(title, "Generated with Taslim Music Studio.", AssetTypes.Music, metadata);
        var resultJson = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Music,
            title,
            format = outputInfo.Format,
            durationSeconds,
            vocalPreference = request.VocalPreference,
            language = request.Language,
        });
        progress.Report(90);
        logger.LogInformation("Music generation output validated. JobId={JobId}; ContentType={ContentType}; SizeBytes={SizeBytes}", job.Id, outputInfo.ContentType, generated.Content.Length);
        var usage = new AiUsageMetadata(
            settings.ProviderKey,
            settings.Model,
            generated.Usage.InputTokens,
            null,
            generated.Usage.OutputTokens,
            generated.Usage.EstimatedCostUsd,
            generated.Usage.ActualCostUsd,
            generated.Usage.LatencyMs,
            generated.Usage.FinishReason,
            false,
            PricingVersion: settings.PricingVersion,
            Currency: settings.Currency,
            CostBasis: generated.Usage.CostBasis,
            SafeMetadataJson: generated.Usage.SafeMetadataJson);
        return new GenerationHandlerResult(resultJson, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadata, artifact, asset)], usage);
    }
}

public sealed record MusicOutputInfo(string ContentType, string Format);

public static class MusicOutputInspector
{
    private static readonly IReadOnlyDictionary<string, string> Supported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = "mp3",
        ["audio/mp3"] = "mp3",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/ogg"] = "ogg",
        ["audio/mp4"] = "m4a",
        ["audio/aac"] = "aac",
        ["audio/flac"] = "flac",
    };

    public static MusicOutputInfo Validate(MusicProviderResult result, int maxOutputBytes)
    {
        if (result.Content.Length <= 0 || result.Content.Length > Math.Min(25 * 1_048_576, Math.Max(1, maxOutputBytes)))
            throw new MusicOutputInvalidException();
        if (string.IsNullOrWhiteSpace(result.ContentType) || !Supported.TryGetValue(result.ContentType.Trim(), out var expectedFormat))
            throw new MusicOutputInvalidException();
        if (string.IsNullOrWhiteSpace(result.Format) || !string.Equals(result.Format.Trim().TrimStart('.'), expectedFormat, StringComparison.OrdinalIgnoreCase))
            throw new MusicOutputInvalidException();
        return new MusicOutputInfo(result.ContentType.Trim().ToLowerInvariant(), expectedFormat);
    }
}
