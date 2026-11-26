using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// Admission of catalog-advertised addresses before they are handed to the studio as clickable links
/// (issue #1267 slice review, round 9).
///
/// The three groups below are deliberately separated, because the honest claim is different for each:
///
/// 1. What <see cref="AcademicProviderLinkPolicy"/> refuses and the shared research boundary does not:
///    dotted machine-local names (RFC 6761/6762 special-use, mDNS, home/intranet spellings) and a 6to4
///    literal that embeds a loopback address. Those are the links a reader can actually be walked into
///    their own network by.
/// 2. What stays clickable: publisher links in either scheme, including public IPv6 spellings and the
///    NAT64 well-known prefix that #1273 reduced to a public embedded IPv4.
/// 3. Where the refusal is enforced: the DTO choke point, exercised through the real OpenAlex and
///    Google Books adapters rather than by calling the policy directly, so the assertion covers the
///    path a provider payload actually travels.
///
/// What no test here claims: that checking an address at ingestion pins anything the browser does
/// later. A hostname that resolves differently when the student clicks it is still clicked, and
/// <see cref="Display_check_is_syntactic_and_does_not_claim_dns_rebinding_protection"/> pins that the
/// check is syntactic and never touches DNS.
/// </summary>
public sealed class AcademicProviderLinkAdmissionTests
{
    // ---------------------------------------------------------------------------------------------
    // Group 1 — addresses that must never be displayed
    // ---------------------------------------------------------------------------------------------

