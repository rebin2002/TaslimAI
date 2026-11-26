using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Book and ISBN catalog lookup against the Open Library search API. Open Library describes
/// editions; it does not provide peer-reviewed abstracts. Internet Archive identifiers reported
/// by the catalog mean a borrowing copy may exist — they are recorded as provenance flags only
/// and never mark the source as having readable full text.
/// </summary>
public sealed class OpenLibraryBooksAdapter(HttpClient httpClient, IOptions<AcademicSourceDiscoveryOptions> optionsAccessor, ILogger<OpenLibraryBooksAdapter> logger) : IAcademicBookCatalogProvider
{
    public const string Key = "openlibrary";
    private const string Fields = "key,title,author_name,first_publish_year,publisher,isbn,oclc,ia,ebook_access,lending_edition,readable,number_of_pages_median,language,cover_i";

    private readonly AcademicSourceDiscoveryOptions options = optionsAccessor.Value.Normalize();
    private readonly AcademicHttpGateway gateway = new(httpClient, optionsAccessor.Value.Normalize());

    public string ProviderKey => Key;

    public bool IsConfigured => options.Enabled && options.OpenLibraryEnabled;

    public async Task<IReadOnlyList<AcademicSourceRecord>> SearchBooksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new AcademicSourceUnavailableException(new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Configuration));
        if (string.IsNullOrWhiteSpace(query.SearchTerm)) return [];

        var target = Math.Clamp(query.MaxResults, 1, options.MaxResultsPerProvider);
        var perPage = (int)Math.Min(options.MaxPerPage, target);
        var pages = Math.Clamp((int)Math.Ceiling(target / (double)perPage), 1, options.MaxPages);
        var results = new List<AcademicSourceRecord>();
        for (var page = 0; page < pages && results.Count < target; page++)
        {
            var docs = await SearchAsync(("q", query.SearchTerm), page * perPage, Math.Min(perPage, target - results.Count), query, cancellationToken);
            foreach (var doc in docs)
            {
                var record = MapDocument(doc);
                if (record is not null) results.Add(record);
                if (results.Count >= target) break;
            }
            if (docs.Length < perPage) break;
        }
        return results;
    }

    public async Task<IReadOnlyList<AcademicSourceRecord>> LookupByIsbnAsync(IReadOnlyList<string> isbns, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new AcademicSourceUnavailableException(new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Configuration));
        var results = new List<AcademicSourceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var budget = Math.Min(isbns.Count, options.MaxIdentifierLookups);
        for (var index = 0; index < budget; index++)
        {
            var isbn = AcademicSourceIdentity.ToIsbn13(isbns[index]);
            if (isbn is null || !seen.Add(isbn)) continue;
            var docs = await SearchAsync(("isbn", isbn), 0, 5, null, cancellationToken);
            foreach (var doc in docs)
            {
                var record = MapDocument(doc);
                if (record is not null) results.Add(record);
            }
        }
        return results;
    }

    private async Task<JsonElement[]> SearchAsync((string, string) term, int offset, int limit, AcademicDiscoveryQuery? filters, CancellationToken cancellationToken)
    {
        var pairs = new List<KeyValuePair<string, string?>>
        {
            new(term.Item1, term.Item2),
            new("fields", Fields),
            new("offset", offset.ToString()),
            new("limit", Math.Clamp(limit, 1, options.MaxPerPage).ToString()),
        };
        if (filters?.YearFrom is { } from) pairs.Add(new("published_start", from.ToString()));
        if (filters?.YearTo is { } to) pairs.Add(new("published_end", to.ToString()));
        if (filters is not null && filters.OpenAccessOnly) pairs.Add(new("ebook_access", "public"));
        var endpoint = AcademicEndpoint.Create(options.OpenLibraryBaseUrl, "search.json", pairs);
        var response = await gateway.GetJsonAsync(endpoint, cancellationToken);
        if (!response.Root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("Book catalog search returned an unusable page. HttpStatus={HttpStatus}; FailureCategory=malformed", response.StatusCode);
            throw new AcademicSourceMalformedException(new AcademicProviderErrorDetails(response.StatusCode, AcademicSourceFailureCategories.Malformed));
        }
        return docs.EnumerateArray().ToArray();
    }

    internal AcademicSourceRecord? MapDocument(JsonElement doc)
    {
        var key = ReadString(doc, "key");
        var title = ReadString(doc, "title");
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith('/')) return null;
        if (string.IsNullOrWhiteSpace(title)) return null; // A catalog row without a title is unusable; never substitute one.

        var authors = ReadStringArray(doc, "author_name");
        var publishers = ReadStringArray(doc, "publisher");
        var archiveIds = ReadStringArray(doc, "ia");
        var isbn = ReadIsbn(doc);
        var ebookAccess = ReadString(doc, "ebook_access");
        var isPubliclyReadable = string.Equals(ebookAccess, "public", StringComparison.OrdinalIgnoreCase);
        var hasLendingCopy = archiveIds.Count > 0 || doc.TryGetProperty("lending_edition", out var lending) && lending.ValueKind == JsonValueKind.String;

        string? language = null;
        if (doc.TryGetProperty("language", out var languages) && languages.ValueKind == JsonValueKind.Array && languages.GetArrayLength() > 0)
            language = ReadString(languages[0], "value") ?? (languages[0].ValueKind == JsonValueKind.String ? languages[0].GetString() : null);

        var safeMetadata = new Dictionary<string, object?>
        {
            ["openLibraryKey"] = key,
            ["hasLendingCopy"] = hasLendingCopy,
            ["ebookAccess"] = ebookAccess,
            ["catalogOclc"] = ReadFirstNumber(doc, "oclc"),
        };

        return new AcademicSourceRecord
        {
            ProviderKey = Key,
            ProviderWorkId = key,
            Title = AcademicText.Trim(title, options.MaxTitleCharacters),
            Isbn13 = isbn.Isbn13,
            Isbn10 = isbn.Isbn10,
            Authors = authors.Take(50).ToArray(),
            Publisher = publishers.Count > 0 ? AcademicText.Trim(publishers[0], 300) : null,
            PublicationYear = TryInt(doc, "first_publish_year"),
            SourceType = AcademicSourceTypes.Book,
            Language = language,
            PageCount = TryInt(doc, "number_of_pages_median"),
            // A lending copy is borrowable text behind controlled digital lending, not an open
            // full-text marker; only an explicit "public" access facet advertises readable text.
            AccessLevel = isPubliclyReadable ? AcademicAccessLevel.FullTextIndicated : AcademicAccessLevel.Metadata,
            FullTextUrl = null,
            LandingUrl = AcademicEndpoint.Create(options.OpenLibraryBaseUrl, key).ToString(),
            SafeMetadataJson = AcademicText.BoundedSerialize(safeMetadata),
        };
    }

    private (string? Isbn13, string? Isbn10) ReadIsbn(JsonElement doc)
    {
        string? isbn13 = null, isbn10 = null;
        if (!doc.TryGetProperty("isbn", out var list) || list.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var element in list.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) continue;
            var candidate = element.GetString();
            isbn13 ??= AcademicSourceIdentity.ToIsbn13(candidate);
            isbn10 ??= AcademicSourceIdentity.NormalizeIsbn(candidate)?.Length == 10 ? AcademicSourceIdentity.NormalizeIsbn(candidate) : AcademicSourceIdentity.ToIsbn10(candidate);
        }
        return (isbn13, isbn10 ?? (isbn13 is null ? null : AcademicSourceIdentity.ToIsbn10(isbn13)));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return values;
        foreach (var item in array.EnumerateArray())
        {
            if (values.Count >= 50) break;
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value && !string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
        }
        return values;
    }

    private static string? ReadFirstNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) return null;
        var first = array[0];
        return first.TryGetInt64(out var number) ? number.ToString() : first.ValueKind == JsonValueKind.String ? first.GetString() : null;
    }

    private static int? TryInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;
}
