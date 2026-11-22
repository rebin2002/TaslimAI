using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;
namespace Taslim.Api.Tests;
/// <summary>
/// Regression coverage for the public-web-host half of the server-side citation guard. A citation
/// URL is untrusted provider output that is persisted on research source rows, sent to the report
/// provider, exported through the CSV manifest and the DOCX/PDF source list, and rendered as a
/// clickable source link, so a link that denotes loopback, private, link-local, shared-address or
/// otherwise non-public infrastructure must never become a citation. Fixtures are synthetic and
/// served from an in-memory handler, so no research provider, credential, or network call is used.
/// </summary>
public sealed class ResearchCitationPublicHostTests
{
    [Theory]
    // Registered public hosts remain eligible, including the RFC-reserved names that the
    // deterministic adapters and fixtures rely on.
    [InlineData("https://example.gov/iraq-appliances")]
    [InlineData("http://example.org/iraq-market")]
    [InlineData("https://sub.domain.example.com/reports/q3")]
    [InlineData("https://example.test/source")]
    [InlineData("https://untrusted.example/claims")]
    // A port does not make a public host private.
    [InlineData("https://example.gov:8443/reports")]
    [InlineData("http://example.org:8080/market")]
    // Routable IP literals are public targets.
    [InlineData("https://8.8.8.8/dns-query")]
    [InlineData("https://[2606:4700:4700::1111]/status")]
    // "@" below the authority stays a legitimate citation character.
    [InlineData("https://example.gov/@handle/report")]
    [InlineData("https://example.gov/search?q=iraq%40gov")]
    public void Policy_admits_public_web_citation_targets(string value) =>
        Assert.True(ResearchCitationUrlPolicy.IsSafe(value));
    [Theory]
    // Loopback, in both host-name and literal forms.
    [InlineData("http://127.0.0.1/report")]
    [InlineData("https://127.0.0.1:8443/report")]
    [InlineData("http://localhost/report")]
    [InlineData("http://db.localhost/report")]
    [InlineData("http://[::1]/report")]
    // Private and link-local infrastructure, including cloud metadata services.
    [InlineData("http://10.0.0.8/report")]
    [InlineData("http://172.16.5.9/report")]
    [InlineData("http://192.168.4.10/report")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://169.254.170.2/v2/credentials")]
    [InlineData("http://[fd00::1]/report")]
    [InlineData("http://[fe80::1]/report")]
    // Shared address space, benchmarking, documentation and reserved ranges.
    [InlineData("http://100.64.0.1/report")]
    [InlineData("http://198.18.0.1/report")]
    [InlineData("http://192.0.2.1/report")]
    [InlineData("http://198.51.100.4/report")]
    [InlineData("http://203.0.113.7/report")]
    [InlineData("http://0.0.0.0/report")]
    [InlineData("http://224.0.0.1/report")]
    [InlineData("http://255.255.255.255/report")]
    // Alternate IP encodings that a reader's browser resolves to loopback; System.Uri canonicalizes
    // them before the guard sees them, so they must fail on the canonical form.
    [InlineData("http://2130706433/report")]
    [InlineData("http://0x7f000001/report")]
    [InlineData("http://127.1/report")]
    [InlineData("http://0177.0.0.1/report")]
    // IPv4-mapped IPv6 literals hide a loopback target behind an IPv6 spelling.
    [InlineData("http://[::ffff:127.0.0.1]/report")]
    [InlineData("http://[::127.0.0.1]/report")]
    // Single-label names resolve only through a reader's private search domain or a predictable
    // intranet hostname, never through the public root.
    [InlineData("http://nas/report")]
    [InlineData("http://router/admin")]
    public void Policy_rejects_internal_and_non_public_citation_targets(string value) =>
        Assert.False(ResearchCitationUrlPolicy.IsSafe(value));
    [Theory]
    // The credential-boundary half of the guard is unchanged.
    [InlineData("https://taslim.ai:token@evil.example/report")]
    [InlineData("http://admin:admin@localhost/report")]
    [InlineData("ftp://example.gov/report")]
    [InlineData("file://etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Policy_still_rejects_credentials_non_web_schemes_and_malformed_values(string value) =>
        Assert.False(ResearchCitationUrlPolicy.IsSafe(value));
    [Fact]
    public void Policy_still_rejects_empty_and_whitespace_values()
    {
        Assert.False(ResearchCitationUrlPolicy.IsSafe(null));
        Assert.False(ResearchCitationUrlPolicy.IsSafe("   "));
    }
    private static ResearchSourceCandidate Web(string citationId, string url, int rank, string? canonicalUrl = null) =>
        new(citationId, url, canonicalUrl ?? url, $"Title {citationId}", new Uri(url).Host, null, null, DateTime.UtcNow, "web", "Snippet", "Extracted", null, rank, true, null);
    private static ResearchSourceCandidate Uploaded(string citationId, Guid storedFileId) =>
        new(citationId, null, null, "private-plan.pdf", "uploaded file", null, null, DateTime.UtcNow, "uploaded", "Snippet", "Extracted", null, 1, true, null, storedFileId);
    [Fact]
    public void Boundary_keeps_public_citations_and_drops_only_inadmissible_web_links()
    {
        var storedFileId = Guid.NewGuid();
        var kept = new[]
        {
            Web("S1", "https://example.gov/iraq-appliances", 1),
            Uploaded("S2", storedFileId),
            Web("S4", "https://example.org/iraq-market", 4),
        };
        var dropped = new[]
        {
            Web("S3", "http://169.254.169.254/latest/meta-data/", 3),
            Web("S5", "https://example.gov/ok", 5, "http://127.0.0.1/hidden"),
        };
        var result = ResearchCitationUrlPolicy.FilterAdmissibleSourceLinks(dropped.Concat(kept).ToArray());
        // Citation identifiers and ranks are server-assigned upstream, so the surviving sources keep
        // them and the report can still cite them; only the inadmissible rows disappear.
        Assert.Equal(new[] { "S1", "S2", "S4" }, result.Select(source => source.CitationId));
        Assert.Equal(new[] { 1, 4 }, result.Where(source => source.Url is not null).Select(source => source.Rank));
        Assert.All(result, source => Assert.True(ResearchCitationUrlPolicy.AdmitsCitationLink(source.Url, source.CanonicalUrl)));
        Assert.DoesNotContain(result, source => source.Url?.Contains("169.254", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result, source => source.CanonicalUrl?.Contains("127.0.0.1", StringComparison.Ordinal) == true);
    }
    [Fact]
    public void Boundary_keeps_uploaded_sources_that_carry_no_web_link()
    {
        var sources = new[] { Uploaded("S1", Guid.NewGuid()) };
        Assert.Single(ResearchCitationUrlPolicy.FilterAdmissibleSourceLinks(sources));
        Assert.True(ResearchCitationUrlPolicy.AdmitsCitationLink(null, null));
    }
    [Fact]
    public async Task Adapter_never_promotes_an_internal_metadata_url_to_a_citation()
    {
        var handler = new StubHandler(_ => Response(ProviderResponse(
            "http://169.254.169.254/latest/meta-data/",
            "https://example.gov/iraq-appliances")));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        var result = await provider.SearchAsync(Request(), ResearchOptions());
        var source = Assert.Single(result.Sources);
        Assert.Equal("S1", source.CitationId);
        Assert.Equal("https://example.gov/iraq-appliances", source.Url);
        Assert.True(ResearchCitationUrlPolicy.IsSafe(source.Url));
        Assert.NotEqual("169.254.169.254", source.Domain);
        // The metadata annotation matched nothing, so it contributes neither citation metadata nor
        // evidence for the surviving source.
        Assert.Null(source.MetadataJson);
        Assert.Empty(result.Evidence);
    }
    [Fact]
    public async Task Adapter_fails_closed_when_every_provider_url_denotes_internal_infrastructure()
    {
        var handler = new StubHandler(_ => Response(ProviderResponse(
            "http://169.254.169.254/latest/meta-data/",
            "http://127.0.0.1:8080/report")));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        await Assert.ThrowsAsync<ResearchSearchFailedException>(() => provider.SearchAsync(Request(), ResearchOptions()));
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
    private static string ProviderResponse(string firstUrl, string secondUrl) => $$"""
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
                { "type": "url_citation", "start_index": 0, "end_index": 42, "url": "{{firstUrl}}", "title": "Internal source" }
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
