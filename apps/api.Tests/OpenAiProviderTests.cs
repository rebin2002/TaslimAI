using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class OpenAiProviderTests
{
    [Fact]
    public async Task Adapter_converts_responses_api_events_to_provider_independent_events()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello \"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Taslim\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":100,\"input_tokens_details\":{\"cached_tokens\":20},\"output_tokens\":50}}}\n\n")
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "Hello")], "instruction", "Smart", true), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);

        Assert.Equal(3, events.Count);
        Assert.Equal("Hello ", ((AiMessageDelta)events[0]).Delta);
        Assert.Equal("Taslim", ((AiMessageDelta)events[1]).Delta);
        var completed = Assert.IsType<AiMessageCompleted>(events[2]);
        Assert.Equal(100, completed.Usage.InputTokens);
        Assert.Equal(20, completed.Usage.CachedInputTokens);
        Assert.Equal(50, completed.Usage.OutputTokens);
        Assert.Equal("https://example.test/v1/responses", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.Request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Adapter_returns_safe_failure_for_non_success_provider_response()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("secret provider payload") });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var exception = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new AiChatRequest([], "instruction", "Smart", true), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) { }
        });
        Assert.Equal("The configured AI provider failed.", exception.Message);
        Assert.DoesNotContain("secret provider payload", exception.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(responseFactory(request));
        }
    }
}
