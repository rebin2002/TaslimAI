using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;

namespace Taslim.Api.Movies;

public sealed class MovieSoundOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "unconfigured";
    public string Model { get; set; } = "unconfigured";
    public int MaxOutputBytes { get; set; } = 25 * 1_048_576;
    public int MaxDescriptionCharacters { get; set; } = 4_000;
    public int MaxAdditionalInstructionsCharacters { get; set; } = 2_000;
    public int MaxDurationMilliseconds { get; set; } = 86_400_000;
    public string PricingVersion { get; set; } = "movie-sound-provider-unconfigured";
    public string Currency { get; set; } = "USD";
}

/// <summary>
/// Deterministic, in-process adapter used by execution tests and local contract checks.
/// It is never selected by default and performs no external I/O.
/// </summary>
public sealed class FakeMovieSoundProvider : IMovieSoundProvider
{
    public string Key => "fake";

    public Task<MovieSoundProviderResult> GenerateAsync(MovieSoundGenerationInput request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wav = CreateWav(request.DurationMilliseconds);
        return Task.FromResult(new MovieSoundProviderResult(
            wav,
            "audio/wav",
            "wav",
            request.DurationMilliseconds,
            new MovieSoundProviderUsage(0, 0m, 0m, SafeMetadataJson: "{\"deterministic\":true}")));
    }

    private static byte[] CreateWav(int durationMilliseconds)
    {
        const int sampleRate = 8_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var sampleCount = Math.Max(1, sampleRate * durationMilliseconds / 1_000);
        var dataLength = sampleCount * channels * (bitsPerSample / 8);
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();
        return stream.ToArray();
    }
}

public sealed class UnavailableMovieSoundProvider : IMovieSoundProvider
{
    public string Key => "unconfigured";
    public Task<MovieSoundProviderResult> GenerateAsync(MovieSoundGenerationInput request, CancellationToken cancellationToken = default) =>
        Task.FromException<MovieSoundProviderResult>(new MovieSoundProviderUnavailableException());
}

public sealed class MovieSoundGenerationJobHandler(
    IEnumerable<IMovieSoundProvider> providers,
    IOptions<MovieSoundOptions> options,
    ILogger<MovieSoundGenerationJobHandler> logger) : IGenerationJobHandler
{
    private readonly MovieSoundOptions settings = options.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.MovieSoundGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        MovieSoundGenerationInput request;
        try
        {
            request = JsonSerializer.Deserialize<MovieSoundGenerationInput>(job.InputJson)
                ?? throw new MovieSoundRequestValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The sound request is invalid.");
        }
        catch (JsonException)
        {
            throw new MovieSoundRequestValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The sound request is invalid.");
        }

        Validate(request, settings);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(10);
        if (!settings.Enabled) throw new MovieSoundProviderUnavailableException();
        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, settings.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null) throw new MovieSoundProviderUnavailableException();
        progress.Report(25);
        var generated = await provider.GenerateAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(82);
        var output = MovieSoundOutputInspector.Validate(generated, settings.MaxOutputBytes);
        var durationMilliseconds = generated.DurationMilliseconds ?? request.DurationMilliseconds;
        var metadata = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Audio,
            trackId = request.MovieSoundTrackId,
            kind = request.Kind,
            layer = request.Layer,
            format = output.Format,
            durationMilliseconds,
        });
        var artifact = new GeneratedFileArtifact($"movie-sound-{job.Id:N}.{output.Format}", output.ContentType, generated.Content, metadata);
        var asset = new GeneratedAssetDescriptor("Movie sound track", "Sound created for a movie production track.", AssetTypes.Audio, metadata);
        var resultJson = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Audio,
            trackId = request.MovieSoundTrackId,
            format = output.Format,
            durationMilliseconds,
        });
        var usage = new AiUsageMetadata(
            settings.ProviderKey,
            settings.Model,
            null,
            null,
            null,
            generated.Usage.EstimatedCostUsd,
            generated.Usage.ActualCostUsd,
            generated.Usage.LatencyMs,
            generated.Usage.FinishReason,
            false,
            PricingVersion: settings.PricingVersion,
            Currency: settings.Currency,
            CostBasis: generated.Usage.CostBasis,
            SafeMetadataJson: generated.Usage.SafeMetadataJson);
        logger.LogInformation("Movie sound output validated. JobId={JobId}; TrackId={TrackId}; ContentType={ContentType}; SizeBytes={SizeBytes}", job.Id, request.MovieSoundTrackId, output.ContentType, generated.Content.Length);
        progress.Report(90);
        return new GenerationHandlerResult(resultJson, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadata, artifact, asset)], usage);
    }

    private static void Validate(MovieSoundGenerationInput request, MovieSoundOptions options)
    {
        if (request.MovieProjectId == Guid.Empty || request.MovieSoundTrackId == Guid.Empty)
            throw new MovieSoundRequestValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The sound target is invalid.");
        if (!MovieSoundKinds.Supported.Contains(request.Kind) || !MovieSoundLayers.Supported.Contains(request.Layer))
            throw new MovieSoundRequestValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The sound type or layer is not supported.");
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > options.MaxDescriptionCharacters)
            throw new MovieSoundRequestValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The sound description is invalid.");
        if (request.DurationMilliseconds is < 1 or > 86_400_000 || request.DurationMilliseconds > options.MaxDurationMilliseconds)
            throw new MovieSoundRequestValidationException(GenerationJobErrorCodes.MovieSoundCueInvalid, "The sound cue duration is invalid.");
        if (request.AdditionalInstructions?.Length > options.MaxAdditionalInstructionsCharacters)
            throw new MovieSoundRequestValidationException(GenerationJobErrorCodes.MovieSoundRequestInvalid, "The additional sound instructions are too long.");
    }
}

public sealed record MovieSoundOutputInfo(string ContentType, string Format);

public static class MovieSoundOutputInspector
{
    private static readonly IReadOnlyDictionary<string, string> Supported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = "mp3",
        ["audio/mp3"] = "mp3",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/ogg"] = "ogg",
        ["audio/flac"] = "flac",
    };

    public static MovieSoundOutputInfo Validate(MovieSoundProviderResult result, int maxOutputBytes)
    {
        if (result.Content.Length <= 0 || result.Content.Length > Math.Min(25 * 1_048_576, Math.Max(1, maxOutputBytes)))
            throw new MovieSoundOutputInvalidException();
        if (string.IsNullOrWhiteSpace(result.ContentType) || !Supported.TryGetValue(result.ContentType.Trim(), out var expectedFormat))
            throw new MovieSoundOutputInvalidException();
        if (!string.Equals(result.Format?.Trim().TrimStart('.'), expectedFormat, StringComparison.OrdinalIgnoreCase))
            throw new MovieSoundOutputInvalidException();
        if (!HasValidAudioSignature(result.Content.Span, expectedFormat))
            throw new MovieSoundOutputInvalidException();
        return new MovieSoundOutputInfo(result.ContentType.Trim().ToLowerInvariant(), expectedFormat);
    }

    public static bool HasValidAudioSignature(ReadOnlySpan<byte> content, string format) => format.ToLowerInvariant() switch
    {
        "mp3" => content.Length >= 4 && ((content[..3].SequenceEqual("ID3"u8) && content.Length >= 10) || (content[0] == 0xFF && (content[1] & 0xE0) == 0xE0)),
        "wav" => content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WAVE"u8),
        "ogg" => content.Length >= 4 && content[..4].SequenceEqual("OggS"u8),
        "flac" => content.Length >= 4 && content[..4].SequenceEqual("fLaC"u8),
        _ => false,
    };
}
