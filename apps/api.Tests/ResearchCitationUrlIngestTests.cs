using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Regression coverage for the server-side citation URL ingest guard. Fixtures are synthetic and
/// served from an in-memory handler, so no research provider, credential, or network call is used.
/// </summary>
public sealed class ResearchCitationUrlIngestTests
{
    [Theory]
    // Ordinary public citation URLs remain eligible.
    [InlineData("https://example.gov/iraq-appliances", true)]
    [InlineData("http://example.org/iraq-market", true)]
    [InlineData("https://example.gov/reports/q3#appendix", true)]
    // "@" below the authority is a legitimate path or query character.
    [InlineData("https://example.gov/@handle/report", true)]
    [InlineData("https://example.gov/search?q=iraq%40gov", true)]
    // Credentials in the authority make the visible prefix impersonate another host while the
    // link, the persisted citation, and the exported manifest point elsewhere.
    [InlineData("https://taslim.ai:secret@evil.example/report", false)]
    [InlineData("https://research.taslim.ai@evil.example/report", false)]
    [InlineData("https://@evil.example/report", false)]
    [InlineData("http://admin:admin@localhost/report", false)]
    // Non-web schemes and unusable values were never acceptable citations.
    [InlineData("ftp://example.gov/report", false)]
    [InlineData("file://example.gov/report", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html;base64,PHN2Zz4=", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Policy_admits_only_web_urls_without_credentials(string? value, bool expected) =>
        Assert.Equal(expected, ResearchCitationUrlPolicy.IsSafe(value));

    [Fact]
    public async Task Credential_bearing_provider_urls_never_become_citations()
    {
        var handler = new StubHandler(_ => Response(ProviderResponse(
            "https://taslim.ai:token@evil.example/report",
            "https://example.gov/iraq-appliances")));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.SearchAsync(Request(), ResearchOptions());

        var source = Assert.Single(result.Sources);
        Assert.Equal("https://example.gov/iraq-appliances", source.Url);
        Assert.Equal("S1", source.CitationId);
        Assert.NotNull(source.CanonicalUrl);
        Assert.Equal("example.gov", source.Domain);
        // The credential-bearing annotation matched nothing, so it contributes neither
        // citation metadata nor evidence for the surviving source.
        Assert.Null(source.MetadataJson);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task Search_fails_when_every_provider_url_carries_credentials()
    {
        var handler = new StubHandler(_ => Response(ProviderResponse(
            "https://taslim.ai:token@evil.example/report",
            "https://docs.taslim.ai@evil.example/second")));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<ResearchSearchFailedException>(() => provider.SearchAsync(Request(), ResearchOptions()));
    }

    [Fact]
    public async Task Annotation_credentials_do_not_smuggle_a_source_url_back_in()
    {
        var handler = new StubHandler(_ => Response(ProviderResponse(
            "https://example.gov/iraq-appliances",
            "https://example.org/iraq-market",
            annotationUrl: "https://taslim.ai:token@evil.example/report")));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.SearchAsync(Request(), ResearchOptions());

        Assert.Equal(2, result.Sources.Count);
        Assert.All(result.Sources, source =>
        {
            Assert.DoesNotContain("@", source.Url!, StringComparison.Ordinal);
            Assert.Null(source.MetadataJson);
        });
        Assert.Empty(result.Evidence);
    }

    private static OpenAiResearchSearchProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }),
        Options.Create(new ResearchGenerationOptions { SearchModel = "gpt-5.5" }),
        NullLogger<OpenAiResearchSearchProvider>.Instance);

    private static ResearchGenerationOptions ResearchOptions() => new() { SearchModel = "gpt-5.5" };

    private static ResearchSearchRequest Request()
    {
        var input = new ResearchGenerationInput(
            Guid.NewGuid(), null,
            "What are the current trends in the home appliance market in Iraq in 2026?",
            "Taslim research report", "standard", "research_report", "en", null, "Iraq", "2025-2026", null, [], [], true, []);
        var plan = new ResearchPlan(input.Question, "Cited market report", [input.Question], ["refrigerators"], input.GeographicFocus, input.TimePeriod, ["official"]);
        return new ResearchSearchRequest(input, plan, new ResearchSourcePreference(true, [], []));
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static string ProviderResponse(string firstUrl, string secondUrl, string? annotationUrl = null) => $$"""
    {
      "model": "gpt-5.5",
      "status": "completed",
      "output": [
        {
          "type": "web_search_call",
          "status": "completed",
          "action": {
            "type": "search",
            "query": "home appliance market Iraq 2025-2026",
            "sources": [
              { "type": "url", "url": "{{firstUrl}}" },
              { "type": "url", "url": "{{secondUrl}}" }
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
                { "type": "url_citation", "start_index": 0, "end_index": 42, "url": "{{annotationUrl ?? firstUrl}}", "title": "Iraq appliance market source" }
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }
}
