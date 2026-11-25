using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Scholarly works discovery against the OpenAlex REST catalog. Emits only provider-reported
/// bibliographic data. An abstract reconstructed from the provider's inverted index is marked as
/// an abstract, never as full text; a <c>pdf_url</c> on an open-access location marks
/// <see cref="AcademicAccessLevel.FullTextIndicated"/> — an advertised location, not read content.
/// </summary>
public sealed class OpenAlexWorksAdapter(HttpClient httpClient, IOptions<AcademicSourceDiscoveryOptions> optionsAccessor, ILogger<OpenAlexWorksAdapter> logger) : IAcademicWorkSearchProvider
{
    public const string Key = "openalex";
    private const string SelectFields = "id,doi,title,display_name,authorships,publication_year,language,type,biblio,primary_location,best_oa_location,open_access,cited_by_count,referenced_by_count,abstract_inverted_index,relevance_score";

    private readonly AcademicSourceDiscoveryOptions options = optionsAccessor.Value.Normalize();
    private readonly AcademicHttpGateway gateway = new(httpClient, optionsAccessor.Value.Normalize());

    public string ProviderKey => Key;

    public bool IsConfigured => options.Enabled && options.OpenAlexEnabled;

    public async Task<IReadOnlyList<AcademicSourceRecord>> SearchWorksAsync(AcademicDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new AcademicSourceUnavailableException(new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Configuration));
        if (string.IsNullOrWhiteSpace(query.SearchTerm)) return [];

        var results = new List<AcademicSourceRecord>();
        var target = Math.Clamp(query.MaxResults, 1, options.MaxResultsPerProvider);
        var perPage = (int)Math.Min(options.MaxPerPage, target);
        var pages = Math.Clamp((int)Math.Ceiling(target / (double)perPage), 1, options.MaxPages);

