using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Regression coverage for Research source URL canonicalization. The canonical URL is the citation
/// locator that is persisted on the source row and exported verbatim in the citation manifest, so it
/// has to stay an escaped URL that a strict parser resolves back to the same document, never the
/// percent-decoded display form. Fixtures are synthetic and served from an in-memory handler, so no
/// research provider, credential, or network call is used.
/// </summary>
public sealed class ResearchSourceUrlCanonicalizationTests
{
    private const int PersistedUrlCharacters = 2_000;

    [Theory]
    // Encoded spaces, quotes, angle brackets, and non-ASCII segments must survive canonicalization
    // instead of being decoded into a string that is no longer a URL.
    [InlineData("https://example.gov/reports/Iraq%20Market%202026.pdf", "https://example.gov/reports/Iraq%20Market%202026.pdf")]
    [InlineData("https://example.gov/Baghdad%20market%20report?utm_source=gov#summary", "https://example.gov/Baghdad%20market%20report")]
    [InlineData("https://example.gov/a%22b", "https://example.gov/a%22b")]
    [InlineData("https://example.gov/%3Ca%3E", "https://example.gov/%3Ca%3E")]
    [InlineData("https://example.gov/%C3%A9tude", "https://example.gov/%C3%A9tude")]
    [InlineData("https://example.gov/étude", "https://example.gov/%C3%A9tude")]
    // Ordinary normalization is unchanged: scheme and host casing, default ports, trailing slash,
    // query, and fragment handling all keep their existing shape.
    [InlineData("https://EXAMPLE.gov:443/Iraq-Appliances/", "https://example.gov/Iraq-Appliances")]
    [InlineData("http://example.gov:80/report", "http://example.gov/report")]
    [InlineData("https://example.gov/iraq-appliances", "https://example.gov/iraq-appliances")]
    public async Task Canonical_citation_locator_is_the_escaped_url_that_is_persisted_and_exported(string providerUrl, string expectedCanonicalUrl)
    {
        var result = await SearchAsync(providerUrl);

        var source = Assert.Single(result.Sources);
        Assert.Equal(expectedCanonicalUrl, source.CanonicalUrl);
        Assert.NotNull(source.CanonicalUrl);
        Assert.True(Uri.IsWellFormedUriString(source.CanonicalUrl, UriKind.Absolute), $"canonical citation URL is not a well-formed URI: {source.CanonicalUrl}");
        Assert.Equal(source.CanonicalUrl, new Uri(source.CanonicalUrl, UriKind.Absolute).AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public async Task Escaped_and_decoded_spellings_of_one_document_stay_one_citation()
    {
        var result = await SearchAsync("https://example.gov/Baghdad%20market%20report", "https://example.gov/Baghdad market report");

        var source = Assert.Single(result.Sources);
        Assert.Equal("https://example.gov/Baghdad%20market%20report", source.Url);
        Assert.Equal("https://example.gov/Baghdad%20market%20report", source.CanonicalUrl);
    }

    [Fact]
    public async Task Encoded_slash_segment_is_not_deduplicated_against_its_decoded_path()
    {
        var result = await SearchAsync("https://example.gov/archive/a%2Fb", "https://example.gov/archive/a/b");

        Assert.Equal(2, result.Sources.Count);
        Assert.Equal(
            new[] { "https://example.gov/archive/a%2Fb", "https://example.gov/archive/a/b" },
            result.Sources.Select(source => source.CanonicalUrl).ToArray());
    }

    [Fact]
    public async Task Canonical_locator_is_omitted_when_escaping_would_not_fit_the_persisted_width()
    {
        // Percent-escaping can be several times longer than the provider URL itself, so the bounded
        // canonical column must not be handed a value PostgreSQL would reject: the canonical locator
        // is omitted and the raw citation URL is kept.
        var providerUrl = "https://example.gov/" + string.Concat(Enumerable.Repeat("عربي", 400));
        Assert.True(providerUrl.Length < PersistedUrlCharacters);

        var result = await SearchAsync(providerUrl);

        var source = Assert.Single(result.Sources);
        Assert.Equal(providerUrl, source.Url);
        Assert.Equal("example.gov", source.Domain);
        Assert.Null(source.CanonicalUrl);
    }

    private static async Task<ResearchSearchResult> SearchAsync(params string[] providerUrls)
    {
        var handler = new StubHandler(_ => Response(ProviderResponse(providerUrls)));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        return await provider.SearchAsync(Request(), SearchOptions());
    }

    private static OpenAiResearchSearchProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }),
        Options.Create(new ResearchGenerationOptions { SearchModel = "gpt-5.5" }),
        NullLogger<OpenAiResearchSearchProvider>.Instance);

    private static ResearchGenerationOptions SearchOptions() => new() { SearchModel = "gpt-5.5" };

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

    private static string ProviderResponse(string[] providerUrls) => JsonSerializer.Serialize(new
    {
        model = "gpt-5.5",
        status = "completed",
        output = new object[]
        {
            new
            {
                type = "web_search_call",
                status = "completed",
                action = new
                {
                    type = "search",
                    query = "home appliance market Iraq 2025-2026",
                    sources = providerUrls.Select(url => new { type = "url", url }).ToArray(),
                },
            },
            new
            {
                type = "message",
                status = "completed",
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = "The Iraq market shows current appliance trends.",
                        annotations = new[]
                        {
                            new { type = "url_citation", start_index = 0, end_index = 42, url = providerUrls[0], title = "Iraq appliance market source" },
                        },
                    },
                },
            },
        },
        usage = new { input_tokens = 12, output_tokens = 8 },
    });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }
}
