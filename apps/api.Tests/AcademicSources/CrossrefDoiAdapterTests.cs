using System.Net;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class CrossrefDoiAdapterTests
{
    private static (CrossrefDoiAdapter adapter, ScriptedHandler handler) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> script,
        Action<AcademicSourceDiscoveryOptions>? configure = null)
    {
        var handler = new ScriptedHandler(script);
        var options = AcademicFixture.Defaults(configure);
        var adapter = new CrossrefDoiAdapter(AcademicFixture.Client(handler), AcademicFixture.Wrapped(options), AcademicFixture.Logger<CrossrefDoiAdapter>());
        return (adapter, handler);
    }

    [Fact]
    public async Task Registered_doi_yields_verified_bibliographic_record_without_full_text_claim()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.RegisteredArticle));
        var result = await adapter.ValidateAsync("https://doi.org/10.1016/J.KNR.2021.02.003");

        Assert.True(result.IsRegistered);
        Assert.Equal("10.1016/j.knr.2021.02.003", result.ResolvedDoi);
        var source = result.Source!;
        Assert.Equal("crossref", source.ProviderKey);
        Assert.Equal("10.1016/j.knr.2021.02.003", source.Doi);
        Assert.Equal("Pharmaceutical market access in the Kurdistan Region of Iraq", source.Title);
        Assert.Equal(["Dara Rahman", "Shilan Karim"], source.Authors);
        Assert.Equal("Kurdish Health Policy Review", source.ContainerTitle);
        Assert.Equal("Example Academic Press", source.Publisher);
        Assert.Equal(2021, source.PublicationYear);
        Assert.Equal("12", source.Volume);
        Assert.Equal("4", source.Issue);
        Assert.Equal("110-128", source.PageRange);
        Assert.Equal(AcademicSourceTypes.JournalArticle, source.SourceType);
        Assert.Equal(21, source.CitationCount);
        // A publisher link alone never becomes a full-text claim; only the JATS abstract raises
        // the marker, and the tags are stripped from the stored text.
        Assert.Equal(AcademicAccessLevel.Abstract, source.AccessLevel);
        Assert.Null(source.FullTextUrl);
        Assert.Equal("Aims to assess registration delays for essential medicines.", source.Abstract);
        Assert.Equal("https://doi.org/10.1016/j.knr.2021.02.003", source.LandingUrl);
        Assert.Contains("resourceLinksAdvertised\":1", source.SafeMetadataJson);
        Assert.Contains("referenceCount\":38", source.SafeMetadataJson);

        var url = handler.LastRequest.RequestUri!;
        Assert.Equal("api.crossref.org", url.Host);
        Assert.Equal("/works/10.1016/j.knr.2021.02.003", url.AbsolutePath);
    }

    [Fact]
    public async Task Unregistered_doi_is_reported_not_registered_instead_of_throwing()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.NotFound, HttpStatusCode.NotFound));
        var result = await adapter.ValidateAsync("10.9999/nonexistent.12345");
        Assert.False(result.IsRegistered);
        Assert.Equal("not_found", result.FailureCategory);
        Assert.Null(result.Source);
    }

    [Fact]
    public async Task Invalid_doi_syntax_never_reaches_the_network()
    {
        foreach (var bad in new[] { "not-a-doi", "10.12/short", "https://example.org/10.1234/x", "10.1234567", "" })
        {
            var (adapter, handler) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.RegisteredArticle));
            var result = await adapter.ValidateAsync(bad);
            Assert.False(result.IsRegistered);
            Assert.Equal("invalid_identifier", result.FailureCategory);
            Assert.Equal(0, handler.RequestCount);
        }
    }

    [Fact]
    public async Task Hostile_doi_suffix_cannot_escape_the_works_endpoint()
    {
        // Path traversal markers are refused outright: they would collapse into a different API route.
        var (refusal, refusalHandler) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.RegisteredArticle));
        var hostile = await refusal.ValidateAsync("10.1234/../admin?dump=1#frag");
        Assert.False(hostile.IsRegistered);
        Assert.Equal("invalid_identifier", hostile.FailureCategory);
        Assert.Equal(0, refusalHandler.RequestCount);

        // Query and fragment delimiters inside a legitimate suffix are percent-encoded so the
        // request cannot grow a query string or fragment of its own.
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.RegisteredArticle));
        await adapter.ValidateAsync("10.1234/a?b=c#d");
        var url = handler.LastRequest.RequestUri!;
        Assert.Equal("/works/10.1234/a%3fb=c%23d", url.AbsolutePath);
        Assert.Empty(url.Query);
        Assert.Empty(url.Fragment);
    }

    [Fact]
    public async Task Book_registration_carries_verified_isbn_pair()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.RegisteredBook));
        var result = await adapter.ValidateAsync("10.1007/978-3-030-50000-0");
        Assert.True(result.IsRegistered);
        var source = result.Source!;
        Assert.Equal("9783030500009", source.Isbn13);
        Assert.Equal(AcademicSourceTypes.Book, source.SourceType);
        Assert.Equal("Institute of Health Economics", Assert.Single(source.Authors));
        Assert.Equal(AcademicAccessLevel.Metadata, source.AccessLevel);
    }

    [Fact]
    public async Task Electronic_article_number_is_not_reported_as_false_page_range_data_loss()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.RegisteredElectronicArticle));
        var result = await adapter.ValidateAsync("10.2139/ssrn.3800000");
        var source = result.Source!;
        // Crossref literally printed "vp" as the page field. It is kept verbatim and never
        // converted into numeric pages; the access level stays metadata-only.
        Assert.Equal("vp", source.PageRange);
        Assert.Equal(AcademicAccessLevel.Metadata, source.AccessLevel);
        Assert.Null(source.Abstract);
    }

    [Fact]
    public async Task Provider_rate_limit_is_categorized_safely_in_the_result()
    {
        var (adapter, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        });
        var result = await adapter.ValidateAsync("10.1234/x");
        Assert.False(result.IsRegistered);
        Assert.Equal("rate_limited", result.FailureCategory);
    }

    [Fact]
    public async Task Malformed_success_payload_is_not_reported_as_registered()
    {
        var (adapter, _) = Create((_, _) => AcademicFixture.Json("{\"status\":\"ok\"}"));
        var result = await adapter.ValidateAsync("10.1234/x");
        Assert.False(result.IsRegistered);
        Assert.Equal("malformed", result.FailureCategory);
    }

    [Fact]
    public async Task Disabled_switch_skips_lookups_without_traffic()
    {
        var (adapter, handler) = Create((_, _) => AcademicFixture.Json(CrossrefFixtures.RegisteredArticle), options => options.CrossrefEnabled = false);
        Assert.False(adapter.IsConfigured);
        var result = await adapter.ValidateAsync("10.1234/x");
        Assert.False(result.IsRegistered);
        Assert.Equal("configuration", result.FailureCategory);
        Assert.Equal(0, handler.RequestCount);
    }
}
