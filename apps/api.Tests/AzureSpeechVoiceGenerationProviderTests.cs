using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AzureSpeechVoiceGenerationProviderTests
{
    [Fact]
    public async Task Successful_generation_uses_internal_voice_mapping_and_returns_validated_audio()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = AudioContent(CreateWav()),
        });
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            ResponseFormat = "wav",
            PricingUsdPerMillionCharacters = 10m,
            AzureSpeech = EnabledAzure(),
        });

        var result = await provider.GenerateAsync(Input(VoiceGenerationValues.English, VoiceGenerationValues.Expressive));

        Assert.Equal("https://eastus.tts.speech.microsoft.com/cognitiveservices/v1", handler.Request!.RequestUri!.ToString());
        Assert.Equal("test-key", handler.Request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        Assert.Equal("riff-24khz-16bit-mono-pcm", handler.Request.Headers.GetValues("X-Microsoft-OutputFormat").Single());
        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal("wav", result.Format);
        Assert.Equal(24_000, result.SampleRateHz);
        Assert.Equal(1000, result.DurationMilliseconds);
        Assert.Equal(Input().Text.Length, result.Usage.InputCharacters);
        Assert.Equal(decimal.Round(Input().Text.Length * 10m / 1_000_000m, 8, MidpointRounding.AwayFromZero), result.Usage.EstimatedCostUsd);
        Assert.DoesNotContain("test-key", result.Usage.SafeMetadataJson ?? string.Empty);
        Assert.Contains("en-US-JennyNeural", handler.RequestBody);
        Assert.Contains("style=\"cheerful\"", handler.RequestBody);
        Assert.Contains("&amp;", handler.RequestBody);
        Assert.DoesNotContain("Warm", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_configuration_returns_safe_unavailable_without_calling_provider()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("provider should not be called"));
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = new AzureSpeechOptions { Enabled = true, Region = "eastus" },
        });

        await Assert.ThrowsAsync<VoiceProviderUnavailableException>(() => provider.GenerateAsync(Input()));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Provider_disabled_returns_safe_unavailable_without_calling_provider()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("provider should not be called"));
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = false,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(),
        });

        await Assert.ThrowsAsync<VoiceProviderUnavailableException>(() => provider.GenerateAsync(Input()));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Timeout_is_normalized_without_exposing_upstream_details()
    {
        var handler = new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(new OperationCanceledException()));
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(maxRetryAttempts: 0),
        });

        var exception = await Assert.ThrowsAsync<VoiceProviderTimeoutException>(() => provider.GenerateAsync(Input()));
        Assert.DoesNotContain("Azure", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_is_normalized_without_exposing_upstream_body()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("private Azure quota response"),
        });
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(maxRetryAttempts: 0),
        });

        var exception = await Assert.ThrowsAsync<VoiceProviderRateLimitException>(() => provider.GenerateAsync(Input()));
        Assert.DoesNotContain("private Azure quota response", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transient_rate_limit_retries_safely_then_succeeds()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = AudioContent(CreateMp3()) };
        });
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(maxRetryAttempts: 1, retryBaseDelayMilliseconds: 25),
        });

        var result = await provider.GenerateAsync(Input());

        Assert.Equal(2, calls);
        Assert.Equal("mp3", result.Format);
    }

    [Fact]
    public async Task Malformed_audio_is_rejected_before_success()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = AudioContent([0x01, 0x02, 0x03]),
        });
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(),
        });

        await Assert.ThrowsAsync<VoiceOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_does_not_become_provider_error()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("provider should not be called"));
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(),
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Input(), cancellation.Token));
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(VoiceProviderAuthenticationException))]
    [InlineData(HttpStatusCode.BadRequest, typeof(VoiceProviderInvalidInputException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(VoiceProviderUnavailableException))]
    public async Task Azure_statuses_are_normalized_to_safe_exceptions(HttpStatusCode status, Type expectedType)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("private upstream error"),
        });
        var provider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(maxRetryAttempts: 0),
        });

        var exception = await Record.ExceptionAsync(() => provider.GenerateAsync(Input()));

        Assert.NotNull(exception);
        Assert.IsType(expectedType, exception);
        Assert.DoesNotContain("private upstream error", exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_language_and_format_fail_before_network_call()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("provider should not be called"));
        var languageProvider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            AzureSpeech = EnabledAzure(),
        });

        await Assert.ThrowsAsync<VoiceLanguageUnsupportedException>(() => languageProvider.GenerateAsync(Input("ku")));

        var formatProvider = CreateProvider(handler, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "azure-speech",
            ResponseFormat = "flac",
            AzureSpeech = EnabledAzure(),
        });
        await Assert.ThrowsAsync<VoiceProviderUnsupportedRequestException>(() => formatProvider.GenerateAsync(Input()));
        Assert.Null(handler.Request);
    }

    private static AzureSpeechVoiceGenerationProvider CreateProvider(StubHandler handler, VoiceGenerationOptions settings) =>
        new(new HttpClient(handler), Options.Create(settings), NullLogger<AzureSpeechVoiceGenerationProvider>.Instance);

    private static AzureSpeechOptions EnabledAzure(int maxRetryAttempts = 0, int retryBaseDelayMilliseconds = 250) => new()
    {
        Enabled = true,
        ApiKey = "test-key",
        Region = "eastus",
        ModelKey = "azure-test-model",
        MaxRetryAttempts = maxRetryAttempts,
        RetryBaseDelayMilliseconds = retryBaseDelayMilliseconds,
    };

    private static VoiceGenerationInput Input(string language = "en", string speakingStyle = "clear") => new(
        Guid.NewGuid(),
        null,
        "Hello & welcome to Taslim.",
        language,
        VoiceGenerationValues.Warm,
        speakingStyle,
        null,
        null);

    private static ByteArrayContent AudioContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(bytes.Length > 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ? "audio/wav" : "audio/mpeg");
        return content;
    }

    private static byte[] CreateMp3() => [0x49, 0x44, 0x33, 0x04, 0x00, 0x00];

    private static byte[] CreateWav()
    {
        const int sampleRate = 24_000;
        const int dataSize = sampleRate * 2;
        var bytes = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), dataSize);
        return bytes;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : this((request, _) => Task.FromResult(responseFactory(request))) { }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) => this.responseFactory = responseFactory;

        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responseFactory(request, cancellationToken);
        }
    }
}
