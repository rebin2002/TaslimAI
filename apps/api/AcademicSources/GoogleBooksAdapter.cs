using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Optional book/work catalog backed by the Google Books volumes API. It participates only when
/// explicitly enabled and an API key is configured; without one it reports itself unconfigured
/// and the orchestrator simply omits it. Publisher descriptions are stored separately from
/// abstracts because a marketing blurb is not an academic summary, and view indications never
/// claim that the service read the book.
/// </summary>
public sealed class GoogleBooksAdapter(HttpClient httpClient, IOptions<AcademicSourceDiscoveryOptions> optionsAccessor, ILogger<GoogleBooksAdapter> logger) : IAcademicWorkSearchProvider, IAcademicBookCatalogProvider
{
    public const string Key = "googlebooks";

    private readonly AcademicSourceDiscoveryOptions options = optionsAccessor.Value.Normalize();
    private readonly AcademicHttpGateway gateway = new(httpClient, optionsAccessor.Value.Normalize());

    public string ProviderKey => Key;

    public bool IsConfigured => options.Enabled && options.GoogleBooksEnabled && !string.IsNullOrWhiteSpace(options.GoogleBooksApiKey);

    public async Task<IReadOnlyList<AcademicSourceRecord>> SearchWorksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
        => await SearchAsync(query, cancellationToken);

    public async Task<IReadOnlyList<AcademicSourceRecord>> SearchBooksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
        => await SearchAsync(query, cancellationToken);

