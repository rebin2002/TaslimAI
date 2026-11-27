using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// Direct-dial regression for the scholarly transport (raised in the #1270 rollout review).
///
/// <see cref="SocketsHttpHandler"/> defaults to <c>UseProxy = true</c>, and when a proxy is in play
/// the endpoint it hands to <see cref="SocketsHttpHandler.ConnectCallback"/> is the <em>proxy</em>,
/// not the destination. That matters here because the connect callback is where this module
/// re-checks the endpoint allow list and decides which address the socket may touch. Handed a proxy,
/// the allow-list comparison judges the proxy hostname and the resolver pins the proxy address, so
/// the provider's real address is never admitted by anything in this module. Measured on .NET 8
/// before the fix, with <c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c> present in the process environment: the
/// pinned callback received <c>proxy.example.net:18899</c> for a request aimed at
/// <c>https://api.openalex.org/works</c>, the resolver was never asked about the provider host, and
/// the call failed with the blocked-endpoint rejection — discovery stops working. With a proxy host
/// that <em>is</em> allow-listed (a base URL aimed at the proxy), the same handler sent
/// <c>CONNECT api.openalex.org:443</c> to that listener, so the destination is chosen by a third
/// party and none of the admission rules above were applied to it.
///
/// The environment variables themselves cannot be reproduced inside a test assembly: .NET reads the
/// ambient proxy once at process start, so a value set mid-run is ignored (verified). The suite
/// therefore drives the identical code path with an explicit <see cref="WebProxy"/>, which the
/// runtime honours per handler: the control test shows the endpoint really is swapped, and the
/// shipped handler shows it can no longer be.
/// </summary>
public sealed class AcademicSourceProxyDirectDialTests
{
    private const string ProxyUrl = "http://proxy.invalid:18899";
    private const string ProxyEndpoint = "proxy.invalid:18899";
    private const string ProviderHost = "api.openalex.org";
    private const string ProviderTarget = "https://api.openalex.org/works?q=vaccine-misinformation-policy-study";

    /// <summary>
    /// Records the endpoint the runtime decided this connection is for, then throws before any socket
    /// exists. An existing callback (the pinned handler's own) is re-installed underneath the recorder
    /// where one exists, so the recorder never hides the production callback.
    /// </summary>
    private static string ProbeCallbackEndpoint(SocketsHttpHandler handler, string requestUri)
    {
        string? seen = null;
        handler.ConnectCallback = (context, cancellationToken) =>
        {
            seen = $"{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}";
            throw new InvalidOperationException("endpoint probe");
        };
        try
        {
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
            client.GetAsync(requestUri).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            // the probe's own throw, arriving unwrapped
        }
        catch (HttpRequestException)
        {
            // the same throw, wrapped by the transport layer
        }
        return seen ?? "<callback never invoked>";
    }

    [Fact]
    public void A_proxy_is_the_endpoint_the_runtime_hands_to_a_default_handler()
    {
        // Control for everything below. Nothing about this handler is custom except the proxy, and the
        // callback stops naming the provider the moment a proxy exists. If this ever reports the
        // provider endpoint, the shipped assertion is guarding nothing.
        using var control = new SocketsHttpHandler();
        Assert.True(control.UseProxy);                       // the runtime default this module opted out of
        Assert.Null(control.Proxy);                          // and Proxy == null is not what keeps it direct
        control.Proxy = new WebProxy(ProxyUrl);
        Assert.Equal(ProxyEndpoint, ProbeCallbackEndpoint(control, ProviderTarget));
    }

