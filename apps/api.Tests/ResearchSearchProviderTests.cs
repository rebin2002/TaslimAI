using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchSearchProviderTests
{
    [Fact]
    public async Task Production_equivalent_request_omits_empty_filters_and_uses_current_web_search_contract()
    {
        var handler = new StubHandler(_ => SuccessResponse(ValidResponseJson()));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.SearchAsync(ProductionRequest(), new ResearchGenerationOptions { SearchModel = "gpt-5.5" });

        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var root = payload.RootElement;
        Assert.Equal("gpt-5.5", root.GetProperty("model").GetString());
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.Equal("web_search_call.action.sources", root.GetProperty("include")[0].GetString());
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("web_search", tool.GetProperty("type").GetString());
        Assert.Equal("medium", tool.GetProperty("search_context_size").GetString());
        Assert.True(tool.GetProperty("external_web_access").GetBoolean());
        Assert.False(tool.TryGetProperty("filters", out _));
        Assert.Contains("home appliance market in Iraq", root.GetProperty("input").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Iraq", root.GetProperty("input").GetString()!.Split("Geographic focus: ")[1].Split('\n')[0]);
        Assert.Equal(2, result.Sources.Count);
        Assert.Equal("https://example.gov/iraq-appliances", result.Sources[0].Url);
        Assert.Contains("citationStartIndex", result.Sources[0].MetadataJson, StringComparison.Ordinal);
        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task Populated_domain_filters_use_current_allowed_and_blocked_domain_names()
    {
        var handler = new StubHandler(_ => SuccessResponse(ValidResponseJson()));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        var request = ProductionRequest() with
        {
            Preferences = new ResearchSourcePreference(true, ["gov.iq", "who.int"], ["reddit.com"]),
        };

        await provider.SearchAsync(request, new ResearchGenerationOptions { SearchModel = "gpt-5.5" });

        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var filters = payload.RootElement.GetProperty("tools")[0].GetProperty("filters");
        Assert.Equal(new[] { "gov.iq", "who.int" }, filters.GetProperty("allowed_domains").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("reddit.com", filters.GetProperty("blocked_domains")[0].GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request_error", "unsupported_value", "tools[0].filters", "provider")]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_request_error", "invalid_api_key", "", "configuration")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error", "rate_limit_exceeded", "", "rate_limited")]
    [InlineData(HttpStatusCode.BadGateway, "server_error", "upstream_error", "", "transient")]
    public async Task Provider_failure_classification_is_safe_and_structured(HttpStatusCode status, string errorType, string errorCode, string errorParam, string category)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent($"{{\"error\":{{\"type\":\"{errorType}\",\"code\":\"{errorCode}\",\"param\":{(string.IsNullOrEmpty(errorParam) ? "null" : $"\"{errorParam}\"")},\"message\":\"private customer query and provider details\"}}}}"),
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.SearchAsync(ProductionRequest(), new ResearchGenerationOptions { SearchModel = "gpt-5.5" }));
        var details = exception switch
        {
            ResearchSearchFailedException failed => failed.Details,
            ResearchSearchUnavailableException unavailable => unavailable.Details,
            _ => null,
        };
        Assert.NotNull(details);
        Assert.Equal((int)status, details!.HttpStatusCode);
        Assert.Equal(errorType, details.ErrorType);
        Assert.Equal(errorCode, details.ErrorCode);
        Assert.Equal(string.IsNullOrEmpty(errorParam) ? null : errorParam, details.ErrorParam);
        Assert.Equal(category, details.FailureCategory);
        Assert.DoesNotContain("private customer query", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider details", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_is_classified_without_provider_content()
    {
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<ResearchSearchTimeoutException>(() => provider.SearchAsync(ProductionRequest(), new ResearchGenerationOptions { SearchModel = "gpt-5.5" }));
    }

    [Fact]
    public async Task Malformed_output_and_no_sources_are_rejected_without_fabricating_citations()
    {
        var malformedHandler = new StubHandler(_ => SuccessResponse("not-json"));
        using var malformedClient = new HttpClient(malformedHandler);
        var malformedProvider = CreateProvider(malformedClient);
        await Assert.ThrowsAsync<ResearchSearchFailedException>(() => malformedProvider.SearchAsync(ProductionRequest(), new ResearchGenerationOptions { SearchModel = "gpt-5.5" }));

        var noSourcesHandler = new StubHandler(_ => SuccessResponse("{\"status\":\"completed\",\"output\":[]}"));
        using var noSourcesClient = new HttpClient(noSourcesHandler);
        var noSourcesProvider = CreateProvider(noSourcesClient);
        await Assert.ThrowsAsync<ResearchSearchFailedException>(() => noSourcesProvider.SearchAsync(ProductionRequest(), new ResearchGenerationOptions { SearchModel = "gpt-5.5" }));

        var invalidUrlHandler = new StubHandler(_ => SuccessResponse(ValidResponseJson("ftp://invalid.example/source").Replace("https://example.org/iraq-market", "ftp://invalid.example/second", StringComparison.Ordinal)));
        using var invalidUrlClient = new HttpClient(invalidUrlHandler);
        var invalidUrlProvider = CreateProvider(invalidUrlClient);
        await Assert.ThrowsAsync<ResearchSearchFailedException>(() => invalidUrlProvider.SearchAsync(ProductionRequest(), new ResearchGenerationOptions { SearchModel = "gpt-5.5" }));
    }

    private static OpenAiResearchSearchProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }),
        Options.Create(new ResearchGenerationOptions { SearchModel = "gpt-5.5" }),
        NullLogger<OpenAiResearchSearchProvider>.Instance);

    private static ResearchSearchRequest ProductionRequest()
    {
        var input = new ResearchGenerationInput(
            Guid.NewGuid(), null,
            "What are the current trends in the home appliance market in Iraq in 2026? Focus on refrigerators, washing machines, and air conditioners.",
            "Taslim research report", "standard", "research_report", "en", "Business decision makers", "Iraq", "2025-2026", null, [], [], true, []);
        var plan = new ResearchPlan(input.Question, "Cited market report", [input.Question, "Iraq home appliance market 2025-2026"], ["refrigerators", "washing machines", "air conditioners"], input.GeographicFocus, input.TimePeriod, ["official", "industry"]);
        return new ResearchSearchRequest(input, plan, new ResearchSourcePreference(true, input.PreferredDomains, input.ExcludedDomains));
    }

    private static HttpResponseMessage SuccessResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static string ValidResponseJson(string? firstUrl = null) => $$"""
    {
      "model": "gpt-5.5-2026-04-23",
      "status": "completed",
      "output": [
        {
          "type": "web_search_call",
          "status": "completed",
          "action": {
            "type": "search",
            "query": "home appliance market Iraq 2025-2026",
            "sources": [
              { "type": "url", "url": "{{firstUrl ?? "https://example.gov/iraq-appliances"}}" },
              { "type": "url", "url": "https://example.org/iraq-market" }
            ]
          }
        },
        {
          "type": "message",
          "status": "completed",
          "role": "assistant",
          "content": [
            {
              "type": "output_text",
              "text": "The Iraq market shows current appliance trends.",
              "annotations": [
                { "type": "url_citation", "start_index": 0, "end_index": 42, "url": "{{firstUrl ?? "https://example.gov/iraq-appliances"}}", "title": "Iraq appliance market source" }
              ]
            }
          ]
        }
      ],
      "usage": { "input_tokens": 12, "output_tokens": 8 }
    }
    """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return factory(request);
        }
    }
}