    public async Task<IReadOnlyList<AcademicSourceRecord>> LookupByIsbnAsync(IReadOnlyList<string> isbns, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new AcademicSourceUnavailableException(new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Configuration));
        var results = new List<AcademicSourceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var budget = Math.Min(isbns.Count, options.MaxIdentifierLookups);
        for (var index = 0; index < budget && results.Count < options.MaxResultsPerProvider; index++)
        {
            var isbn = AcademicSourceIdentity.ToIsbn13(isbns[index]);
            if (isbn is null || !seen.Add(isbn)) continue;
            var items = await FetchItemsAsync($"isbn:{isbn}", 0, 5, null, cancellationToken);
            foreach (var item in items)
            {
                var record = MapItem(item);
                if (record is not null) results.Add(record);
            }
        }
        return results;
    }

    private async Task<IReadOnlyList<AcademicSourceRecord>> SearchAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new AcademicSourceUnavailableException(new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Configuration));
        if (string.IsNullOrWhiteSpace(query.SearchTerm)) return [];

        var target = Math.Clamp(query.MaxResults, 1, options.MaxResultsPerProvider);
        // The volumes API caps maxResults at 40; page budget uses the configured bound.
        var perPage = Math.Min(40, Math.Min(options.MaxPerPage, target));
        var pages = Math.Clamp((int)Math.Ceiling(target / (double)perPage), 1, options.MaxPages);
        var results = new List<AcademicSourceRecord>();
        for (var page = 0; page < pages && results.Count < target; page++)
        {
            var items = await FetchItemsAsync(query.SearchTerm, page * perPage, Math.Min(perPage, target - results.Count), query, cancellationToken);
            foreach (var item in items)
            {
                var record = MapItem(item);
                if (record is not null) results.Add(record);
                if (results.Count >= target) break;
            }
            if (items.Length < perPage) break;
        }
        return results;
    }

    private async Task<JsonElement[]> FetchItemsAsync(string term, int startIndex, int maxResults, AcademicDiscoveryQuery? filters, CancellationToken cancellationToken)
    {
        var queryTerm = term;
        if (filters?.YearFrom is { } from) queryTerm += $" +after:{from}";
        if (filters?.YearTo is { } to) queryTerm += $" +before:{to}";
        var pairs = new List<KeyValuePair<string, string?>>
        {
            new("q", queryTerm),
            new("startIndex", startIndex.ToString()),
            new("maxResults", Math.Clamp(maxResults, 1, 40).ToString()),
        };
        if (filters is not null && !string.IsNullOrWhiteSpace(filters.Language)) pairs.Add(new("langRestrict", filters.Language));
        if (filters is not null && filters.OpenAccessOnly) pairs.Add(new("filter", "free-ebooks"));
        // The key rides the query string because the volumes API has no header auth; it is
        // configured server-side and never derived from request input.
        pairs.Add(new("key", options.GoogleBooksApiKey!.Trim()));
        var endpoint = AcademicEndpoint.Create(options.GoogleBooksBaseUrl, "books/v1/volumes", pairs);
        var response = await gateway.GetJsonAsync(endpoint, cancellationToken);
        if (!response.Root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            // "totalItems": 0 legitimately carries no items array.
            if (response.Root.TryGetProperty("totalItems", out var total) && total.TryGetInt32(out var zero) && zero == 0) return [];
            logger.LogWarning("Book volumes search returned an unusable page. HttpStatus={HttpStatus}; FailureCategory=malformed", response.StatusCode);
            throw new AcademicSourceMalformedException(new AcademicProviderErrorDetails(response.StatusCode, AcademicSourceFailureCategories.Malformed));
        }
        return items.EnumerateArray().ToArray();
    }

    internal AcademicSourceRecord? MapItem(JsonElement item)
    {
        var id = ReadString(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (!item.TryGetProperty("volumeInfo", out var volume) || volume.ValueKind != JsonValueKind.Object) return null;
        var title = ReadString(volume, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var authors = ReadStringArray(volume, "authors");
        string? isbn13 = null, isbn10 = null;
        if (volume.TryGetProperty("industryIdentifiers", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in identifiers.EnumerateArray())
            {
                var kind = ReadString(entry, "type");
                var value = ReadString(entry, "identifier");
                if (string.Equals(kind, "ISBN_13", StringComparison.Ordinal)) isbn13 ??= AcademicSourceIdentity.ToIsbn13(value);
                else if (string.Equals(kind, "ISBN_10", StringComparison.Ordinal)) isbn10 ??= AcademicSourceIdentity.NormalizeIsbn(value);
            }
            isbn13 ??= AcademicSourceIdentity.ToIsbn13(isbn10);
            isbn10 ??= AcademicSourceIdentity.ToIsbn10(isbn13);
        }

        var access = AcademicAccessLevel.Metadata;
        if (item.TryGetProperty("accessInfo", out var accessInfo) && accessInfo.ValueKind == JsonValueKind.Object)
        {
            // Only an explicit "full" view, public domain status, or an available EPUB/PDF marks a
            // full-text indication. "partial"/"preview" is advertising, never readable content.
            var viewStatus = ReadString(accessInfo, "accessViewStatus");
            var isPublicDomain = accessInfo.TryGetProperty("isPublicDomain", out var publicDomain) && publicDomain.ValueKind == JsonValueKind.True;
            var pdfAvailable = accessInfo.TryGetProperty("pdf", out var pdf) && pdf.ValueKind == JsonValueKind.Object
                && pdf.TryGetProperty("isAvailable", out var pdfFlag) && pdfFlag.ValueKind == JsonValueKind.True;
            var epubAvailable = accessInfo.TryGetProperty("epub", out var epub) && epub.ValueKind == JsonValueKind.Object
                && epub.TryGetProperty("isAvailable", out var epubFlag) && epubFlag.ValueKind == JsonValueKind.True;
            if ((string.Equals(viewStatus, "full", StringComparison.OrdinalIgnoreCase) && (isPublicDomain || pdfAvailable || epubAvailable)) || (isPublicDomain && (pdfAvailable || epubAvailable)))
                access = AcademicAccessLevel.FullTextIndicated;
        }

        var description = ReadString(volume, "description");
        var publishedDate = ReadString(volume, "publishedDate");

        var safeMetadata = new Dictionary<string, object?>
        {
            ["volumeId"] = id,
            ["accessViewStatus"] = item.TryGetProperty("accessInfo", out var info) && info.ValueKind == JsonValueKind.Object ? ReadString(info, "accessViewStatus") : null,
            ["isPublicDomain"] = item.TryGetProperty("accessInfo", out var info2) && info2.ValueKind == JsonValueKind.Object && info2.TryGetProperty("isPublicDomain", out var pd) ? pd.ValueKind == JsonValueKind.True : null,
        };

        return new AcademicSourceRecord
        {
            ProviderKey = Key,
            ProviderWorkId = id,
            Isbn13 = isbn13,
            Isbn10 = isbn10,
            Title = AcademicText.Trim(title, options.MaxTitleCharacters),
            Authors = authors,
            Publisher = ReadString(volume, "publisher") is { } publisher ? AcademicText.Trim(publisher, 300) : null,
            PublicationYear = ReadYear(publishedDate),
            SourceType = AcademicSourceTypes.Book,
            Language = ReadString(volume, "languageCode"),
            PageCount = TryInt(volume, "pageCount"),
            // Google descriptions are publisher blurbs; they never count as a provider abstract.
            PublisherDescription = string.IsNullOrWhiteSpace(description) ? null : AcademicText.Trim(description, options.MaxDescriptionCharacters),
            AccessLevel = access,
            FullTextUrl = null,
            LandingUrl = ReadString(volume, "canonicalVolumeLink") is { } canonical && AcademicSourceUrlGuard.IsProviderIssuedUrlSafe(canonical) ? canonical : null,
            SafeMetadataJson = AcademicText.BoundedSerialize(safeMetadata),
        };
    }

    /// <summary>Accepts only <c>yyyy</c>, <c>yyyy-MM</c>, or <c>yyyy-MM-dd</c> forms; anything else yields no year.</summary>
    internal static int? ReadYear(string? publishedDate)
    {
        if (string.IsNullOrWhiteSpace(publishedDate)) return null;
        var trimmed = publishedDate.Trim();
        if (trimmed.Length is not (4 or 7 or 10)) return null;
        if (!int.TryParse(trimmed[..4], out var year) || year < 1000 || year > 9999) return null;
        if (trimmed.Length == 4) return year;
        if (trimmed[4] != '-' || !int.TryParse(trimmed[5..7], out var month) || month is < 1 or > 12) return null;
        if (trimmed.Length == 7) return year;
        if (trimmed[7] != '-' || !int.TryParse(trimmed[8..10], out var day) || day is < 1 or > 31) return null;
        return year;
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

    private static int? TryInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;
}
