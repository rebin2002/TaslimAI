using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class StableAudioMusicGenerationProviderTests
{
    [Fact]
    public async Task Creates_polls_and_returns_valid_audio_with_safe_cost_metadata()
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-1\"}"),
            2 => new HttpResponseMessage(HttpStatusCode.Accepted),
            _ => Audio(HttpStatusCode.OK, ValidMp3(), "audio/mpeg"),
        });
        var provider = CreateProvider(handler);

        var result = await provider.GenerateAsync(Input());

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("mp3", result.Format);
        Assert.Equal(60, result.DurationSeconds);
        Assert.Equal(0.26m, result.Usage.ActualCostUsd);
        Assert.Equal(UsageCostBasis.Actual, result.Usage.CostBasis);
        Assert.Contains("generation-1", result.Usage.SafeMetadataJson);
        Assert.DoesNotContain("secret-api-key", result.Usage.SafeMetadataJson, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Bearer secret-api-key", handler.Requests[0].Headers.Authorization?.ToString());
        Assert.Contains("v2beta/audio/stable-audio/text-to-audio", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("v2beta/audio/results/generation-1", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("prompt", handler.RequestBodies[0]);
        Assert.Contains("Genre: cinematic", handler.RequestBodies[0]);
        Assert.Contains("duration", handler.RequestBodies[0]);
        Assert.Contains("output_format", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Maps_rejected_prompt_without_exposing_upstream_body()
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber == 1
            ? Json(HttpStatusCode.Accepted, "{\"id\":\"generation-rejected\"}")
            : Json(HttpStatusCode.UnprocessableEntity, "{\"message\":\"private moderation payload\",\"category\":\"safety\"}"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<MusicProviderRejectedException>(() => provider.GenerateAsync(Input()));

        Assert.DoesNotContain("private moderation payload", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-api-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retries_transient_poll_failure_once_and_completes()
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-retry\"}"),
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Audio(HttpStatusCode.OK, ValidMp3(), "audio/mpeg"),
        });
        var provider = CreateProvider(handler, retryAttempts: 1, retryDelayMilliseconds: 1);

        var result = await provider.GenerateAsync(Input());

        Assert.Equal("mp3", result.Format);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Bounded_polling_of_an_accepted_generation_stops_without_a_second_billable_submission()
    {
        // The text-to-audio POST was answered with a generation id, so Stability has accepted a paid
        // generation; bounded polling then ran out with every result still 202. Charging state is
        // unknown, so the retryable timeout class would instruct RetryAsync to re-run CreateAsync and
        // post a second generation. The job stops on the terminal retrieval class with one POST, and no
        // audio is fabricated.
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber == 1
            ? Json(HttpStatusCode.Accepted, "{\"id\":\"generation-timeout\"}")
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 1);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Timeout_while_an_accepted_generation_is_still_pending_is_terminal_not_a_retryable_timeout()
    {
        // The generation id exists and every result GET still answers 202 - Stability has accepted the
        // paid generation but its completion and charge are unconfirmed - when the provider's own bound
        // expires underneath the wait for the next poll. Accepted-but-unconfirmed is not the retryable
        // stage: a retry would pay for a second generation while the accepted one may still be running.
        // A 2xx result with a hanging body would be the post-billing stage instead, so this fixture
        // deliberately never answers 2xx.
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber == 1
            ? Json(HttpStatusCode.Accepted, "{\"id\":\"generation-pending-timeout\"}")
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var provider = CreateProvider(handler, maxPollAttempts: 5, pollIntervalMilliseconds: 30_000, providerTimeoutSeconds: 1);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Timeout_before_the_create_is_answered_stays_a_retryable_provider_timeout()
    {
        // The genuinely unsubmitted stage: the create POST never returns a body, so no generation id
        // exists and nothing can have been charged. This is the one case that keeps the retryable class.
        var handler = new StableAudioHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StreamContent(new HangingStream()),
        });
        var provider = CreateProvider(handler, providerTimeoutSeconds: 1);

        await Assert.ThrowsAsync<MusicProviderTimeoutException>(() => provider.GenerateAsync(Input()));
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Disabled_or_missing_configuration_makes_no_request()
    {
        var handler = new StableAudioHandler((_, _) => throw new InvalidOperationException("request should not be sent"));
        var disabled = CreateProvider(handler, enabled: false);
        await Assert.ThrowsAsync<MusicProviderUnavailableException>(() => disabled.GenerateAsync(Input()));
        Assert.Empty(handler.Requests);

        var missingKey = CreateProvider(handler, apiKey: "");
        await Assert.ThrowsAsync<MusicProviderUnavailableException>(() => missingKey.GenerateAsync(Input()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejects_malformed_provider_output_before_success()
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-invalid\"}"),
            _ => Audio(HttpStatusCode.OK, Encoding.UTF8.GetBytes("not audio"), "audio/mpeg"),
        });
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Download_stream_transport_failure_is_normalized_to_provider_unavailable()
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-transport\"}"),
            2 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultingStream()),
            },
            _ => throw new InvalidOperationException("unexpected request"),
        });
        // retryAttempts: 0 pins the re-fetch budget so this proves the immediate classification;
        // the recovery path itself is proven by the re-fetch tests below.
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 0);

        // The generation already exists and was billed, so even with a zero re-fetch budget this is
        // the post-billing retrieval class, not a plain provider outage that invites a new request.
        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Download_stream_io_failure_is_normalized_to_provider_unavailable()
    {
        // A socket reset arrives as IOException/SocketException rather than HttpRequestException.
        // Before the retry-classification lane, that type escaped the provider unclassified and
        // reached the worker as an unexpected fault.
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-io-fault\"}"),
            2 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new IoFaultingStream()),
            },
            _ => throw new InvalidOperationException("unexpected request"),
        });
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 0);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Faulted_result_body_is_re_fetched_with_a_get_without_a_second_billable_generation()
    {
        // The generation already exists and was billed once the result GET returned it, so the
        // recovery must be another result GET - never a second text-to-audio POST.
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-refetch\"}"),
            2 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultingStream()),
            },
            _ => Audio(HttpStatusCode.OK, ValidMp3(), "audio/mpeg"),
        });
        var provider = CreateProvider(handler, maxPollAttempts: 3, retryAttempts: 1, retryDelayMilliseconds: 1);

        var result = await provider.GenerateAsync(Input());

        Assert.Equal("mp3", result.Format);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("v2beta/audio/results/generation-refetch", handler.Requests[2].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persistent_result_transport_fault_exhausts_the_budget_without_a_second_generation()
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-refetch-exhaust\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultingStream()),
            },
        });
        var provider = CreateProvider(handler, maxPollAttempts: 6, retryAttempts: 2, retryDelayMilliseconds: 1);

        // Exhaustion happens after the generation was created and billed, so it is reported as an
        // isolated retrieval failure rather than the generic "please try again" provider outage:
        // a retry of the generic class would POST a second billable generation.
        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);

        // one create POST plus three result GETs (the original and two bounded re-fetches)
        Assert.Equal(4, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Caller_cancellation_stops_async_polling_without_becoming_timeout()
    {
        var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StableAudioHandler(async (_, requestNumber, cancellationToken) =>
        {
            if (requestNumber == 1) return Json(HttpStatusCode.Accepted, "{\"id\":\"generation-cancel\"}");
            secondRequest.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 1);
        using var cancellation = new CancellationTokenSource();

        var generation = provider.GenerateAsync(Input(), cancellation.Token);
        await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(MusicProviderAuthenticationException))]
    [InlineData(HttpStatusCode.PaymentRequired, typeof(MusicProviderQuotaException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(MusicProviderRateLimitException))]
    [InlineData(HttpStatusCode.BadRequest, typeof(MusicProviderInvalidRequestException))]
    public async Task Normalizes_common_provider_failures(HttpStatusCode status, Type exceptionType)
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber == 1
            ? new HttpResponseMessage(status) { Content = new StringContent("private upstream response") }
            : throw new InvalidOperationException("unexpected request"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.GenerateAsync(Input()));

        Assert.Equal(exceptionType, exception.GetType());
        Assert.DoesNotContain("private upstream response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_timeout_while_reading_a_billed_result_is_a_retrieval_failure_not_a_retryable_timeout()
    {
        // The result endpoint answered 2xx, so the generation exists and Stability billed it (the
        // success metadata says as much). The internal provider timeout firing underneath the body
        // read used to surface as MusicProviderTimeoutException, which the job layer still retries:
        // a retry would create a second paid generation and orphan the finished one. Post-billing it
        // is the terminal retrieval class, which MusicPostBillingRetrievalIsolationTests proves
        // blocks the retry. The pre-completion timeout keeps its retryable class, as the polling
        // bound test above shows.
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.Accepted, "{\"id\":\"generation-billed-timeout\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream()),
            },
        });
        var provider = CreateProvider(handler, maxPollAttempts: 3, providerTimeoutSeconds: 1);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    private static StableAudioMusicGenerationProvider CreateProvider(
        HttpMessageHandler handler,
        bool enabled = true,
        string apiKey = "secret-api-key",
        int maxPollAttempts = 3,
        int pollIntervalMilliseconds = 1,
        int retryAttempts = 2,
        int retryDelayMilliseconds = 1,
        int providerTimeoutSeconds = 15) => new(
            new HttpClient(handler),
            Options.Create(new MusicGenerationOptions
            {
                Enabled = enabled,
                ProviderKey = "stable-audio",
                Model = "stable-audio-3",
                ProviderTimeoutSeconds = providerTimeoutSeconds,
                MaxPromptCharacters = 4000,
                MaxOutputBytes = 1024,
                StableAudioApiBaseUrl = "https://api.stability.ai/",
                StableAudioApiKey = apiKey,
                StableAudioModel = "stable-audio-3",
                StableAudioOutputFormat = "mp3",
                StableAudioMaxPollAttempts = maxPollAttempts,
                StableAudioPollIntervalMilliseconds = pollIntervalMilliseconds,
                StableAudioMaxRetryAttempts = retryAttempts,
                StableAudioRetryBaseDelayMilliseconds = retryDelayMilliseconds,
                StableAudioCreditsPerGeneration = 26,
                StableAudioUsdPerCredit = 0.01m,
            }),
            NullLogger<StableAudioMusicGenerationProvider>.Instance);

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
        Guid.NewGuid());

    private static byte[] ValidMp3() =>
        [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x64];

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Audio(HttpStatusCode status, byte[] body, string contentType) => new(status)
    {
        Content = new ByteArrayContent(body)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(contentType) },
        },
    };

    private sealed class StableAudioHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder;
        private int requestCount;

        public StableAudioHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : this((request, number, _) => Task.FromResult(responder(request, number))) { }

        public StableAudioHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder) => this.responder = responder;

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return await responder(request, Interlocked.Increment(ref requestCount), cancellationToken);
        }
    }

    private sealed class FaultingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new HttpRequestException("synthetic stream failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new HttpRequestException("synthetic stream failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A body that never delivers: reads park until the caller's token fires, which is how the
    /// provider's own timeout is made to expire underneath a billed result read.
    /// </summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new OperationCanceledException("synthetic body read timeout");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class IoFaultingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("synthetic stream io failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic stream io failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
