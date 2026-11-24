using System.Net;
using System.Net.Sockets;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// Connection-layer SSRF tests. Every case drives the policy with a fixed answer table, so no
/// test depends on DNS and no test opens a socket: the transport is a recorder that throws if the
/// policy ever decides to dial.
/// </summary>
public sealed class AcademicSourceConnectionPolicyTests
{
    private const string AllowListedHost = "api.openalex.org";

    [Theory]
    // IPv4: loopback, RFC1918, link-local incl. the cloud metadata address, CGNAT, documentation,
    // benchmarking, reserved and multicast space.
    [InlineData("127.0.0.1")]
    [InlineData("127.9.9.9")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.7.7")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.42")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.4")]
    [InlineData("203.0.113.9")]
    [InlineData("198.18.0.1")]
    [InlineData("192.88.99.1")]
    [InlineData("224.0.0.5")]
    [InlineData("240.0.0.1")]
    // IPv6: loopback, link-local, ULA, site-local, documentation, discard-only, 6to4, v4-mapped.
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("fc00::1")]
    [InlineData("fec0::1")]
    [InlineData("2001:db8::1")]
    [InlineData("100::1")]
    [InlineData("2002:c000:204::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    // The rest of the special-use tail that IsIPv6LinkLocal does not flag: multicast/reserved
    // ff00::/8 and unicast link-local fe00::/9 outside fe80::/10. A resolver that answers with
    // any of these used to be pinned and dialled.
    [InlineData("ff02::1")]
    [InlineData("ff13::1")]
    [InlineData("ffff::1")]
    [InlineData("fe00::1")]
    [InlineData("fe7f::1")]
    // IPv4-compatible ::/96 spellings of non-public v4 space, including the metadata address.
    [InlineData("::a9fe:a9fe")]
    [InlineData("::7f00:1")]
    [InlineData("::a00:1")]
    [InlineData("::c000:201")]
    [InlineData("::")]
    // Translator and IETF/ORCHID assignment space: a hostname that rebinds into one of these is
    // dialled at a non-public destination even though the address is not flagged by
    // IsIPv6LinkLocal/SiteLocal/Teredo and is not in the fc00::/6 tail. The NAT64 rows are the
    // metadata, loopback, RFC1918 and v4-documentation addresses reached through a v6 prefix whose
    // embedded v4 is not reduced by the ::/96 rule, so they used to be pinned and dialled.
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b::c000:201")]
    [InlineData("64:ff9b:1::8.8.8.8")]
    [InlineData("64:ff9b:1::a9fe:a9fe")]
    [InlineData("2001:1::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:10::1")]
    [InlineData("2001:20::1")]
    [InlineData("2620:4f80::1")]
    public async Task Allow_listed_hostname_that_rebinds_to_non_public_space_is_refused_before_connect(string answer)
    {
        var resolver = new AcademicFixture.StubResolver(answer);
        var transport = new AcademicFixture.RecordingTransport();

        var decision = await AcademicSourceConnectionPolicy.DecideAsync(AllowListedHost, resolver, CancellationToken.None);

        Assert.Equal(AcademicConnectDecisionStatus.NonPublicAddress, decision.Status);
        Assert.Null(decision.Address);
        Assert.False(decision.IsPinned);

        // Fresh resolver and transport: the connect attempt must make exactly one decision, and
        // it must make it before the transport sees an address.
        var connectResolver = new AcademicFixture.StubResolver(answer);
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() =>
            AcademicSourceConnectionPolicy.OpenConnectionAsync(AllowListedHost, 443, connectResolver, transport.ConnectAsync, CancellationToken.None));
        Assert.Equal(AcademicSourceFailureCategories.BlockedEndpoint, exception.Details?.FailureCategory);
        Assert.Empty(transport.Dialed);                       // refused before the socket layer
        Assert.Equal(1, connectResolver.ResolveCount);        // one decision per connection
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("104.16.1.2")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:448:1234::5")]                          // academic IPv6 must not be false-blocked
    [InlineData("::10a0:1")]                                   // ::/96 spelling of public 16.160.0.1
    [InlineData("::d5c6:d6d7")]                                // ::/96 spelling of public 213.198.214.215
    // Real v6 provider space adjacent to the newly refused assignments must stay pinned: these are
    // the addresses an over-broad fix would false-block.
    [InlineData("2001:200::1")]                                // first /20 above 2001::/23
    [InlineData("2001:4860:4860::8888")]                       // public recursive DNS in v6 space
    [InlineData("2620:4f7f:ffff::1")]                          // just below 2620:4f80::/32
    [InlineData("2620:4f81::1")]                               // just above it
    public async Task Public_answers_are_pinned_to_the_address_that_was_validated(string answer)
    {
        var resolver = new AcademicFixture.StubResolver(answer);
        var transport = new AcademicFixture.RecordingTransport();

        var decision = await AcademicSourceConnectionPolicy.DecideAsync(AllowListedHost, resolver, CancellationToken.None);

        Assert.True(decision.IsPinned);
        Assert.Equal(IPAddress.Parse(answer), decision.Address);
        // The dial target is the validated address, not the hostname: a second, post-validation
        // resolution by the socket layer is what makes rebinding possible.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcademicSourceConnectionPolicy.OpenConnectionAsync(AllowListedHost, 443, resolver, transport.ConnectAsync, CancellationToken.None));
        var dialed = Assert.Single(transport.Dialed);
        Assert.Equal(decision.Address!, dialed.Address);
        Assert.Equal(443, dialed.Port);
    }

    [Fact]
    public async Task Dual_stack_answer_prefers_the_stable_ipv4_record()
    {
        var resolver = new AcademicFixture.StubResolver("2606:4700:4700::1111", "104.16.1.2");
        var decision = await AcademicSourceConnectionPolicy.DecideAsync(AllowListedHost, resolver, CancellationToken.None);
        Assert.Equal(AddressFamily.InterNetwork, decision.Address!.AddressFamily);
    }

    [Fact]
    public async Task Mixed_answer_with_one_private_record_refuses_rather_than_falling_back()
    {
        // The classic rebinding payload: a public answer alongside an internal one. Silently
        // keeping the public record would make the check advisory.
        var resolver = new AcademicFixture.StubResolver("104.16.1.2", "169.254.169.254");
        var decision = await AcademicSourceConnectionPolicy.DecideAsync(AllowListedHost, resolver, CancellationToken.None);
        Assert.Equal(AcademicConnectDecisionStatus.NonPublicAddress, decision.Status);
        Assert.Null(decision.Address);
    }

    [Fact]
    public async Task Empty_resolution_is_a_refusal_not_a_permission()
    {
        var resolver = new AcademicFixture.StubResolver();
        var decision = await AcademicSourceConnectionPolicy.DecideAsync(AllowListedHost, resolver, CancellationToken.None);
        Assert.Equal(AcademicConnectDecisionStatus.NoAddress, decision.Status);
        Assert.Equal(1, resolver.ResolveCount);
    }

    [Theory]
    [InlineData("[::1]")]
    [InlineData("[fd00::1]")]
    [InlineData("[fe80::1]")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[ff02::1]")]
    [InlineData("[fe00::1]")]
    [InlineData("[::a9fe:a9fe]")]
    [InlineData("[64:ff9b::a9fe:a9fe]")]
    [InlineData("[64:ff9b:1::a9fe:a9fe]")]
    [InlineData("[2001:1::1]")]
    [InlineData("[2001:2::1]")]
    [InlineData("[2001:10::1]")]
    [InlineData("[2001:20::1]")]
    [InlineData("[2620:4f80::1]")]
    public async Task Ip_literals_are_classified_without_consulting_dns(string literal)
    {
        var resolver = new AcademicFixture.StubResolver("8.8.8.8");
        var decision = await AcademicSourceConnectionPolicy.DecideAsync(literal, resolver, CancellationToken.None);
        Assert.Equal(AcademicConnectDecisionStatus.NonPublicAddress, decision.Status);
        Assert.Equal(0, resolver.ResolveCount);               // never handed to a resolver
    }

    [Fact]
    public async Task Public_ip_literal_is_dialed_at_the_literal_itself()
    {
        var resolver = new AcademicFixture.StubResolver("1.1.1.1");
        var transport = new AcademicFixture.RecordingTransport();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            AcademicSourceConnectionPolicy.OpenConnectionAsync("[2001:448:1234::5]", 443, resolver, transport.ConnectAsync, CancellationToken.None));
        var dialed = Assert.Single(transport.Dialed);
        Assert.Equal(IPAddress.Parse("2001:448:1234::5"), dialed.Address);
        Assert.Equal(0, resolver.ResolveCount);
    }

    [Theory]
    [InlineData("fe80::1%25eth0")]   // scoped link-local: no meaningful public target
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[::1")]             // unterminated bracket form
    public async Task Unusable_hosts_are_refused_without_resolution(string host)
    {
        var resolver = new AcademicFixture.StubResolver("8.8.8.8");
        var decision = await AcademicSourceConnectionPolicy.DecideAsync(host, resolver, CancellationToken.None);
        Assert.Equal(AcademicConnectDecisionStatus.InvalidHost, decision.Status);
        Assert.Equal(0, resolver.ResolveCount);
    }

    [Fact]
    public async Task Normalization_is_applied_before_the_resolver_is_asked()
    {
        var resolver = new AcademicFixture.StubResolver("8.8.8.8");
        await AcademicSourceConnectionPolicy.DecideAsync("API.OpenAlex.org.", resolver, CancellationToken.None);
        Assert.Equal(AllowListedHost, Assert.Single(resolver.ResolvedHosts));
    }

    [Fact]
    public void Pinned_handler_disables_redirects_and_cookies_and_keeps_default_certificate_validation()
    {
        var options = AcademicFixture.Defaults();
        using var handler = AcademicSourceConnectionPolicy.CreatePinnedHandler(new AcademicFixture.StubResolver("8.8.8.8"), options);

        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.AllowAutoRedirect);   // the gateway revalidates each hop instead
        Assert.False(handler.UseCookies);          // no cookie jar that could follow a hop
        Assert.Null(handler.Proxy);
        // The identity check stays with the framework: no custom validation callback is installed
        // and no target-host override is baked into the handler, so SNI and certificate name
        // validation still use the original request hostname even though the socket is pinned.
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.LocalCertificateSelectionCallback);
        Assert.True(string.IsNullOrEmpty(handler.SslOptions.TargetHost));
        Assert.Equal(options.MaxConnectionsPerServer, handler.MaxConnectionsPerServer);
    }
}

