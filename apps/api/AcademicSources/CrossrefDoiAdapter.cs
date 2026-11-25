using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Bibliographic validation against DOI registration agency metadata. A DOI is reported as
/// registered only when the resolver returns a registration record; unregistered DOIs produce a
/// not-registered result rather than an exception, so callers can reject fabricated citations.
/// Publisher-advertised full-text links are recorded as counts only — Crossref does not certify
/// open access, so this adapter never marks <see cref="AcademicAccessLevel.FullTextIndicated"/>
/// from a bare link entry.
/// </summary>
public sealed class CrossrefDoiAdapter(HttpClient httpClient, IOptions<AcademicSourceDiscoveryOptions> optionsAccessor, ILogger<CrossrefDoiAdapter> logger) : IAcademicDoiValidator
{
    public const string Key = "crossref";
    private static readonly IReadOnlySet<int> NotFound = new HashSet<int> { 404 };

    private readonly AcademicSourceDiscoveryOptions options = optionsAccessor.Value.Normalize();
    private readonly AcademicHttpGateway gateway = new(httpClient, optionsAccessor.Value.Normalize());

    public string ProviderKey => Key;

    public bool IsConfigured => options.Enabled && options.CrossrefEnabled;

    public async Task<AcademicDoiValidationResult> ValidateAsync(string doi, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new AcademicDoiValidationResult { IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.Configuration };

        var normalized = AcademicSourceIdentity.NormalizeDoi(doi);
        var pathSafe = normalized is null ? null : AcademicSourceIdentity.ToPathSafeDoi(normalized);
        if (pathSafe is null)
            return new AcademicDoiValidationResult { IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.InvalidIdentifier };

        try
        {
            var endpoint = AcademicEndpoint.Create(options.CrossrefBaseUrl, $"works/{pathSafe}");
            var response = await gateway.GetJsonAsync(endpoint, cancellationToken, NotFound);
            if (response.StatusCode == 404 || !response.IsSuccess)
                return new AcademicDoiValidationResult { ResolvedDoi = normalized, IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.NotFound };
            if (!response.Root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning("DOI metadata response carried no registration record. HttpStatus={HttpStatus}; FailureCategory=malformed", response.StatusCode);
                return new AcademicDoiValidationResult { ResolvedDoi = normalized, IsRegistered = false, FailureCategory = AcademicSourceFailureCategories.Malformed };
            }
            return new AcademicDoiValidationResult
            {
                ResolvedDoi = normalized,
                IsRegistered = true,
                Source = MapRegistration(message, pathSafe),
            };
        }
        catch (AcademicSourceException exception)
        {
            return new AcademicDoiValidationResult { IsRegistered = false, FailureCategory = exception.Details?.FailureCategory ?? AcademicSourceFailureCategories.Transient };
        }
    }

    internal AcademicSourceRecord MapRegistration(JsonElement message, string pathSafeDoi)
    {
        var registeredDoi = ReadString(message, "DOI") ?? pathSafeDoi;
        var title = ReadFirstString(message, "title") ?? ReadFirstString(message, "subtitle");
        var authors = ReadAuthors(message);
        var abstractText = ReadString(message, "abstract") is { } raw ? AcademicText.Trim(AcademicText.StripXmlTags(raw) ?? string.Empty, options.MaxAbstractCharacters) : null;
        var type = ReadString(message, "type");

        string? isbn13 = null, isbn10 = null;
        if (message.TryGetProperty("identifier", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var identifier in identifiers.EnumerateArray())
            {
                var kind = ReadString(identifier, "type");
                var value = ReadString(identifier, "ID");
                if (!string.Equals(kind, "isbn", StringComparison.OrdinalIgnoreCase) || value is null) continue;
                isbn13 ??= AcademicSourceIdentity.ToIsbn13(value);
                isbn10 ??= AcademicSourceIdentity.NormalizeIsbn(value)?.Length == 10 ? AcademicSourceIdentity.NormalizeIsbn(value) : AcademicSourceIdentity.ToIsbn10(value);
            }
        }
        if (isbn13 is null && message.TryGetProperty("isbn", out var isbnList) && isbnList.ValueKind == JsonValueKind.Array)
            isbn13 = isbnList.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String ? AcademicSourceIdentity.ToIsbn13(element.GetString()) : null).FirstOrDefault(value => value is not null);
        if (isbn10 is null && isbn13 is not null) isbn10 = AcademicSourceIdentity.ToIsbn10(isbn13);