    [Fact]
    public void The_pinned_handler_is_built_without_proxy_support()
    {
        using var handler = AcademicSourceConnectionPolicy.CreatePinnedHandler(
            new AcademicFixture.StubResolver("8.8.8.8"),
            AcademicFixture.Defaults());
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.NotNull(handler.ConnectCallback);
        // TLS identity stays with the framework: no name-validation override, no target-host
        // substitution, so SNI and certificate checks still use the request hostname.
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.LocalCertificateSelectionCallback);
        Assert.True(string.IsNullOrEmpty(handler.SslOptions.TargetHost));
        Assert.Equal($"{ProviderHost}:443", ProbeCallbackEndpoint(handler, ProviderTarget));
    }

    [Fact]
    public void An_attached_proxy_stays_inert_on_the_pinned_handler()
    {
        // UseProxy is the switch, not Proxy: attaching a proxy to the shipped handler must not be able
        // to retarget the callback, which is the property the allow-list check depends on.
        using var handler = AcademicSourceConnectionPolicy.CreatePinnedHandler(
            new AcademicFixture.StubResolver("8.8.8.8"),
            AcademicFixture.Defaults());
        handler.Proxy = new WebProxy(ProxyUrl);
        Assert.False(handler.UseProxy);
        Assert.Equal($"{ProviderHost}:443", ProbeCallbackEndpoint(handler, ProviderTarget));
    }

    [Fact]
    public async Task Admission_still_runs_against_the_provider_host_when_a_proxy_is_attached()
    {
        // Full callback, no socket: the stub resolver answers loopback, which the policy refuses before
        // dialing. Reaching that refusal for the provider host is the point — it means the callback was
        // never pointed at the proxy instead, so the address rules really did judge the provider.
        var resolver = new AcademicFixture.StubResolver("127.0.0.1");
        using var handler = AcademicSourceConnectionPolicy.CreatePinnedHandler(resolver, AcademicFixture.Defaults());
        handler.Proxy = new WebProxy(ProxyUrl);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };

        var failure = await Record.ExceptionAsync(() => client.GetAsync(ProviderTarget));
        Assert.NotNull(failure);
        var rejection = Unwrap<AcademicSourceRejectedException>(failure!);
        Assert.NotNull(rejection);
        Assert.Equal(AcademicSourceFailureCategories.BlockedEndpoint, rejection!.Details?.FailureCategory);
        Assert.Equal(ProviderHost, Assert.Single(resolver.ResolvedHosts));
    }

    [Fact]
    public void Every_composed_provider_client_gets_the_direct_dial_handler()
    {
        // The factory method is called from one place, but the four provider clients are registered
        // independently, so a later per-client override would be caught here instead of in production.
        // Real DI, no network: an options action appended after the feature's own registration runs at
        // pipeline-build time, when the primary handler the feature installed is already on the
        // builder, so the test reads the transport each client actually gets rather than rebuilding it.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AcademicSourceDiscoveryOptions.SectionName}:Enabled"] = "true",
            [$"{AcademicSourceDiscoveryOptions.SectionName}:ContactMailto"] = "ops@example.com",
        }).Build();
        var services = new ServiceCollection();
        services.AddAcademicSourceDiscovery(configuration);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        foreach (var name in new[]
        {
            nameof(OpenAlexWorksAdapter),
            nameof(CrossrefDoiAdapter),
            nameof(OpenLibraryBooksAdapter),
            nameof(GoogleBooksAdapter),
        })
        {
            SocketsHttpHandler? installed = null;
            services.Configure<HttpClientFactoryOptions>(name, options =>
                options.HttpMessageHandlerBuilderActions.Add(builder =>
                    installed = builder.PrimaryHandler as SocketsHttpHandler));
            using var providerWithCapture = services.BuildServiceProvider();
            using (var client = providerWithCapture.GetRequiredService<IHttpClientFactory>().CreateClient(name))
            {
            }
            var transport = installed;
            Assert.NotNull(transport);
            Assert.False(transport!.UseProxy);
            Assert.Null(transport.Proxy);
            Assert.NotNull(transport.ConnectCallback);
            Assert.False(transport.AllowAutoRedirect);
        }
    }

    private static TException? Unwrap<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match) return match;
        }
        return null;
    }
}