        for (var page = 1; page <= pages && results.Count < target; page++)
        {
            var endpoint = AcademicEndpoint.Create(options.OpenAlexBaseUrl, "works", BuildQuery(query, page, perPage, target - results.Count));
            var response = await gateway.GetJsonAsync(endpoint, cancellationToken);
            if (!response.Root.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Academic catalog search returned an unusable page. HttpStatus={HttpStatus}; FailureCategory=malformed", response.StatusCode);
                throw new AcademicSourceMalformedException(new AcademicProviderErrorDetails(response.StatusCode, AcademicSourceFailureCategories.Malformed));
            }
            foreach (var item in items.EnumerateArray())
            {
                var record = MapWork(item);
                if (record is not null) results.Add(record);
                if (results.Count >= target) break;
            }
            if (items.GetArrayLength() < perPage) break;
        }
        return results;
    }

    private List<KeyValuePair<string, string?>> BuildQuery(AcademicDiscoveryQuery query, int page, int perPage, int remaining)
    {
        var pairs = new List<KeyValuePair<string, string?>>
        {
            new("search", query.SearchTerm),
            new("per-page", Math.Min(perPage, remaining).ToString()),
            new("page", page.ToString()),
            new("select", SelectFields),
            new("sort", "relevance_score:desc"),
        };
        var filters = new List<string>();
        if (query.YearFrom is { } from) filters.Add($"from_publication_date:{from}-01-01");
        if (query.YearTo is { } to) filters.Add($"to_publication_date:{to}-12-31");
        if (!string.IsNullOrWhiteSpace(query.Language)) filters.Add($"language:{query.Language}");
        if (query.OpenAccessOnly) filters.Add("open_access.is_oa:true");
        if (filters.Count > 0) pairs.Add(new("filter", string.Join(',', filters)));
        if (IsContactAddress(options.ContactMailto)) pairs.Add(new("mailto", options.ContactMailto!.Trim()));
        return pairs;
    }

    internal AcademicSourceRecord? MapWork(JsonElement work)
    {
        var id = ReadString(work, "id");
        if (id is null) return null;
        var workId = id[(id.LastIndexOf('/') + 1)..];
        var title = ReadString(work, "display_name") ?? ReadString(work, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var authors = new List<string>();
        if (work.TryGetProperty("authorships", out var authorships) && authorships.ValueKind == JsonValueKind.Array)
        {
            foreach (var authorship in authorships.EnumerateArray())
            {
                if (authors.Count >= 50) break;
                if (authorship.TryGetProperty("author", out var author) && ReadString(author, "display_name") is { } name && !string.IsNullOrWhiteSpace(name))
                    authors.Add(name.Trim());
            }
        }

        var landingUrl = ReadNested(work, ["primary_location", "landing_page_url"]) ?? id;
        var isOa = work.TryGetProperty("open_access", out var openAccess) && openAccess.ValueKind == JsonValueKind.Object
            && openAccess.TryGetProperty("is_oa", out var isOaElement) && isOaElement.ValueKind == JsonValueKind.True;
        var oaStatus = ReadNested(work, ["open_access", "oa_status"]);
        var pdfUrl = isOa ? ReadNested(work, ["best_oa_location", "pdf_url"]) : null;
        var abstractText = BuildAbstract(work);

        string? volume = null, issue = null, pageRange = null;
        if (work.TryGetProperty("biblio", out var biblio) && biblio.ValueKind == JsonValueKind.Object)
        {
            volume = ReadNumberish(biblio, "volume");
            issue = ReadNumberish(biblio, "issue");
            var first = ReadNumberish(biblio, "first_page");
            var last = ReadNumberish(biblio, "last_page");
            if (first is not null && last is not null) pageRange = $"{first}-{last}";
            else if (first is not null) pageRange = first;
        }

        var access = pdfUrl is not null && AcademicSourceUrlGuard.IsProviderIssuedUrlSafe(pdfUrl)
            ? AcademicAccessLevel.FullTextIndicated
            : abstractText is not null ? AcademicAccessLevel.Abstract : AcademicAccessLevel.Metadata;

        var safeMetadata = new Dictionary<string, object?>
        {
            ["openAlexId"] = workId,
            ["oaStatus"] = oaStatus,
            ["openAccess"] = isOa,
            ["fullTextAdvertised"] = access >= AcademicAccessLevel.FullTextIndicated,
        };
        if (work.TryGetProperty("referenced_by_count", out var referenced) && referenced.TryGetInt32(out var referencedCount)) safeMetadata["referencedByCount"] = referencedCount;

        return new AcademicSourceRecord
        {
            ProviderKey = Key,
            ProviderWorkId = workId,
            Doi = AcademicSourceIdentity.NormalizeDoi(ReadString(work, "doi")),
            Title = AcademicText.Trim(title, options.MaxTitleCharacters),
            Authors = authors,
            SourceType = MapType(ReadString(work, "type")),
            PublicationYear = work.TryGetProperty("publication_year", out var year) && year.TryGetInt32(out var yearValue) ? yearValue : null,
            Language = ReadString(work, "language"),
            ContainerTitle = ReadNested(work, ["primary_location", "source", "display_name"]) is { } container ? AcademicText.Trim(container, options.MaxTitleCharacters) : null,
            Volume = volume,
            Issue = issue,
            PageRange = pageRange,
            CitationCount = work.TryGetProperty("cited_by_count", out var cited) && cited.TryGetInt32(out var citedValue) ? citedValue : null,
            Abstract = abstractText,
            LandingUrl = AcademicSourceUrlGuard.IsProviderIssuedUrlSafe(landingUrl) ? landingUrl : null,
            FullTextUrl = access >= AcademicAccessLevel.FullTextIndicated ? pdfUrl : null,
            AccessLevel = access,
            RelevanceScore = work.TryGetProperty("relevance_score", out var relevance) && relevance.TryGetDouble(out var relevanceValue) ? relevanceValue : null,
            SafeMetadataJson = AcademicText.BoundedSerialize(safeMetadata),
        };
    }

    private string? BuildAbstract(JsonElement work)
    {
        if (!work.TryGetProperty("abstract_inverted_index", out var inverted) || inverted.ValueKind != JsonValueKind.Object) return null;
        // Positions of every word the provider exposed. Gaps mean words the provider withheld, so
        // the reconstruction marks them with an ellipsis instead of silently joining sentences.
        var positions = new SortedDictionary<int, string>();
        foreach (var property in inverted.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var indexElement in property.Value.EnumerateArray())
            {
                if (!indexElement.TryGetInt32(out var index)) continue;
                if (!positions.TryAdd(index, property.Name)) positions[index] = positions[index] + " " + property.Name;
            }
        }
        if (positions.Count == 0) return null;
        var builder = new StringBuilder();
        int? previous = null;
        foreach (var (index, word) in positions)
        {
            if (builder.Length > 0)
            {
                if (previous is not null && index == previous.Value + 1) builder.Append(' ');
                else builder.Append(" … ");
            }
            builder.Append(word);
            previous = index;
        }
        return AcademicText.Trim(builder.ToString(), options.MaxAbstractCharacters);
    }

    private static string MapType(string? providerType) => providerType switch
    {
        "article" => AcademicSourceTypes.JournalArticle,
        "book" or "monograph" => AcademicSourceTypes.Book,
        "book-chapter" => AcademicSourceTypes.BookChapter,
        "proceedings-article" or "proceedings-series-report" => AcademicSourceTypes.ConferencePaper,
        "dissertation" => AcademicSourceTypes.Thesis,
        "preprint" => AcademicSourceTypes.Preprint,
        "report" => AcademicSourceTypes.Report,
        "dataset" => AcademicSourceTypes.Dataset,
        "component" or "supplementary-materials" => AcademicSourceTypes.Component,
        null or "" => AcademicSourceTypes.Unknown,
        _ => AcademicSourceTypes.Other,
    };

    private static bool IsContactAddress(string? value) => value is { Length: <= 254 } && value.Contains('@') && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadNumberish(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number.ToString() : null,
            _ => null,
        };
    }

    private static string? ReadNested(JsonElement element, string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}

internal static class AcademicText
{
    public static string Trim(string value, int max) => max <= 0 ? string.Empty : value.Length <= max ? value : value[..Math.Max(1, max - 3)].TrimEnd() + "...";

    public static string? BoundedSerialize(Dictionary<string, object?> fields, int maxCharacters = 2_000)
    {
        if (maxCharacters <= 0) return null;
        var json = JsonSerializer.Serialize(fields);
        return json.Length <= maxCharacters ? json : JsonSerializer.Serialize(new Dictionary<string, object?> { ["note"] = "metadata_truncated" });
    }

    /// <summary>Strips markup from provider-supplied summary text (e.g. JATS XML abstracts).</summary>
    public static string? StripXmlTags(string value)
    {
        var builder = new StringBuilder(value.Length);
        var inside = false;
        foreach (var c in value)
        {
            if (c == '<') { inside = true; continue; }
            if (c == '>') { inside = false; builder.Append(' '); continue; }
            if (!inside) builder.Append(c);
        }
        var collapsed = System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }
}
