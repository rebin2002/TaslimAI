using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Documents;
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
        Assert.True(JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("stream").GetBoolean());
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

    [Fact]
    public async Task Adapter_serializes_strict_json_schema_and_uses_non_streaming_completion()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"title\\\":\\\"Draft\\\"}\"}]}],\"usage\":{\"input_tokens\":12,\"output_tokens\":8}}"),
        });
        using var client = new HttpClient(handler);
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"]}").RootElement.Clone();
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "draft")], "instruction", "Smart", EnableStreaming: false, MaxOutputTokens: 123, StructuredOutput: new AiStructuredOutputSpec("draft", schema, "A draft")), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);

        Assert.Equal(2, events.Count);
        Assert.Equal("{\"title\":\"Draft\"}", ((AiMessageDelta)events[0]).Delta);
        var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(123, payload.RootElement.GetProperty("max_output_tokens").GetInt32());
        var format = payload.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("draft", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request", AiProviderFailureCategories.UnsupportedRequest)]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_api_key", AiProviderFailureCategories.Configuration)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", AiProviderFailureCategories.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, "upstream_error", AiProviderFailureCategories.Transient)]
    public async Task Adapter_classifies_provider_http_failures_without_exposing_response_text(HttpStatusCode status, string code, string category)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent($"{{\"error\":{{\"type\":\"{code}\",\"message\":\"private provider details\"}}}}"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var exception = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new AiChatRequest([], "instruction", "Smart", EnableStreaming: false, StructuredOutput: DocumentDraftStructuredOutput.Spec), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) { }
        });

        Assert.Equal((int)status, exception.HttpStatusCode);
        Assert.Equal(code, exception.ProviderErrorCode);
        Assert.Equal(category, exception.FailureCategory);
        Assert.True(exception.StructuredOutputRequested);
        Assert.False(exception.StreamingRequested);
        Assert.DoesNotContain("private provider details", exception.Message);
    }

    [Fact]
    public async Task Adapter_maps_transport_cancellation_to_safe_timeout()
    {
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        await Assert.ThrowsAsync<AiProviderTimeoutException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new AiChatRequest([], "instruction", "Smart", EnableStreaming: false), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) { }
        });
    }

    [Fact]
    public async Task Adapter_rejects_malformed_non_stream_response_without_exposing_body()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var exception = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new AiChatRequest([], "instruction", "Smart", EnableStreaming: false), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) { }
        });

        Assert.Equal(AiProviderFailureCategories.MalformedResponse, exception.FailureCategory);
        Assert.Null(exception.HttpStatusCode);
        Assert.DoesNotContain("usage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
