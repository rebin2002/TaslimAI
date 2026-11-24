namespace Taslim.Api.AcademicSources;

/// <summary>
/// One normalized scholarly record in the customer-facing shape. There is deliberately no
/// provider field, no provider-native id, no raw provider metadata blob and no full-text body:
/// identity stays operational (<see cref="AcademicSourceRecord.ProviderKey"/>), and access stays
/// an indication (<see cref="FullTextIndicated"/>). Everything else is bibliographic data a
/// catalog actually returned for this work.
/// </summary>
public sealed record AcademicSearchSourceDto(
    string Title,
    IReadOnlyList<string> Authors,
    int? PublicationYear,
    /// <summary>Customer-safe source type; unknown provider classifications collapse to "other".</summary>
    string SourceType,
    string? ContainerTitle,
    string? Publisher,
    string? Volume,
    string? Issue,
    /// <summary>Page range exactly as reported. Never derived.</summary>
    string? PageRange,
    int? PageCount,
    string? Language,
    int? CitationCount,
    string? Doi,
    string? Isbn13,
    string? Isbn10,
    /// <summary>Abstract text the catalog itself returned, or null. Not a quotation from the work.</summary>
    string? Abstract,
    /// <summary> Publisher blurb/catalog description when returned. Marketing text, never labelled an abstract.</summary>
    string? PublisherDescription,
    string? LandingUrl,
    /// <summary>An advertised full-text address. Presence proves the provider pointed at one, not that it was read.</summary>
    string? FullTextUrl,
    /// <summary>One of "metadata" or "abstract": the highest level at which THIS service has content to show.</summary>
    string AccessLevel,
    /// <summary>True when a catalog indicated a public full-text location. The student still has not read it.</summary>
    bool FullTextIndicated,
    AcademicSourceProvenanceDto Provenance,
    double? Relevance);

/// <summary>
/// Honest provenance for a discovered record: how the studio came to know about it, and when.
/// <see cref="Basis"/> is always "scholarly_catalog_metadata" — a machine-readable promise that
/// bibliographic metadata (at most a provider-supplied abstract) was found, never that the work
/// was read, quoted or verified for retraction.
/// </summary>
public sealed record AcademicSourceProvenanceDto(
    string Basis,
    DateTime RetrievedAtUtc,
    string AccessDescription);

/// <summary>Outcome of one DOI registration check, without any provider identity.</summary>
public sealed record AcademicDoiCheckDto(
    /// <summary>The DOI exactly as the caller submitted it.</summary>
    string SubmittedDoi,
    bool IsRegistered,
    /// <summary>
    /// Safe, provider-neutral failure category when <see cref="IsRegistered"/> is false:
    /// "not_registered", "invalid_syntax", "rate_limited", "timeout", "unavailable",
    /// "malformed_response", "configuration".
    /// </summary>
    string? FailureCode);

public sealed record AcademicSourceSearchResponse(
    Guid WorkspaceId,
    int Page,
    int PageSize,
    /// <summary>
    /// Distinct records this bounded search surfaced, counted across the whole pool THIS request
    /// fetched, so it agrees with <see cref="Sources"/> in this response and tells a client how many
    /// pages exist at this page size. It is NOT a snapshot of the catalogs: this endpoint keeps no
    /// cursor and caches no pool, so a request for another page is a second, independent search whose
    /// pool can differ. A catalog that re-ranked the topic, added a record, or answered this time but
    /// not last can make a later page repeat an item the caller already saw or omit one it had.
    /// Consistency is promised within one response, never across two.
    /// </summary>
    int TotalMatched,
    /// <summary>
    /// True when this search stopped at one of its own ceilings and therefore cannot claim to have
    /// surfaced everything that matched: the combined pool filled <c>MaxCombinedResults</c>, some
    /// catalog came back with exactly its <c>MaxResultsPerProvider</c> ceiling, or the whole-request
    /// discovery budget ran out before every catalog had answered. False means the bounded search
    /// completed, and <see cref="TotalMatched"/> is the whole of what it was allowed to find — which
    /// is NOT the same as the number of works in the world that match, and is not a "there is no
    /// next page" flag either: that is answered by <see cref="TotalMatched"/> against
    /// <see cref="PageSize"/>. Treating this flag as the next-page signal strands a caller on page 1
    /// of any search whose pool is shorter than the bound.
    /// </summary>
    bool HasMore,
    /// <summary>True when part of the catalog pool did not answer; the result is not exhaustive.</summary>
    bool Partial,
    string? PartialNotice,
    IReadOnlyList<AcademicSearchSourceDto> Sources,
    IReadOnlyList<AcademicDoiCheckDto> DoiChecks);