public sealed class AcademicSourceRedirectCredentialTests
{
    private static (AcademicHttpGateway gateway, ScriptedHandler handler) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> script,
        Action<AcademicSourceDiscoveryOptions>? configure = null,
        bool withAuthorization = false)
    {
        var handler = new ScriptedHandler(script);
        var options = AcademicFixture.Defaults(configure);
        var client = withAuthorization ? AcademicFixture.ClientWithAuthorization(handler) : AcademicFixture.Client(handler);
        return (new AcademicHttpGateway(client, options), handler);
    }

    private static Uri Endpoint(string host = "api.openalex.org") => new($"https://{host}/works", UriKind.Absolute);

    [Fact]
    public async Task Redirect_to_another_allow_listed_host_is_refused_by_default()
    {
        // api.crossref.org is on the allow list, yet getting there through a provider-issued
        // Location is a different trust decision than the operator configuring it directly.
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.crossref.org/works/10.1234/x") } },
            _ => AcademicFixture.Json("{}"),
        });
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(Endpoint(), CancellationToken.None));
        Assert.Equal(AcademicSourceFailureCategories.BlockedEndpoint, exception.Details?.FailureCategory);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Explicitly_allowed_cross_host_hop_drops_credential_parameters()
    {
        var (gateway, handler) = Create(
            (_, number) => number switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://api.crossref.org/works?doi=10.1038%2Fnature&key=SECRETKEY&client_secret=tt&safe=1") },
                },
                _ => AcademicFixture.Json("""{"hop":"final"}"""),
            },
            options => options.AllowCrossHostRedirects = true);

        var response = await gateway.GetJsonAsync(Endpoint(), CancellationToken.None);

        Assert.Equal("final", response.Root.GetProperty("hop").GetString());
        Assert.Equal(2, handler.RequestCount);
        var hop = AcademicFixture.Query(handler.Requests[1]);
        Assert.Equal("10.1038/nature", hop["doi"]);           // legitimate parameters survive
        Assert.Equal("1", hop["safe"]);
        Assert.False(hop.ContainsKey("key"));                 // provider credential never crosses over
        Assert.False(hop.ContainsKey("client_secret"));
        Assert.Equal("api.crossref.org", handler.Requests[1].RequestUri!.Host);
    }

    [Fact]
    public async Task Same_host_redirect_keeps_the_query_the_provider_supplied()
    {
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.openalex.org/works-v2?key=KEEPME") } },
            _ => AcademicFixture.Json("""{"hop":"same"}"""),
        });
        var response = await gateway.GetJsonAsync(Endpoint(), CancellationToken.None);
        Assert.Equal("same", response.Root.GetProperty("hop").GetString());
        Assert.Equal("KEEPME", AcademicFixture.Query(handler.Requests[1])["key"]);
    }

    [Fact]
    public async Task Cross_host_hop_is_refused_when_the_client_carries_an_authorization_default()
    {
        // A default Authorization header cannot be stripped for a single send, so the hop is
        // refused outright rather than risking the credential leaving the original authority.
        var (gateway, handler) = Create(
            (_, number) => number switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.crossref.org/works/10.1234/x") } },
                _ => AcademicFixture.Json("{}"),
            },
            options => options.AllowCrossHostRedirects = true,
            withAuthorization: true);
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(Endpoint(), CancellationToken.None));
        Assert.Equal(AcademicSourceFailureCategories.BlockedEndpoint, exception.Details?.FailureCategory);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Same_host_hop_still_works_when_the_client_carries_credentials()
    {
        var (gateway, handler) = Create(
            (_, number) => number switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.openalex.org/works-v2") } },
                _ => AcademicFixture.Json("""{"hop":"kept"}"""),
            },
            withAuthorization: true);
        var response = await gateway.GetJsonAsync(Endpoint(), CancellationToken.None);
        Assert.Equal("kept", response.Root.GetProperty("hop").GetString());
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public void Credential_query_keys_are_stripped_case_insensitively()
    {
        var scrubbed = AcademicHttpGateway.ScrubCredentials(new Uri("https://api.crossref.org/works?KEY=1&Token=2&Access_Token=3&query=ok"));
        var kept = scrubbed.Query;
        Assert.Contains("query=ok", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("KEY", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("Token", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("Access", kept, StringComparison.Ordinal);
        // Every parameter a credential: the rebuilt URL carries no query marker at all.
        Assert.Equal("https://api.crossref.org/works",
            AcademicHttpGateway.ScrubCredentials(new Uri("https://api.crossref.org/works?key=1&token=2")).ToString());
    }

    [Fact]
    public void Scrubbing_leaves_a_credential_free_url_untouched()
    {
        foreach (var text in new[] { "https://api.crossref.org/works?rows=5&doi=10.1", "https://api.crossref.org/works" })
        {
            var plain = new Uri(text);
            Assert.Same(plain, AcademicHttpGateway.ScrubCredentials(plain));
        }
    }

    [Fact]
    public async Task Host_port_change_counts_as_a_different_authority()
    {
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.openalex.org:8443/works") } },
            _ => AcademicFixture.Json("{}"),
        });
        await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(Endpoint(), CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }
}
