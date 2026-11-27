using System.Net;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class OpenLibraryBooksAdapterTests
{
    private static (OpenLibraryBooksAdapter adapter, ScriptedHandler handler) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> script,
        Action<AcademicSourceDiscoveryOptions>? configure = null)
    {
        var handler = new ScriptedHandler(script);
        var options = AcademicFixture.Defaults(configure);
        var adapter = new OpenLibraryBooksAdapter(AcademicFixture.Client(handler), AcademicFixture.Wrapped(options), AcademicFixture.Logger<OpenLibraryBooksAdapter>());
        return (adapter, handler);
    }

    [Fact]
    public async Task Catalog_search_normalizes_editions_and_keeps_lending_copies_out_of_full_text_markers()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenLibraryFixtures.SearchPage));
        var results = await adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "essential medicine regulation" });

        Assert.Equal(2, results.Count);
        var borrowable = results.First(record => record.ProviderWorkId == "/books/OL25000000M");
        Assert.Equal("openlibrary", borrowable.ProviderKey);
        Assert.Equal("Essential Medicine Regulation", borrowable.Title);
        Assert.Equal(["L. Barzani", "T. Aziz"], borrowable.Authors);
        Assert.Equal("Kurdistan University Press", borrowable.Publisher);
        Assert.Equal(2018, borrowable.PublicationYear);
        Assert.Equal(AcademicSourceTypes.Book, borrowable.SourceType);
        Assert.Equal(342, borrowable.PageCount);
        Assert.Equal("9781234567897", borrowable.Isbn13);
        Assert.Equal("123456789X", borrowable.Isbn10);
        // An Internet Archive lending id is controlled digital lending, not a readable copy for
        // this service: metadata is the honest marker, while the flag records what exists.
        Assert.Equal(AcademicAccessLevel.Metadata, borrowable.AccessLevel);
        Assert.Null(borrowable.FullTextUrl);
        Assert.Contains("hasLendingCopy\":true", borrowable.SafeMetadataJson);
        Assert.Equal("https://openlibrary.org/books/OL25000000M", borrowable.LandingUrl);

        var openRow = results.First(record => record.ProviderWorkId == "/works/OL19000000W");
        Assert.Equal("9789290000006", openRow.Isbn13);
        Assert.Equal(AcademicAccessLevel.FullTextIndicated, openRow.AccessLevel); // ebook_access == public
        Assert.Equal("https://openlibrary.org/works/OL19000000W", openRow.LandingUrl);

        var query = AcademicFixture.Query(handler.LastRequest);
        Assert.Equal("essential medicine regulation", query["q"]);
        Assert.Contains("isbn", query["fields"]);
        Assert.Equal("0", query["offset"]);
    }

    [Fact]
    public async Task Row_without_provider_title_is_dropped_instead_of_fabricating_one()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json(OpenLibraryFixtures.UntitledRow));
        var results = await adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "ghost" });
        Assert.Empty(results);
    }

    [Fact]
    public async Task Isbn_lookup_hits_isbn_facet_and_skips_invalid_identifiers_without_traffic()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenLibraryFixtures.IsbnPage("9781234567897")));
        var results = await adapter.LookupByIsbnAsync(["978-1-2345-6789-7", "totally-invalid", "1234567890"]);
        Assert.Single(results);
        Assert.Equal(1, handler.RequestCount); // only the one verified ISBN produced a lookup
        var query = AcademicFixture.Query(handler.LastRequest);
        Assert.Equal("9781234567897", query["isbn"]);
        var record = Assert.Single(results);
        Assert.Equal("9781234567897", record.Isbn13);
        // search_only access: the catalog can show records about the book, never the book.
        Assert.Equal(AcademicAccessLevel.Metadata, record.AccessLevel);
        Assert.Contains("hasLendingCopy\":true", record.SafeMetadataJson);
    }

    [Fact]
    public async Task Year_and_open_access_filters_map_to_provider_parameters()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenLibraryFixtures.EmptyPage));
        await adapter.SearchBooksAsync(new AcademicDiscoveryQuery
        {
            SearchTerm = "x",
            YearFrom = 2015,
            YearTo = 2023,
            OpenAccessOnly = true,
        });
        var query = AcademicFixture.Query(handler.LastRequest);
        Assert.Equal("2015", query["published_start"]);
        Assert.Equal("2023", query["published_end"]);
        Assert.Equal("public", query["ebook_access"]);
    }

    [Fact]
    public async Task Pagination_stays_within_configured_page_budget()
    {
        var (adapter, handler) = Create(
            (_, _) => AcademicFixture.Json(OpenLibraryFixtures.SearchPage),
            options => { options.MaxPerPage = 2; options.MaxPages = 3; });
        var results = await adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "paging", MaxResults = 10 });
        // 10 results at 2/page would need 5 pages; the configured budget stops at 3.
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(6, results.Count);
    }

    [Fact]
    public async Task Server_errors_retry_bounded_then_report_transient_safely()
    {
        var (adapter, handler) = Create(
            (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            options => options.MaxAttempts = 2);
        await Assert.ThrowsAsync<AcademicSourceUnavailableException>(() =>
            adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Disabled_adapter_reports_unconfigured_and_makes_no_calls()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(OpenLibraryFixtures.SearchPage), options => options.OpenLibraryEnabled = false);
        Assert.False(adapter.IsConfigured);
        await Assert.ThrowsAsync<AcademicSourceUnavailableException>(() => adapter.LookupByIsbnAsync(["9781234567897"]));
        Assert.Equal(0, handler.RequestCount);
    }
}
