using Taslim.Api.Contracts;

namespace Taslim.Api.Voice;

public sealed record VoiceProviderUsage(
    string ModelKey,
    int? InputCharacters,
    int? OutputBytes,
    decimal? ActualCostUsd,
    int LatencyMs,
    string FinishReason = "completed",
    string? PricingVersion = null,
    string? PricingSnapshotJson = null,
    string? Currency = null,
    string? CostBasis = null,
    string? SafeMetadataJson = null);

public sealed record VoiceProviderResult(
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string Format,
    long? DurationMilliseconds,
    int? SampleRateHz,
    VoiceProviderUsage Usage);

public interface IVoiceGenerationProvider
{
    string Key { get; }

    Task<VoiceProviderResult> GenerateAsync(
        VoiceGenerationInput request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The default adapter keeps the job contract and failure lifecycle usable until
/// an approved production speech provider is configured. It never fabricates audio.
/// </summary>
public sealed class UnconfiguredVoiceGenerationProvider : IVoiceGenerationProvider
{
    public string Key => "unconfigured";

    public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default) =>
        throw new VoiceProviderUnavailableException();
}

public sealed class VoiceProviderUnavailableException() : Exception("No configured voice provider is available.");
public sealed class VoiceProviderTimeoutException() : Exception("The voice provider timed out.");
public sealed class VoiceProviderFailureException() : Exception("The voice provider failed safely.");
public sealed class VoiceOutputInvalidException() : Exception("The voice provider returned invalid audio.");