/// <summary>
/// Maps internal discovery records onto the customer-facing DTOs. This is the only place the
/// provider identity fields are dropped, so the guarantee is auditable in one file:
/// <see cref="AcademicSourceRecord.ProviderKey"/>, <see cref="AcademicSourceRecord.ProviderWorkId"/>
/// and <see cref="AcademicSourceRecord.SafeMetadataJson"/> have no counterpart on any DTO above.
/// </summary>
public static class AcademicSourcePresentation
{
    public const string ProvenanceBasis = "scholarly_catalog_metadata";

    public static AcademicSearchSourceDto ToDto(AcademicSourceRecord record)
    {
        var fullTextIndicated = record.AccessLevel >= AcademicAccessLevel.FullTextIndicated;
        var hasAbstract = !string.IsNullOrWhiteSpace(record.Abstract);
        // Catalog-advertised addresses are admitted for display here rather than inside an adapter, so
        // one choke point governs every link a reader can click — including a merged record, where the
        // surviving address came from whichever catalog answered.
        var landingUrl = AcademicProviderLinkPolicy.Project(record.LandingUrl);
        var fullTextUrl = AcademicProviderLinkPolicy.Project(record.FullTextUrl);
        // A full-text claim resting solely on an address this service refuses to link is no longer a
        // claim about a public location, so the flag leaves with the link instead of advertising a
        // reading the studio will not open. A record that carries only an access level, with no
        // advertised address at all, keeps its indication: that is what the catalog asserted.
        var advertisedFullText = !string.IsNullOrWhiteSpace(record.FullTextUrl);
        fullTextIndicated = fullTextIndicated && (!advertisedFullText || fullTextUrl is not null);
        return new AcademicSearchSourceDto(
            record.Title,
            record.Authors,
            record.PublicationYear,
            ToCustomerSourceType(record.SourceType),
            record.ContainerTitle,
            record.Publisher,
            record.Volume,
            record.Issue,
            record.PageRange,
            record.PageCount,
            record.Language,
            record.CitationCount,
            record.Doi,
            record.Isbn13,
            record.Isbn10,
            record.Abstract,
            record.PublisherDescription,
            landingUrl,
            fullTextUrl,
            hasAbstract ? "abstract" : "metadata",
            fullTextIndicated,
            new AcademicSourceProvenanceDto(
                ProvenanceBasis,
                record.RetrievedAtUtc,
                fullTextIndicated
                    ? "Bibliographic record found; a public full-text location was indicated but not read."
                    : hasAbstract
                        ? "Bibliographic record and catalog abstract found; the full work has not been read."
                        : "Bibliographic record found; the content itself has not been read."),
            record.RelevanceScore);
    }

    public static AcademicDoiCheckDto ToDoiDto(AcademicDoiValidationResult result)
    {
        var submitted = result.SubmittedDoi ?? result.ResolvedDoi;
        return new AcademicDoiCheckDto(
            submitted ?? string.Empty,
            result.IsRegistered,
            result.IsRegistered ? null : ToCustomerFailureCode(result.FailureCategory));
    }

    /// <summary>
    /// Provider classifications collapse onto the source types the studio understands. Anything
    /// the provider did not classify is honestly reported as "other" rather than guessed into a
    /// more precise type.
    /// </summary>
    private static string ToCustomerSourceType(string providerType) => providerType switch
    {
        AcademicSourceTypes.JournalArticle => "journal_article",
        AcademicSourceTypes.Book => "book",
        AcademicSourceTypes.BookChapter => "book_chapter",
        AcademicSourceTypes.ConferencePaper => "conference_paper",
        AcademicSourceTypes.Thesis => "thesis",
        AcademicSourceTypes.Preprint => "working_paper",
        AcademicSourceTypes.Report => "report",
        AcademicSourceTypes.Dataset => "dataset",
        _ => "other",
    };

    private static string ToCustomerFailureCode(string? category) => category switch
    {
        AcademicSourceFailureCategories.NotFound => "not_registered",
        AcademicSourceFailureCategories.InvalidIdentifier => "invalid_syntax",
        AcademicSourceFailureCategories.RateLimited => "rate_limited",
        AcademicSourceFailureCategories.Timeout => "timeout",
        AcademicSourceFailureCategories.Transient => "unavailable",
        AcademicSourceFailureCategories.Malformed => "malformed_response",
        AcademicSourceFailureCategories.Configuration => "configuration",
        _ => "unavailable",
    };
}
