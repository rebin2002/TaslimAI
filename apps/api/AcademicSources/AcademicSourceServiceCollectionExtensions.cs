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
/// because every request carries its own cancellation deadline.
/// </summary>
public static class AcademicSourceServiceCollectionExtensions
{
    private static readonly Regex SafeContactPattern = new(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IServiceCollection AddAcademicSourceDiscovery(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AcademicSourceDiscoveryOptions>(configuration.GetSection(AcademicSourceDiscoveryOptions.SectionName));

        services.AddHttpClient<OpenAlexWorksAdapter>((services, client) => ConfigureClient(services, client))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<CrossrefDoiAdapter>((services, client) => ConfigureClient(services, client))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<OpenLibraryBooksAdapter>((services, client) => ConfigureClient(services, client))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<GoogleBooksAdapter>((services, client) => ConfigureClient(services, client))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddSingleton<IAcademicWorkSearchProvider>(sp => sp.GetRequiredService<OpenAlexWorksAdapter>());
        services.AddSingleton<IAcademicWorkSearchProvider>(sp => sp.GetRequiredService<GoogleBooksAdapter>());
        services.AddSingleton<IAcademicBookCatalogProvider>(sp => sp.GetRequiredService<OpenLibraryBooksAdapter>());
        services.AddSingleton<IAcademicBookCatalogProvider>(sp => sp.GetRequiredService<GoogleBooksAdapter>());
        services.AddSingleton<IAcademicDoiValidator>(sp => sp.GetRequiredService<CrossrefDoiAdapter>());
        services.AddSingleton<IAcademicSourceDiscoveryService, AcademicSourceDiscoveryService>();
        return services;
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
