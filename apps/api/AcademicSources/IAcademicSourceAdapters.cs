namespace Taslim.Api.AcademicSources;

/// <summary>
/// A scholarly catalog that can search works by free-text and bibliographic filters. Providers
/// that are switched off or missing credentials report <see cref="IsConfigured"/> false so the
/// orchestrator can skip them without failing the batch.
/// </summary>
public interface IAcademicWorkSearchProvider
{
    /// <summary>Internal key; never rendered to customers.</summary>
    string ProviderKey { get; }

    bool IsConfigured { get; }

    Task<IReadOnlyList<AcademicSourceRecord>> SearchWorksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Authoritative DOI registration lookup. Validates that a DOI resolves to registered metadata;
/// never produces a citation for an unregistered DOI and never fabricates bibliographic fields.
/// </summary>
public interface IAcademicDoiValidator
{
    string ProviderKey { get; }

    bool IsConfigured { get; }

    Task<AcademicDoiValidationResult> ValidateAsync(string doi, CancellationToken cancellationToken = default);
}

/// <summary>Book catalog lookup (Open Library and Google Books volumes).</summary>
public interface IAcademicBookCatalogProvider
{
    string ProviderKey { get; }

    bool IsConfigured { get; }

    Task<IReadOnlyList<AcademicSourceRecord>> SearchBooksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Lookups by verified ISBN; invalid ISBNs are skipped, not repaired.</summary>
    Task<IReadOnlyList<AcademicSourceRecord>> LookupByIsbnAsync(IReadOnlyList<string> isbns, CancellationToken cancellationToken = default);
}