    [Theory]
    // Loopback and machine-local spellings, in every shape a browser still resolves locally.
    [InlineData("http://127.0.0.1:8080/stolen")]
    [InlineData("http://localhost:9200/elastic")]
    [InlineData("http://LOCALHOST./report")]
    [InlineData("http://api.localhost/health")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://2130706433/")]          // decimal 127.0.0.1
    [InlineData("http://0177.0.0.1/")]          // octal-encoded 127.0.0.1
    [InlineData("http://0x7f.0.0.1/")]          // hex-encoded 127.0.0.1
    [InlineData("http://127.1/")]               // short-form dotted
    [InlineData("http://0.0.0.0/")]
    // Cloud identity metadata and other non-public literals.
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]   // NAT64 well-known embedding loopback
    [InlineData("http://[2002:7f00:1::]/")]     // 6to4 embedding loopback
    [InlineData("http://[2001:db8::1]/")]       // documentation range
    [InlineData("http://[fe80::1%eth0]/")]      // scoped link-local: refused rather than guessed at
    // Private and single-label network names, plus the reserved local-only suffixes.
    [InlineData("http://nas/admin")]
    [InlineData("http://host.test/x")]
    [InlineData("http://db.internal/x")]
    [InlineData("http://nas.local:5001/x")]
    [InlineData("http://printer.lan/job")]
    [InlineData("http://router.home/setup")]
    [InlineData("http://staging.intranet/docs")]
    [InlineData("http://x.invalid/")]
    [InlineData("http://box.localdomain/x")]
    // Not a displayable link at all, whatever the host claims.
    [InlineData("http://user:pass@openalex.org/W2741770304")]
    [InlineData("https://publisher.example@collector.invalid/report")]
    [InlineData("ftp://publisher.example/work.pdf")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Non_public_or_unparseable_addresses_are_never_displayable(string? url) =>
        Assert.False(AcademicProviderLinkPolicy.IsDisplayable(url));

    [Theory]
    // The public scholarly web the feature is supposed to link to.
    [InlineData("https://openalex.org/W2741770304")]
    [InlineData("https://books.google.com/books?id=_lYfBgAAQBAJ")]
    [InlineData("https://doi.org/10.1016/j.knr.2021.02.003")]
    [InlineData("https://journal.example.org/articles/45.pdf")]
    [InlineData("http://arxiv.org/pdf/1234")]                    // plain http to a real host stays allowed
    [InlineData("https://en.m.wikipedia.org/wiki/Kurdistan_Region")]
    [InlineData("https://kurdistan-health.example/report")]      // fixture host, public-shaped name
    [InlineData("https://example.com./x")]                       // root dot is not a different target
    [InlineData("https://arxiv1.example.org/x")]                 // digits inside a label are ordinary
    [InlineData("https://192.168-not-private.example/x")]        // a name that merely looks numeric-ish
    [InlineData("https://[2606:4700:4700::1111]/x")]              // public IPv6 literal
    [InlineData("https://[64:ff9b::d5c6:d6d7]/x")]                // NAT64 well-known with public embedded v4
    public void Public_publisher_addresses_stay_displayable(string url) =>
        Assert.True(AcademicProviderLinkPolicy.IsDisplayable(url));

    [Fact]
    public void The_added_scope_is_exactly_local_only_names_and_encoded_literals()
    {
        // Measured at head b904bcae: the shared research boundary already refused the plain loopback
        // spellings named in the review, so nothing here re-does that. What it does NOT refuse, and
        // what this policy now does, is a dotted name that only ever means "somewhere on the reader's
        // own network", and an IPv6 literal that carries a private address inside it.
        foreach (var added in new[] { "http://host.test/x", "http://db.internal/x", "http://nas.local/x", "http://printer.lan/x", "http://router.home/x", "http://staging.intranet/x", "http://x.invalid/x", "http://[2002:7f00:1::]/" })
        {
            Assert.True(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe(added), $"shared research policy admits {added}");
            Assert.False(AcademicProviderLinkPolicy.IsDisplayable(added), $"display admission must refuse {added}");
        }
        // And the review's headline examples, pinned as already covered rather than newly fixed.
        foreach (var already in new[] { "http://127.0.0.1:8080/", "http://localhost/", "http://[::1]/" })
        {
            Assert.False(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe(already));
            Assert.False(AcademicProviderLinkPolicy.IsDisplayable(already));
        }
    }

    [Fact]
    public void The_shared_research_policy_is_still_the_boundary_for_persisted_citations()
    {
        // This change is additive: the shared predicate must keep answering its own question. A local
        // name is still not a citation there either, but a public link must not start disappearing
        // from research exports because a display filter was pushed down into it.
        Assert.False(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe("http://localhost/report"));
        Assert.True(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe("https://journal.example.org/articles/45"));
        Assert.True(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe("http://host.test/x"));
    }

    [Fact]
    public void Display_check_is_syntactic_and_does_not_claim_dns_rebinding_protection()
    {
        // A name that is public today and rebinds to 127.0.0.1 tomorrow is still admitted here,
        // because this check never resolves anything — deliberately, so that no provider can make
        // what a student sees depend on network state. Stated in a test so the limitation cannot be
        // quietly re-described as a fix.
        Assert.True(AcademicProviderLinkPolicy.IsDisplayable("https://rebinding.example/article"));
        Assert.False(AcademicProviderLinkPolicy.IsDisplayable("https://rebinding.example.localhost/article"));
    }

    // ---------------------------------------------------------------------------------------------
    // Group 3 — adapter → DTO, on real provider payloads
    // ---------------------------------------------------------------------------------------------

    private const string OpenAlexHostileWork = """
    {"meta":{"count":1,"page":1,"per-page":25},
     "results":[
      {"id":"https://api.openalex.org/works/W10000000001",
       "display_name":"Pricing and reimbursement in the Kurdistan Region",
       "publication_year":2021,"type":"article","cited_by_count":3,
       "authorships":[{"author":{"display_name":"D. Rahman"}}],
       "open_access":{"is_oa":true,"oa_status":"hybrid","oa_url":"http://nas.local:9200/elastic.pdf"},
       "primary_location":{"landing_page_url":"http://openalex-mirror.test/W10000000001","source":{"display_name":"Kurdish Health Policy Review"}},
       "best_oa_location":{"is_oa":true,"pdf_url":"http://nas.local:9200/elastic.pdf"}}]}
    """;

    private const string OpenAlexPublicWork = """
    {"meta":{"count":1,"page":1,"per-page":25},
     "results":[
      {"id":"https://api.openalex.org/works/W2741770304",
       "display_name":"Essential medicines lists in fragile regulatory systems",
       "publication_year":2020,"type":"article","cited_by_count":5,
       "authorships":[{"author":{"display_name":"A. Jafari"}}],
       "open_access":{"is_oa":true,"oa_status":"gold","oa_url":"https://journal.example.org/articles/45.pdf"},
       "primary_location":{"landing_page_url":"https://journal.example.org/articles/45","source":{"display_name":"Pharmaceutical Policy"}},
       "best_oa_location":{"is_oa":true,"pdf_url":"https://journal.example.org/articles/45.pdf"}}]}
    """;

    private static async Task<AcademicSearchSourceDto> OpenAlexDto(string payload)
    {
        var handler = new ScriptedHandler((_, _) => AcademicFixture.Json(payload));
        var options = AcademicFixture.Defaults();
        var adapter = new OpenAlexWorksAdapter(AcademicFixture.Client(handler), AcademicFixture.Wrapped(options), AcademicFixture.Logger<OpenAlexWorksAdapter>());
        var records = await adapter.SearchWorksAsync(new AcademicDiscoveryQuery { SearchTerm = "reimbursement policy kurdistan" });
        var record = Assert.Single(records);
        return AcademicSourcePresentation.ToDto(record);
    }

    [Fact]
    public async Task OpenAlex_local_cluster_landing_url_is_not_emitted_as_a_link()
    {
        var dto = await OpenAlexDto(OpenAlexHostileWork);

        Assert.Equal("Pricing and reimbursement in the Kurdistan Region", dto.Title);
        // The adapter's own (weaker, shared) predicate admitted these addresses, which is exactly why
        // the refusal has to sit where the DTO is built.
        Assert.Null(dto.LandingUrl);
        Assert.Null(dto.FullTextUrl);
        // A full-text claim resting on an address this service will not link is not shown as a claim
        // about a public location: the flag leaves with the link.
        Assert.False(dto.FullTextIndicated);
        Assert.Contains("content itself has not been read", dto.Provenance.AccessDescription);
        Assert.DoesNotContain("full-text location was indicated", dto.Provenance.AccessDescription);
        // Nothing else about the record is lost with the link.
        Assert.Equal(2021, dto.PublicationYear);
        Assert.Equal("Kurdish Health Policy Review", dto.ContainerTitle);
        Assert.Equal(3, dto.CitationCount);
    }

    [Fact]
    public async Task OpenAlex_public_journal_links_and_full_text_indication_survive()
    {
        var dto = await OpenAlexDto(OpenAlexPublicWork);

        Assert.Equal("https://journal.example.org/articles/45", dto.LandingUrl);
        Assert.Equal("https://journal.example.org/articles/45.pdf", dto.FullTextUrl);
        Assert.True(dto.FullTextIndicated);
        Assert.Contains("full-text location was indicated", dto.Provenance.AccessDescription);
        // Access stays an indication: no body text is ever carried.
        Assert.DoesNotContain("read the full", dto.Provenance.AccessDescription);
    }

    [Fact]
    public async Task GoogleBooks_internal_mirror_canonical_link_is_not_emitted_as_a_link()
    {
        var payload = """
        {"kind":"books#volumes","totalItems":1,"items":[
          {"kind":"books#volume","id":"mirror-vol",
           "volumeInfo":{"title":"Pharmaceutical Regulation and Market Access",
             "authors":["K. Halbert"],"publisher":"Brill Academic","publishedDate":"2018-05-08",
             "canonicalVolumeLink":"http://publisher-mirror.internal/books?id=mirror-vol",
             "languageCode":"en"},
           "accessInfo":{"accessViewStatus":"NONE","epub":{"isAvailable":false},"pdf":{"isAvailable":false},"isPublicDomain":false,"viewability":"NONE"}}]}
        """;
        var handler = new ScriptedHandler((_, _) => AcademicFixture.Json(payload));
        var options = AcademicFixture.Defaults(o => { o.GoogleBooksEnabled = true; o.GoogleBooksApiKey = "configured-test-key"; });
        var adapter = new GoogleBooksAdapter(AcademicFixture.Client(handler), AcademicFixture.Wrapped(options), AcademicFixture.Logger<GoogleBooksAdapter>());
        var records = await adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "pharmaceutical regulation" });

