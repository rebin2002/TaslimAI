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
    [InlineData("https://sub.openlibrary.org/search.json", true)] // sub-domain of an allow-listed host
    [InlineData("https://API.OPENALEX.ORG./works", true)]          // case and trailing dot are not bypasses
    [InlineData("https://api.openalex.org.evil.example/works", false)]
    [InlineData("https://openalex.org.evil.example/works", false)]
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

    [Fact]
    public void Ip_literals_require_exact_allow_list_membership_and_public_space()
    {
        Assert.True(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("https://8.8.8.8/"), ["8.8.8.8"]));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("https://8.8.8.8/"), AllowedHosts));
        // Even an explicitly configured address cannot be a loopback or metadata endpoint.
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("http://127.0.0.1:8200/"), ["127.0.0.1"]));
        Assert.False(AcademicSourceUrlGuard.IsAllowedEndpoint(new Uri("http://169.254.169.254/"), ["169.254.169.254"]));
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("172.15.5.4", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
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
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("2001:448:1234::5", true)] // academic IPv6 space must not be false-blocked
    public void Publicly_routable_ipv6_classification(string address, bool expected)
        => Assert.Equal(expected, AcademicSourceUrlGuard.IsPubliclyRoutable(IPAddress.Parse(address)));

    [Fact]
    public void V4_mapped_ipv6_loopback_is_blocked()
        => Assert.False(AcademicSourceUrlGuard.IsPubliclyRoutable(IPAddress.Parse("::ffff:127.0.0.1")));

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
    }
}
