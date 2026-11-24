using System.Net;
using Microsoft.Extensions.Options;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class OpenAlexWorksAdapterTests
{
    private static (OpenAlexWorksAdapter adapter, ScriptedHandler handler) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> script,
        Action<AcademicSourceDiscoveryOptions>? configure = null)
    {
        var handler = new ScriptedHandler(script);
        var options = AcademicFixture.Defaults(configure);
        var adapter = new OpenAlexWorksAdapter(AcademicFixture.Client(handler), AcademicFixture.Wrapped(options), AcademicFixture.Logger<OpenAlexWorksAdapter>());
        return (adapter, handler);
    }

    [Fact]
    public async Task Normalizes_open_access_work_with_reconstructed_abstract_and_full_text_indication()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenAlexFixtures.SearchPage));
        var results = await adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "pharmaceutical market access kurdistan" });

        Assert.Equal(3, results.Count);
        var article = Assert.Single(results, record => record.ProviderWorkId == "W4210177603");
        Assert.Equal("openalex", article.ProviderKey);
        Assert.Equal("Pharmaceutical market access in the Kurdistan Region of Iraq", article.Title);
        Assert.Equal(["D. Rahman", "S. Karim"], article.Authors);
        Assert.Equal("10.1016/j.kr.2021.02.003", article.Doi); // lower-cased, resolver prefix stripped
        Assert.Equal(AcademicSourceTypes.JournalArticle, article.SourceType);
        Assert.Equal(2021, article.PublicationYear);
        Assert.Equal("Kurdish Health Policy Review", article.ContainerTitle);
        Assert.Equal("12", article.Volume);
        Assert.Equal("3", article.Issue);
        Assert.Equal("110-128", article.PageRange);
        Assert.Equal(14, article.CitationCount);
        Assert.Equal(412.7, article.RelevanceScore);
        Assert.Equal(AcademicAccessLevel.FullTextIndicated, article.AccessLevel);
        Assert.Equal("https://journal.example.org/articles/45.pdf", article.FullTextUrl);
        Assert.Contains("fullTextAdvertised\":true", article.SafeMetadataJson);

        // Inverted index has gaps at positions 3, 4 and 6; the reconstruction must mark the
        // withheld words instead of silently joining sentences.
        Assert.Equal("This examines market … access … regulation in Kurdistan Region of Iraq and its reimbursement policy", article.Abstract);

        var closed = Assert.Single(results, record => record.ProviderWorkId == "W2987000001");
        Assert.Equal(AcademicAccessLevel.Metadata, closed.AccessLevel);
        Assert.Null(closed.Abstract);
        Assert.Null(closed.FullTextUrl);
        Assert.Null(closed.Volume);
        Assert.Null(closed.PageRange);

        // The credential-bearing landing URL must never be persisted.
        var unsafeUrlRow = Assert.Single(results, record => record.ProviderWorkId == "W2999000002");
        Assert.Null(unsafeUrlRow.LandingUrl);
        Assert.Equal(AcademicSourceTypes.Dataset, unsafeUrlRow.SourceType);
    }

    [Fact]
    public async Task Uses_polite_pool_mailto_and_bounded_select_fields_on_every_request()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenAlexFixtures.EmptyPage));
        await adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "test", MaxResults = 5 });

        var query = AcademicFixture.Query(handler.LastRequest);
        Assert.Equal("test", query["search"]);
        Assert.Equal("discovery@taslim.test", query["mailto"]);
        Assert.Contains("abstract_inverted_index", query["select"]);
        Assert.Equal("5", query["per-page"]);
        Assert.Equal("1", query["page"]);
        Assert.Equal("https", handler.LastRequest.RequestUri!.Scheme);
        Assert.Equal("api.openalex.org", handler.LastRequest.RequestUri.Host);
    }

    [Fact]
    public async Task Maps_filters_year_language_and_open_access_to_provider_filter_syntax()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenAlexFixtures.EmptyPage));
        await adapter.SearchWorksAsync(new AcademicDiscoveryQuery
        {
            SearchTerm = "regulation",
            YearFrom = 2019,
            YearTo = 2024,
            Language = "en",
            OpenAccessOnly = true,
        });

        var query = AcademicFixture.Query(handler.LastRequest);
        Assert.Contains("from_publication_date:2019-01-01", query["filter"]);
        Assert.Contains("to_publication_date:2024-12-31", query["filter"]);
        Assert.Contains("language:en", query["filter"]);
        Assert.Contains("open_access.is_oa:true", query["filter"]);
    }

    [Fact]
    public async Task Pagination_is_bounded_by_page_and_maxpages_configuration()
    {
        var (adapter, handler) = Create(
            (_, _) => AcademicFixture.Json(OpenAlexFixtures.PageWithWorks(25)),
            options => { options.MaxPerPage = 25; options.MaxPages = 2; });

        var results = await adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "bounded", MaxResults = 200 });

        // MaxResultsPerProvider (50) already caps the operation; 25/page * 2 pages = 50.
        Assert.Equal(50, results.Count);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("2", AcademicFixture.Query(handler.Requests[1])["page"]);
    }

    [Fact]
    public async Task Stops_pagination_when_page_returns_less_than_page_size()
    {
        var (adapter, handler) = Create((_, requestNumber) => AcademicFixture.Json(
            requestNumber == 1 ? OpenAlexFixtures.PageWithWorks(4) : OpenAlexFixtures.PageWithWorks(25)));
        var results = await adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "short pages", MaxResults = 50 });
        Assert.Equal(4, results.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Rate_limited_response_surfaces_as_safe_rate_limited_failure()
    {
        var (adapter, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"TooManyRequests\"}", System.Text.Encoding.UTF8, "application/json"),
        });
        var exception = await Assert.ThrowsAsync<AcademicSourceRateLimitedException>(() =>
            adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }));
        Assert.Equal(429, exception.Details?.HttpStatusCode);
        Assert.Equal("rate_limited", exception.Details?.FailureCategory);
        // The user-visible message never names the provider.
        Assert.DoesNotContain("openalex", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenAlex", exception.Message);
    }

    [Fact]
    public async Task Malformed_payload_surfaces_as_malformed_failure()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json("{\"meta\":{},\"not_results\":[]}"));
        await Assert.ThrowsAsync<AcademicSourceMalformedException>(() =>
            adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }));
    }

    [Fact]
    public async Task Cancellation_propagates_without_provider_call_when_already_cancelled()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenAlexFixtures.SearchPage));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }, cancelled.Token));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Disabled_switch_prevents_any_http_traffic()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenAlexFixtures.SearchPage), options => options.OpenAlexEnabled = false);
        Assert.False(adapter.IsConfigured);
        await Assert.ThrowsAsync<AcademicSourceUnavailableException>(() =>
            adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Abstract_without_open_access_marks_abstract_level_not_full_text()
    {
        const string closedWithAbstract = """
        {"meta":{"count":1},"results":[{
          "id":"https://api.openalex.org/works/W1","display_name":"Closed but abstracted","type":"article",
          "abstract_inverted_index":{"Sentences":[0],"only":[1]},
          "open_access":{"is_oa":false,"oa_status":"closed"},
          "primary_location":{"landing_page_url":"https://paywalled.example/w1","source":null},
          "best_oa_location":null}]}
        """;
        var (adapter, _) = Create((_, _) => AcademicFixture.Json(closedWithAbstract));
        var results = await adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" });
        var record = Assert.Single(results);
        Assert.Equal(AcademicAccessLevel.Abstract, record.AccessLevel);
        Assert.Equal("Sentences only", record.Abstract);
        Assert.Null(record.FullTextUrl);
    }
}
