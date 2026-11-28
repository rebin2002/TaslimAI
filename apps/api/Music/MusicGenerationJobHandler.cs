using System.Buffers.Binary;
using System.Text;
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

    /// <summary>
    /// F-36.1: the same submission with a durable acceptance sink, invoked with the provider's own
    /// operation id the moment the paid create call is answered. A legacy provider cannot expose
    /// that id, but must record dispatch before it starts. Until its outcome is known, the durable
    /// marker prevents a second submission.
    /// </summary>
    async Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request,
        CancellationToken cancellationToken,
        Func<string, Task>? onSubmissionAccepted,
        Func<Task>? onDispatchSubmitting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (onDispatchSubmitting is not null) await onDispatchSubmitting();
        return await GenerateAsync(request, cancellationToken);
    }

    /// <summary>
    /// F-36.1b: asks the provider, by the operation id recorded at acceptance, what actually happened
    /// to a submission whose outcome this process never observed. Both shipped music providers answer
    /// with the same non-billable status GET they already use while polling, so asking cannot itself
    /// create or bill a generation. The default returns Unknown: a provider that has not adopted
    /// reconciliation changes nothing, and recovery keeps its fail-closed terminal.
    /// </summary>
    Task<MusicSubmissionReconciliation> ReconcileAcceptedSubmissionAsync(
        string providerOperationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(MusicSubmissionReconciliation.Unknown);
}

/// <summary>
/// F-36.1b: what a provider reports about an accepted submission. Unknown is deliberately distinct
/// from "did not happen": the absence of an answer is never evidence that no credits were spent,
/// which is the whole basis of the fail-closed posture.
/// </summary>
public enum MusicSubmissionOutcome
{
    Unknown,
    InProgress,
    Completed,
    ProviderDeclaredFailure,
}

/// <summary>
/// F-36.1b: a reconciliation answer plus the vendor's own billing statement about it.
/// ProviderDeclaresNoCharge is true only where the published contract says a failed generation is
/// not charged (Stability: "you will not be charged for failed generations"). Mubert publishes no
/// such statement, so a Mubert failure answer keeps ProviderDeclaresNoCharge false and the retry
/// stays blocked: the release path exists only where a contract actually supports it.
/// </summary>
public sealed record MusicSubmissionReconciliation(
    MusicSubmissionOutcome Outcome,
    bool ProviderDeclaresNoCharge,
    decimal? ActualCostUsd = null)
{
    public static readonly MusicSubmissionReconciliation Unknown = new(MusicSubmissionOutcome.Unknown, false);
}

/// <summary>
/// F-36.1: a handler that runs a paid provider submission accepts the worker's durable acceptance
/// sink. The worker owns the database and the provider-attempt row, so the record that a provider
/// accepted paid work is written where lease recovery can still read it after this process is gone;
/// the handler only forwards it to the provider it is about to call.
/// </summary>
public interface IAcceptsMusicSubmissionEvidence
{
    Func<string, Task>? AcceptedSubmissionSink { get; set; }

    /// <summary>
    /// F-36.1b: called immediately BEFORE the paid create request is handed to the transport. Neither
    /// music vendor accepts a caller-supplied operation id or idempotency key on the create call, so
    /// the id only exists once the response has been parsed; a process lost between those instants
    /// would otherwise leave no trace that a paid generation may have been created. This marker is
    /// what narrows that window to a durable "dispatched, outcome unknown" state.
    /// </summary>
    Func<Task>? DispatchingSink { get; set; }
}

public sealed class MusicGenerationJobHandler(
    IEnumerable<IMusicGenerationProvider> providers,
    IOptions<MusicGenerationOptions> options,
    ILogger<MusicGenerationJobHandler> logger) : IGenerationJobHandler, IAcceptsMusicSubmissionEvidence
{
    private readonly MusicGenerationOptions settings = options.Value;

    /// <summary>
    /// F-36.1: supplied by the generation worker before this handler runs and forwarded to the
    /// provider call. Null when no evidence sink was attached, in which case providers take their
    /// pre-F-36.1 path unchanged.
    /// </summary>
    public Func<string, Task>? AcceptedSubmissionSink { get; set; }

    /// <summary>F-36.1b: supplied by the worker alongside the acceptance sink.</summary>
    public Func<Task>? DispatchingSink { get; set; }

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
        var generated = await provider.GenerateAsync(request, cancellationToken, AcceptedSubmissionSink, DispatchingSink);
        if (cancellationToken.IsCancellationRequested)
            throw new MusicProviderCancellationWithAcceptedSubmissionException(
                providerOperationId: null,
                outputConfirmedComplete: true,
                actualCostUsd: generated.Usage.ActualCostUsd);
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
    private static readonly IReadOnlySet<string> SupportedM4aBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "M4A ", "M4B ", "isom", "iso2", "mp41", "mp42",
    };

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
        if (!HasValidAudioSignature(result.Content.Span, expectedFormat))
            throw new MusicOutputInvalidException();
        return new MusicOutputInfo(result.ContentType.Trim().ToLowerInvariant(), expectedFormat);
    }

    public static bool HasValidAudioSignature(ReadOnlySpan<byte> content, string format) => format.ToLowerInvariant() switch
    {
        "mp3" => content.Length >= 4 && ((content[..3].SequenceEqual("ID3"u8) && content.Length >= 10) || (content[0] == 0xFF && (content[1] & 0xE0) == 0xE0)),
        "wav" => content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WAVE"u8),
        "ogg" => content.Length >= 4 && content[..4].SequenceEqual("OggS"u8),
        "m4a" => HasValidM4aFileTypeBox(content),
        "aac" => content.Length >= 2 && content[0] == 0xFF && (content[1] & 0xF6) == 0xF0,
        "flac" => content.Length >= 4 && content[..4].SequenceEqual("fLaC"u8),
        _ => false,
    };

    private static bool HasValidM4aFileTypeBox(ReadOnlySpan<byte> content)
    {
        if (content.Length < 16 || !content.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;

        var boxSize = BinaryPrimitives.ReadUInt32BigEndian(content[..4]);
        if (boxSize < 16 || boxSize > content.Length) return false;

        var majorBrand = Encoding.ASCII.GetString(content.Slice(8, 4));
        return SupportedM4aBrands.Contains(majorBrand);
    }
}
