using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class AcademicSourceMergerTests
{
    private static AcademicSourceRecord Record(
        string provider,
        string id,
        string title,
        string? doi = null,
        string? isbn13 = null,
        string? isbn10 = null,
        AcademicAccessLevel access = AcademicAccessLevel.Metadata,
        string? abstractText = null,
        string? fullTextUrl = null,
        int? year = null,
        string? pageRange = null) => new()
        {
            ProviderKey = provider,
            ProviderWorkId = id,
            Doi = doi,
            Isbn13 = isbn13,
            Isbn10 = isbn10,
            Title = title,
            SourceType = AcademicSourceTypes.JournalArticle,
            AccessLevel = access,
            Abstract = abstractText,
            FullTextUrl = fullTextUrl,
            PublicationYear = year,
            PageRange = pageRange,
        };

    [Fact]
    public void Collapses_records_with_equal_doi_across_providers_and_spelling_variants()
    {
        var merged = AcademicSourceMerger.Merge([
            Record("openalex", "W1", "Market Access", doi: "https://doi.org/10.1016/J.KR.2021.02.003", access: AcademicAccessLevel.FullTextIndicated, fullTextUrl: "https://journal.example/45.pdf", year: 2021),
            Record("crossref", "10.1016/j.kr.2021.02.003", "Pharmaceutical Market Access in Kurdistan", doi: "10.1016/j.kr.2021.02.003", abstractText: "A real abstract.", year: 2021, pageRange: "110-128"),
        ], 100);

        var record = Assert.Single(merged);
        // DOI-bearing, abstract-bearing Crossref wins primary; identifiers and the stronger
        // provider-indicated full-text marker are unioned.
        Assert.Equal("crossref", record.ProviderKey);
        Assert.Equal("10.1016/j.kr.2021.02.003", record.Doi);
        Assert.Equal("Pharmaceutical Market Access in Kurdistan", record.Title);
        Assert.Equal("A real abstract.", record.Abstract);
        Assert.Equal(AcademicAccessLevel.FullTextIndicated, record.AccessLevel);
        Assert.Equal("https://journal.example/45.pdf", record.FullTextUrl);
        Assert.Equal("110-128", record.PageRange);
        Assert.Contains("openalex", record.SafeMetadataJson);
        Assert.Contains("crossref", record.SafeMetadataJson);
    }

    [Fact]
    public void Treats_isbn_10_and_13_of_the_same_edition_as_one_identity()
    {
        var merged = AcademicSourceMerger.Merge([
            Record("googlebooks", "GB1", "Regulation Book", isbn13: "9781234567897"),
            Record("openlibrary", "OL1", "Regulation Book", isbn10: "123456789X"),
        ], 100);
        var record = Assert.Single(merged);
        Assert.Equal("9781234567897", record.Isbn13);
        Assert.Equal("123456789X", record.Isbn10);
    }

    [Fact]
    public void Keeps_identifier_less_records_from_different_providers_distinct()
    {
        var merged = AcademicSourceMerger.Merge([
            Record("openalex", "W9", "Untitled dataset"),
            Record("openlibrary", "W9", "Untitled dataset"),
        ], 100);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merging_never_upgrades_a_metadata_only_group_to_full_text()
    {
        var merged = AcademicSourceMerger.Merge([
            Record("openalex", "A", "Book A", doi: "10.5555/a", access: AcademicAccessLevel.Abstract, abstractText: "abstract"),
            Record("crossref", "B", "Book A", doi: "10.5555/a", access: AcademicAccessLevel.Abstract, abstractText: "abstract 2"),
        ], 100);
        var record = Assert.Single(merged);
        Assert.Equal(AcademicAccessLevel.Abstract, record.AccessLevel);
        Assert.Null(record.FullTextUrl);
    }

    [Fact]
    public void Result_cap_trips_over_the_largest_records_first_seen_in_order()
    {
        var inputs = Enumerable.Range(0, 10).Select(index => Record("p", $"W{index}", $"Title {index}", doi: $"10.5555/{index}")).ToArray();
        var merged = AcademicSourceMerger.Merge(inputs, 3);
        Assert.Equal(3, merged.Count);
        Assert.Equal("10.5555/0", merged[0].Doi);
        Assert.Equal("10.5555/2", merged[2].Doi);
    }

    [Fact]
    public void Empty_merge_is_empty() => Assert.Empty(AcademicSourceMerger.Merge([], 10));
}
