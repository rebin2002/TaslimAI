namespace Taslim.Api.AcademicSources;

/// <summary>
/// Configuration for scholarly source discovery. All bounds are enforced server-side; provider
/// base URLs are configuration, never request input, and their hosts form the endpoint allow
/// list used by <see cref="AcademicSourceUrlGuard"/>.
/// <para>
/// Every switch defaults to off. A deployment that registers the feature but forgets to write the
/// configuration section performs no outbound calls and reports each provider as disabled, rather
/// than silently starting to crawl four public APIs with a default User-Agent. Enabling discovery
/// is an explicit, per-provider decision.
/// </para>
/// </summary>
public sealed class AcademicSourceDiscoveryOptions
{
    public const string SectionName = "AcademicSourceDiscovery";

    /// <summary>Master switch. Off by default: absence of configuration must not activate egress.</summary>
    public bool Enabled { get; set; }

    public bool OpenAlexEnabled { get; set; }

    public bool CrossrefEnabled { get; set; }

    public bool OpenLibraryEnabled { get; set; }

    /// <summary>Google Books requires a configured API key; enabling it without a key disables the adapter rather than failing requests.</summary>
    public bool GoogleBooksEnabled { get; set; }

    public string? GoogleBooksApiKey { get; set; }

    /// <summary>Contact address used for the providers' polite pools (OpenAlex <c>mailto</c>, Crossref User-Agent). Recommended in production.</summary>
    public string? ContactMailto { get; set; }

    public string OpenAlexBaseUrl { get; set; } = "https://api.openalex.org";

    public string CrossrefBaseUrl { get; set; } = "https://api.crossref.org";

    public string OpenLibraryBaseUrl { get; set; } = "https://openlibrary.org";

    public string GoogleBooksBaseUrl { get; set; } = "https://www.googleapis.com";

    /// <summary>
    /// Hosts whose sub-domains may additionally be read. Empty by default, which makes the
    /// derived allow list <em>exact-host only</em>: a configured <c>openlibrary.org</c> permits
    /// <c>openlibrary.org</c> and nothing else. Entries here are the only way to admit breadth,
    /// so each one is an explicit operator decision and is logged as such. Wildcards, IP
    /// literals and empty entries are dropped by <see cref="Normalize"/>.
    /// </summary>
    public IReadOnlyList<string> EndpointHostExceptions { get; set; } = [];

    /// <summary>
    /// Whether a provider redirect may land on a <em>different</em> allow-listed host than the
    /// endpoint that was requested. Off by default: staying on one authority per operation is what
    /// keeps a provider-issued <c>Location</c> from carrying request credentials to another host.
    /// When turned on, cross-authority hops still revalidate the allow list and the resolved
    /// address, and never forward credentials (see <see cref="AcademicHttpGateway"/>).
    /// </summary>
    public bool AllowCrossHostRedirects { get; set; }

    /// <summary>How long a pooled (already-pinned) connection may be reused before reconnecting.</summary>
    public int PooledConnectionLifetimeSeconds { get; set; } = 300;

    /// <summary>Concurrent connections per provider host, bounded alongside the operation budget.</summary>
    public int MaxConnectionsPerServer { get; set; } = 8;

