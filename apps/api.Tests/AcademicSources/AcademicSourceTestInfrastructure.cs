using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.AcademicSources;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// Scriptable HTTP handler: responses are chosen per request number, requests are captured for
/// assertions, and no test ever leaves the process. The fixtures below are hand-reduced shapes of
/// the documented provider contracts.
/// </summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _script;

    public ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> script) => _script = script;

    public List<HttpRequestMessage> Requests { get; } = [];
    public HttpRequestMessage LastRequest => Requests[^1];
    public int RequestCount => Requests.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(_script(request, Requests.Count));
    }
}

internal static class AcademicFixture
{
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static HttpClient Client(ScriptedHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Test options. Production leaves every switch off, so the ones these tests need are turned
    /// on here explicitly: a default change cannot silently alter what is exercised, and the
    /// fail-closed posture itself stays under direct test.
    /// </summary>
    public static AcademicSourceDiscoveryOptions Defaults(Action<AcademicSourceDiscoveryOptions>? configure = null)
    {
        var options = new AcademicSourceDiscoveryOptions
        {
            Enabled = true,
            OpenAlexEnabled = true,
            CrossrefEnabled = true,
            OpenLibraryEnabled = true,
            BackoffBaseMilliseconds = 0,
            MaxRetryDelaySeconds = 0,
            RequestTimeoutSeconds = 10,
            OperationTimeoutSeconds = 20,
            ContactMailto = "discovery@taslim.test",
        };
        configure?.Invoke(options);
        return options.Normalize();
    }

    /// <summary>
    /// Deterministic resolver: a fixed answer table, never the system resolver, so rebinding is
    /// reproducible and no unit test depends on DNS.
    /// </summary>
    public sealed class StubResolver : IAcademicHostResolver
    {
        private readonly IReadOnlyList<IPAddress> _answers;

        public StubResolver(params string[] answers) => _answers = [.. answers.Select(IPAddress.Parse)];

        public List<string> ResolvedHosts { get; } = [];

        public int ResolveCount => ResolvedHosts.Count;

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            ResolvedHosts.Add(host);
            return Task.FromResult(_answers);
        }
    }

    /// <summary>
    /// Stands in for the socket layer: records the exact endpoint the policy decided to dial and
    /// never opens one, which is what makes "refused before connect" assertable.
    /// </summary>
    public sealed class RecordingTransport
    {
        public List<IPEndPoint> Dialed { get; } = [];

