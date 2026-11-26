using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// The fail-closed posture: configuring nothing must mean "do not call anyone". These cases build
/// options and adapters from bare defaults instead of <see cref="AcademicFixture.Defaults"/>, so
/// they would notice a default flipped back to on.
/// </summary>
public sealed class AcademicSourceFailClosedDefaultsTests
{
    [Fact]
    public void Bare_options_enable_nothing_and_hold_no_secrets()
    {
        var options = new AcademicSourceDiscoveryOptions();
        Assert.False(options.Enabled);
        Assert.False(options.OpenAlexEnabled);
        Assert.False(options.CrossrefEnabled);
        Assert.False(options.OpenLibraryEnabled);
        Assert.False(options.GoogleBooksEnabled);
        Assert.Null(options.GoogleBooksApiKey);
        Assert.Null(options.ContactMailto);
        Assert.False(options.AllowCrossHostRedirects);
        Assert.Empty(options.EndpointHostExceptions);
    }

    [Fact]
    public void Adapters_report_themselves_unconfigured_on_bare_defaults()
    {
        using var handler = new ScriptedHandler((_, _) => AcademicFixture.Json("{}"));
        using var client = new HttpClient(handler);
        var bare = AcademicFixture.Wrapped(new AcademicSourceDiscoveryOptions());

        Assert.False(new OpenAlexWorksAdapter(client, bare, NullLogger<OpenAlexWorksAdapter>.Instance).IsConfigured);
        Assert.False(new CrossrefDoiAdapter(client, bare, NullLogger<CrossrefDoiAdapter>.Instance).IsConfigured);
        Assert.False(new OpenLibraryBooksAdapter(client, bare, NullLogger<OpenLibraryBooksAdapter>.Instance).IsConfigured);
        Assert.False(new GoogleBooksAdapter(client, bare, NullLogger<GoogleBooksAdapter>.Instance).IsConfigured);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Gateway_sends_nothing_and_reports_configuration_failure_when_disabled()
    {
        var handler = new ScriptedHandler((_, _) => AcademicFixture.Json("{}"));
        // Deliberately not AcademicFixture.Defaults: this is the bare, un-wired shape.
        var gateway = new AcademicHttpGateway(AcademicFixture.Client(handler), new AcademicSourceDiscoveryOptions());

        var exception = await Assert.ThrowsAsync<AcademicSourceUnavailableException>(() =>
            gateway.GetJsonAsync(new Uri("https://api.openalex.org/works"), CancellationToken.None));

        Assert.Equal(AcademicSourceFailureCategories.Configuration, exception.Details?.FailureCategory);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void Enabling_the_master_switch_without_a_provider_still_calls_nobody()
    {
        var options = new AcademicSourceDiscoveryOptions { Enabled = true };
        using var handler = new ScriptedHandler((_, _) => AcademicFixture.Json("{}"));
        using var client = new HttpClient(handler);
        var wrapped = AcademicFixture.Wrapped(options);

        Assert.False(new OpenAlexWorksAdapter(client, wrapped, NullLogger<OpenAlexWorksAdapter>.Instance).IsConfigured);
        Assert.False(new CrossrefDoiAdapter(client, wrapped, NullLogger<CrossrefDoiAdapter>.Instance).IsConfigured);
        Assert.False(new OpenLibraryBooksAdapter(client, wrapped, NullLogger<OpenLibraryBooksAdapter>.Instance).IsConfigured);
        Assert.False(new GoogleBooksAdapter(client, wrapped, NullLogger<GoogleBooksAdapter>.Instance).IsConfigured);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void Composition_options_validator_accepts_bare_config_and_refuses_dangerous_values()
    {
        var validator = new AcademicSourceDiscoveryOptionsValidator();
        Assert.True(validator.Validate("x", new AcademicSourceDiscoveryOptions()).Succeeded);
        Assert.False(validator.Validate("x", new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "http://169.254.169.254" }).Succeeded);
        Assert.False(validator.Validate("x", new AcademicSourceDiscoveryOptions { CrossrefBaseUrl = "https://u:p@api.crossref.org" }).Succeeded);
        Assert.False(validator.Validate("x", new AcademicSourceDiscoveryOptions { OpenLibraryBaseUrl = "ftp://openlibrary.org" }).Succeeded);
        // Kept by Normalize so it can be reported, then refused here: one label would admit every domain.
        Assert.False(validator.Validate("x", new AcademicSourceDiscoveryOptions { EndpointHostExceptions = ["com"] }).Succeeded);
    }

    [Fact]
    public void A_plaintext_provider_base_is_a_configuration_error_not_a_degraded_mode()
    {
        var validator = new AcademicSourceDiscoveryOptionsValidator();
        // Every catalog this feature speaks to serves HTTPS, so an http:// base can only be a typo
        // or a proxy — and the request it would carry is the reader's topic plus, for Google Books,
        // the deployment's key. Refusing at options resolution means a deployment cannot boot into
        // that shape by accident; the message names the transport, not the host, so an operator is
        // not sent to the allow list to look for a host that was already there.
        var plaintext = validator.Validate("x", new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "http://api.openalex.org" });
        Assert.False(plaintext.Succeeded);
        Assert.Contains("plaintext HTTP", plaintext.FailureMessage, StringComparison.Ordinal);
        Assert.False(validator.Validate("x", new AcademicSourceDiscoveryOptions { GoogleBooksBaseUrl = "http://www.googleapis.com" }).Succeeded);
        Assert.True(validator.Validate("x", new AcademicSourceDiscoveryOptions { OpenAlexBaseUrl = "https://api.openalex.org" }).Succeeded);
    }

    [Fact]
    public void The_plaintext_switch_is_not_reachable_from_configuration()
    {
        // The gateway lets a test inject a plaintext transport, and nothing else may. A switch with
        // no public setter cannot be bound from appsettings, an environment variable or a command
        // line, so a deployment cannot silently downgrade its own egress; if this ever reads as
        // public, the escape hatch has become a production setting and this test is the alarm.
        // Looked up across both visibilities on purpose: the point of the assertion is which of the
        // two the setter ended up in, so a search that could only see one would prove nothing.
        var property = typeof(AcademicSourceDiscoveryOptions).GetProperty(
            "AllowInsecureProviderEndpoints",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.True(property!.CanRead);
        // The accessor lookup answers for the visibility asked about: false means public only. A
        // null here and a method there is exactly the shape the gateway depends on — the switch
        // exists for tests in this assembly, and no binder, appsettings file or environment
        // variable running as the application can reach it.
        Assert.Null(property.GetSetMethod(false));
        Assert.NotNull(property.GetSetMethod(true));
    }

    [Fact]
    public void Feature_composes_from_an_empty_configuration_section_and_stays_inert()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAcademicSourceDiscovery(new Microsoft.Extensions.Configuration.ConfigurationManager());
        using var provider = services.BuildServiceProvider();

        // The whole graph resolves, which is what the wiring PR needs to know: composition works
        // with no configuration section present at all.
        Assert.IsType<SystemAcademicHostResolver>(provider.GetRequiredService<IAcademicHostResolver>());
        Assert.NotNull(provider.GetRequiredService<IAcademicSourceDiscoveryService>());
        Assert.Equal(2, provider.GetRequiredService<IEnumerable<IAcademicWorkSearchProvider>>().Count());
        Assert.Equal(2, provider.GetRequiredService<IEnumerable<IAcademicBookCatalogProvider>>().Count());
        Assert.Single(provider.GetRequiredService<IEnumerable<IAcademicDoiValidator>>());

        // And it resolves to an inert state: options validate, nothing was switched on, so every
        // adapter reports itself unconfigured and the orchestrator skips all of them.
        Assert.False(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AcademicSourceDiscoveryOptions>>().Value.Enabled);
        Assert.False(provider.GetRequiredService<OpenAlexWorksAdapter>().IsConfigured);
        Assert.False(provider.GetRequiredService<CrossrefDoiAdapter>().IsConfigured);
        Assert.False(provider.GetRequiredService<OpenLibraryBooksAdapter>().IsConfigured);
        Assert.False(provider.GetRequiredService<GoogleBooksAdapter>().IsConfigured);
    }
}
