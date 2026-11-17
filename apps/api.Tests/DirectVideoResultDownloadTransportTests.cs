using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free, deterministic contract for the result-download leg of the direct
/// video HTTP transport performed by <c>RetrieveAsync</c>: a HEAD probe at retrieval
/// time plus a lazily opened GET stream. The regressions pinned here are that a missing
/// or unremembered provider asset fails closed before any download request leaves the
/// process, that the media type is validated on both the HEAD probe and the GET body so
/// a non-video payload is never handed to storage, that a declared size that is absent,
/// zero, shrinks between probe and body, or exceeds the configured output budget is
/// refused when the stream is opened rather than after a partial persist, that a failed
/// body download keeps the safe provider-unavailable category, that caller cancellation
/// is not relabelled as a provider timeout, and that the returned stream owns its HTTP
/// response lease. Only an in-memory handler and synthetic payloads are used, so no
/// provider, credential, or paid call is reachable from these tests.
/// </summary>
public sealed class DirectVideoResultDownloadTransportTests
{
    private const string OutputUrl = "https://outputs.invalid/clip-1.mp4";
    private const int ClipLength = 12;
    private static readonly byte[] VideoPayload = [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D];

    [Fact]
    public async Task Ordinary_download_probes_the_asset_then_streams_the_declared_video_bytes()
    {
        var handler = new DownloadHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");
        var output = await adapter.RetrieveAsync("provider-job-1", status);

        Assert.Equal(DirectVideoJobStatus.Succeeded, status.Status);
        Assert.Equal("video/mp4", output.ContentType);
        Assert.Equal(ClipLength, output.SizeBytes);
        Assert.Equal("clip.mp4", output.FileName);
        // The body is a lease the caller opens, not a buffer the adapter pre-fetches.
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Head }, handler.Methods);

        await using var stream = await output.OpenReadAsync(CancellationToken.None);
        using var reader = new BinaryReader(stream);
        Assert.Equal(VideoPayload, reader.ReadBytes(ClipLength));
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Head, HttpMethod.Get }, handler.Methods);
        Assert.Equal(new Uri(OutputUrl), handler.RequestUris[2]);
        Assert.Equal(new Uri(OutputUrl), handler.RequestUris[3]);
    }

    [Fact]
    public async Task A_path_traversal_provider_file_name_is_sanitized_before_any_persistence_decision()
    {
        // The traversal prefix is resolved away before the name informs any decision,
        // and the remaining base name is never echoed with its path intact.
        var handler = new DownloadHandler(fileName: "../../etc/clip.mp4");
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");
        var output = await adapter.RetrieveAsync("provider-job-1", status);

        Assert.Equal("clip.mp4", output.FileName);
        Assert.DoesNotContain("..", output.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_traversal_base_name_the_storage_boundary_cannot_persist_is_rejected_before_the_download_lease()
    {
        // "../../etc/passwd" sanitises to the extensionless "passwd", which
        // GeneratedMediaSecurity.ValidateDescriptor could never persist. The output
        // upload preflight now names that at the adapter boundary from transport
        // metadata alone, instead of handing the artifact downstream to fail later
        // as a storage error with the wrong operator-facing code.
        var handler = new DownloadHandler(fileName: "../../etc/passwd");
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync("provider-job-1", status));
        // The HEAD probe ran, so the declaration was judged; the body GET lease is
        // never handed back for an unstorable output.
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Head }, handler.Methods);
    }

    [Fact]
    public async Task Retrieval_fails_closed_without_a_remembered_asset_before_any_download_request()
    {
        var handler = new DownloadHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        var submission = await adapter.SubmitAsync(Request());
        var succeeded = new DirectVideoStatus(DirectVideoJobStatus.Succeeded, 100);

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync(submission.ProviderJobId, succeeded));
        Assert.DoesNotContain(HttpMethod.Head, handler.Methods);
        Assert.DoesNotContain(HttpMethod.Get, handler.Methods);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://outputs.invalid/clip-1.mp4")]
    [InlineData("/relative/clip-1.mp4")]
    [InlineData("not-a-uri")]
    public async Task An_absent_or_non_https_provider_asset_is_never_remembered_or_downloaded(string? outputUrl)
    {
        var handler = new DownloadHandler(outputUrl: outputUrl);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync("provider-job-1", status));
        Assert.DoesNotContain(HttpMethod.Head, handler.Methods);
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get }, handler.Methods);
    }

    [Fact]
    public async Task Retrieval_fails_closed_for_a_non_terminal_provider_job()
    {
        var handler = new DownloadHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        await adapter.GetStatusAsync("provider-job-1");
        var running = new DirectVideoStatus(DirectVideoJobStatus.Running, 40);

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync("provider-job-1", running));
        Assert.DoesNotContain(HttpMethod.Head, handler.Methods);
    }

    [Fact]
    public async Task A_failed_head_probe_fails_closed_without_opening_the_download()
    {
        var handler = new DownloadHandler(headStatus: HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync("provider-job-1", status));
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Head }, handler.Methods);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task A_head_response_without_a_usable_declared_size_fails_closed(long? contentLength)
    {
        var handler = new DownloadHandler(headContentLength: contentLength);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync("provider-job-1", status));
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Head }, handler.Methods);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("audio/mpeg")]
    [InlineData(null)]
    public async Task A_non_video_head_media_type_fails_closed_before_the_body_lease(string? headMediaType)
    {
        var handler = new DownloadHandler(headMediaType: headMediaType);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync("provider-job-1", status));
        Assert.Contains(HttpMethod.Head, handler.Methods);
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Head }, handler.Methods);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("audio/mpeg")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task A_body_whose_media_type_disagrees_with_the_probe_is_refused_at_open_time(string? getMediaType)
    {
        var handler = new DownloadHandler(getMediaType: getMediaType);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");
        var output = await adapter.RetrieveAsync("provider-job-1", status);

        // The probe passed, so the adapter hands back a lease; the disagreement must be
        // caught when the lease is opened, before storage can consume a partial file.
        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => output.OpenReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_body_that_shrinks_below_the_probed_size_fails_closed_at_open_time()
    {
        var handler = new DownloadHandler(getContentLength: 4);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");
        var output = await adapter.RetrieveAsync("provider-job-1", status);

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => output.OpenReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_asset_beyond_the_configured_output_budget_is_rejected_without_a_body_request()
    {
        var handler = new DownloadHandler(headContentLength: 4_096);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client, maxOutputBytes: 2_048);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");

        await Assert.ThrowsAsync<DirectVideoOutputNormalizationException>(
            () => adapter.RetrieveAsync("provider-job-1", status));
        Assert.Equal(new HttpMethod[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Head }, handler.Methods);
    }

    [Fact]
    public async Task A_failed_body_download_maps_to_the_safe_provider_unavailable_category()
    {
        var handler = new DownloadHandler(getStatus: HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");
        var output = await adapter.RetrieveAsync("provider-job-1", status);

        var exception = await Assert.ThrowsAsync<DirectVideoProviderException>(
            () => output.OpenReadAsync(CancellationToken.None));

        Assert.Equal(DirectVideoErrorCategory.Unavailable, exception.Category);
        Assert.Equal(DirectVideoErrorCodes.ProviderUnavailable, exception.Code);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_download_is_not_relabelled_as_a_provider_timeout()
    {
        var handler = new DownloadHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);
        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");
        var output = await adapter.RetrieveAsync("provider-job-1", status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => output.OpenReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task Disposing_the_artifact_stream_releases_the_body_lease()
    {
        var handler = new DownloadHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client);

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");
        var output = await adapter.RetrieveAsync("provider-job-1", status);

        var stream = await output.OpenReadAsync(CancellationToken.None);
        Assert.False(handler.MarkedStream.IsDisposed);
        stream.Dispose();
        // Disposing the artifact stream must release the underlying body so the
        // connection is not leaked; the response itself may cascade a second dispose.
        Assert.True(handler.MarkedStream.IsDisposed);
        Assert.True(handler.MarkedStream.DisposeCount >= 1);
    }

    private static DirectVideoHttpProviderAdapter CreateAdapter(HttpClient client, long maxOutputBytes = 1_048_576) => new(
        client,
        Options.Create(new DirectVideoProviderOptions
        {
            Enabled = true,
            ProviderKey = "synthetic-provider",
            ModelKey = "synthetic-model",
            ApiBaseUrl = "https://provider.invalid/api/",
            ApiKey = "synthetic-key",
            MaxOutputBytes = maxOutputBytes,
        }),
        new AllowAllUrlPolicy(),
        NullLogger<DirectVideoHttpProviderAdapter>.Instance);

    private static DirectVideoRequest Request() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        MovieStudioOperations.SceneClip,
        "A provider-neutral synthetic shot.",
        5,
        "16:9",
        DirectVideoResolutions.Hd720,
        false,
        null,
        null,
        null);

    private sealed class AllowAllUrlPolicy : IProviderUrlPolicy
    {
        public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DisposalMarker
    {
        public bool IsDisposed => DisposeCount > 0;
        public int DisposeCount { get; private set; }

        public void MarkDisposed() => DisposeCount++;
    }

    private sealed class MarkedStream(Stream inner, DisposalMarker marker) : Stream
    {
        public bool IsDisposed => marker.IsDisposed;
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
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                marker.MarkDisposed();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class DownloadHandler(
        HttpStatusCode headStatus = HttpStatusCode.OK,
        string? headMediaType = "video/mp4",
        long? headContentLength = ClipLength,
        HttpStatusCode getStatus = HttpStatusCode.OK,
        string? getMediaType = "video/mp4",
        long? getContentLength = ClipLength,
        string? outputUrl = OutputUrl,
        string? fileName = "clip.mp4") : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public List<Uri> RequestUris { get; } = [];
        public DisposalMarker MarkedStream { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            RequestUris.Add(request.RequestUri!);
            AssertBearer(request);
            await Task.Delay(1, cancellationToken);
            if (request.Method == HttpMethod.Post) return Json(new { id = "provider-job-1" });
            if (request.Method == HttpMethod.Head) return Head();
            if (request.RequestUri!.AbsolutePath.Contains("video/generations"))
            {
                return Json(new
                {
                    id = "provider-job-1",
                    status = "succeeded",
                    progressPercent = 100,
                    outputUrl,
                    fileName,
                    durationSeconds = 5,
                });
            }

            return Body();
        }

        private HttpResponseMessage Head()
        {
            var response = new HttpResponseMessage(headStatus);
            if (headStatus == HttpStatusCode.OK)
            {
                response.Content = new StringContent(string.Empty, Encoding.UTF8);
                SetMediaType(response.Content.Headers, headMediaType);
                if (headContentLength is { } length)
                {
                    response.Content.Headers.ContentLength = length;
                }
                else
                {
                    response.Content.Headers.Remove("Content-Length");
                }
            }

            return response;
        }

        private HttpResponseMessage Body()
        {
            if (getStatus != HttpStatusCode.OK) return new HttpResponseMessage(getStatus);
            var response = new HttpResponseMessage(getStatus);
            var content = new StreamContent(new MarkedStream(new MemoryStream(VideoPayload), MarkedStream));
            SetMediaType(content.Headers, getMediaType);
            content.Headers.ContentLength = getContentLength;
            response.Content = content;
            return response;
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        /// <summary>
        /// Applies the requested media type, or removes the header entirely when the
        /// scenario has none. Values that are not syntactically valid media types
        /// cannot be attached to a response at all, so they also arrive as "no header",
        /// which the adapter must still fail closed on.
        /// </summary>
        private static void SetMediaType(HttpContentHeaders headers, string? mediaType)
        {
            if (mediaType is not null && MediaTypeHeaderValue.TryParse(mediaType, out var parsed))
            {
                headers.ContentType = parsed;
                return;
            }

            headers.Remove("Content-Type");
        }

        private static void AssertBearer(HttpRequestMessage request)
        {
            var hasBearer = request.Headers.Authorization is { Scheme: "Bearer", Parameter: "synthetic-key" };
            var isProviderRequest = request.RequestUri!.Host == "provider.invalid";
            if (isProviderRequest)
            {
                Assert.True(hasBearer, "Provider control-plane requests must carry the configured bearer credential.");
            }
            else
            {
                Assert.False(hasBearer, "Asset downloads must not leak the control-plane credential to the output host.");
            }
        }
    }
}