        var resourceLinks = message.TryGetProperty("link", out var links) && links.ValueKind == JsonValueKind.Array ? links.GetArrayLength() : 0;
        var licenseCount = message.TryGetProperty("license", out var licenses) && licenses.ValueKind == JsonValueKind.Array ? licenses.GetArrayLength() : 0;
        var safeMetadata = new Dictionary<string, object?>
        {
            ["registeredDoi"] = registeredDoi,
            ["publisherType"] = type,
            ["referenceCount"] = TryInt(message, "reference-count"),
            ["resourceLinksAdvertised"] = resourceLinks,
            ["licenseCount"] = licenseCount,
        };

        return new AcademicSourceRecord
        {
            ProviderKey = Key,
            ProviderWorkId = registeredDoi,
            Doi = AcademicSourceIdentity.NormalizeDoi(registeredDoi),
            Isbn13 = isbn13,
            Isbn10 = isbn10,
            Title = AcademicText.Trim(string.IsNullOrWhiteSpace(title) ? registeredDoi : title!, options.MaxTitleCharacters),
            Authors = authors,
            Publisher = ReadString(message, "publisher") is { } publisher ? AcademicText.Trim(publisher, 300) : null,
            PublicationYear = ReadYear(message),
            SourceType = MapType(type),
            ContainerTitle = ReadFirstString(message, "container-title") is { } container ? AcademicText.Trim(container, options.MaxTitleCharacters) : null,
            Volume = ReadString(message, "volume"),
            Issue = ReadString(message, "issue"),
            // "page" is the verbatim printed range; many electronic-only articles carry an article
            // number instead and are reported without a page range rather than a fake one.
            PageRange = NormalizePageRange(ReadString(message, "page")),
            CitationCount = TryInt(message, "is-referenced-by-count"),
            Abstract = string.IsNullOrWhiteSpace(abstractText) ? null : abstractText,
            LandingUrl = $"https://doi.org/{pathSafeDoi}",
            FullTextUrl = null,
            AccessLevel = string.IsNullOrWhiteSpace(abstractText) ? AcademicAccessLevel.Metadata : AcademicAccessLevel.Abstract,
            SafeMetadataJson = AcademicText.BoundedSerialize(safeMetadata),
        };
    }

    internal static string? NormalizePageRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        // Crossref prints ranges as "123-145" (sometimes en dashes); anything else is an
        // article-number-style value and is kept verbatim without being called a page range.
        return trimmed.Length <= 64 ? trimmed : null;
    }

    private static IReadOnlyList<string> ReadAuthors(JsonElement message)
    {
        var authors = new List<string>();
        if (!message.TryGetProperty("author", out var list) || list.ValueKind != JsonValueKind.Array) return authors;
        foreach (var author in list.EnumerateArray())
        {
            if (authors.Count >= 50) break;
            var family = ReadString(author, "family");
            var given = ReadString(author, "given");
            var literal = ReadString(author, "name");
            var name = family is not null ? (given is not null ? $"{given} {family}" : family) : literal;
            if (!string.IsNullOrWhiteSpace(name)) authors.Add(name.Trim());
        }
        return authors;
    }

    private static int? ReadYear(JsonElement message)
    {
        foreach (var property in new[] { "issued", "published-print", "published-online", "created" })
        {
            if (message.TryGetProperty(property, out var date) && date.ValueKind == JsonValueKind.Object
                && date.TryGetProperty("date-parts", out var parts) && parts.ValueKind == JsonValueKind.Array
                && parts.GetArrayLength() > 0)
            {
                var first = parts[0];
                if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 && first[0].TryGetInt32(out var year) && year >= 1000 && year <= 9999)
                    return year;
            }
        }
        return null;
    }

    private static string MapType(string? providerType) => providerType switch
    {
        "journal-article" => AcademicSourceTypes.JournalArticle,
        "book-chapter" or "book-section" => AcademicSourceTypes.BookChapter,
        "book" or "monograph" or "edited-book" or "reference-book" => AcademicSourceTypes.Book,
        "proceedings" or "proceedings-article" => AcademicSourceTypes.ConferencePaper,
        "report" => AcademicSourceTypes.Report,
        "posted-content" => AcademicSourceTypes.Preprint,
        "dataset" => AcademicSourceTypes.Dataset,
        "component" => AcademicSourceTypes.Component,
        "dissertation" => AcademicSourceTypes.Thesis,
        null or "" => AcademicSourceTypes.Unknown,
        _ => AcademicSourceTypes.Other,
    };

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadFirstString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) return null;
        return array[0].ValueKind == JsonValueKind.String ? array[0].GetString() : null;
    }

    private static int? TryInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;
}
