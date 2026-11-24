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
        // Set the moment the provider reports a finished track URL. From that point the customer has
        // been billed for a generation that exists on the provider side, so any later failure - including
        // the internal timeout cancelling underneath a download - is a retrieval failure and must not
        // surface as the retryable timeout code, because a retry would POST a second paid track.
        var billableOutputReceived = false;
        // Set as soon as the create POST is answered with a track id. Mubert has then ACCEPTED a paid
        // generation, but whether it finished - and whether it was charged - is unknown to us until a
        // status poll says so. This middle stage is deliberately distinct from "nothing was submitted":
        // reporting MUSIC_PROVIDER_TIMEOUT here invites the job layer's automatic retry, which re-runs
        // CreateAsync and POSTs a second track while the first may still be running and billing. Until
        // the provider exposes a way to enumerate accepted tracks by our own request id, the policy is
        // to stop the machine from submitting again (the terminal retrieval class, retry-blocked at
        // GenerationJobExecution.RetryAsync) and to leave the track id in the log so the pending
        // generation can be reconciled by hand.
        var providerSubmissionAccepted = false;
        string? providerTrackId = null;

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
            providerSubmissionAccepted = true;
            providerTrackId = created.Id;

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
                if (!string.IsNullOrWhiteSpace(generation?.Url))
                {
                    billableOutputReceived = true;
                    break;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Clamp(settings.MubertPollIntervalMilliseconds, 100, 30_000)),
                    timeout.Token);
            }

            if (string.IsNullOrWhiteSpace(generation?.Url))
            {
                // Bounded polling ran out on a track Mubert accepted but never confirmed as finished.
                // Same reconciliation posture as an unconfirmed timeout: the submission is not repeated.
                logger.LogWarning(
                    "Mubert music provider stopped polling an accepted track whose completion was never confirmed; the generation was not re-submitted. ProviderKey={ProviderKey}; ModelKey={ModelKey}; ProviderOperationId={ProviderOperationId}; PollAttempts={PollAttempts}",
                    Key,
                    settings.Model,
                    providerTrackId,
                    pollAttempts);
                throw providerSubmissionAccepted
                    ? new MusicOutputRetrievalFailedException()
                    : new MusicProviderTimeoutException();
            }

            var download = await DownloadWithTransportRecoveryAsync(new Uri(generation.Url, UriKind.Absolute), timeout.Token);
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
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (billableOutputReceived)
            {
                // The track is finished and billed; the timeout only stopped us reading it. Reported as
                // the terminal retrieval failure so the job cannot be retried into a second paid track.
                logger.LogWarning(
                    exception,
                    "Mubert music provider timed out while retrieving an already-completed track. ProviderKey={ProviderKey}; ModelKey={ModelKey}",
                    Key,
                    settings.Model);
                throw new MusicOutputRetrievalFailedException();
            }

            if (providerSubmissionAccepted)
            {
                // Accepted, unconfirmed: the paid generation may exist, so the retryable timeout code
                // would be a duplicate-submission instruction. Terminal, with the id kept for
                // reconciliation; nothing provider-sensitive is carried in the message.
                logger.LogWarning(
                    exception,
                    "Mubert music provider timed out before an accepted track's completion could be confirmed; the generation was not re-submitted. ProviderKey={ProviderKey}; ModelKey={ModelKey}; ProviderOperationId={ProviderOperationId}",
                    Key,
                    settings.Model,
                    providerTrackId);
                throw new MusicOutputRetrievalFailedException();
            }

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
        // Track creation is a billable POST without an idempotency contract;
        // only the idempotent status GET may be retried here.
        var maxRetries = method == HttpMethod.Get ? Math.Clamp(settings.MubertMaxRetryAttempts, 0, 5) : 0;
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

    /// <summary>
    /// Re-fetches a completed track's artifact with the same GET when the transport fails. The
    /// track already exists and was billed by Mubert once the status reported it, so the only
    /// safe recovery is another artifact GET: creating a second track would bill the generation
    /// again and orphan the first artifact. Auth and quota faults are excluded because they are
    /// not transport recoveries, and integrity faults (MusicOutputInvalidException,
    /// FileUploadValidationException) never reach this wrapper. A failed artifact <i>status</i>
    /// reaches it through <see cref="CreateArtifactRetrievalFailure"/>: only a transient status is
    /// treated as recoverable on the same URL, while a permanent one is already terminal.
    /// </summary>
    private async Task<MubertDownload> DownloadWithTransportRecoveryAsync(Uri artifactUri, CancellationToken cancellationToken)
    {
        var budget = Math.Clamp(settings.MubertMaxRetryAttempts, 0, 5);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await DownloadAsync(artifactUri, cancellationToken);
            }
            catch (MusicProviderUnavailableException exception) when (
                exception is not MusicProviderAuthenticationException
                && exception is not MusicProviderQuotaException
                // Already terminal for a billed track: re-GETting it here would just consume the
                // transport budget on a status that will not heal, and the wrapper's own budget
                // exhaustion produces the same type anyway.
                && exception is not MusicOutputRetrievalFailedException
                )
            {
                // The budget is spent on GETs of the same artifact, never on a second track
                // request. Exhaustion is post-billing, so it is isolated from the generic
                // provider-unavailable class: the generic code invites a retry, and a retry
                // here would create a second paid track and orphan the finished one.
                if (attempt >= budget)
                {
                    logger.LogWarning(
                        exception,
                        "Mubert artifact transport fault persisted after {TransportFaults} bounded artifact re-fetch(es). The completed track was not re-created, so no second billable request was issued.",
                        attempt);
                    throw new MusicOutputRetrievalFailedException();
                }

                logger.LogWarning(
                    exception,
                    "Mubert artifact transport fault after the track completed; re-fetching the same artifact URL instead of creating a second billable track. Attempt={Attempt}; MaxBytes={MaxBytes}",
                    attempt + 1,
                    Math.Min(25 * 1_048_576, Math.Max(1, settings.MaxOutputBytes)));
                await RetryDelayAsync(attempt, cancellationToken);
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
                throw new MusicProviderUnavailableException();
            }

            using (response)
            {
                try
                {
                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        if (redirect == ProviderDownloadSecurity.MaxRedirects || response.Headers.Location is null)
                            throw new MusicOutputInvalidException();
                        uri = new Uri(uri, response.Headers.Location);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) throw CreateArtifactRetrievalFailure(response.StatusCode);
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
                catch (HttpRequestException exception)
                {
                    logger.LogWarning(exception, "Mubert download stream failed.");
                    throw new MusicProviderUnavailableException();
                }
                catch (IOException exception)
                {
                    // A reset or aborted socket while the artifact body is still streaming surfaces
                    // as IOException (SocketException derives from it) rather than HttpRequestException.
                    // Without this the transport fault reaches the job handler unclassified, so it is
                    // reported as the same transient provider outage a failed request produces.
                    // MusicOutputInvalidException and FileUploadValidationException above are not
                    // IOException-derived, so integrity rejections keep their existing categories.
                    logger.LogWarning(exception, "Mubert download stream failed during transport.");
                    throw new MusicProviderUnavailableException();
                }
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

    /// <summary>
    /// Classifies a failed artifact GET that happens after the track was created, completed and
    /// billed. A transient status (server error, timeout or rate limit) can heal on the same URL, so
    /// it stays a transport fault and the bounded re-fetch budget is spent on that URL. Any other
    /// status will not be healed by asking again and is reported as terminal straight away. Neither
    /// path may surface as <see cref="MusicProviderFailureException"/>, because that type has no
    /// dedicated public code and falls through to the generic music-generation-failed class, which
    /// invites a retry: a retry POSTs a second billable track and orphans the completed one. Both
    /// paths therefore end at the post-billing retrieval terminal.
    /// </summary>
    private Exception CreateArtifactRetrievalFailure(HttpStatusCode statusCode)
    {
        logger.LogWarning(
            "Mubert artifact download returned HTTP status after the track was billed; treating it as post-billing retrieval rather than a generic retryable failure. StatusCode={StatusCode}",
            (int)statusCode);
        return IsTransient(statusCode)
            ? new MusicProviderUnavailableException()
            : new MusicOutputRetrievalFailedException();
    }

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
