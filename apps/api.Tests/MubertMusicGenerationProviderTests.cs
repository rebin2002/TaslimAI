using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Files;
using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MubertMusicGenerationProviderTests
{
    [Fact]
    public async Task Creates_polls_and_downloads_a_track_using_documented_contract()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-1\",\"session_id\":\"session-1\"}}"),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"pending\",\"url\":null}]}}"),
                3 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Signature=test\"}]}}"),
                _ => Binary(HttpStatusCode.OK, "ID3-test-audio", "audio/mpeg"),
            });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 100);

        var result = await provider.GenerateAsync(Input());

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("mp3", result.Format);
        Assert.Equal("ID3-test-audio", Encoding.UTF8.GetString(result.Content.Span));
        Assert.Equal(60, result.DurationSeconds);
        Assert.Contains("providerOperationId", result.Usage.SafeMetadataJson);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("customer", handler.Requests[0].Headers.GetValues("customer-id").Single());
        Assert.Equal("token", handler.Requests[0].Headers.GetValues("access-token").Single());
        var requestJson = handler.RequestBodies[0];
        Assert.Contains("\"duration\":60", requestJson);
        Assert.Contains("\"format\":\"mp3\"", requestJson);
        Assert.Contains("Description", requestJson);
    }

    [Fact]
    public async Task Retries_transient_provider_response_with_a_bounded_attempt_count()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-2\",\"session_id\":\"session-2\"}}"),
                3 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"),
                _ => Binary(HttpStatusCode.OK, "ID3-audio", "audio/mpeg"),
            });
        var provider = CreateProvider(handler, retryAttempts: 1, retryDelayMilliseconds: 25);

        var result = await provider.GenerateAsync(Input());

        Assert.Equal("mp3", result.Format);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Missing_credentials_are_unavailable_without_making_a_request()
    {
        var handler = new MubertHandler((_, _) => throw new InvalidOperationException("request should not be sent"));
        var provider = CreateProvider(handler, customerId: "", accessToken: "");

        await Assert.ThrowsAsync<MusicProviderUnavailableException>(() => provider.GenerateAsync(Input()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Non_transient_provider_failure_is_mapped_without_exposing_provider_payload()
    {
        var handler = new MubertHandler((_, _) => Json(HttpStatusCode.BadRequest, "{\"error\":\"private provider payload\"}"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<MusicProviderFailureException>(() => provider.GenerateAsync(Input()));

        Assert.DoesNotContain("private provider payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Polling_is_bounded_and_times_out_without_fabricating_audio()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-3\",\"session_id\":\"session-3\"}}"),
                _ => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"processing\",\"url\":null}]}}"),
            });
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 100);

        await Assert.ThrowsAsync<MusicProviderTimeoutException>(() => provider.GenerateAsync(Input()));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_stops_polling_and_is_not_converted_to_timeout()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber == 1
                ? Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-4\",\"session_id\":\"session-4\"}}")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new BlockingStream()),
                });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 100);
        using var cancellation = new CancellationTokenSource();
        var generation = provider.GenerateAsync(Input(), cancellation.Token);
        await handler.WaitForRequestAsync(2);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
    }

    [Fact]
    public async Task Oversized_download_is_rejected_before_a_music_result_is_returned()
    {
        var handler = CompleteTrackHandler(_ => Binary(HttpStatusCode.OK, "ID3-too-large", "audio/mpeg"));
        var provider = CreateProvider(handler, maxOutputBytes: 8);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Html_body_with_an_audio_mime_type_is_rejected()
    {
        var handler = CompleteTrackHandler(_ => Binary(HttpStatusCode.OK, "<html>provider error</html>", "audio/mpeg"));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Mismatched_download_mime_type_is_rejected()
    {
        var handler = CompleteTrackHandler(_ => Binary(HttpStatusCode.OK, "ID3-audio", "audio/wav"));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Truncated_download_is_rejected_when_content_length_is_declared()
    {
        var handler = CompleteTrackHandler(_ => BinaryWithDeclaredLength(HttpStatusCode.OK, "ID3", "audio/mpeg", 10));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Redirect_to_private_ip_is_rejected_before_following_the_redirect()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-private-redirect\",\"session_id\":\"session\"}}"),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://1.1.1.1/track.mp3\"}]}}"),
                3 => Redirect(HttpStatusCode.Found, "https://127.0.0.1/metadata"),
                _ => throw new InvalidOperationException("private redirect must not be requested"),
            });
        var provider = CreateProvider(handler, urlPolicy: new ProviderUrlPolicy());

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Hanging_download_is_cancelled_by_the_provider_timeout()
    {
        var handler = CompleteTrackHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream()),
        });
        var provider = CreateProvider(handler, providerTimeoutSeconds: 1);

        await Assert.ThrowsAsync<MusicProviderTimeoutException>(() => provider.GenerateAsync(Input()));
    }

    private static MubertMusicGenerationProvider CreateProvider(
        HttpMessageHandler handler,
        string customerId = "customer",
        string accessToken = "token",
        int maxPollAttempts = 3,
        int pollIntervalMilliseconds = 1,
        int retryAttempts = 2,
        int retryDelayMilliseconds = 1,
        int maxOutputBytes = 1024,
        int providerTimeoutSeconds = 15,
        IProviderUrlPolicy? urlPolicy = null) => new(
            new HttpClient(handler),
            Options.Create(new MusicGenerationOptions
            {
                Enabled = true,
                ProviderKey = "mubert",
                Model = "mubert-text-to-music",
                ProviderTimeoutSeconds = providerTimeoutSeconds,
                MaxPromptCharacters = 255,
                MaxOutputBytes = maxOutputBytes,
                MubertApiBaseUrl = "https://music-api.mubert.com/api/v3/public/",
                MubertCustomerId = customerId,
                MubertAccessToken = accessToken,
                MubertMaxPollAttempts = maxPollAttempts,
                MubertPollIntervalMilliseconds = pollIntervalMilliseconds,
                MubertMaxRetryAttempts = retryAttempts,
                MubertRetryBaseDelayMilliseconds = retryDelayMilliseconds,
            }),
            NullLogger<MubertMusicGenerationProvider>.Instance,
            urlPolicy ?? new MockedProviderUrlPolicy());

    private static MusicGenerationInput Input() => new(
        "Warm piano and soft strings",
        "Background music for a product launch video",
        "cinematic",
        "calm",
        60,
        "instrumental",
        "en",
        "Launch bed",
        "Keep the arrangement gentle",
        null);

    private sealed class MubertHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> RequestBodies { get; } = [];
        private int requestCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var count = Interlocked.Increment(ref requestCount);
            return responder(request, count);
        }

        public async Task WaitForRequestAsync(int expected)
        {
            while (Requests.Count < expected) await Task.Delay(1);
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitAsync(cancellationToken));

        private static async Task<int> WaitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Binary(HttpStatusCode status, string body, string contentType) => new(status)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) },
        },
    };

    private static MubertHandler CompleteTrackHandler(Func<int, HttpResponseMessage> download) => new(
        (_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-fixture\",\"session_id\":\"session\"}}"),
            2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"),
            _ => download(requestNumber),
        });

    private static HttpResponseMessage BinaryWithDeclaredLength(HttpStatusCode status, string body, string contentType, long length)
    {
        var response = Binary(status, body, contentType);
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location) => new(status)
    {
        Headers = { Location = new Uri(location) },
    };

    private sealed class MockedProviderUrlPolicy : IProviderUrlPolicy
    {
        public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException();
            return Task.CompletedTask;
        }
    }
}
