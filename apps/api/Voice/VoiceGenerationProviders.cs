using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Ai;

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
/// Production adapter for OpenAI's Audio Speech endpoint. Provider and model details
/// stay behind the voice boundary; callers receive only provider-independent audio data.
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

    public async Task<VoiceProviderResult> GenerateAsync(
        VoiceGenerationInput request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled
            || !string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || !aiSettings.OpenAI.Enabled
            || string.IsNullOrWhiteSpace(aiSettings.OpenAI.ApiKey))
            throw new VoiceProviderUnavailableException();

        // OpenAI's published TTS language list does not include Kurdish Sorani.
        // Reject it before the network call instead of claiming native support or
        // silently returning a different language.
        if (string.Equals(request.Language, VoiceGenerationValues.KurdishSorani, StringComparison.OrdinalIgnoreCase))
            throw new VoiceLanguageUnsupportedException();

        if (request.Text.Length > Math.Max(1, settings.MaxProviderTextCharacters))
            throw new VoiceProviderUnsupportedRequestException();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 15, 300)));
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildSpeechUrl(aiSettings.OpenAI.BaseUrl));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aiSettings.OpenAI.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = settings.Model,
            voice = VoiceFor(request.VoiceStyle),
            input = request.Text,
            instructions = BuildInstructions(request),
            response_format = "mp3",
        });

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI voice provider timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}", Key, settings.Model);
            throw new VoiceProviderTimeoutException();
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "OpenAI voice provider request failed. ProviderKey={ProviderKey}; ModelKey={ModelKey}", Key, settings.Model);
            throw new VoiceProviderFailureException();
        }

        using (response)
        {
            var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OpenAI voice provider returned an error. ProviderKey={ProviderKey}; ModelKey={ModelKey}; HttpStatus={HttpStatus}; RequestId={RequestId}",
                    Key,
                    settings.Model,
                    (int)response.StatusCode,
                    requestId ?? "none");
                throw response.StatusCode is HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or >= HttpStatusCode.InternalServerError
                    ? new VoiceProviderUnavailableException()
                    : new VoiceProviderFailureException();
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(contentType) && !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                throw new VoiceOutputInvalidException();
            if (response.Content.Headers.ContentLength > settings.MaxOutputBytes)
                throw new VoiceOutputInvalidException();

            ReadOnlyMemory<byte> content;
            try
            {
                content = await ReadBoundedAsync(response.Content, settings.MaxOutputBytes, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("OpenAI voice provider response timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}", Key, settings.Model);
                throw new VoiceProviderTimeoutException();
            }

            if (content.Length == 0 || !LooksLikeMp3(content.Span))
                throw new VoiceOutputInvalidException();

            var safeMetadata = JsonSerializer.Serialize(new
            {
                usageReportedByProvider = false,
                inputCharacters = request.Text.Length,
                outputBytes = content.Length,
                requestId,
            });
            var usage = new VoiceProviderUsage(
                settings.Model,
                request.Text.Length,
                content.Length,
                ActualCostUsd: null,
                Math.Max(1, (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds)),
                SafeMetadataJson: safeMetadata,
                CostBasis: "Unreported",
                Currency: "USD");

            logger.LogInformation(
                "OpenAI voice provider completed. ProviderKey={ProviderKey}; ModelKey={ModelKey}; SizeBytes={SizeBytes}; DurationMs={DurationMs}; RequestId={RequestId}",
                Key,
                settings.Model,
                content.Length,
                stopwatch.ElapsedMilliseconds,
                requestId ?? "none");
            return new VoiceProviderResult(content, "audio/mpeg", "mp3", null, null, usage);
        }
    }

    private static Uri BuildSpeechUrl(string baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        return new Uri($"{normalized}/audio/speech", UriKind.Absolute);
    }

    private static string VoiceFor(string voiceStyle) => voiceStyle.Trim().ToLowerInvariant() switch
    {
        VoiceGenerationValues.Warm => "coral",
        VoiceGenerationValues.Professional => "onyx",
        VoiceGenerationValues.Storytelling => "fable",
        _ => "echo",
    };

    private static string BuildInstructions(VoiceGenerationInput request)
    {
        var language = request.Language.Equals(VoiceGenerationValues.Arabic, StringComparison.OrdinalIgnoreCase) ? "Arabic" : "English";
        var values = new List<string>
        {
            $"Speak in {language}.",
            $"Use a {request.VoiceStyle} voice and a {request.SpeakingStyle} speaking style.",
        };
        if (!string.IsNullOrWhiteSpace(request.Instructions)) values.Add(request.Instructions.Trim());
        return string.Join(' ', values);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpContent content, int configuredMaximum, CancellationToken cancellationToken)
    {
        var maximum = Math.Min(10 * 1_048_576, Math.Max(1, configuredMaximum));
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new VoiceOutputInvalidException();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static bool LooksLikeMp3(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8)) return true;
        var scanLimit = Math.Min(bytes.Length - 1, 1_024);
        for (var index = 0; index < scanLimit; index++)
        {
            if (bytes[index] == 0xFF && (bytes[index + 1] & 0xE0) == 0xE0) return true;
        }
        return false;
    }
}

/// <summary>
/// The disabled fallback keeps the durable job contract and failure lifecycle usable
/// until a production speech provider is configured. It never fabricates audio.
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
public sealed class VoiceProviderUnsupportedRequestException() : Exception("The voice provider does not support this request.");
public sealed class VoiceLanguageUnsupportedException() : Exception("The configured voice provider does not support this language.");
public sealed class VoiceOutputInvalidException() : Exception("The voice provider returned invalid audio.");
