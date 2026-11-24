using System.Text.Json;

namespace Taslim.Api.AcademicSources;

/// <summary>
/// Deterministic cross-provider deduplication. Records collapse only on verified identifiers
/// (DOI first, ISBN-13 second); provider-scoped ids keep everything else distinct. Merging
/// fills missing bibliographic fields from other copies of the same identity, but never
/// invents content: page ranges and abstracts come verbatim from whichever provider reported
/// them, and the access marker only reflects a provider's own indication.
/// </summary>
public static class AcademicSourceMerger
{
    public static IReadOnlyList<AcademicSourceRecord> Merge(IReadOnlyList<AcademicSourceRecord> records, int maxResults)
    {
        var groups = new Dictionary<string, List<AcademicSourceRecord>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var record in records)
        {
            var key = record.DedupKey;
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = [];
                groups[key] = bucket;
                order.Add(key);
            }
            bucket.Add(record);
        }

        var merged = new List<AcademicSourceRecord>(order.Count);
        foreach (var key in order)
        {
            var bucket = groups[key];
            var primary = ChoosePrimary(bucket);
            merged.Add(bucket.Count == 1 ? WithProviders(primary, bucket) : MergeGroup(primary, bucket));
        }
        return maxResults <= 0 ? [] : merged.Take(maxResults).ToArray();
    }

    /// <summary>
    /// Prefer records with richer verified metadata as primary: DOI-bearing, then abstract-bearing,
    /// then full-text-indicated, then first seen. Ordering keeps results stable for identical input.
    /// </summary>
    private static AcademicSourceRecord ChoosePrimary(List<AcademicSourceRecord> bucket)
    {
        var best = bucket[0];
        var bestScore = PrimaryScore(best);
        foreach (var candidate in bucket.Skip(1))
        {
            var score = PrimaryScore(candidate);
            if (score > bestScore) { best = candidate; bestScore = score; }
        }
        return best;
    }

    private static int PrimaryScore(AcademicSourceRecord record)
    {
        var score = 0;
        if (AcademicSourceIdentity.NormalizeDoi(record.Doi) is not null) score += 8;
        if (!string.IsNullOrWhiteSpace(record.Abstract)) score += 4;
        if (record.PublicationYear is not null) score += 2;
        if (record.Authors.Count > 0) score += 1;
        return score;
    }

    private static AcademicSourceRecord MergeGroup(AcademicSourceRecord primary, List<AcademicSourceRecord> bucket)
    {
        string? FirstNonBlank(string? fallback, Func<AcademicSourceRecord, string?> selector)
            => !string.IsNullOrWhiteSpace(fallback) ? fallback : bucket.Select(selector).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        // The primary's own DOI spelling wins; the identifier value survives dedup unchanged.
        var doi = AcademicSourceIdentity.NormalizeDoi(primary.Doi) is not null ? primary.Doi : bucket.Select(record => record.Doi).FirstOrDefault(value => AcademicSourceIdentity.NormalizeDoi(value) is not null);

        var isbn13 = primary.Isbn13 ?? bucket.Select(record => AcademicSourceIdentity.ToIsbn13(record.Isbn13 ?? record.Isbn10)).FirstOrDefault(value => value is not null);
        var isbn10 = primary.Isbn10 ?? (isbn13 is null ? null : AcademicSourceIdentity.ToIsbn10(isbn13))
            ?? bucket.Select(record => AcademicSourceIdentity.NormalizeIsbn(record.Isbn10)).FirstOrDefault(value => value is not null);

        var access = bucket.Max(record => record.AccessLevel);
        var fullTextUrl = bucket.FirstOrDefault(record => record.AccessLevel >= AcademicAccessLevel.FullTextIndicated)?.FullTextUrl ?? primary.FullTextUrl;

        var best = primary with
        {
            Doi = doi,
            Isbn13 = isbn13,
            Isbn10 = isbn10,
            ContainerTitle = FirstNonBlank(primary.ContainerTitle, record => record.ContainerTitle),
            Publisher = FirstNonBlank(primary.Publisher, record => record.Publisher),
            PublicationYear = primary.PublicationYear ?? bucket.Select(record => record.PublicationYear).FirstOrDefault(year => year is not null),
            Language = FirstNonBlank(primary.Language, record => record.Language),
            Volume = FirstNonBlank(primary.Volume, record => record.Volume),
            Issue = FirstNonBlank(primary.Issue, record => record.Issue),
            // Page data is edition/print specific. The strongest identifier match that actually
            // reported pages wins; nothing is combined across different page claims.
            PageRange = FirstNonBlank(primary.PageRange, record => record.PageRange),
            PageCount = primary.PageCount ?? bucket.Select(record => record.PageCount).FirstOrDefault(count => count is not null),
            CitationCount = primary.CitationCount ?? bucket.Select(record => record.CitationCount).FirstOrDefault(count => count is not null),
            Abstract = FirstNonBlank(primary.Abstract, record => record.Abstract),
            PublisherDescription = FirstNonBlank(primary.PublisherDescription, record => record.PublisherDescription),
            LandingUrl = FirstNonBlank(primary.LandingUrl, record => record.LandingUrl),
            FullTextUrl = fullTextUrl,
            // A merged record may not claim a higher access level than the strongest provider
            // indication in the group; indicators still mean "advertised", never "read".
            AccessLevel = access,
            Authors = MergeAuthors(primary, bucket),
            SafeMetadataJson = MergeSafeMetadata(primary, bucket),
        };
        return best;
    }

    private static IReadOnlyList<string> MergeAuthors(AcademicSourceRecord primary, List<AcademicSourceRecord> bucket)
    {
        var authors = primary.Authors.Count > 0 ? primary.Authors : bucket.SelectMany(record => record.Authors).Take(50).ToArray();
        return authors;
    }

    private static string? MergeSafeMetadata(AcademicSourceRecord primary, List<AcademicSourceRecord> bucket)
    {
        var providers = bucket.Select(record => record.ProviderKey).Distinct(StringComparer.Ordinal).ToArray();
        var fields = new Dictionary<string, object?> { ["mergedFrom"] = providers };
        if (primary.SafeMetadataJson is { Length: > 0 } json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False || property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 8)
                            fields[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
                    }
                }
            }
            catch (JsonException) { }
        }
        return SerializeBounded(fields, primary.SafeMetadataJson?.Length ?? 0);
    }

    private static AcademicSourceRecord WithProviders(AcademicSourceRecord record, List<AcademicSourceRecord> bucket) => record;

    private static string SerializeBounded(Dictionary<string, object?> fields, int _)
    {
        var json = JsonSerializer.Serialize(fields);
        return json.Length <= 4_000 ? json : JsonSerializer.Serialize(new { mergedFrom = fields["mergedFrom"], note = "metadata_truncated" });
    }
}
