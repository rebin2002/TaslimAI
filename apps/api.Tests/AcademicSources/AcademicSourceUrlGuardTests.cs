using System.Net;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class AcademicSourceUrlGuardTests
{
    private static readonly string[] AllowedHosts = ["api.openalex.org", "api.crossref.org", "openlibrary.org", "www.googleapis.com"];

    [Theory]
    [InlineData("https://api.openalex.org/works?search=x", true)]
    [InlineData("http://api.crossref.org/works/10.1234/x", true)] // http permitted for legacy provider bases
    [InlineData("https://sub.openlibrary.org/search.json", false)] // exact-host ACL: breadth is not implied
    [InlineData("https://api.openalex.org:8443/works", true)]      // same host, alternate port is still the host
    [InlineData("https://API.OPENALEX.ORG./works", true)]          // case and trailing dot are not bypasses
    [InlineData("https://api.openalex.org.evil.example/works", false)]
    [InlineData("https://openalex.org.evil.example/works", false)]
    [InlineData("https://openalex.org/works", false)]              // parent zone of an allow-listed sub-domain
    [InlineData("https://evil.example/", false)]
    [InlineData("https://api.openalex.org@evil.example/", false)]  // userinfo moves the real host
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://api.openalex.org/pub", false)]
    [InlineData("https://127.0.0.1/works", false)]
    [InlineData("https://169.254.169.254/latest/meta-data/", false)]
    [InlineData("https://[::1]/", false)]
    [InlineData("https://localhost/works", false)] // "localhost" is neither allow-listed nor an IP literal
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Endpoint_allow_listing_blocks_metadata_private_and_decoy_hosts(string? url, bool expected)
    {
        Uri.TryCreate(url, UriKind.Absolute, out var parsed);
        Assert.Equal(expected, AcademicSourceUrlGuard.IsAllowedEndpoint(parsed, AllowedHosts));
    }

    [Theory]
    [InlineData("https://search.openlibrary.org/x", true)]   // explicit configured exception admits breadth
    [InlineData("https://openlibrary.org/x", true)]          // the exception entry itself stays allowed
    [InlineData("https://openlibrary.org.evil.example/x", false)] // suffix look-alike is still refused
    [InlineData("https://deep.sub.openlibrary.org/x", true)] // any depth under the excepted parent
    [InlineData("https://api.openalex.org/x", true)]         // exact members never need an exception
    public void Sub_domain_breadth_requires_an_explicit_configured_exception(string url, bool expected)
    {
        var exceptions = new[] { "openlibrary.org" };
        Assert.Equal(expected, AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri(url), AllowedHosts, exceptions));
        // Without the exception entry a strict sub-domain must be refused: that is the whole point.
        if (new Uri(url).Host.EndsWith(".openlibrary.org", StringComparison.Ordinal))
            Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri(url), AllowedHosts));
    }

    [Fact]
    public void Malformed_exception_entries_are_dropped_and_unwise_ones_are_rejected_by_validation()
    {
        var options = new AcademicSourceDiscoveryOptions { EndpointHostExceptions = ["*.bad", "", " ", "10.0.0.1", "OpenLibrary.ORG."] }.Normalize();
        Assert.Equal(["openlibrary.org"], options.EndpointHostExceptions);
        // A single label would admit every domain under it: kept visible, then refused by validation.
        var broad = new AcademicSourceDiscoveryOptions { EndpointHostExceptions = ["com"] }.Normalize();
        Assert.Contains("com", broad.EndpointHostExceptions);
        Assert.NotEmpty(broad.Validate());
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Validate_rejects_base_urls_that_would_point_egress_inward()
    {
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "http://169.254.169.254" }.Validate());
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { CrossrefBaseUrl = "https://user:pw@api.crossref.org" }.Validate());
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { OpenLibraryBaseUrl = "ftp://openlibrary.org" }.Validate());
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { GoogleBooksBaseUrl = "" }.Validate());
        // Bracketed IPv6 spellings of the same destinations: metadata as ::/96, and the two
        // special-use families that IsIPv6LinkLocal does not flag. Each used to validate clean, so
        // the feature could be switched on with egress aimed at link-local multicast space.
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "https://[::a9fe:a9fe]" }.Validate());
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "https://[::7f00:1]" }.Validate());
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { CrossrefBaseUrl = "https://[ff02::1]" }.Validate());
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { OpenLibraryBaseUrl = "https://[fe00::1]" }.Validate());
        Assert.NotEmpty(new AcademicSourceDiscoveryOptions { GoogleBooksBaseUrl = "https://[::a00:1]" }.Validate());
        Assert.Empty(new AcademicSourceDiscoveryOptions().Validate());
    }

    [Fact]
    public void Non_public_base_url_hosts_never_enter_the_allow_list()
    {
        var options = new AcademicSourceDiscoveryOptions
        {
            OpenAlexBaseUrl = "https://169.254.169.254",
            CrossrefBaseUrl = "https://127.0.0.1:8200",
            OpenLibraryBaseUrl = "https://[fd00::1]",
        }.Normalize();
        Assert.Equal(["www.googleapis.com"], options.AllowedEndpointHosts());
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("http://169.254.169.254/latest/meta-data/"), options.AllowedEndpointHosts()));
        // The same exclusion must hold for the IPv6 spellings that used to be admitted: a configured
        // base URL in the special-use tail never reaches the endpoint allow list.
        foreach (var literal in new[] { "[::a9fe:a9fe]", "[ff02::1]", "[fe00::1]", "[::7f00:1]" })
        {
            var admitted = new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "https://" + literal }
                .Normalize().AllowedEndpointHosts();
            Assert.DoesNotContain(literal.TrimStart('[').TrimEnd(']'), admitted);
        }
    }

    [Fact]
    public void Bracketed_ipv6_literals_reach_the_routability_rules()
    {
        // Uri.Host reports "[::1]"; without bracket stripping IPAddress.TryParse fails and the
        // literal would be treated as an ordinary hostname instead of classified.
        Assert.True(AcademicSourceUrlGuard.TryParseIpLiteral("[::1]", out var loopback));
        Assert.False(AcademicSourceUrlGuard.IsPubliclyRoutable(loopback));
        Assert.True(AcademicSourceUrlGuard.TryParseIpLiteral("[2606:4700:4700::1111]", out var publicV6));
        Assert.True(AcademicSourceUrlGuard.IsPubliclyRoutable(publicV6));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint("[fd00::1]", ["fd00::1", "[fd00::1]"]));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint("fe80::1%25eth0", ["fe80::1%25eth0"]));
    }

    [Fact]
    public void Ip_literals_require_exact_allow_list_membership_and_public_space()
    {
        Assert.True(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("https://8.8.8.8/"), ["8.8.8.8"]));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("https://8.8.8.8/"), AllowedHosts));
        // Even an explicitly configured address cannot be a loopback or metadata endpoint.
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("http://127.0.0.1:8200/"), ["127.0.0.1"]));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("http://169.254.169.254/"), ["169.254.169.254"]));
        // A public literal admitted by the exceptions list is still admitted by exact membership.
        Assert.True(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("https://[2001:448:1234::5]/"), ["2001:448:1234::5"]));
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("172.15.5.4", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)] // top of the CGNAT /10, not only its first octet pair
    [InlineData("100.128.0.1", true)]         // immediately above CGNAT stays public
    [InlineData("192.0.2.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    public void Publicly_routable_ipv4_classification(string address, bool expected)
        => Assert.Equal(expected, AcademicSourceUrlGuard.IsPubliclyRoutable(IPAddress.Parse(address)));

    [Theory]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:c000:204::1", false)] // 6to4 embeds a v4 address; refused rather than decoded
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("2001:448:1234::5", true)] // academic IPv6 space must not be false-blocked
    // ff00::/8 is multicast plus reserved; IsIPv6LinkLocal does not flag any of it.
    [InlineData("ff02::1", false)]
    [InlineData("ff13::1", false)]
    [InlineData("ffff::1", false)]
    // fe80::/10 is link-local, but the rest of fe80::/9 is not flagged by IsIPv6LinkLocal.
    [InlineData("fe00::1", false)]
    [InlineData("fe3f::1", false)]
    [InlineData("fe7f::1", false)]
    // ::/96 (IPv4-compatible) spellings inherit the v4 rules: metadata and private stay closed,
    // public embedded v4 stays open.
    [InlineData("::a9fe:a9fe", false)]
    [InlineData("::7f00:1", false)]
    [InlineData("::a00:1", false)]
    [InlineData("::c000:201", false)] // 192.0.2.1, documentation
    [InlineData("::", false)]
    [InlineData("::10a0:1", true)]    // 16.160.0.1
    [InlineData("::d5c6:d6d7", true)] // 213.198.214.215
    public void Publicly_routable_ipv6_classification(string address, bool expected)
        => Assert.Equal(expected, AcademicSourceUrlGuard.IsPubliclyRoutable(IPAddress.Parse(address)));

    [Fact]
    public void V4_mapped_ipv6_loopback_is_blocked()
        => Assert.False(AcademicSourceUrlGuard.IsPubliclyRoutable(IPAddress.Parse("::ffff:127.0.0.1")));

    [Theory]
    // The whole fc00::/6 special-use tail plus the ::/96 and ::ffff: forms of every non-public v4
    // range must be refused *even when the operator allow-listed that exact literal*, so neither a
    // misbehaving answer nor a misconfigured base URL can turn it into a read target. The allow
    // list below deliberately contains the literal, so only the classifier can refuse it.
    [InlineData("ff02::1")]
    [InlineData("fe00::1")]
    [InlineData("fe7f::1")]
    [InlineData("::a9fe:a9fe")]
    [InlineData("::7f00:1")]
    [InlineData("::a00:1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Special_use_literals_are_rejected_as_endpoints_even_when_configured(string literal)
    {
        var bracketed = "[" + literal + "]";
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(literal, new[] { literal }));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(bracketed, new[] { literal }));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("https://" + bracketed + "/"), new[] { literal }));
        Assert.False(AcademicSourceUrlGuard.TryAllowedEndpoint("https://" + bracketed + "/", new[] { literal }, out _));
    }

    [Fact]
    public void TryAllowedEndpoint_rejects_credentials_even_for_allow_listed_hosts()
    {
        Assert.False(AcademicSourceUrlGuard.TryAllowedEndpoint("https://api.openalex.org:secret@api.evil/works", AllowedHosts, out _));
        Assert.True(AcademicSourceUrlGuard.TryAllowedEndpoint("https://api.openalex.org/works?search=x", AllowedHosts, out var endpoint));
        Assert.NotNull(endpoint);
    }

    [Fact]
    public void Provider_issued_urls_reuse_the_citation_safety_boundary()
    {
        Assert.True(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe("https://example.org/a%20b/report.pdf"));
        Assert.False(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe("https://trusted.example:token@host.example/report"));
        Assert.False(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe("javascript:alert(1)"));
        Assert.False(AcademicSourceUrlGuard.IsProviderIssuedUrlSafe(null));
    }

    [Fact]
    public void Options_allow_list_derives_hosts_from_configured_bases_only()
    {
        var options = new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "https://api.openalex.org" }.Normalize();
        var hosts = options.AllowedEndpointHosts();
        Assert.Contains("api.openalex.org", hosts);
        Assert.DoesNotContain("evil.example", hosts);
        Assert.Empty(options.EndpointHostExceptions);
    }

    [Fact]
    public void Base_url_hosts_are_normalized_and_deduplicated()
    {
        var options = new AcademicSourceDiscoveryOptions
        {
            OpenAlexBaseUrl = "https://API.OPENALEX.ORG./",
            CrossrefBaseUrl = "https://api.openalex.org",
            OpenLibraryBaseUrl = "https://[::1]",
            GoogleBooksBaseUrl = "not a url",
        }.Normalize();
        var hosts = options.AllowedEndpointHosts();
        Assert.Single(hosts);
        Assert.Equal("api.openalex.org", hosts[0]);
    }
}
