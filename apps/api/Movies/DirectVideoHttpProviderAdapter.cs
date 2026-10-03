using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Files;

namespace Taslim.Api.Movies;

/// <summary>
/// Concrete REST adapter for a configured direct-video provider. Vendor-specific
/// schemas stop at this boundary: the rest of Movie Studio only sees the
/// provider-neutral direct-video contracts and normalized error codes.
/// </summary>
public sealed class DirectVideoHttpProviderAdapter(
    HttpClient httpClient,
    IOptions<DirectVideoProviderOptions> options,
    IProviderUrlPolicy urlPolicy,
    ILogger<DirectVideoHttpProviderAdapter> logger) : IDirectVideoProviderAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly DirectVideoCapabilityDeclaration DeclaredCapabilities = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DirectVideoOperations.TextToVideo,
            DirectVideoOperations.ImageToVideo,
            DirectVideoOperations.VideoToVideo,
            DirectVideoOperations.Upscale,
            MovieStudioOperations.QuickMovie,
            MovieStudioOperations.SceneClip,
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DirectVideoResolutions.Hd720,
            DirectVideoResolutions.FullHd,
            DirectVideoResolutions.Uhd4k,
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9", "9:16", "1:1" },
        1,
        60,
        SupportsReferenceImage: true,
        SupportsContinuation: true,
        SupportsUpscaling: true,
        SupportsNativeAudio: false);

    private readonly DirectVideoProviderOptions settings = options.Value;
    private readonly ConcurrentDictionary<string, Uri> outputUris = new(StringComparer.Ordinal);
    private DirectVideoProviderConfiguration Configuration => settings.ToConfiguration();

    public string Key => string.IsNullOrWhiteSpace(settings.ProviderKey) ? "direct" : settings.ProviderKey.Trim();
    public bool IsAvailable => Configuration.IsConfigured;
    public DirectVideoCapabilityDeclaration Capabilities => DeclaredCapabilities;
    public IDirectVideoProviderHealthHook Health => new DirectVideoProviderHealthHook(Configuration);

    public async Task<DirectVideoSubmission> SubmitAsync(DirectVideoRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var payload = new
        {
            model = settings.ModelKey.Trim(),
            operation = request.Operation,
            prompt = request.PromptText,
            durationSeconds = request.DurationSeconds,
            aspectRatio = request.AspectRatio,
            resolution = request.Resolution,
            upscaleRequested = request.UpscaleRequested,
            sourceImageUri = request.SourceImageUri,
            continuationProviderJobId = request.ContinuationProviderJobId,
            continuityContextJson = request.ContinuityContextJson,
        };
        var body = await SendJsonAsync(HttpMethod.Post, settings.SubmitPath, payload, cancellationToken);
        var providerJobId = ReadString(body, "id", "jobId", "job_id", "taskId", "task_id", "providerJobId");
        if (string.IsNullOrWhiteSpace(providerJobId) || providerJobId.Length > 240)
            throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure);
        return new DirectVideoSubmission(providerJobId);
    }

    public async Task<DirectVideoStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var normalizedId = NormalizeJobId(providerJobId);
        var body = await SendJsonAsync(HttpMethod.Get, settings.StatusPathTemplate.Replace("{id}", Uri.EscapeDataString(normalizedId), StringComparison.Ordinal), null, cancellationToken);
        RememberOutputUri(normalizedId, body);
        return DirectVideoResultNormalizer.NormalizeStatus(JsonSerializer.Deserialize<DirectVideoProviderStatusResponse>(body, JsonOptions)
            ?? throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure));
    }

    public async Task<DirectVideoProviderOutput> RetrieveAsync(string providerJobId, DirectVideoStatus status, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (status.Status != DirectVideoJobStatus.Succeeded || !outputUris.TryGetValue(NormalizeJobId(providerJobId), out var outputUri))
            throw new DirectVideoOutputNormalizationException();
        try { await urlPolicy.EnsureSafeAsync(outputUri, cancellationToken); }
        catch (InvalidDataException) { throw new DirectVideoOutputNormalizationException(); }

        using var head = new HttpRequestMessage(HttpMethod.Head, outputUri);
        using var headResponse = await SendProviderRequestAsync(head, cancellationToken);
        if (!headResponse.IsSuccessStatusCode) throw new DirectVideoOutputNormalizationException();
        var contentType = headResponse.Content.Headers.ContentType?.MediaType;
        var sizeBytes = headResponse.Content.Headers.ContentLength.GetValueOrDefault();
        if (sizeBytes <= 0 || sizeBytes > Configuration.MaxOutputBytes || string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            throw new DirectVideoOutputNormalizationException();

        async Task<Stream> OpenReadAsync(CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, outputUri);
            HttpResponseMessage response;
            try { response = await SendProviderRequestAsync(request, token); }
            catch { request.Dispose(); throw; }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new DirectVideoProviderException(DirectVideoErrorCategory.Unavailable, DirectVideoErrorCodes.ProviderUnavailable);
            }
            var responseType = response.Content.Headers.ContentType?.MediaType;
            var responseLength = response.Content.Headers.ContentLength;
            if (responseLength != sizeBytes || responseLength <= 0 || responseLength > Configuration.MaxOutputBytes || string.IsNullOrWhiteSpace(responseType) || !responseType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                throw new DirectVideoOutputNormalizationException();
            }
            return new ResponseOwnedStream(await response.Content.ReadAsStreamAsync(token), response);
        }

        return DirectVideoResultNormalizer.NormalizeOutput(
            contentType,
            status.FileName ?? $"{NormalizeJobId(providerJobId)}.mp4",
            sizeBytes,
            OpenReadAsync,
            status.DurationSeconds,
            status.EstimatedCostUsd,
            status.ActualCostUsd,
            status.Currency,
            status.CostBasis,
            status.SafeMetadataJson,
            settings.ModelKey,
            Configuration.MaxOutputBytes);
    }

    public async Task CancelAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return;
        try
        {
            var path = settings.CancelPathTemplate.Replace("{id}", Uri.EscapeDataString(NormalizeJobId(providerJobId)), StringComparison.Ordinal);
            using var request = CreateRequest(HttpMethod.Post, path, null);
            using var response = await SendProviderRequestAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                logger.LogWarning("Direct video cancellation returned a non-success status. StatusCode={StatusCode}", (int)response.StatusCode);
        }
        catch (DirectVideoProviderException) { }
    }

    private async Task<string> SendJsonAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        var attempts = Math.Clamp(settings.MaxTransientRetries, 0, 8) + 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var request = CreateRequest(method, path, payload);
            using var response = await SendProviderRequestAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(cancellationToken);
            if (attempt == attempts || !IsRetryable(response.StatusCode))
                throw DirectVideoErrorNormalizer.ToException(response.StatusCode);
        }
        throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure);
    }

    private async Task<HttpResponseMessage> SendProviderRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Configuration.RequestTimeoutSeconds));
        try { return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DirectVideoProviderException(DirectVideoErrorCategory.TimedOut, DirectVideoErrorCodes.ProviderTimeout); }
        catch (HttpRequestException)
        { throw new DirectVideoProviderException(DirectVideoErrorCategory.Unavailable, DirectVideoErrorCodes.ProviderUnavailable); }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? payload)
    {
        EnsureAvailable();
        var request = new HttpRequestMessage(method, BuildUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private Uri BuildUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Uri.TryCreate(path.Trim(), UriKind.Absolute, out _))
            throw new DirectVideoProviderException(DirectVideoErrorCategory.InvalidRequest, DirectVideoErrorCodes.RequestInvalid);
        return new Uri(Configuration.ApiBaseUri!, path.Trim().TrimStart('/'));
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable) throw new DirectVideoProviderException(DirectVideoErrorCategory.Unavailable, DirectVideoErrorCodes.ProviderUnavailable);
    }

    private void RememberOutputUri(string providerJobId, string body)
    {
        var output = ReadString(body, "outputUrl", "output_url", "videoUrl", "video_url", "url");
        if (Uri.TryCreate(output, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            outputUris[providerJobId] = uri;
    }

    private static string? ReadString(string body, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var name in names)
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static string NormalizeJobId(string providerJobId)
    {
        var normalized = providerJobId?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 240 || normalized.Any(char.IsControl))
            throw new DirectVideoProviderException(DirectVideoErrorCategory.InvalidRequest, DirectVideoErrorCodes.RequestInvalid);
        return normalized;
    }

    private static bool IsRetryable(HttpStatusCode statusCode) => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private sealed class ResponseOwnedStream(Stream inner, IDisposable response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
