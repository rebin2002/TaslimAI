using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Music;

/// <summary>
/// Server-only adapter for Stability AI's Stable Audio 3 asynchronous text-to-audio API.
/// The durable Taslim generation job owns retries across worker leases and idempotency;
/// this adapter bounds transient retries for polling and output retrieval without exposing
/// provider payloads or controls that are not part of the Taslim Music Studio contract.
/// </summary>
public sealed class StableAudioMusicGenerationProvider(
    HttpClient httpClient,
    IOptions<MusicGenerationOptions> options,
    ILogger<StableAudioMusicGenerationProvider> logger) : IMusicGenerationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly MusicGenerationOptions settings = options.Value;

    public string Key => "stable-audio";

    public async Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled
            || !string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.StableAudioApiKey))
        {
            throw new MusicProviderUnavailableException();
        }

        var pollAttempts = 0;

        try
        {
            var baseUri = BuildBaseUri(settings.StableAudioApiBaseUrl);
            var model = NormalizeModel(settings.StableAudioModel);
            var format = NormalizeFormat(settings.StableAudioOutputFormat);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 15, 900)));
            var stopwatch = Stopwatch.StartNew();
            var createdBody = await SendCreateAsync(
                new Uri(baseUri, "v2beta/audio/stable-audio/text-to-audio"),
                BuildForm(request, model, format),
                timeout.Token);
            var generationId = ReadGenerationId(createdBody);

            byte[]? output = null;
            string? contentType = null;
            for (pollAttempts = 1; pollAttempts <= Math.Max(1, settings.StableAudioMaxPollAttempts); pollAttempts++)
            {
                using var result = await SendResultAsync(
                    new Uri(baseUri, $"v2beta/audio/results/{Uri.EscapeDataString(generationId)}"),
                    timeout.Token);
                if (result.StatusCode == HttpStatusCode.Accepted)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(Math.Clamp(settings.StableAudioPollIntervalMilliseconds, 100, 30_000)),
                        timeout.Token);
                    continue;
                }

                if (!result.IsSuccessStatusCode)
                    throw await MapFailureAsync(result);

                (output, contentType) = await ReadBoundedAudioAsync(result, timeout.Token);
                break;
            }

            if (output is null || string.IsNullOrWhiteSpace(contentType))
                throw new MusicProviderTimeoutException();

            var normalizedContentType = NormalizeContentType(contentType, format);
            if (!MusicOutputInspector.HasValidAudioSignature(output, format))
                throw new MusicOutputInvalidException();
            var safeMetadata = JsonSerializer.Serialize(new
            {
                providerOperationId = generationId,
                model,
                pollAttempts,
                credits = Math.Max(0, settings.StableAudioCreditsPerGeneration),
                billedOnSuccess = true,
            }, JsonOptions);
            var cost = CalculateCost();
            var usage = new MusicProviderUsage(
                InputTokens: null,
                OutputTokens: null,
                EstimatedCostUsd: cost,
                ActualCostUsd: cost,
                LatencyMs: Math.Max(1, (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds)),
                FinishReason: "completed",
                CostBasis: UsageCostBasis.Actual,
                SafeMetadataJson: safeMetadata);

            logger.LogInformation(
                "Stable Audio music provider completed. ProviderKey={ProviderKey}; DurationMs={DurationMs}; PollAttempts={PollAttempts}",
                Key,
                stopwatch.ElapsedMilliseconds,
                pollAttempts);
            return new MusicProviderResult(output, normalizedContentType, format, request.DurationSeconds, usage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Stable Audio music provider timed out. ProviderKey={ProviderKey}", Key);
            throw new MusicProviderTimeoutException();
        }
        catch (UriFormatException exception)
        {
            logger.LogWarning(exception, "Stable Audio provider configuration contains an invalid URI.");
            throw new MusicProviderUnavailableException();
        }
    }

    private async Task<string> SendCreateAsync(Uri uri, MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        using (form)
        using (var request = CreateRequest(HttpMethod.Post, uri))
        {
            request.Content = form;
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Stable Audio create request failed.");
                throw new MusicProviderUnavailableException();
            }

            using (response)
            {
                if (response.StatusCode != HttpStatusCode.Accepted)
                    throw await MapFailureAsync(response);
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage> SendResultAsync(Uri uri, CancellationToken cancellationToken)
    {
        return await SendWithBoundedRetryAsync(() => CreateRequest(HttpMethod.Get, uri), retryTransientResponses: true, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithBoundedRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        bool retryTransientResponses,
        CancellationToken cancellationToken)
    {
        var maxRetries = Math.Clamp(settings.StableAudioMaxRetryAttempts, 0, 5);
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < maxRetries)
            {
                logger.LogWarning(exception, "Stable Audio request failed transiently; retrying. Attempt={Attempt}", attempt + 1);
                await RetryDelayAsync(attempt, cancellationToken);
                continue;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Stable Audio request failed after bounded retries.");
                throw new MusicProviderUnavailableException();
            }

            if (retryTransientResponses && IsTransient(response.StatusCode) && attempt < maxRetries)
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                logger.LogWarning("Stable Audio request returned a transient status; retrying. StatusCode={StatusCode}; Attempt={Attempt}", (int)statusCode, attempt + 1);
                await RetryDelayAsync(attempt, cancellationToken);
                continue;
            }

            return response;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.StableAudioApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        return request;
    }

    private MultipartFormDataContent BuildForm(MusicGenerationInput request, string model, string format)
    {
        var form = new MultipartFormDataContent();
        AddField(form, "prompt", BuildPrompt(request));
        AddField(form, "model", model);
        AddField(form, "duration", request.DurationSeconds.ToString(CultureInfo.InvariantCulture));
        AddField(form, "output_format", format);
        return form;
    }

    private string BuildPrompt(MusicGenerationInput request)
    {
        var parts = new List<string>
        {
            $"Genre: {request.Genre}",
            $"Mood: {request.Mood}",
            $"Description: {Clip(request.Description, 1_000)}",
            $"Purpose: {Clip(request.Purpose, 500)}",
        };

        if (!string.IsNullOrWhiteSpace(request.Title)) parts.Add($"Title: {Clip(request.Title, 160)}");
        if (!string.IsNullOrWhiteSpace(request.AdditionalInstructions)) parts.Add($"Instructions: {Clip(request.AdditionalInstructions, 600)}");
        // Stable Audio does not expose a language parameter and does not promise
        // controllable vocals. Only the instrumental preference is safely expressible.
        if (string.Equals(request.VocalPreference, MusicGenerationValues.Instrumental, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("Instrumental music with no vocals.");
        }

        var max = Math.Min(10_000, Math.Clamp(settings.MaxPromptCharacters, 1, 10_000));
        var value = string.Join(". ", parts);
        return value.Length <= max ? value : value[..max];
    }

    private async Task<(byte[] Content, string? ContentType)> ReadBoundedAudioAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Min(25 * 1_048_576, Math.Max(1, settings.MaxOutputBytes));
        if (response.Content.Headers.ContentLength > maxBytes) throw new MusicOutputInvalidException();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream(Math.Min((int)(response.Content.Headers.ContentLength ?? 0), maxBytes));
        var chunk = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new MusicOutputInvalidException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<Exception> MapFailureAsync(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        var body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(); } catch { }
        logger.LogWarning("Stable Audio request returned a provider status. StatusCode={StatusCode}; FailureCategory={FailureCategory}", (int)status, ClassifyFailure(status, body));

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new MusicProviderAuthenticationException(),
            HttpStatusCode.PaymentRequired => new MusicProviderQuotaException(),
            HttpStatusCode.TooManyRequests => new MusicProviderRateLimitException(),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity when IsRejected(body) => new MusicProviderRejectedException(),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new MusicProviderInvalidRequestException(),
            _ when status == HttpStatusCode.RequestTimeout || (int)status >= 500 => new MusicProviderUnavailableException(),
            _ => new MusicProviderFailureException(),
        };
    }

    private static string ReadGenerationId(string body)
    {
        try
        {
            var response = JsonSerializer.Deserialize<StableAudioCreateResponse>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(response?.Id)) return response.Id;
        }
        catch (JsonException) { }
        throw new MusicProviderFailureException();
    }

    private static bool IsRejected(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ToString().Contains("moderation", StringComparison.OrdinalIgnoreCase)
                || document.RootElement.ToString().Contains("rejected", StringComparison.OrdinalIgnoreCase)
                || document.RootElement.ToString().Contains("safety", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private static string ClassifyFailure(HttpStatusCode status, string body) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication",
            HttpStatusCode.PaymentRequired => "quota",
            HttpStatusCode.TooManyRequests => "rate_limit",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity when IsRejected(body) => "rejected_prompt",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "invalid_request",
            _ when (int)status >= 500 => "provider_unavailable",
            _ => "provider_failure",
        };

    private async Task RetryDelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = Math.Clamp(settings.StableAudioRetryBaseDelayMilliseconds, 25, 10_000) * Math.Pow(2, attempt);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 30_000)), cancellationToken);
    }

    private decimal CalculateCost() => Math.Max(0, settings.StableAudioCreditsPerGeneration) * Math.Max(0, settings.StableAudioUsdPerCredit);

    private static void AddField(MultipartFormDataContent form, string name, string value) =>
        form.Add(new StringContent(value, Encoding.UTF8), name);

    private static Uri BuildBaseUri(string value)
    {
        var uri = new Uri(string.IsNullOrWhiteSpace(value) ? "https://api.stability.ai/" : value.TrimEnd('/') + "/", UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new UriFormatException("Stable Audio API must use HTTPS.");
        return uri;
    }

    private static string NormalizeModel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "stable-audio-3" => "stable-audio-3",
        _ => throw new MusicProviderUnavailableException(),
    };

    private static string NormalizeFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "wav" => "wav",
        "mp3" => "mp3",
        _ => throw new MusicProviderUnavailableException(),
    };

    private static string NormalizeContentType(string value, string format) => value.Trim().ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" when format == "wav" => "audio/wav",
        "audio/mpeg" or "audio/mp3" when format == "mp3" => "audio/mpeg",
        _ => throw new MusicOutputInvalidException(),
    };

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static string Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private sealed record StableAudioCreateResponse([property: JsonPropertyName("id")] string? Id);
}
