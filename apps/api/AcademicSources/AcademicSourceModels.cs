namespace Taslim.Api.AcademicSources;

/// <summary>
/// Coarse classification of what a scholarly provider says a source is. Values mirror the
/// provider-reported type only; an adapter never infers a more prestigious type than reported.
/// </summary>
public static class AcademicSourceTypes
{
    public const string JournalArticle = "journal_article";
    public const string Book = "book";
    public const string BookChapter = "book_chapter";
    public const string ConferencePaper = "conference_paper";
    public const string Thesis = "thesis";
    public const string Preprint = "preprint";
    public const string Report = "report";
    public const string Dataset = "dataset";
    public const string Component = "component";
    public const string Other = "other";
    public const string Unknown = "unknown";
}

/// <summary>
/// What the provider itself indicates about access to the source body. These markers describe an
/// <em>indication</em> returned by the catalog; they never assert that this service has retrieved
/// or read the full text. Discovery adapters only ever emit <see cref="Metadata"/>,
/// <see cref="Abstract"/> or <see cref="FullTextIndicated"/>; <see cref="FullTextContent"/> is
/// reserved for a later phase that actually fetches and stores content behind the provider link.
/// </summary>
public enum AcademicAccessLevel
{
    /// <summary>Bibliographic metadata only. Never present it as though content was read.</summary>
    Metadata = 0,

    /// <summary>The provider returned an abstract or equivalent summary text.</summary>
    Abstract = 1,

    /// <summary>The provider points at a publicly accessible full-text location. The location was advertised, not read.</summary>
    FullTextIndicated = 2,

    /// <summary>Full-text content was actually retrieved and stored. Not produced by Phase A discovery.</summary>
    FullTextContent = 3,
}

/// <summary>
/// A normalized scholarly source produced by exactly one provider. Identifiers (DOI, ISBN) carry
/// only values the provider returned; titles, abstracts, pages and publication data are never
/// fabricated or inferred. <see cref="Abstract"/> is null unless the provider itself supplied
/// abstract text, and <see cref="AccessLevel"/> never exceeds what the provider indicated.
/// </summary>
public sealed record AcademicSourceRecord
{
    /// <summary>Internal provider key ("openalex", "crossref", "openlibrary", "googlebooks").
    /// Provider identity is operational metadata and is never rendered to customers.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Provider-native work/volume identifier as returned by the provider.</summary>
    public required string ProviderWorkId { get; init; }

    public string? Doi { get; init; }

    /// <summary>ISBN-13 normalized with a verified check digit, when a provider returned one.</summary>
    public string? Isbn13 { get; init; }

    /// <summary>ISBN-10 as returned (or derived from a verified ISBN-13 by exact conversion).</summary>
    public string? Isbn10 { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = [];

    public string? Publisher { get; init; }

    public int? PublicationYear { get; init; }

    /// <summary>One of <see cref="AcademicSourceTypes"/>; provider-reported classification only.</summary>
    public required string SourceType { get; init; }

    public string? Language { get; init; }

    /// <summary>Journal/proceedings container name when the provider returned one.</summary>
    public string? ContainerTitle { get; init; }

    public string? Volume { get; init; }

    public string? Issue { get; init; }

    /// <summary>Page range exactly as the provider printed it. Never derived or guessed.</summary>
    public string? PageRange { get; init; }

    /// <summary>Page count when the provider stated one (catalog data), never inferred.</summary>
    public int? PageCount { get; init; }

    public int? CitationCount { get; init; }

    /// <summary>Abstract text the provider actually returned, trimmed to a bounded length.</summary>
    public string? Abstract { get; init; }

    /// <summary>
    /// Publisher description/blurb (Google Books) or first line (Open Library). This is catalog
    /// marketing text, not a peer-reviewed abstract, and is kept separate so the UI never labels
    /// it as one.
    /// </summary>
    public string? PublisherDescription { get; init; }

    /// <summary>Landing page URL exactly as issued by the provider, after the safety guard.</summary>
    public string? LandingUrl { get; init; }

    /// <summary>Full-text/PDF location the provider explicitly advertised, after the safety guard.
    /// Presence means "the provider says this exists", not "this service read the document".</summary>
    public string? FullTextUrl { get; init; }

    /// <summary>Highest access level the provider indicated. See <see cref="AcademicAccessLevel"/>.</summary>
    public required AcademicAccessLevel AccessLevel { get; init; }

    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Search relevance in provider units when the provider ranked the result.</summary>
    public double? RelevanceScore { get; init; }

    /// <summary>Sanitized, bounded JSON of provider-reported extras (open-access status, identifiers,
    /// reference count). Contains no credentials and no full text.</summary>
    public string? SafeMetadataJson { get; init; }

    /// <summary>Stable identity for dedup: DOI wins, then normalized ISBN-13, else provider-scoped id.</summary>
    public string DedupKey => AcademicSourceIdentity.BuildDedupKey(this);
}

/// <summary>A discovery request against scholarly catalogs.</summary>
public sealed record AcademicDiscoveryQuery
{
    /// <summary>Free-text scholarly search term. Required when searching; may be null for identifier-only lookups.</summary>
    public string? SearchTerm { get; init; }

