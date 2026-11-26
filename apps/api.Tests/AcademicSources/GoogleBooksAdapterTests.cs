using System.Net;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class GoogleBooksAdapterTests
{
    private static (GoogleBooksAdapter adapter, ScriptedHandler handler) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> script,
        Action<AcademicSourceDiscoveryOptions>? configure = null)
    {
        var handler = new ScriptedHandler(script);
        var options = AcademicFixture.Defaults(configure);
        var adapter = new GoogleBooksAdapter(AcademicFixture.Client(handler), AcademicFixture.Wrapped(options), AcademicFixture.Logger<GoogleBooksAdapter>());
        return (adapter, handler);
    }

    [Fact]
    public async Task Requires_a_configured_key_and_makes_no_calls_without_one()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(GoogleBooksFixtures.VolumesPage), options =>
        {
            options.GoogleBooksEnabled = true;
            options.GoogleBooksApiKey = null;
        });
        Assert.False(adapter.IsConfigured);
        await Assert.ThrowsAsync<AcademicSourceUnavailableException>(() =>
            adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Volumes_page_maps_identifiers_views_and_keeps_descriptions_out_of_abstracts()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(GoogleBooksFixtures.VolumesPage), options =>
        {
            options.GoogleBooksEnabled = true;
            options.GoogleBooksApiKey = "configured-test-key";
        });
        Assert.True(adapter.IsConfigured);
        var results = await adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "pharmaceutical regulation" });

        Assert.Equal(2, results.Count);
        var preview = results.First(record => record.ProviderWorkId == "_lYfBgAAQBAJ");
        Assert.Equal("googlebooks", preview.ProviderKey);
        Assert.Equal("9789004363168", preview.Isbn13);
        Assert.Equal("9004363165", preview.Isbn10);
        Assert.Equal(2018, preview.PublicationYear);
        Assert.Equal(412, preview.PageCount);
        // PARTIAL preview is marketing, not readable content.
        Assert.Equal(AcademicAccessLevel.Metadata, preview.AccessLevel);
        Assert.Null(preview.FullTextUrl);
        Assert.Null(preview.Abstract);
        // The blurb is kept, but visibly typed as a publisher description.
        Assert.Equal("A comparative study of pricing and reimbursement frameworks.", preview.PublisherDescription);
        Assert.StartsWith("https://books.example.com/books/about/", preview.LandingUrl);

        var publicDomain = results.First(record => record.ProviderWorkId == "public-domain-vol");
        Assert.Equal(AcademicAccessLevel.FullTextIndicated, publicDomain.AccessLevel); // FULL + public domain + pdf available
        Assert.Equal(1931, publicDomain.PublicationYear);
        Assert.Null(publicDomain.Isbn13); // the provider reported no identifier; nothing is invented

        var query = AcademicFixture.Query(handler.LastRequest);
        Assert.Equal("pharmaceutical regulation", query["q"]);
        Assert.Equal("configured-test-key", query["key"]);
        Assert.Equal("0", query["startIndex"]);
    }

    [Fact]
    public async Task Isbn_lookup_uses_isbn_query_operator()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(GoogleBooksFixtures.NoResults), options =>
        {
            options.GoogleBooksEnabled = true;
            options.GoogleBooksApiKey = "configured-test-key";
        });
        var results = await adapter.LookupByIsbnAsync(["978-1-2345-6789-7", "invalid"]);
        Assert.Empty(results);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("isbn:9781234567897", AcademicFixture.Query(handler.LastRequest)["q"]);
    }

    [Fact]
    public async Task Check_digit_invalid_identifier_never_becomes_an_isbn()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json(GoogleBooksFixtures.BadIsbnVolume), options =>
        {
            options.GoogleBooksEnabled = true;
            options.GoogleBooksApiKey = "configured-test-key";
        });
        var results = await adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "trap" });
        var record = Assert.Single(results);
        Assert.Null(record.Isbn13);
        Assert.Null(record.Isbn10);
        Assert.Equal(AcademicAccessLevel.Metadata, record.AccessLevel);
    }

    [Theory]
    [InlineData("2018", 2018)]
    [InlineData("2018-05", 2018)]
    [InlineData("2018-05-08", 2018)]
    [InlineData("circa 1900", null)]
    [InlineData("2018-13", null)]
    [InlineData("", null)]
    public void Published_date_year_is_only_taken_from_iso_shapes(string input, int? expected)
        => Assert.Equal(expected, GoogleBooksAdapter.ReadYear(input));

    [Fact]
    public async Task Unauthorized_key_response_is_reported_as_configuration_failure_safely()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json(
            """{"error":{"code":401,"message":"Invalid Credentials","status":"UNAUTHENTICATED"}}""",
            HttpStatusCode.Unauthorized), options =>
        {
            options.GoogleBooksEnabled = true;
            options.GoogleBooksApiKey = "bad-key";
        });
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() =>
            adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "x" }));
        Assert.Equal("configuration", exception.Details?.FailureCategory);
        // The provider's error message never travels into the exception text.
        Assert.DoesNotContain("UNAUTHENTICATED", exception.Message);
        Assert.DoesNotContain("Invalid Credentials", exception.Message);
    }
}
