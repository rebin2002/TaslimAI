using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;

namespace Taslim.Api.Music;

/// <summary>
/// Production adapter for Mubert AI Music API v3 public track generation.
/// Mubert generation is asynchronous: create a track, poll its documented track
/// resource until a download URL is available, then download it immediately
/// because the URL is temporary.
/// </summary>
public sealed class MubertMusicGenerationProvider(
    HttpClient httpClient,
    IOptions<MusicGenerationOptions> options,
    ILogger<MubertMusicGenerationProvider> logger,
    IProviderUrlPolicy? urlPolicy = null) : IMusicGenerationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly MusicGenerationOptions settings = options.Value;
    private readonly IProviderUrlPolicy downloadUrlPolicy = urlPolicy ?? new ProviderUrlPolicy();

    public string Key => "mubert";

    public async Task<MusicProviderResult> GenerateAsync(
        MusicGenerationInput request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled
            || !string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.MubertCustomerId)
            || string.IsNullOrWhiteSpace(settings.MubertAccessToken))
        {
            throw new MusicProviderUnavailableException();
        }

        var baseUri = BuildBaseUri(settings.MubertApiBaseUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 1, 900)));
        var stopwatch = Stopwatch.StartNew();
        var pollAttempts = 0;

        try
        {
            var createdBody = await SendJsonWithRetryAsync(
                HttpMethod.Post,
                new Uri(baseUri, "tracks"),
                new
                {
                    prompt = BuildPrompt(request),
                    duration = request.DurationSeconds,
                    format = NormalizeFormat(settings.MubertFormat),
                    bitrate = settings.MubertBitrate,
                    intensity = NormalizeIntensity(settings.MubertIntensity),
                    mode = NormalizeMode(settings.MubertMode),
                },
                timeout.Token);
            var created = ReadEnvelope<MubertTrackResource>(createdBody)?.Data
                ?? throw new MusicProviderFailureException();
            if (string.IsNullOrWhiteSpace(created.Id)) throw new MusicProviderFailureException();

            MubertGenerationResource? generation = null;
            for (pollAttempts = 1; pollAttempts <= Math.Max(1, settings.MubertMaxPollAttempts); pollAttempts++)
            {
                var statusBody = await SendJsonWithRetryAsync(
                    HttpMethod.Get,
                    new Uri(baseUri, $"tracks/{Uri.EscapeDataString(created.Id)}"),
                    null,
                    timeout.Token);
                var status = ReadEnvelope<MubertTrackStatusResource>(statusBody)?.Data
                    ?? throw new MusicProviderFailureException();
                generation = status.Generations?.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.Url))
                    ?? status.Generations?.LastOrDefault();

                if (generation is not null && IsFailureStatus(generation.Status))
                    throw new MusicProviderFailureException();
                if (!string.IsNullOrWhiteSpace(generation?.Url)) break;

                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Clamp(settings.MubertPollIntervalMilliseconds, 100, 30_000)),
                    timeout.Token);
            }

            if (string.IsNullOrWhiteSpace(generation?.Url))
                throw new MusicProviderTimeoutException();

            var download = await DownloadAsync(new Uri(generation.Url, UriKind.Absolute), timeout.Token);
            var format = NormalizeFormat(settings.MubertFormat);
            var contentType = NormalizeContentType(download.ContentType, format);
            var safeMetadata = JsonSerializer.Serialize(new
            {
                providerOperationId = created.Id,
                providerSessionId = created.SessionId,
                pollAttempts,
                providerStatus = generation.Status,
            }, JsonOptions);
            var usage = new MusicProviderUsage(
                InputTokens: null,
                OutputTokens: null,
                EstimatedCostUsd: null,
                ActualCostUsd: null,
                LatencyMs: Math.Max(1, (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds)),
                FinishReason: "completed",
                CostBasis: null,
                SafeMetadataJson: safeMetadata);

            logger.LogInformation(
                "Mubert music provider completed. ProviderKey={ProviderKey}; DurationMs={DurationMs}; PollAttempts={PollAttempts}",
                Key,
                stopwatch.ElapsedMilliseconds,
                pollAttempts);
            return new MusicProviderResult(download.Content, contentType, format, request.DurationSeconds, usage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Mubert music provider timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}", Key, settings.Model);
            throw new MusicProviderTimeoutException();
        }
        catch (UriFormatException exception)
        {
            logger.LogWarning(exception, "Mubert music provider configuration contains an invalid URI.");
            throw new MusicProviderUnavailableException();
        }
        catch (InvalidDataException)
        {
            throw new MusicOutputInvalidException();
        }
        catch (FileUploadValidationException)
        {
            throw new MusicOutputInvalidException();
        }
    }

    private async Task<string> SendJsonWithRetryAsync(
        HttpMethod method,
        Uri uri,
        object? payload,
        CancellationToken cancellationToken)
    {
        var maxRetries = Math.Clamp(settings.MubertMaxRetryAttempts, 0, 5);
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(method, uri);
            message.Headers.Add("customer-id", settings.MubertCustomerId);
            message.Headers.Add("access-token", settings.MubertAccessToken);
            if (payload is not null) message.Content = JsonContent.Create(payload, options: JsonOptions);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < maxRetries)
            {
                logger.LogWarning(exception, "Mubert request failed transiently; retrying. Attempt={Attempt}", attempt + 1);
                await RetryDelayAsync(attempt, cancellationToken);
                continue;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Mubert request failed after bounded retries.");
                throw new MusicProviderUnavailableException();
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return body;
                if (IsTransient(response.StatusCode) && attempt < maxRetries)
                {
                    logger.LogWarning("Mubert request returned transient HTTP status; retrying. StatusCode={StatusCode}; Attempt={Attempt}", (int)response.StatusCode, attempt + 1);
                    await RetryDelayAsync(attempt, cancellationToken);
                    continue;
                }

                logger.LogWarning("Mubert request returned HTTP status. StatusCode={StatusCode}", (int)response.StatusCode);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    throw new MusicProviderUnavailableException();
                throw new MusicProviderFailureException();
            }
        }
    }

    private async Task<MubertDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Min(25 * 1_048_576, Math.Max(1, settings.MaxOutputBytes));
        for (var redirect = 0; redirect <= ProviderDownloadSecurity.MaxRedirects; redirect++)
        {
            await downloadUrlPolicy.EnsureSafeAsync(uri, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Mubert download request failed.");
                throw new MusicProviderFailureException();
            }

            using (response)
            {
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirect == ProviderDownloadSecurity.MaxRedirects || response.Headers.Location is null)
                        throw new MusicOutputInvalidException();
                    uri = new Uri(uri, response.Headers.Location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new MusicProviderFailureException();
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
                if (response.Content.Headers.ContentLength.HasValue && total != response.Content.Headers.ContentLength.Value)
                    throw new MusicOutputInvalidException();

                var format = NormalizeFormat(settings.MubertFormat);
                var contentType = NormalizeContentType(response.Content.Headers.ContentType?.MediaType, format);
                var descriptor = GeneratedMediaSecurity.ValidateDescriptor($"provider-output.{format}", contentType);
                GeneratedMediaSecurity.ValidateContent(descriptor, buffer.GetBuffer().AsSpan(0, (int)buffer.Length), new Taslim.Api.Files.FileOptions { MaxArchiveEntries = 1, MaxArchiveUncompressedBytes = maxBytes, MaxArchiveEntryBytes = maxBytes });
                return new MubertDownload(buffer.ToArray(), contentType);
            }
        }

        throw new MusicOutputInvalidException();
    }

    private async Task RetryDelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = Math.Clamp(settings.MubertRetryBaseDelayMilliseconds, 25, 10_000) * Math.Pow(2, attempt);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 30_000)), cancellationToken);
    }

    private string BuildPrompt(MusicGenerationInput request)
    {
        var value = string.Join(", ",
            $"Genre: {request.Genre}",
            $"Mood: {request.Mood}",
            $"Style: {request.VocalPreference}",
            $"Language: {request.Language}",
            $"Description: {Clip(request.Description, 100)}",
            $"Purpose: {Clip(request.Purpose, 60)}",
            string.IsNullOrWhiteSpace(request.AdditionalInstructions) ? null : $"Instructions: {Clip(request.AdditionalInstructions, 60)}");
        var max = Math.Clamp(settings.MaxPromptCharacters, 1, 255);
        return value.Length <= max ? value : value[..max];
    }

    private static string Clip(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static Uri BuildBaseUri(string value)
    {
        var configured = string.IsNullOrWhiteSpace(value) ? "https://music-api.mubert.com/api/v3/public/" : value.TrimEnd('/') + "/";
        var uri = new Uri(configured, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new UriFormatException("Mubert API must use HTTPS.");
        return uri;
    }

    private static MubertEnvelope<TResource>? ReadEnvelope<TResource>(string body)
    {
        try { return JsonSerializer.Deserialize<MubertEnvelope<TResource>>(body, JsonOptions); }
        catch (JsonException) { throw new MusicProviderFailureException(); }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static bool IsFailureStatus(string? status) => status?.Trim().ToLowerInvariant() is "failed" or "failure" or "error" or "cancelled" or "canceled" or "rejected";

    private static string NormalizeFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "wav" => "wav",
        _ => "mp3",
    };

    private static string NormalizeIntensity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" => "medium",
        _ => "high",
    };

    private static string NormalizeMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "jingle" => "jingle",
        "loop" => "loop",
        "mix" => "mix",
        _ => "track",
    };

    private static string NormalizeContentType(string? value, string format) => value?.Trim().ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" when format == "wav" => "audio/wav",
        "audio/mpeg" or "audio/mp3" when format == "mp3" => "audio/mpeg",
        null or "" when format == "wav" => "audio/wav",
        _ when format == "mp3" && string.IsNullOrWhiteSpace(value) => "audio/mpeg",
        _ => throw new MusicOutputInvalidException(),
    };

    private sealed record MubertEnvelope<T>([property: JsonPropertyName("data")] T? Data);
    private sealed record MubertTrackResource(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("session_id")] string? SessionId);
    private sealed record MubertTrackStatusResource(
        [property: JsonPropertyName("generations")] IReadOnlyList<MubertGenerationResource>? Generations);
    private sealed record MubertGenerationResource(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("url")] string? Url);
    private sealed record MubertDownload(ReadOnlyMemory<byte> Content, string? ContentType);
}
