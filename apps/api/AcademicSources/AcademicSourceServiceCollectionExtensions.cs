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
        => services.AddHttpClient<TAdapter>((services, client) => ConfigureClient(services, client))
            .ConfigurePrimaryHttpMessageHandler(services =>
            {
                var options = services.GetRequiredService<IOptions<AcademicSourceDiscoveryOptions>>().Value.Normalize();
                return AcademicSourceConnectionPolicy.CreatePinnedHandler(services.GetRequiredService<IAcademicHostResolver>(), options);
            });

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
