using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Ai;
using Taslim.Api.Domain;

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
    string? SafeMetadataJson = null,
    decimal? EstimatedCostUsd = null);

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
/// Production speech adapter for the existing OpenAI credentials and base URL.
/// Provider and model details remain server-side and are never included in the job result.
/// </summary>
public sealed class OpenAiVoiceGenerationProvider(
    HttpClient httpClient,
    IOptions<AiOptions> aiOptions,
    IOptions<VoiceGenerationOptions> voiceOptions,
    ILogger<OpenAiVoiceGenerationProvider> logger) : IVoiceGenerationProvider
{
    private readonly AiOptions aiSettings = aiOptions.Value;
    private readonly VoiceGenerationOptions settings = voiceOptions.Value;

    public string Key => "openai";

    public async Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled
            || !string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || !aiSettings.OpenAI.Enabled
            || string.IsNullOrWhiteSpace(aiSettings.OpenAI.ApiKey)
            || string.IsNullOrWhiteSpace(settings.Model)
            || string.Equals(settings.Model, "unconfigured", StringComparison.OrdinalIgnoreCase))
            throw new VoiceProviderUnavailableException();

        if (!settings.SupportedLanguages.Contains(request.Language, StringComparer.OrdinalIgnoreCase))
            throw new VoiceLanguageUnsupportedException();

        var format = NormalizeFormat(settings.ResponseFormat);
        if (!VoiceGenerationValues.Formats.Contains(format))
            throw new VoiceProviderConfigurationException();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 5, 300)));
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildSpeechUrl(aiSettings.OpenAI.BaseUrl));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aiSettings.OpenAI.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = settings.Model,
            input = request.Text,
            voice = ResolveVoice(request.VoiceStyle),
            response_format = format,
            instructions = BuildInstructions(request),
        });

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI voice provider timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}", Key, settings.Model, request.Language);
            throw new VoiceProviderTimeoutException();
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "OpenAI voice provider request failed. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}", Key, settings.Model, request.Language);
            throw new VoiceProviderFailureException();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OpenAI voice provider returned HTTP {StatusCode}. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}; RequestId={RequestId}",
                    (int)response.StatusCode, Key, settings.Model, request.Language, ReadRequestId(response));
                if (response.StatusCode is HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout)
                    throw new VoiceProviderUnavailableException();
                throw new VoiceProviderFailureException();
            }

            byte[] content;
            try
            {
                content = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("OpenAI voice provider audio download timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}", Key, settings.Model, request.Language);
                throw new VoiceProviderTimeoutException();
            }

            if (content.Length == 0)
                throw new VoiceOutputInvalidException();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var usage = BuildUsage(request, content.Length, stopwatch.ElapsedMilliseconds, format);
            logger.LogInformation("OpenAI voice provider completed. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}; Format={Format}; SizeBytes={SizeBytes}; DurationMs={DurationMs}; RequestId={RequestId}",
                Key, settings.Model, request.Language, format, content.Length, stopwatch.ElapsedMilliseconds, ReadRequestId(response));
            return new VoiceProviderResult(content, string.IsNullOrWhiteSpace(contentType) ? ContentTypeFor(format) : contentType, format, null, null, usage);
        }
    }

    private VoiceProviderUsage BuildUsage(VoiceGenerationInput request, int outputBytes, long latencyMs, string format)
    {
        var inputCharacters = request.Text.Length;
        var estimatedCost = settings.PricingUsdPerMillionCharacters is { } price && price >= 0m
            ? (decimal?)decimal.Round(inputCharacters * price / 1_000_000m, 8, MidpointRounding.AwayFromZero)
            : null;
        var snapshot = settings.PricingUsdPerMillionCharacters is { } configuredPrice
            ? $"{{\"unit\":\"USD per 1M characters\",\"price\":{configuredPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"
            : null;
        var safeMetadata = $"{{\"inputCharacters\":{inputCharacters},\"outputBytes\":{outputBytes},\"format\":\"{format}\"}}";
        return new VoiceProviderUsage(
            settings.Model,
            inputCharacters,
            outputBytes,
            ActualCostUsd: null,
            Math.Max(1, (int)Math.Min(int.MaxValue, latencyMs)),
            PricingVersion: string.IsNullOrWhiteSpace(settings.PricingVersion) ? null : settings.PricingVersion,
            PricingSnapshotJson: snapshot,
            Currency: settings.Currency,
            CostBasis: estimatedCost.HasValue ? UsageCostBasis.Estimated : null,
            SafeMetadataJson: safeMetadata,
            EstimatedCostUsd: estimatedCost);
    }

    private string BuildInstructions(VoiceGenerationInput request)
    {
        var style = request.VoiceStyle switch
        {
            VoiceGenerationValues.Warm => "warm",
            VoiceGenerationValues.Professional => "professional",
            VoiceGenerationValues.Storytelling => "storytelling",
            _ => "neutral",
        };
        var delivery = request.SpeakingStyle switch
        {
            VoiceGenerationValues.Conversational => "conversational",
            VoiceGenerationValues.Expressive => "expressive",
            VoiceGenerationValues.Calm => "calm",
            _ => "clear",
        };
        var language = request.Language switch
        {
            VoiceGenerationValues.Arabic => "Arabic",
            VoiceGenerationValues.KurdishSorani => "Kurdish Sorani",
            _ => "English",
        };
        var baseInstructions = $"Speak in {language} with a {style} voice and {delivery} delivery.";
        return string.IsNullOrWhiteSpace(request.Instructions)
            ? baseInstructions
            : $"{baseInstructions} {request.Instructions.Trim()}";
    }

    private string ResolveVoice(string style) => style switch
    {
        VoiceGenerationValues.Warm => settings.WarmVoice,
        VoiceGenerationValues.Professional => settings.ProfessionalVoice,
        VoiceGenerationValues.Storytelling => settings.StorytellingVoice,
        _ => settings.NeutralVoice,
    };

    private static string NormalizeFormat(string format) => string.IsNullOrWhiteSpace(format) ? "mp3" : format.Trim().ToLowerInvariant();

    private static string ContentTypeFor(string format) => format switch
    {
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "m4a" => "audio/mp4",
        "ogg" or "opus" => "audio/ogg",
        "wav" => "audio/wav",
        "webm" => "audio/webm",
        _ => "audio/mpeg",
    };

    private static Uri BuildSpeechUrl(string baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        return new Uri($"{normalized}/audio/speech", UriKind.Absolute);
    }

    private static string? ReadRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
}

/// <summary>
/// The disabled fallback keeps the durable job contract usable before a provider is configured.
/// It never fabricates audio.
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
public sealed class VoiceProviderConfigurationException() : Exception("The voice provider configuration is incomplete.");
public sealed class VoiceLanguageUnsupportedException() : Exception("The configured voice provider does not support this language.");
public sealed class VoiceOutputInvalidException() : Exception("The voice provider returned invalid audio.");
