using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
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
    public async Task Polling_is_bounded_and_times_out_without_fabricating_audio()
    {
        var handler = new StableAudioHandler((_, requestNumber) => requestNumber == 1
            ? Json(HttpStatusCode.Accepted, "{\"id\":\"generation-timeout\"}")
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 1);

        await Assert.ThrowsAsync<MusicProviderTimeoutException>(() => provider.GenerateAsync(Input()));
        Assert.Equal(3, handler.Requests.Count);
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

    private static StableAudioMusicGenerationProvider CreateProvider(
        HttpMessageHandler handler,
        bool enabled = true,
        string apiKey = "secret-api-key",
        int maxPollAttempts = 3,
        int pollIntervalMilliseconds = 1,
        int retryAttempts = 2,
        int retryDelayMilliseconds = 1) => new(
            new HttpClient(handler),
            Options.Create(new MusicGenerationOptions
            {
                Enabled = enabled,
                ProviderKey = "stable-audio",
                Model = "stable-audio-3",
                ProviderTimeoutSeconds = 15,
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
}
