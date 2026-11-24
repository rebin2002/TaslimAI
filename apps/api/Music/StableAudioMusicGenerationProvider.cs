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
        // Set once the result endpoint answers 2xx, i.e. once Stability has produced and billed the
        // generation. Any timeout after that point only stopped us from reading a track that exists,
        // so it must not be reported as the retryable timeout code: a retry would POST a second
        // billable generation and orphan the first.
        var billableResultReceived = false;
        // Set as soon as the text-to-audio POST is answered with a generation id. Stability has then
        // accepted a paid generation, while its completion and charge are still unknown to us. This
        // middle stage is deliberately distinct from "nothing was submitted": the retryable timeout
        // code makes the job layer re-run CreateAsync, which posts a second generation while the
        // accepted one may still be running and billing. It is equally not the retrieval class, whose
        // customer copy asserts the music was generated. The policy is to stop the machine from
        // submitting again (terminal MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED, whose automatic
        // re-dispatch is suppressed) and keep the generation id in the log so the pending result can
        // be reconciled by hand; an informed customer may still choose to retry once deliberately.
        var providerSubmissionAccepted = false;
        string? providerGenerationId = null;

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
            providerSubmissionAccepted = true;
            providerGenerationId = generationId;

            byte[]? output = null;
            string? contentType = null;
            // A transport fault while the audio body is streaming happens after Stability has
            // already produced and billed this generation id. The only safe recovery is another
            // result GET for the same generation: re-posting text-to-audio would spend credits a
            // second time for the same requested track. Re-fetches are bounded by the existing
            // request-retry budget and deliberately do not consume polling attempts, so a slow
            // but healthy generation keeps its full wait window.
            var transportFaults = 0;
            var transportFaultBudget = Math.Clamp(settings.StableAudioMaxRetryAttempts, 0, 5);
            for (pollAttempts = 1; pollAttempts <= Math.Max(1, settings.StableAudioMaxPollAttempts);)
            {
                HttpResponseMessage result;
                try
                {
                    result = await SendResultAsync(
                        new Uri(baseUri, $"v2beta/audio/results/{Uri.EscapeDataString(generationId)}"),
                        timeout.Token);
                }
                catch (Exception exception) when (providerSubmissionAccepted && exception is MusicProviderUnavailableException or MusicProviderFailureException)
                {
                    // The bounded result-GET retries were exhausted (transport faults persisting past
                    // the budget) for a generation Stability already accepted. As an unavailable or
                    // generic fault this is a retryable public code, and the replay POSTs a second
                    // paid generation while the accepted one may still be running and billing, so the
                    // unknown outcome is reported as an unconfirmed submission instead.
                    throw CreateSubmissionUnconfirmed("result request", exception, providerGenerationId, pollAttempts);
                }

                using (result)
                {
                    if (result.StatusCode == HttpStatusCode.Accepted)
                    {
                        pollAttempts++;
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(Math.Clamp(settings.StableAudioPollIntervalMilliseconds, 100, 30_000)),
                            timeout.Token);
                        continue;
                    }

                    if (!result.IsSuccessStatusCode)
                    {
                        var declared = await MapFailureAsync(result);
                        // A moderation rejection or an invalid-request answer is the provider declaring
                        // that this submission will not produce a track, so its own terminal class and
                        // the normal retry behaviour stay correct. Every other non-success answer on the
                        // polling path - persistent 5xx, 408, 401/403, 402, 429, 404, or the generic
                        // fault - leaves an ACCEPTED generation's outcome unknown, and its retryable
                        // code would instruct a second paid submission.
                        throw IsProviderDeclaredTerminal(declared)
                            ? declared
                            : CreateSubmissionUnconfirmed("result status", declared, providerGenerationId, pollAttempts);
                    }

                    billableResultReceived = true;

                    try
                    {
                        (output, contentType) = await ReadBoundedAudioAsync(result, timeout.Token);
                        break;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException)
                    {
                        // IOException covers SocketException, which derives from it: a reset mid-body
                        // used to reach the job handler unclassified. MusicOutputInvalidException (an
                        // integrity rejection) and the timeout cancellation are not caught here, so a
                        // corrupt artifact is never retried and a cancelled job still reports
                        // MusicProviderTimeoutException below.
                        if (transportFaults >= transportFaultBudget)
                        {
                            logger.LogWarning(
                                exception,
                                "Stable Audio result transport fault persisted after {TransportFaults} bounded result re-fetch(es). The completed generation was not re-created, so no second billable request was issued. ProviderKey={ProviderKey}; OperationId={OperationId}",
                                transportFaults,
                                Key,
                                generationId);
                            // Post-billing exhaustion: the generation exists and is billed, so this is
                            // reported as a retrieval failure rather than a generic provider outage.
                            // The generic code invites the customer to retry, and a retry here would
                            // create a second paid generation instead of delivering the finished one.
                            throw new MusicOutputRetrievalFailedException();
                        }

                        transportFaults++;
                        logger.LogWarning(
                            exception,
                            "Stable Audio result transport fault after the generation completed; re-fetching the same result with a GET instead of creating a second billable generation. Attempt={Attempt}; Stage={Stage}; ProviderKey={ProviderKey}; OperationId={OperationId}",
                            transportFaults,
                            exception is IOException ? "audio_body_stream" : "result_response",
                            Key,
                            generationId);
                        await RetryDelayAsync(transportFaults - 1, timeout.Token);
                        continue;
                    }
                }
            }

            if (output is null || string.IsNullOrWhiteSpace(contentType))
            {
                if (!billableResultReceived)
                    logger.LogWarning(
                        "Stable Audio music provider stopped polling an accepted generation whose completion was never confirmed; the generation was not re-submitted. ProviderKey={ProviderKey}; ProviderOperationId={ProviderOperationId}; PollAttempts={PollAttempts}",
                        Key,
                        providerGenerationId,
                        pollAttempts);

                throw billableResultReceived || providerSubmissionAccepted
                    // A 2xx result that yielded no usable body is post-billing: the generation exists
                    // and was charged, so this is a retrieval failure, not a retryable timeout. Bounded
                    // polling of an accepted generation whose completion was never confirmed shares the
                    // non-repetition posture but not the claim: it reports the unconfirmed class, so no
                    // copy asserts a finished track. Both keep the id in the log for reconciliation.
                    ? billableResultReceived
                        ? new MusicOutputRetrievalFailedException()
                        : new MusicProviderSubmissionUnconfirmedException()
                    : new MusicProviderTimeoutException();
            }

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
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && providerSubmissionAccepted)
        {
            // F-36: same posture as Mubert. The create call was answered with a generation id, so
            // Stable Audio holds an accepted (and possibly billed) generation that this integration
            // never cancels at the provider. A plain cancellation would leave the job on MUSIC_CANCELLED,
            // which RetryAsync treats as safe to restart - a second paid generation while the first is
            // still running. Typed cancellation keeps the member's cancellation and records the unknown.
            if (billableResultReceived)
            {
                logger.LogWarning(
                    exception,
                    "Stable Audio music provider generation was cancelled while delivering an already-completed generation; it was not re-generated. ProviderKey={ProviderKey}; ProviderOperationId={ProviderOperationId}",
                    Key,
                    providerGenerationId);
                throw new MusicProviderCancellationWithAcceptedSubmissionException(providerGenerationId, outputConfirmedComplete: true);
            }
            logger.LogWarning(
                exception,
                "Stable Audio music provider generation was cancelled after the provider accepted it; the accepted outcome is unconfirmed and was not re-submitted. ProviderKey={ProviderKey}; ProviderOperationId={ProviderOperationId}",
                Key,
                providerGenerationId);
            throw new MusicProviderCancellationWithAcceptedSubmissionException(providerGenerationId, outputConfirmedComplete: false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (billableResultReceived)
            {
                logger.LogWarning(
                    exception,
                    "Stable Audio music provider timed out while retrieving an already-completed generation. ProviderKey={ProviderKey}",
                    Key);
                throw new MusicOutputRetrievalFailedException();
            }

            if (providerSubmissionAccepted)
            {
                // Accepted, unconfirmed: charging state unknown, so the retryable timeout code would
                // instruct a second paid submission. Terminal instead, with the id logged.
                logger.LogWarning(
                    exception,
                    "Stable Audio music provider timed out before an accepted generation's completion could be confirmed; the generation was not re-submitted. ProviderKey={ProviderKey}; ProviderOperationId={ProviderOperationId}",
                    Key,
                    providerGenerationId);
                throw new MusicProviderSubmissionUnconfirmedException();
            }

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

    /// <summary>
    /// True when the provider answered in a way that settles the fate of this submission: the prompt
    /// was rejected or the request was invalid, so no generation will come from it and an ordinary
    /// retry is honest. Anything else on the polling path leaves an accepted generation unresolved.
    /// </summary>
    private static bool IsProviderDeclaredTerminal(Exception exception) =>
        exception is MusicProviderRejectedException or MusicProviderInvalidRequestException;

    /// <summary>
    /// Builds the exception for a generation Stability accepted but whose outcome was never observed,
    /// after logging the accepted generation id for hand reconciliation. Only reached after the create
    /// call returned an id, so an unknown outcome is reported neither as the retryable outage class
    /// (whose replay would POST a second paid generation) nor as a finished track (which is what the
    /// retrieval-failure copy asserts). No provider payload or key is carried in the message.
    /// </summary>
    private Exception CreateSubmissionUnconfirmed(string stage, Exception cause, string? providerGenerationId, int attempts)
    {
        logger.LogWarning(
            cause,
            "Stable Audio music provider lost the outcome of an accepted generation during {Stage}; the generation was not re-submitted automatically. ProviderKey={ProviderKey}; ProviderOperationId={ProviderOperationId}; PollAttempts={PollAttempts}",
            stage,
            Key,
            providerGenerationId,
            attempts);
        return new MusicProviderSubmissionUnconfirmedException();
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
