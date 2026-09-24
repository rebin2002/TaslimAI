using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class OpenAiVoiceGenerationProviderTests
{
    [Fact]
    public async Task Adapter_posts_provider_independent_voice_request_and_returns_private_audio_metadata()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("ID3mock-mp3"u8.ToArray()),
        });
        handler.ResponseContentType = "audio/mpeg";
        handler.ResponseRequestId = "req_voice_test";
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.GenerateAsync(Input("en"));

        Assert.Equal("openai", provider.Key);
        Assert.Equal("https://example.test/v1/audio/speech", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.Request.Headers.Authorization?.Parameter);
        Assert.NotNull(handler.RequestBody);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("gpt-4o-mini-tts", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("coral", payload.RootElement.GetProperty("voice").GetString());
        Assert.Equal(Input("en").Text, payload.RootElement.GetProperty("input").GetString());
        Assert.Contains("Speak in English.", payload.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("mp3", payload.RootElement.GetProperty("response_format").GetString());
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("mp3", result.Format);
        Assert.Equal(11, result.Content.Length);
        Assert.Equal(Input("en").Text.Length, result.Usage.InputCharacters);
        Assert.Equal(11, result.Usage.OutputBytes);
        Assert.Null(result.Usage.ActualCostUsd);
        Assert.Equal("Unreported", result.Usage.CostBasis);
        Assert.Contains("usageReportedByProvider", result.Usage.SafeMetadataJson);
    }

    [Fact]
    public async Task Adapter_rejects_kurdish_sorani_without_calling_provider()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("provider should not be called"));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<VoiceLanguageUnsupportedException>(() => provider.GenerateAsync(Input("ku")));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Adapter_maps_provider_failure_to_safe_exception_without_response_details()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("private provider payload"),
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var exception = await Assert.ThrowsAsync<VoiceProviderUnavailableException>(() => provider.GenerateAsync(Input("ar")));

        Assert.Equal("No configured voice provider is available.", exception.Message);
        Assert.DoesNotContain("private provider payload", exception.Message);
    }

    [Fact]
    public async Task Adapter_rejects_malformed_audio_and_preserves_caller_cancellation()
    {
        var malformed = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not audio"u8.ToArray()),
        });
        malformed.ResponseContentType = "audio/mpeg";
        using var malformedClient = new HttpClient(malformed);
        var provider = CreateProvider(malformedClient);
        await Assert.ThrowsAsync<VoiceOutputInvalidException>(() => provider.GenerateAsync(Input("en")));

        using var cancellation = new CancellationTokenSource();
        var cancelled = new StubHandler(_ => throw new OperationCanceledException(cancellation.Token));
        using var cancelledClient = new HttpClient(cancelled);
        var cancelledProvider = CreateProvider(cancelledClient);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledProvider.GenerateAsync(Input("en"), cancellation.Token));
    }

    private static OpenAiVoiceGenerationProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new AiOptions
        {
            OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" },
        }),
        Options.Create(new VoiceGenerationOptions
        {
            Enabled = true,
            ProviderKey = "openai",
            Model = "gpt-4o-mini-tts",
            MaxProviderTextCharacters = 8_000,
        }),
        NullLogger<OpenAiVoiceGenerationProvider>.Instance);

    private static VoiceGenerationInput Input(string language) => new(
        Guid.NewGuid(),
        null,
        language == "ar" ? "مرحبا بكم" : "Hello Taslim",
        language,
        VoiceGenerationValues.Warm,
        VoiceGenerationValues.Clear,
        "Speak naturally.",
        null);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public string? ResponseContentType { get; set; }
        public string? ResponseRequestId { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = responseFactory(request);
            if (!string.IsNullOrWhiteSpace(ResponseContentType)) response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ResponseContentType);
            if (!string.IsNullOrWhiteSpace(ResponseRequestId)) response.Headers.Add("x-request-id", ResponseRequestId);
            return response;
        }
    }
}
