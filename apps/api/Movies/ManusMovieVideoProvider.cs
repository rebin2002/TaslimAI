using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;

namespace Taslim.Api.Movies;

/// <summary>
/// Server-side adapter for the Manus task API. Manus-specific task and file
/// shapes remain inside this class; callers only see the provider-neutral movie
/// contract. The adapter is intentionally opt-in through both feature flags.
/// </summary>
public sealed class ManusMovieVideoProvider(
    HttpClient httpClient,
    IOptions<MovieVideoOptions> options,
    ILogger<ManusMovieVideoProvider> logger,
    IProviderUrlPolicy? urlPolicy = null) : IMovieVideoProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyCollection<string> Operations =
        [MovieStudioOperations.QuickMovie, MovieStudioOperations.SceneClip];

    private readonly MovieVideoOptions settings = options.Value;
    private readonly IProviderUrlPolicy downloadUrlPolicy = urlPolicy ?? new ProviderUrlPolicy();
    private ManusMovieVideoOptions Manus => settings.Manus;

    public string Key => "manus";

    public bool IsAvailable => settings.Enabled
        && string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
        && Manus.Enabled
        && !string.IsNullOrWhiteSpace(Manus.ApiKey)
        && IsSupportedAgentProfile(Manus.AgentProfile)
        && TryGetBaseUri(out _);

    public IReadOnlyCollection<string> SupportedOperations => Operations;

    public async Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (!string.IsNullOrWhiteSpace(request.SourceImageUri)
            && !request.SourceImageUri.TrimStart().StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(request.SourceImageUri.Trim(), UriKind.Absolute, out var sourceUri)
                || sourceUri.Scheme != Uri.UriSchemeHttps)
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
            try { await downloadUrlPolicy.EnsureSafeAsync(sourceUri, cancellationToken); }
            catch (InvalidDataException) { throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false); }
        }
        var payload = BuildCreateRequest(request);
        var body = await SendJsonAsync(
            HttpMethod.Post,
            new Uri(GetBaseUri(), "v2/task.create"),
            payload,
            retryGetOrHead: false,
            cancellationToken);
        var response = Deserialize<CreateTaskResponse>(body);
        if (!response.Ok && string.IsNullOrWhiteSpace(response.TaskId))
            throw MapApiFailure(response.Error, HttpStatusCode.BadRequest);
        if (!IsValidTaskId(response.TaskId))
            throw Failure();

        logger.LogInformation(
            "Manus movie task submitted. Operation={Operation}; DurationSeconds={DurationSeconds}",
            request.Operation,
            request.DurationSeconds);
        return new MovieVideoSubmission(response.TaskId!);
    }

    public async Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        EnsureTaskId(providerJobId);

        var detailBody = await SendJsonAsync(
            HttpMethod.Get,
            BuildTaskUri("v2/task.detail", providerJobId),
            payload: null,
            retryGetOrHead: true,
            cancellationToken);
        var detail = Deserialize<TaskDetailResponse>(detailBody);
        if (!detail.Ok && detail.Task is null)
            throw MapApiFailure(detail.Error, HttpStatusCode.BadRequest);
        if (detail.Task is null || !string.Equals(detail.Task.Id, providerJobId, StringComparison.Ordinal))
            throw Failure();

        var messagesBody = await SendJsonAsync(
            HttpMethod.Get,
            BuildTaskUri("v2/task.listMessages", providerJobId, "order=desc&limit=" + Math.Clamp(Manus.MaxTaskMessages, 1, 100)),
            payload: null,
            retryGetOrHead: true,
            cancellationToken);
        var messages = Deserialize<TaskMessagesResponse>(messagesBody);
        if (!messages.Ok && messages.Messages is null)
            throw MapApiFailure(messages.Error, HttpStatusCode.BadRequest);

        var attachment = FindVideoAttachment(messages.Messages);
        var status = MapStatus(detail.Task, attachment);
        var estimatedCost = EstimateCost(detail.Task.CreditUsage);
        var metadata = attachment is null
            ? null
            : JsonSerializer.Serialize(new ManusOutputMetadata(
                ProviderTaskId: providerJobId,
                OutputUrl: attachment.Url,
                ContentType: attachment.ContentType,
                FileName: attachment.FileName,
                SizeBytes: attachment.SizeBytes,
                DurationSeconds: attachment.DurationSeconds), JsonOptions);

        return new MovieVideoProviderStatus(
            status,
            ProgressFor(status, attachment),
            attachment?.ContentType,
            attachment?.FileName,
            attachment?.SizeBytes,
            attachment?.DurationSeconds,
            metadata,
            ActualCostUsd: null,
            Currency: estimatedCost.HasValue ? "USD" : null,
            CostBasis: estimatedCost.HasValue ? UsageCostBasis.Estimated : null,
            SafeMetadataJson: BuildSafeMetadata(detail.Task, status, attachment is not null, estimatedCost),
            EstimatedCostUsd: estimatedCost);
    }

    public async Task<MovieVideoProviderOutput> RetrieveAsync(
        string providerJobId,
        MovieVideoProviderStatus status,
        CancellationToken cancellationToken)
    {
        EnsureAvailable();
        EnsureTaskId(providerJobId);
        if (status.Status != MovieVideoProviderJobStatus.Succeeded)
            throw Failure();

        var metadata = Deserialize<ManusOutputMetadata>(status.MetadataJson ?? string.Empty);
        if (metadata is null
            || !string.Equals(metadata.ProviderTaskId, providerJobId, StringComparison.Ordinal)
            || !TryGetHttpsUri(metadata.OutputUrl, out var outputUri))
            throw new MovieVideoProviderOutputException();
        try { await downloadUrlPolicy.EnsureSafeAsync(outputUri, cancellationToken); }
        catch (InvalidDataException) { throw new MovieVideoProviderOutputException(); }

        var contentType = NormalizeVideoContentType(metadata.ContentType, outputUri);
        var fileName = NormalizeFileName(metadata.FileName, outputUri);
        var sizeBytes = metadata.SizeBytes.GetValueOrDefault();
        if (sizeBytes <= 0 || !IsVideoContentType(metadata.ContentType))
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, outputUri);
            using var headResponse = await SendAsync(head, cancellationToken);
            if (!headResponse.IsSuccessStatusCode)
                throw new MovieVideoProviderOutputException();
            sizeBytes = headResponse.Content.Headers.ContentLength.GetValueOrDefault();
            contentType = NormalizeVideoContentType(headResponse.Content.Headers.ContentType?.MediaType, outputUri);
        }
        if (sizeBytes <= 0 || sizeBytes > MaxOutputBytes() || !IsVideoContentType(contentType))
            throw new MovieVideoProviderOutputException();

        async Task<Stream> OpenReadAsync(CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, outputUri);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(request, token);
            }
            catch (MovieVideoProviderTimeoutException)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Manus video output retrieval failed without exposing provider response details.");
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnavailable, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnavailable, IsTransient(response.StatusCode));
            }

            var responseType = response.Content.Headers.ContentType?.MediaType;
            var responseLength = response.Content.Headers.ContentLength;
            if (!IsVideoContentType(responseType)
                || responseLength is <= 0
                || responseLength > MaxOutputBytes()
                || responseLength != sizeBytes)
            {
                response.Dispose();
                throw new MovieVideoProviderOutputException();
            }

            var stream = await response.Content.ReadAsStreamAsync(token);
            return new ResponseOwnedStream(stream, response);
        }

        return new MovieVideoProviderOutput(
            contentType,
            fileName,
            sizeBytes,
            OpenReadAsync,
            metadata.DurationSeconds ?? status.DurationSeconds,
            JsonSerializer.Serialize(new
            {
                assetType = AssetTypes.Video,
                contentType,
                durationSeconds = metadata.DurationSeconds ?? status.DurationSeconds,
            }, JsonOptions),
            status.EstimatedCostUsd,
            status.ActualCostUsd,
            status.Currency ?? "USD",
            status.CostBasis ?? UsageCostBasis.Estimated,
            status.SafeMetadataJson,
            ProviderModelKey: "video");
    }

    public async Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
    {
        if (!IsAvailable || !IsValidTaskId(providerJobId))
            return;

        try
        {
            var body = JsonSerializer.Serialize(new StopTaskRequest(providerJobId), JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(GetBaseUri(), "v2/task.stop"))
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                logger.LogWarning("Manus cancellation returned a non-success status. StatusCode={StatusCode}", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Manus cancellation timed out without exposing provider response details.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Manus cancellation failed without exposing provider response details.");
        }
    }

    private CreateTaskRequest BuildCreateRequest(MovieVideoGenerationRequest request)
    {
        if (request.Operation is not (MovieStudioOperations.QuickMovie or MovieStudioOperations.SceneClip))
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
        if (!string.IsNullOrWhiteSpace(request.ContinuationProviderJobId))
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
        if (request.DurationSeconds is < 1 or > 30)
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
        if (request.AspectRatio.Trim() is not ("16:9" or "9:16" or "1:1"))
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);

        var prompt = BuildPrompt(request);
        var content = new List<object> { new TextContentPart("text", prompt, "visible") };
        if (!string.IsNullOrWhiteSpace(request.SourceImageUri))
            content.Add(BuildImageContent(request.SourceImageUri));

        return new CreateTaskRequest(
            new ManusMessageRequest(content),
            InteractiveMode: false,
            HideInTaskList: true,
            ShareVisibility: "private",
            AgentProfile: NormalizeAgentProfile(Manus.AgentProfile),
            Title: "Taslim movie clip");
    }

    private string BuildPrompt(MovieVideoGenerationRequest request)
    {
        var sections = new List<string>
        {
            "Create one short downloadable video clip for the supplied movie shot.",
            "Do not ask follow-up questions. Produce a single video file only; do not return a storyboard, explanation, or still image.",
            $"Description: {request.Description}",
            $"Duration seconds: {request.DurationSeconds}",
            $"Aspect ratio: {request.AspectRatio.Trim()}",
            $"Visual style: {request.Style}",
            $"Language context: {request.Language}",
        };
        AddSection(sections, "Additional direction", request.AdditionalInstructions);
        AddSection(sections, "Continuity guide", request.ContinuityGuideJson);
        AddSection(sections, "Scene direction", request.SceneJson);
        AddSection(sections, "Shot direction", request.ShotJson);
        AddSection(sections, "World context", request.WorldContextJson);
        var prompt = string.Join("\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)).Select(section => section.Trim()));
        var maximum = Math.Clamp(Manus.MaxPromptCharacters, 1_000, 20_000);
        if (prompt.Length < 1)
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
        return prompt.Length <= maximum ? prompt : prompt[..(maximum - 3)].TrimEnd() + "...";
    }

    private static object BuildImageContent(string sourceImageUri)
    {
        var value = sourceImageUri.Trim();
        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length > 20 * 1024 * 1024)
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
            return new FileContentPart("file", null, value, "reference-image", null, "visible");
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && value.Length <= 2_048)
            return new FileContentPart("file", value, null, Path.GetFileName(uri.AbsolutePath), "image/*", "visible");
        throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
    }

    private static void AddSection(ICollection<string> sections, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sections.Add($"{label}: {value.Trim()}");
    }

    private MovieVideoProviderJobStatus MapStatus(ManusTask task, ManusVideoAttachment? attachment)
    {
        var normalized = task.Status?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "running" => MovieVideoProviderJobStatus.Running,
            "queued" or "pending" => MovieVideoProviderJobStatus.Queued,
            "waiting" => MovieVideoProviderJobStatus.Failed,
            "error" => MovieVideoProviderJobStatus.Failed,
            "succeeded" or "completed" => attachment is null ? MovieVideoProviderJobStatus.Failed : MovieVideoProviderJobStatus.Succeeded,
            "stopped" when task.HasRunningBackgroundJobs == true => MovieVideoProviderJobStatus.Running,
            "stopped" when attachment is not null => MovieVideoProviderJobStatus.Succeeded,
            "stopped" => MovieVideoProviderJobStatus.Cancelled,
            _ => throw Failure(),
        };
    }

    private string BuildSafeMetadata(ManusTask task, MovieVideoProviderJobStatus status, bool outputAvailable, decimal? estimatedCost)
    {
        return JsonSerializer.Serialize(new
        {
            status = status.ToString().ToLowerInvariant(),
            outputAvailable,
            creditsUsed = task.CreditUsage,
            estimatedCostUsd = estimatedCost,
        }, JsonOptions);
    }

    private decimal? EstimateCost(long? credits)
    {
        if (credits is not { } value || value < 0 || Manus.CreditUsdPerCredit is not { } rate || rate < 0)
            return null;
        return decimal.Round(value * rate, 8, MidpointRounding.AwayFromZero);
    }

    private static int ProgressFor(MovieVideoProviderJobStatus status, ManusVideoAttachment? attachment) =>
        status switch
        {
            MovieVideoProviderJobStatus.Succeeded => 100,
            MovieVideoProviderJobStatus.Failed or MovieVideoProviderJobStatus.Cancelled => 0,
            MovieVideoProviderJobStatus.Running => 50,
            _ => attachment is null ? 10 : 100,
        };

    private async Task<string> SendJsonAsync(HttpMethod method, Uri uri, object? payload, bool retryGetOrHead, CancellationToken cancellationToken)
    {
        var maxRetries = retryGetOrHead ? Math.Clamp(settings.MaxTransientRetries, 0, 8) : 0;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            AddHeaders(request);
            if (payload is not null)
                request.Content = JsonContent.Create(payload, options: JsonOptions);

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < maxRetries)
            {
                logger.LogWarning(exception, "Manus request failed transiently; retrying. Attempt={Attempt}", attempt + 1);
                await RetryDelayAsync(attempt, cancellationToken);
                continue;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Manus request failed after bounded retries.");
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnavailable, true);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                    return body;
                if (IsTransient(response.StatusCode) && attempt < maxRetries)
                {
                    await RetryDelayAsync(attempt, cancellationToken);
                    continue;
                }
                throw MapApiFailure(null, response.StatusCode);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 1, 120)));
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MovieVideoProviderTimeoutException();
        }
    }

    private void AddHeaders(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("x-manus-api-key", Manus.ApiKey.Trim());

    private async Task RetryDelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(Math.Max(1, settings.RetryMaxDelaySeconds), Math.Max(1, settings.RetryBaseDelaySeconds) * Math.Pow(2, attempt));
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new MovieProviderUnavailableException();
    }

    private Uri GetBaseUri()
    {
        if (!TryGetBaseUri(out var uri))
            throw new MovieProviderUnavailableException();
        return uri;
    }

    private bool TryGetBaseUri(out Uri uri)
    {
        var configured = string.IsNullOrWhiteSpace(Manus.ApiBaseUrl) ? "https://api.manus.ai/" : Manus.ApiBaseUrl.TrimEnd('/') + "/";
        if (Uri.TryCreate(configured, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps)
            return true;
        uri = null!;
        return false;
    }

    private Uri BuildTaskUri(string path, string taskId, string? query = null)
    {
        var uri = new Uri(GetBaseUri(), $"{path}?task_id={Uri.EscapeDataString(taskId)}{(query is null ? string.Empty : "&" + query)}");
        return uri;
    }

    private static ManusVideoAttachment? FindVideoAttachment(IEnumerable<ManusMessage>? messages) =>
        messages?
            .SelectMany(message => message.AssistantMessage?.Attachments ?? [])
            .Where(attachment => IsVideoContentType(attachment.ContentType) && TryGetHttpsUri(attachment.Url, out _))
            .Select(attachment => new ManusVideoAttachment(
                attachment.Url!,
                attachment.ContentType,
                attachment.FileName,
                attachment.SizeBytes,
                attachment.DurationSeconds))
            .FirstOrDefault();

    private static MovieVideoProviderException MapApiFailure(ManusApiError? error, HttpStatusCode statusCode)
    {
        var transient = IsTransient(statusCode) || string.Equals(error?.Code, "rate_limited", StringComparison.OrdinalIgnoreCase);
        var code = statusCode is HttpStatusCode.BadRequest or (HttpStatusCode)422
            ? GenerationJobErrorCodes.MovieProviderUnsupportedRequest
            : statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound || transient
                ? GenerationJobErrorCodes.MovieProviderUnavailable
                : GenerationJobErrorCodes.MovieGenerationFailed;
        return new MovieVideoProviderException(code, transient);
    }

    private static MovieVideoProviderException Failure() => new(GenerationJobErrorCodes.MovieGenerationFailed, false);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static void EnsureTaskId(string? taskId)
    {
        if (!IsValidTaskId(taskId))
            throw Failure();
    }

    private static bool IsValidTaskId(string? taskId) =>
        !string.IsNullOrWhiteSpace(taskId)
        && taskId.Trim().Length <= 240
        && taskId.Trim().All(value => char.IsLetterOrDigit(value) || value is '-' or '_');

    private static bool IsSupportedAgentProfile(string? profile) =>
        !string.IsNullOrWhiteSpace(profile)
        && profile.Trim().ToLowerInvariant() is "standard" or "lite" or "max";

    private static string NormalizeAgentProfile(string? profile) =>
        string.IsNullOrWhiteSpace(profile) ? "standard" : profile.Trim().ToLowerInvariant() switch
        {
            "lite" => "lite",
            "max" => "max",
            _ => "standard",
        };

    private static bool TryGetHttpsUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps;

    private long MaxOutputBytes() => Math.Min(2L * 1024 * 1024 * 1024, Math.Max(1, settings.MaxOutputBytes));

    private static bool IsVideoContentType(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVideoContentType(string? contentType, Uri outputUri) =>
        IsVideoContentType(contentType) ? contentType!.Trim().ToLowerInvariant() : GuessContentType(outputUri);

    private static string GuessContentType(Uri uri) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        _ => throw new MovieVideoProviderOutputException(),
    };

    private static string NormalizeFileName(string? fileName, Uri outputUri)
    {
        var value = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(outputUri.AbsolutePath) : fileName;
        var name = Path.GetFileName(value?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            return "movie-clip.mp4";
        return name.Length > 255 ? name[..255] : name;
    }

    private static T Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Failure();
        }
    }

    private sealed record CreateTaskRequest(
        [property: JsonPropertyName("message")] ManusMessageRequest Message,
        [property: JsonPropertyName("interactive_mode")] bool InteractiveMode,
        [property: JsonPropertyName("hide_in_task_list")] bool HideInTaskList,
        [property: JsonPropertyName("share_visibility")] string ShareVisibility,
        [property: JsonPropertyName("agent_profile")] string AgentProfile,
        [property: JsonPropertyName("title")] string Title);

    private sealed record ManusMessageRequest([property: JsonPropertyName("content")] IReadOnlyList<object> Content);
    private sealed record TextContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("visibility")] string Visibility);
    private sealed record FileContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("file_url")] string? FileUrl,
        [property: JsonPropertyName("file_data")] string? FileData,
        [property: JsonPropertyName("filename")] string? FileName,
        [property: JsonPropertyName("mime_type")] string? MimeType,
        [property: JsonPropertyName("visibility")] string Visibility);
    private sealed record StopTaskRequest([property: JsonPropertyName("task_id")] string TaskId);

    private sealed record CreateTaskResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("task_id")] string? TaskId,
        [property: JsonPropertyName("request_id")] string? RequestId,
        [property: JsonPropertyName("error")] ManusApiError? Error);
    private sealed record TaskDetailResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("request_id")] string? RequestId,
        [property: JsonPropertyName("task")] ManusTask? Task,
        [property: JsonPropertyName("error")] ManusApiError? Error);
    private sealed record TaskMessagesResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("request_id")] string? RequestId,
        [property: JsonPropertyName("task_id")] string? TaskId,
        [property: JsonPropertyName("messages")] List<ManusMessage>? Messages,
        [property: JsonPropertyName("error")] ManusApiError? Error);
    private sealed record ManusApiError(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("message")] string? Message);
    private sealed record ManusTask(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("credit_usage")] long? CreditUsage,
        [property: JsonPropertyName("has_running_background_jobs")] bool? HasRunningBackgroundJobs);
    private sealed record ManusMessage(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("assistant_message")] ManusAssistantMessage? AssistantMessage);
    private sealed record ManusAssistantMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("attachments")] List<ManusAttachment>? Attachments);
    private sealed record ManusAttachment(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("filename")] string? FileName,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("content_type")] string? ContentType,
        [property: JsonPropertyName("size_bytes")] long? SizeBytes,
        [property: JsonPropertyName("duration_seconds")] int? DurationSeconds);
    private sealed record ManusVideoAttachment(string Url, string? ContentType, string? FileName, long? SizeBytes, int? DurationSeconds);
    private sealed record ManusOutputMetadata(
        [property: JsonPropertyName("providerTaskId")] string ProviderTaskId,
        [property: JsonPropertyName("outputUrl")] string OutputUrl,
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("fileName")] string? FileName,
        [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
        [property: JsonPropertyName("durationSeconds")] int? DurationSeconds);

    private sealed class ResponseOwnedStream(Stream inner, IDisposable response) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
