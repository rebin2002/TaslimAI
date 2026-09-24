using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class OpenAiVoiceGenerationProviderTests
{
    [Fact]
    public async Task Adapter_posts_private_request_and_returns_audio_with_usage_metadata()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x49, 0x44, 0x33, 0x01, 0x02]),
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "openai",
            Model = "gpt-4o-mini-tts",
            PricingUsdPerMillionCharacters = 15m,
            PricingVersion = "voice-test-pricing-v1",
        });

        var result = await provider.GenerateAsync(Input());
        using var body = JsonDocument.Parse(handler.RequestBody!);

        Assert.Equal("https://example.test/v1/audio/speech", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.Request.Headers.Authorization?.Parameter);
        Assert.Equal("gpt-4o-mini-tts", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("coral", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal("mp3", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(Input().Text, body.RootElement.GetProperty("input").GetString());
        Assert.Contains("Arabic", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("mp3", result.Format);
        Assert.Equal(5, result.Content.Length);
        Assert.Equal(Input().Text.Length, result.Usage.InputCharacters);
        Assert.Equal(5, result.Usage.OutputBytes);
        Assert.Equal(decimal.Round(Input().Text.Length * 15m / 1_000_000m, 8, MidpointRounding.AwayFromZero), result.Usage.EstimatedCostUsd);
        Assert.Equal(UsageCostBasis.Estimated, result.Usage.CostBasis);
        Assert.Null(result.Usage.ActualCostUsd);
        Assert.DoesNotContain("test-key", result.Usage.SafeMetadataJson ?? string.Empty);
    }

    [Fact]
    public async Task Adapter_rejects_kurdish_by_default_without_calling_provider()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("provider should not be called"));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "openai",
            Model = "gpt-4o-mini-tts",
        });

        await Assert.ThrowsAsync<VoiceLanguageUnsupportedException>(() => provider.GenerateAsync(Input("ku")));
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Adapter_maps_provider_unavailability_to_safe_exception(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(new { error = new { message = "private provider response" } }),
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, new VoiceGenerationOptions { Enabled = true, ProviderKey = "openai", Model = "gpt-4o-mini-tts" });

        var exception = await Assert.ThrowsAsync<VoiceProviderUnavailableException>(() => provider.GenerateAsync(Input()));
        Assert.Equal("No configured voice provider is available.", exception.Message);
        Assert.DoesNotContain("private provider response", exception.Message);
    }

    [Fact]
    public async Task Adapter_preserves_caller_cancellation()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, new VoiceGenerationOptions { Enabled = true, ProviderKey = "openai", Model = "gpt-4o-mini-tts" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Input(), cancellation.Token));
    }

    private static OpenAiVoiceGenerationProvider CreateProvider(HttpClient client, VoiceGenerationOptions settings) =>
        new(client,
            Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }),
            Options.Create(settings),
            NullLogger<OpenAiVoiceGenerationProvider>.Instance);

    private static VoiceGenerationInput Input(string language = "ar") => new(
        Guid.NewGuid(),
        null,
        "مرحبا بكم في تسليم",
        language,
        VoiceGenerationValues.Warm,
        VoiceGenerationValues.Clear,
        "Speak clearly.",
        null);

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
