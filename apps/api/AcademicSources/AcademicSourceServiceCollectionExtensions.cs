using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Composition root for scholarly source discovery. Kept inside the feature folder so the
/// integration step in <c>Program.cs</c> is a single line and this phase stays file-disjoint
/// from the research generation pipeline:
/// <code>builder.Services.AddAcademicSourceDiscovery(builder.Configuration);</code>
/// Clients are registered with automatic redirects disabled (each hop is revalidated against the
/// endpoint allow list by <see cref="AcademicHttpGateway"/>) and an infinite transport timeout,
/// because every request carries its own cancellation deadline. The transport is a pinned
/// <see cref="SocketsHttpHandler"/> from <see cref="AcademicSourceConnectionPolicy"/>: each new
/// connection — including the connection for every redirect hop, since redirects are followed
/// manually as separate requests — resolves the target host, refuses it unless every returned
/// address is public, and dials exactly the address it validated. TLS is still performed by the
/// handler against the original hostname, so certificate name validation is unchanged.
/// Configuration that would widen egress (non-public base URL, embedded credentials, an
/// over-broad sub-domain exception) fails at options resolution instead of at request time.
/// </summary>
public static class AcademicSourceServiceCollectionExtensions
{
    private static readonly Regex SafeContactPattern = new(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IServiceCollection AddAcademicSourceDiscovery(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AcademicSourceDiscoveryOptions.SectionName);
        services.Configure<AcademicSourceDiscoveryOptions>(section);
        services.AddSingleton<IValidateOptions<AcademicSourceDiscoveryOptions>, AcademicSourceDiscoveryOptionsValidator>();
        services.AddSingleton<IAcademicHostResolver, SystemAcademicHostResolver>();

        RegisterProviderClient<OpenAlexWorksAdapter>(services);
        RegisterProviderClient<CrossrefDoiAdapter>(services);
        RegisterProviderClient<OpenLibraryBooksAdapter>(services);
        RegisterProviderClient<GoogleBooksAdapter>(services);

        services.AddSingleton<IAcademicWorkSearchProvider>(sp => sp.GetRequiredService<OpenAlexWorksAdapter>());
        services.AddSingleton<IAcademicWorkSearchProvider>(sp => sp.GetRequiredService<GoogleBooksAdapter>());
        services.AddSingleton<IAcademicBookCatalogProvider>(sp => sp.GetRequiredService<OpenLibraryBooksAdapter>());
        services.AddSingleton<IAcademicBookCatalogProvider>(sp => sp.GetRequiredService<GoogleBooksAdapter>());
        services.AddSingleton<IAcademicDoiValidator>(sp => sp.GetRequiredService<CrossrefDoiAdapter>());
        services.AddSingleton<IAcademicSourceDiscoveryService, AcademicSourceDiscoveryService>();
        return services;
    }

    private static void RegisterProviderClient<TAdapter>(IServiceCollection services)
        where TAdapter : class
    {
        var builder = services.AddHttpClient<TAdapter>((services, client) => ConfigureClient(services, client))
            .ConfigurePrimaryHttpMessageHandler(services =>
            {
                var options = services.GetRequiredService<IOptions<AcademicSourceDiscoveryOptions>>().Value.Normalize();
                return AcademicSourceConnectionPolicy.CreatePinnedHandler(services.GetRequiredService<IAcademicHostResolver>(), options);
            });
        // Built-in HttpClientFactory request logging writes the request target at Information level
        // ("Start processing HTTP request GET <url>") under the category
        // `System.Net.Http.HttpClient.<name>.<stage>`, and every scholarly request target IS a query
        // string: the reader's research topic in `q=`, and for Google Books the deployment's
        // configured key in `key=` as well. Neither belongs in a log sink the deployment ships to,
        // and neither is recoverable from a line that was redacted badly, so each provider client
        // gets its own transport-log filter at LogLevel.None. The category is the client's name plus
        // a pipeline stage, so matching on the name covers both stages now and any stage a future
        // runtime adds, and it leaves every other client's transport logging untouched. What an
        // operator needs about a provider call — status plus a coarse failure category, never a
        // URL, payload or credential — is recorded by the feature's own loggers instead.
        services.AddLogging(logging =>
            logging.AddFilter($"System.Net.Http.HttpClient.{builder.Name}", LogLevel.None));
    }

    private static void ConfigureClient(IServiceProvider services, HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        var options = services.GetRequiredService<IOptions<AcademicSourceDiscoveryOptions>>().Value;
        var contact = options.ContactMailto;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            contact is { Length: <= 320 } && SafeContactPattern.IsMatch(contact.Trim())
                ? $"TaslimResearch/1.0 (academic source discovery; mailto:{contact.Trim()})"
                : "TaslimResearch/1.0 (academic source discovery)");
    }
}

/// <summary>
/// Fails options resolution when the discovery section would open an unsafe egress path. Runs on
/// first use of <c>IOptions&lt;AcademicSourceDiscoveryOptions&gt;</c>, so a bad section is a
/// configuration error rather than a request that quietly reaches an internal address.
/// </summary>
public sealed class AcademicSourceDiscoveryOptionsValidator : IValidateOptions<AcademicSourceDiscoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, AcademicSourceDiscoveryOptions options)
    {
        var failures = options.Normalize().Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
