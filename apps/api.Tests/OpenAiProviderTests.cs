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
    public async Task Adapter_flushes_a_terminal_event_at_eof_without_a_blank_delimiter()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}}")
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "Hello")], "instruction", "Smart", true), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);

        Assert.Equal(2, events.Count);
        Assert.Equal("Hello", ((AiMessageDelta)events[0]).Delta);
        Assert.IsType<AiMessageCompleted>(events[1]);
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

    [Fact]
    public async Task Adapter_completes_a_stream_as_truncated_when_the_provider_emits_response_incomplete()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Partial \"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}\n\ndata: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"usage\":{\"input_tokens\":80,\"input_tokens_details\":{\"cached_tokens\":10},\"output_tokens\":2048}}}\n\ndata: [DONE]\n\n"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "long answer")], "instruction", "Smart", EnableStreaming: true, MaxOutputTokens: 2048), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);

        Assert.Equal(3, events.Count);
        Assert.Equal("Partial ", ((AiMessageDelta)events[0]).Delta);
        Assert.Equal("answer", ((AiMessageDelta)events[1]).Delta);
        var completed = Assert.IsType<AiMessageCompleted>(events[2]);
        Assert.Equal("incomplete:max_output_tokens", completed.Usage.FinishReason);
        Assert.Equal(80, completed.Usage.InputTokens);
        Assert.Equal(10, completed.Usage.CachedInputTokens);
        Assert.Equal(2048, completed.Usage.OutputTokens);
        Assert.Equal(2048, JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public async Task Adapter_falls_back_to_the_bare_incomplete_finish_reason_for_unknown_provider_reasons()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Partial\"}\n\ndata: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"unapproved raw provider status\",\"incomplete_details\":{\"reason\":\"private provider detail text\"},\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}}\n\n"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "long answer")], "instruction", "Smart", EnableStreaming: true, MaxOutputTokens: 2048), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);

        var completed = Assert.IsType<AiMessageCompleted>(Assert.Single(events.OfType<AiMessageCompleted>()));
        Assert.Equal("incomplete", completed.Usage.FinishReason);
        Assert.DoesNotContain("private", completed.Usage.FinishReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unapproved", completed.Usage.FinishReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Adapter_fails_an_incomplete_stream_without_useful_output_and_keeps_only_safe_usage(string delta)
    {
        var deltaEvent = delta.Length == 0 ? string.Empty : $"data: {{\"type\":\"response.output_text.delta\",\"delta\":\"{delta}\"}}\n\n";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{deltaEvent}data: {{\"type\":\"response.incomplete\",\"response\":{{\"status\":\"unapproved raw provider status\",\"incomplete_details\":{{\"reason\":\"private provider detail text\"}},\"usage\":{{\"input_tokens\":10,\"output_tokens\":20}}}}}}\n\n"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);
        var events = new List<AiStreamEvent>();

        var exception = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "filtered answer")], "instruction", "Smart", EnableStreaming: true, MaxOutputTokens: 2048), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);
        });

        Assert.DoesNotContain(events, item => item is AiMessageCompleted);
        Assert.All(events, item => Assert.IsType<AiMessageDelta>(item));
        Assert.Equal(AiProviderFailureCategories.MalformedResponse, exception.FailureCategory);
        Assert.NotNull(exception.Usage);
        Assert.Equal(10, exception.Usage.InputTokens);
        Assert.Equal(20, exception.Usage.OutputTokens);
        Assert.Equal("incomplete", exception.Usage.FinishReason);
        Assert.DoesNotContain("private", exception.Usage.FinishReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unapproved", exception.Usage.FinishReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.ProviderErrorCode);
    }

    [Fact]
    public async Task Adapter_maps_an_unrecognized_terminal_status_to_a_bounded_safe_finish_reason()
    {
        var privateStatus = $"provider-internal status: {new string('x', 2048)}";
        var terminal = JsonSerializer.Serialize(new
        {
            type = "response.completed",
            response = new { status = privateStatus, usage = new { input_tokens = 1, output_tokens = 1 } },
        });
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"data: {{\"type\":\"response.output_text.delta\",\"delta\":\"Answer\"}}\n\ndata: {terminal}\n\n"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "answer")], "instruction", "Smart", EnableStreaming: true), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);

        var completed = Assert.IsType<AiMessageCompleted>(Assert.Single(events.OfType<AiMessageCompleted>()));
        Assert.Equal("unknown", completed.Usage.FinishReason);
        Assert.True(completed.Usage.FinishReason.Length <= 16);
        Assert.DoesNotContain("provider-internal", completed.Usage.FinishReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adapter_keeps_a_stream_fatal_when_no_terminal_event_arrives()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\ndata: [DONE]\n\n"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var exception = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new AiChatRequest([new("user", "Hello")], "instruction", "Smart", EnableStreaming: true, MaxOutputTokens: 2048), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) { }
        });
        Assert.Equal(AiProviderFailureCategories.MalformedResponse, exception.FailureCategory);
    }

    [Fact]
    public async Task Adapter_marks_a_non_stream_incomplete_response_as_truncated_completion()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Partial draft\"}]}],\"usage\":{\"input_tokens\":5,\"output_tokens\":10}}"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(new AiChatRequest([new("user", "draft")], "instruction", "Smart", EnableStreaming: false, MaxOutputTokens: 2048), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) events.Add(item);

        Assert.Equal(2, events.Count);
        Assert.Equal("Partial draft", ((AiMessageDelta)events[0]).Delta);
        var completed = Assert.IsType<AiMessageCompleted>(events[1]);
        Assert.Equal("incomplete:max_output_tokens", completed.Usage.FinishReason);
    }

    [Fact]
    public async Task Adapter_fails_a_non_stream_incomplete_response_without_useful_output_and_preserves_usage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"content_filter\"},\"output_text\":\"  \",\"usage\":{\"input_tokens\":7,\"output_tokens\":2}}"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client, Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiProvider>.Instance);

        var exception = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new AiChatRequest([new("user", "filtered draft")], "instruction", "Smart", EnableStreaming: false, MaxOutputTokens: 2048), new AiProviderSelection("openai", "gpt-5.6-terra", "Taslim Smart", "Smart", false))) { }
        });

        Assert.Equal(AiProviderFailureCategories.MalformedResponse, exception.FailureCategory);
        Assert.NotNull(exception.Usage);
        Assert.Equal(7, exception.Usage.InputTokens);
        Assert.Equal(2, exception.Usage.OutputTokens);
        Assert.Equal("incomplete:content_filter", exception.Usage.FinishReason);
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
