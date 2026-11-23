namespace Taslim.Api.AcademicSources;

/// <summary>
/// Configuration for scholarly source discovery. All bounds are enforced server-side; provider
/// base URLs are configuration, never request input, and their hosts form the endpoint allow
/// list used by <see cref="AcademicSourceUrlGuard"/>.
/// </summary>
public sealed class AcademicSourceDiscoveryOptions
{
    public const string SectionName = "AcademicSourceDiscovery";

    public bool Enabled { get; set; } = true;

    public bool OpenAlexEnabled { get; set; } = true;

    public bool CrossrefEnabled { get; set; } = true;

    public bool OpenLibraryEnabled { get; set; } = true;

    /// <summary>Google Books requires a configured API key; enabling it without a key disables the adapter rather than failing requests.</summary>
    public bool GoogleBooksEnabled { get; set; }

    public string? GoogleBooksApiKey { get; set; }

    /// <summary>Contact address used for the providers' polite pools (OpenAlex <c>mailto</c>, Crossref User-Agent). Recommended in production.</summary>
    public string? ContactMailto { get; set; }

    public string OpenAlexBaseUrl { get; set; } = "https://api.openalex.org";

    public string CrossrefBaseUrl { get; set; } = "https://api.crossref.org";

    public string OpenLibraryBaseUrl { get; set; } = "https://openlibrary.org";

    public string GoogleBooksBaseUrl { get; set; } = "https://www.googleapis.com";

    /// <summary>Hard per-request budget. The HttpClient itself stays unbounded; each call is cancelled at this deadline.</summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>Combined budget for one adapter operation including retries and pagination.</summary>
    public int OperationTimeoutSeconds { get; set; } = 60;

    public int MaxPerPage { get; set; } = 25;

    /// <summary>Bounded pagination: an adapter never walks more than this many pages in one operation.</summary>
    public int MaxPages { get; set; } = 2;

    public int MaxResultsPerProvider { get; set; } = 50;

    public int MaxCombinedResults { get; set; } = 150;

    /// <summary>Identifier-only lookups per batch (DOIs and ISBNs), bounded to prevent unbounded fan-out.</summary>
    public int MaxIdentifierLookups { get; set; } = 25;

    public int MaxAbstractCharacters { get; set; } = 4_000;

    public int MaxTitleCharacters { get; set; } = 600;

    public int MaxDescriptionCharacters { get; set; } = 4_000;

    public int MaxSearchTermCharacters { get; set; } = 1_000;

    /// <summary>Bounded JSON payload read per response. Larger payloads are refused, not streamed.</summary>
    public long MaxResponseBytes { get; set; } = 2_000_000;

    public int MaxRedirectHops { get; set; } = 3;

    /// <summary>Total attempts (first included) for retryable failures: 429, 408, 5xx and transport errors.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Ceiling applied to a provider-supplied Retry-After before waiting.</summary>
    public int MaxRetryDelaySeconds { get; set; } = 10;

    /// <summary>Base used for exponential backoff when no Retry-After is supplied.</summary>
    public int BackoffBaseMilliseconds { get; set; } = 400;

    /// <summary>Clamps configuration so a mistuned section cannot disable all bounds.</summary>
    public AcademicSourceDiscoveryOptions Normalize()
    {
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 2, 120);
        OperationTimeoutSeconds = Math.Clamp(OperationTimeoutSeconds, RequestTimeoutSeconds, 300);
        MaxPerPage = Math.Clamp(MaxPerPage, 1, 50);
        MaxPages = Math.Clamp(MaxPages, 1, 10);
        MaxResultsPerProvider = Math.Clamp(MaxResultsPerProvider, 1, 200);
        MaxCombinedResults = Math.Clamp(MaxCombinedResults, 1, 500);
        MaxIdentifierLookups = Math.Clamp(MaxIdentifierLookups, 1, 100);
        MaxAbstractCharacters = Math.Clamp(MaxAbstractCharacters, 0, 20_000);
        MaxTitleCharacters = Math.Clamp(MaxTitleCharacters, 16, 1_200);
        MaxDescriptionCharacters = Math.Clamp(MaxDescriptionCharacters, 0, 20_000);
        MaxSearchTermCharacters = Math.Clamp(MaxSearchTermCharacters, 8, 4_000);
        MaxResponseBytes = Math.Clamp(MaxResponseBytes, 4_096, 16_000_000);
        MaxRedirectHops = Math.Clamp(MaxRedirectHops, 0, 5);
        MaxAttempts = Math.Clamp(MaxAttempts, 1, 5);
        MaxRetryDelaySeconds = Math.Clamp(MaxRetryDelaySeconds, 0, 60);
        BackoffBaseMilliseconds = Math.Clamp(BackoffBaseMilliseconds, 0, 5_000);
        return this;
    }

    /// <summary>Hosts whose endpoints may be read. Built only from configured base URLs.</summary>
    public IReadOnlyList<string> AllowedEndpointHosts()
    {
        var hosts = new List<string>();
        foreach (var url in new[] { OpenAlexBaseUrl, CrossrefBaseUrl, OpenLibraryBaseUrl, GoogleBooksBaseUrl })
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                hosts.Add(uri.Host);
        }
        return hosts;
    }
}