    /// <summary>DOIs to validate/resolve through the DOI metadata provider.</summary>
    public IReadOnlyList<string> Dois { get; init; } = [];

    /// <summary>ISBN-10 or ISBN-13 strings to look up in book catalogs. Invalid identifiers are rejected before any network call.</summary>
    public IReadOnlyList<string> Isbns { get; init; } = [];

    public int? YearFrom { get; init; }

    public int? YearTo { get; init; }

    public string? Language { get; init; }

    /// <summary>Ask catalogs to restrict to sources they mark open access. Providers that cannot honor this simply return their normal results.</summary>
    public bool OpenAccessOnly { get; init; }

    /// <summary>Bounded by <see cref="AcademicSourceDiscoveryOptions.MaxResultsPerProvider"/>.</summary>
    public int MaxResults { get; init; } = 25;
}

/// <summary>Aggregated outcome of one provider participation inside a discovery batch.</summary>
public sealed record AcademicProviderOutcome
{
    public required string ProviderKey { get; init; }
    public required string Status { get; init; }
    public int SourceCount { get; init; }

    /// <summary>Coarse failure category ("rate_limited", "timeout", "transient", "configuration", "rejected", "malformed"). Never contains provider payloads.</summary>
    public string? FailureCategory { get; init; }

    /// <summary>True when the provider returned fewer pages than requested because of a bounded budget.</summary>
    public bool Truncated { get; init; }
}

public static class AcademicProviderOutcomeStatus
{
    public const string Ok = "ok";
    public const string Empty = "empty";
    public const string Disabled = "disabled";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

/// <summary>Result of a merged, deduplicated multi-provider discovery batch.</summary>
public sealed record AcademicDiscoveryBatch
{
    public required IReadOnlyList<AcademicSourceRecord> Sources { get; init; }

    /// <summary>One outcome per provider that was asked to participate.</summary>
    public required IReadOnlyList<AcademicProviderOutcome> ProviderOutcomes { get; init; }

    /// <summary>Per-DOI registration checks, in the order the DOIs were supplied.</summary>
    public IReadOnlyList<AcademicDoiValidationResult> DoiValidations { get; init; } = [];

    /// <summary>True when at least one provider failed or was truncated, i.e. the batch is not exhaustive.</summary>
    public bool PartialResult => ProviderOutcomes.Any(outcome =>
        outcome.Status is AcademicProviderOutcomeStatus.Failed or AcademicProviderOutcomeStatus.Partial);
}

/// <summary>Outcome of validating a DOI against authoritative registration metadata.</summary>
public sealed record AcademicDoiValidationResult
{
    /// <summary>DOI exactly as registered (provider casing), null when not found.</summary>
    public string? ResolvedDoi { get; init; }

    /// <summary>True only when the DOI resolver returned a registration record.</summary>
    public required bool IsRegistered { get; init; }

    /// <summary>Normalized record built from registration metadata; null when unresolvable.</summary>
    public AcademicSourceRecord? Source { get; init; }

    /// <summary>Safe category when resolution failed ("not_found", "rejected", "rate_limited", "timeout", "transient", "malformed", "invalid_syntax").</summary>
    public string? FailureCategory { get; init; }
}