        var record = Assert.Single(records);
        Assert.Equal("http://publisher-mirror.internal/books?id=mirror-vol", record.LandingUrl); // admitted by the shared predicate
        var dto = AcademicSourcePresentation.ToDto(record);
        Assert.Null(dto.LandingUrl);
        Assert.Null(dto.FullTextUrl);
        Assert.False(dto.FullTextIndicated);
        Assert.Equal("Brill Academic", dto.Publisher);
    }

    [Fact]
    public async Task GoogleBooks_public_canonical_link_survives_and_domain_without_a_url_keeps_its_indication()
    {
        var handler = new ScriptedHandler((_, _) => AcademicFixture.Json(GoogleBooksFixtures.VolumesPage));
        var options = AcademicFixture.Defaults(o => { o.GoogleBooksEnabled = true; o.GoogleBooksApiKey = "configured-test-key"; });
        var adapter = new GoogleBooksAdapter(AcademicFixture.Client(handler), AcademicFixture.Wrapped(options), AcademicFixture.Logger<GoogleBooksAdapter>());
        var records = await adapter.SearchBooksAsync(new AcademicDiscoveryQuery { SearchTerm = "pharmaceutical regulation" });
        var dtos = records.Select(AcademicSourcePresentation.ToDto).ToArray();

        // A public-domain volume advertises no address at all, only an access level; that indication
        // is what the catalog asserted and stays, so the change cannot silently downgrade honest rows.
        var publicDomain = Assert.Single(dtos, dto => dto.Title == "History of Public Health in Kurdistan");
        Assert.True(publicDomain.FullTextIndicated);
        Assert.Null(publicDomain.FullTextUrl);

        var preview = Assert.Single(dtos, dto => dto.Title == "Pharmaceutical Regulation and Market Access");
        Assert.StartsWith("https://books.example.com/books/about/", preview.LandingUrl);
        Assert.False(preview.FullTextIndicated);
    }

    [Fact]
    public void A_record_built_by_the_merger_is_filtered_at_the_same_choke_point()
    {
        // Merging keeps the field of whichever catalog won, so a hostile address that survives a merge
        // must meet the same refusal. One gate, not one per adapter.
        var merged = AcademicSourceMerger.Merge([
            new AcademicSourceRecord
            {
                ProviderKey = "openalex",
                ProviderWorkId = "W1",
                Doi = "10.1016/j.knr.2021.02.003",
                Title = "Market Access",
                SourceType = AcademicSourceTypes.JournalArticle,
                AccessLevel = AcademicAccessLevel.FullTextIndicated,
                LandingUrl = "http://openalex-mirror.test/W1",
                FullTextUrl = "http://192.168.1.10/router/admin",
            },
        ], 100);
        var dto = AcademicSourcePresentation.ToDto(Assert.Single(merged));

        Assert.Null(dto.LandingUrl);
        Assert.Null(dto.FullTextUrl);
        Assert.False(dto.FullTextIndicated);
    }

    [Fact]
    public void The_projection_never_rewrites_a_link_it_refuses()
    {
        // Either the address the catalog gave is shown exactly, or nothing is shown: no host swap, no
        // scheme upgrade, no trimmed variant that a reviewer would have to re-derive.
        Assert.Equal("https://doi.org/10.1016/j.knr.2021.02.003", AcademicProviderLinkPolicy.Project("  https://doi.org/10.1016/j.knr.2021.02.003  "));
        Assert.Null(AcademicProviderLinkPolicy.Project("http://db.internal/x"));
        Assert.Null(AcademicProviderLinkPolicy.Project(null));
        Assert.Null(AcademicProviderLinkPolicy.Project("   "));
    }
}