        public ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            Dialed.Add(new IPEndPoint(address, port));
            throw new InvalidOperationException("stub transport never dials a socket");
        }
    }

    /// <summary>Client whose defaults carry a bearer credential, like a caller that adds auth later.</summary>
    public static HttpClient ClientWithAuthorization(ScriptedHandler handler)
    {
        var client = Client(handler);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "seeded-credential");
        return client;
    }

    public static IOptions<AcademicSourceDiscoveryOptions> Wrapped(AcademicSourceDiscoveryOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    public static NullLogger<T> Logger<T>() => NullLogger<T>.Instance;

    public static Dictionary<string, string> Query(HttpRequestMessage request)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = request.RequestUri!.Query.TrimStart('?');
        if (query.Length == 0) return result;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var key = index < 0 ? pair : pair[..index];
            var value = index < 0 ? string.Empty : pair[(index + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return result;
    }
}

internal static class OpenAlexFixtures
{
    /// <summary>Page wrapper with a single open-access article carrying an inverted-index abstract
    /// with a withheld word (gap between positions 2 and 5), plus a closed article and an entry
    /// whose landing URL embeds credentials that the safety guard must drop.</summary>
    public const string SearchPage = """
    {"meta":{"count":3,"db_response_time_ms":18,"page":1,"per-page":25},
     "results":[
      {"id":"https://api.openalex.org/works/W4210177603",
       "doi":"https://doi.org/10.1016/J.KR.2021.02.003",
       "display_name":"Pharmaceutical market access in the Kurdistan Region of Iraq",
       "publication_year":2021,
       "language":"en",
       "type":"article",
       "cited_by_count":14,
       "referenced_by_count":2,
       "relevance_score":412.7,
       "authorships":[
         {"author":{"display_name":"D. Rahman"}},
         {"author":{"display_name":"S. Karim"}}],
       "biblio":{"volume":"12","issue":"3","first_page":"110","last_page":"128"},
       "abstract_inverted_index":{"This":[0],"examines":[1],"market":[2],"access":[5],"regulation":[7],"in":[8],"Kurdistan":[9],"Region":[10],"of":[11],"Iraq":[12],"and":[13],"its":[14],"reimbursement":[15],"policy":[16]},
       "open_access":{"is_oa":true,"oa_status":"hybrid","oa_url":"https://journal.example.org/articles/45"},
       "primary_location":{"landing_page_url":"https://journal.example.org/articles/45","source":{"display_name":"Kurdish Health Policy Review"}},
       "best_oa_location":{"is_oa":true,"pdf_url":"https://journal.example.org/articles/45.pdf","landing_page_url":"https://journal.example.org/articles/45"}},
      {"id":"https://api.openalex.org/works/W2987000001",
       "doi":"https://doi.org/10.1007/s13347-020-00405-x",
       "display_name":"Essential medicines lists in fragile regulatory systems",
       "publication_year":2020,
       "type":"article",
       "cited_by_count":5,
       "authorships":[{"author":{"display_name":"A. Jafari"}}],
       "biblio":{"volume":null,"issue":null,"first_page":null,"last_page":null},
       "open_access":{"is_oa":false,"oa_status":"closed","oa_url":null},
       "primary_location":{"landing_page_url":"https://link.springer.example/article/10.1007/s13347-020-00405-x","source":{"display_name":"Pharmaceutical Policy"}},
       "best_oa_location":null},
      {"id":"https://api.openalex.org/works/W2999000002",
       "display_name":"Pricing and reimbursement datasets",
       "publication_year":2019,
       "type":"dataset",
       "open_access":{"is_oa":false,"oa_status":"closed"},
       "primary_location":{"landing_page_url":"https://trusted.example:token@internal-host.example/report","source":null},
       "best_oa_location":null}]}
    """;

    public const string EmptyPage = """{"meta":{"count":0,"page":1,"per-page":25},"results":[]}""";

    public static string PageWithWorks(int count)
    {
        var works = Enumerable.Range(0, count).Select(index =>
            $$"""{"id":"https://api.openalex.org/works/W{{index:D8}}","display_name":"Bounded page result {{index}}","type":"article","publication_year":2020,"open_access":{"is_oa":false},"primary_location":null,"best_oa_location":null,"authorships":[]}""");
        return $$"""{"meta":{"count":1000,"page":1,"per-page":{{count}}},"results":[{{string.Join(',', works)}}]}""";
    }
}

internal static class CrossrefFixtures
{
    public const string RegisteredArticle = """
    {"status":"ok","message-type":"work","message":{
      "reference-count":38,"is-referenced-by-count":21,
      "DOI":"10.1016/j.knr.2021.02.003","type":"journal-article",
      "created":{"date-parts":[[2021,2,15]]},
      "issued":{"date-parts":[[2021,4,1]]},
      "title":["Pharmaceutical market access in the Kurdistan Region of Iraq"],
      "subtitle":[],
      "container-title":["Kurdish Health Policy Review"],
      "language":"en",
      "link":[{"URL":"https://publisher.example/link/articles/knr.2021.02.003","content-type":"unspecified"}],
      "publisher":"Example Academic Press",
      "volume":"12",
      "issue":"4",
      "published-print":{"date-parts":[[2021,4]]},
      "page":"110-128",
      "author":[{"given":"Dara","family":"Rahman","sequence":"first"},{"given":"Shilan","family":"Karim","sequence":"additional"}],
      "abstract":"<jats:p xmlns:jats=\"\">Aims to assess registration delays for essential medicines.</jats:p>",
      "url":"https://api.crossref.org/works/10.1016/j.knr.2021.02.003"}}
    """;

    public const string RegisteredBook = """
    {"status":"ok","message-type":"work","message":{
      "reference-count":0,"is-referenced-by-count":3,
      "DOI":"10.1007/978-3-030-50000-0","type":"monograph",
      "title":["Regulatory Pharma Economics"],
      "container-title":["Springer Textbooks"],
      "publisher":"Springer Nature",
      "published-online":{"date-parts":[[2020,9,10]]},
      "author":[{"name":"Institute of Health Economics"}],
      "identifier":[{"type":"isbn","ID":"978-3-030-50000-9"}]}}
    """;

    public const string NotFound = """{"status":"failed","message-type":"error","error":"not_found that resource does not exist"}""" ;

    /// <summary>An electronic-only article with an article-number style value instead of a page range.</summary>
    public const string RegisteredElectronicArticle = """
    {"status":"ok","message-type":"work","message":{
      "reference-count":12,"is-referenced-by-count":0,
      "DOI":"10.2139/ssrn.3800000","type":"journal-article",
      "title":["Reimbursement policy in emerging jurisdictions"],
      "container-title":["Health Policy and Planning"],
      "publisher":"Oxford Example",
      "issued":{"date-parts":[[2022,6]]},
      "page":"vp",
      "author":[{"given":"H.","family":"Ahmed"}]}}
    """;
}

internal static class OpenLibraryFixtures
{
    public const string SearchPage = """
    {"numFound":2,"start":0,"docs":[
      {"key":"/books/OL25000000M","title":"Essential Medicine Regulation",
       "author_name":["L. Barzani","T. Aziz"],"publisher":["Kurdistan University Press"],
       "first_publish_year":2018,"language":["eng"],"number_of_pages_median":342,
       "isbn":["9781234567897","123456789X"],"ia":["essentialmedicine0000barz"],
       "ebook_access":"borrow_all","lending_edition":"OL25000000M","readable":true,
       "oclc":[1000000001],"cover_i":12345},
      {"key":"/works/OL19000000W","title":"Open Health Data Handbook",
       "author_name":["WHO Regional Office"],"publisher":["Public Health Press"],
       "first_publish_year":2019,"language":["eng"],"number_of_pages_median":98,
       "isbn":["9789290000006"],"ebook_access":"public",
       "oclc":[1100000002]}]}
    """;

    public const string EmptyPage = """{"numFound":0,"start":0,"docs":[]}""";

    public const string UntitledRow = """
    {"numFound":1,"start":0,"docs":[{"key":"/books/OL99999999M","author_name":["Anonymous"],"isbn":["9781234567897"]}]}
    """;

    public static string IsbnPage(string isbn) =>
        $$"""{"numFound":1,"start":0,"docs":[{"key":"/books/OL12345678M","title":"Market Access in Fragile States","author_name":["R. Aziz"],"publisher":["Academic Press"],"first_publish_year":2017,"isbn":["{{isbn}}"],"number_of_pages_median":256,"ebook_access":"search_only","ia":["marketaccessfragi000000aziz"]}]}""";
}

internal static class GoogleBooksFixtures
{
    public const string VolumesPage = """
    {"kind":"books#volumes","totalItems":2,"items":[
      {"kind":"books#volume","id":"_lYfBgAAQBAJ",
       "volumeInfo":{"title":"Pharmaceutical Regulation and Market Access",
         "authors":["K. Halbert","M. Saleh"],"publisher":"Brill Academic","publishedDate":"2018-05-08",
         "industryIdentifiers":[{"type":"ISBN_13","identifier":"9789004363168"},{"type":"ISBN_10","identifier":"9004363165"}],
         "pageCount":412,"canonicalVolumeLink":"https://books.example.com/books/about/Pharmaceutical_Regulation?id=_lYfBgAAQBAJ",
         "languageCode":"en","description":"A comparative study of pricing and reimbursement frameworks.",
         "categories":["BUSINESS & ECONOMICS / Health Care"]},
       "accessInfo":{"accessViewStatus":"PARTIAL","epub":{"isAvailable":false},"pdf":{"isAvailable":true},
         "isPublicDomain":false,"viewability":"PARTIAL","webReaderLink":"https://books.example.com/reader?id=_lYfBgAAQBAJ"}},
      {"kind":"books#volume","id":"public-domain-vol",
       "volumeInfo":{"title":"History of Public Health in Kurdistan","authors":["J. Bakr"],"publisher":"Press of the Region","publishedDate":"1931",
         "canonicalVolumeLink":"https://books.example.com/books/about/History?id=public-domain-vol","languageCode":"en"},
       "accessInfo":{"accessViewStatus":"FULL","epub":{"isAvailable":false},"pdf":{"isAvailable":true},
         "isPublicDomain":true,"viewability":"ALL_PAGES"}}]}
    """;

    public const string NoResults = """{"kind":"books#volumes","totalItems":0}""";

    /// <summary>Volume whose identifier fails the ISBN check digit; must yield no identifier.</summary>
    public const string BadIsbnVolume = """
    {"kind":"books#volumes","totalItems":1,"items":[{"id":"bad-vol","volumeInfo":{"title":"Check Digit Trap","industryIdentifiers":[{"type":"ISBN_13","identifier":"9781234567890"}]},"accessInfo":{"accessViewStatus":"NONE","pdf":{"isAvailable":false},"epub":{"isAvailable":false},"isPublicDomain":false}}]}
    """;
}