    /// <summary>Hard per-request budget. The HttpClient itself stays unbounded; each call is cancelled at this deadline.</summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>Combined budget for one adapter operation including retries and pagination.</summary>
    public int OperationTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Budget for the whole discovery request, across every catalog it visits and every DOI it
    /// checks. <see cref="OperationTimeoutSeconds"/> bounds ONE adapter operation, and because
    /// participants are visited one after another, a search's own worst case is the SUM of them:
    /// four catalogs and ten DOI checks at 60s each is a request that stays inside every per-call
    /// timeout while holding a shared search slot for minutes. When this budget runs out the
    /// search stops asking, returns what it already has, and says so honestly: the unanswered
    /// participants are reported as timeouts, the batch is marked partial, and the caller never
    /// sees a quiet empty "success". It can never be set below one request attempt plus one
    /// second, so a mistuned value cannot make the feature unreachable.
    /// </summary>
    public int DiscoveryBudgetSeconds { get; set; } = 90;

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
        // Never shorter than one request attempt plus a second, so a mistuned value cannot cut off
        // every catalog before any of them could answer once. It is deliberately NOT floored at a
        // whole operation budget: an operator tightening the request budget is choosing to accept
        // honestly-reported partial results, which is the safer direction than a slot held open
        // while each participant spends its own full timeout in sequence.
        DiscoveryBudgetSeconds = Math.Max(RequestTimeoutSeconds + 1, Math.Clamp(DiscoveryBudgetSeconds, 1, 600));
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
        PooledConnectionLifetimeSeconds = Math.Clamp(PooledConnectionLifetimeSeconds, 15, 3600);
        MaxConnectionsPerServer = Math.Clamp(MaxConnectionsPerServer, 1, 64);
        var exceptions = new List<string>();
        foreach (var entry in EndpointHostExceptions ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry) || entry.Contains('*')) continue;
            var normalized = AcademicSourceConnectionPolicy.NormalizeHost(entry);
            if (normalized.Length == 0) continue;
            // An IP literal in the exceptions list would be an allow-list entry, not breadth; the
            // exact list built from base URLs already covers literal endpoints. Syntactically
            // usable entries are kept even when they are unwise (a single label would admit every
            // domain under it) so <see cref="Validate"/> can fail loudly instead of the operator's
            // intent being silently discarded.
            if (System.Net.IPAddress.TryParse(normalized, out _)) continue;
            if (!exceptions.Contains(normalized, StringComparer.OrdinalIgnoreCase)) exceptions.Add(normalized);
        }
        EndpointHostExceptions = exceptions;
        return this;
    }

    /// <summary>Hosts whose endpoints may be read. Built only from configured base URLs.</summary>
    public IReadOnlyList<string> AllowedEndpointHosts()
    {
        var hosts = new List<string>();
        foreach (var url in new[] { OpenAlexBaseUrl, CrossrefBaseUrl, OpenLibraryBaseUrl, GoogleBooksBaseUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) continue;
            var normalized = AcademicSourceConnectionPolicy.NormalizeHost(uri.Host);
            if (normalized.Length == 0) continue;
            // A base URL aimed at loopback or a cloud metadata address is never admitted, so a
            // misconfigured section cannot create an internal target even before validation runs.
            if (AcademicSourceUrlGuard.TryParseIpLiteral(normalized, out var literal) && !AcademicSourceUrlGuard.IsPubliclyRoutable(literal)) continue;
            if (!hosts.Contains(normalized, StringComparer.OrdinalIgnoreCase)) hosts.Add(normalized);
        }
        return hosts;
    }

    /// <summary>
    /// Configuration errors that must stop the feature rather than degrade it. Called by the
    /// composed options so a mistuned section surfaces as a configuration failure instead of an
    /// over-broad egress path. Deliberately cheap to evaluate: no DNS, no I/O.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        foreach (var (name, url) in new[]
                 {
                     (nameof(OpenAlexBaseUrl), OpenAlexBaseUrl),
                     (nameof(CrossrefBaseUrl), CrossrefBaseUrl),
                     (nameof(OpenLibraryBaseUrl), OpenLibraryBaseUrl),
                     (nameof(GoogleBooksBaseUrl), GoogleBooksBaseUrl),
                 })
        {
            if (string.IsNullOrWhiteSpace(url)) { failures.Add($"{name} is empty"); continue; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            { failures.Add($"{name} is not an absolute http(s) URL"); continue; }
            if (uri.UserInfo.Length > 0) { failures.Add($"{name} embeds credentials"); continue; }
            if (AcademicSourceUrlGuard.TryParseIpLiteral(uri.Host, out var literal) && !AcademicSourceUrlGuard.IsPubliclyRoutable(literal))
                failures.Add($"{name} points at a non-public address");
        }
        foreach (var exception in EndpointHostExceptions)
        {
            // Breadth is the dangerous knob: a single-label exception would admit every domain.
            if (exception.Count(c => c == '.') < 1) failures.Add($"endpoint host exception '{exception}' is too broad");
        }
        return failures;
    }
}
