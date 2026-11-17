using System.Text;
using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic synthetic contracts for <see cref="ResearchExportReferenceOrdering"/>:
/// the reference order of the Research export citation list when several
/// sources share a title. Every input is synthetic (fixed literals), the
/// assertions are byte-exact, and nothing here touches the network, a
/// database, or production wiring. URL escaping/canonicalization is
/// deliberately out of scope: references must echo the stored URL verbatim.
/// </summary>
public sealed class ResearchExportReferenceOrderingTests
{
    private static ResearchSourceCandidate Source(
        string citationId,
        string title,
        string? url = null,
        int rank = 0) =>
        new(citationId, url, url, title, "example.gov", null, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "web",
            null, null, null, rank, true, null);

    [Fact]
    public void Same_title_sources_are_sequenced_by_citation_id_not_discovery_order()
    {
        // Deliberately shuffled arrival order with ascending discovery ranks.
        var sources = new[]
        {
            Source("S3", "Flood Risk Assessment", "https://example.gov/flood-c", rank: 1),
            Source("S1", "Flood Risk Assessment", "https://example.gov/flood-a", rank: 2),
            Source("S2", "Flood Risk Assessment", "https://example.gov/flood-b", rank: 3),
        };

        var ordered = ResearchExportReferenceOrdering.OrderForExport(sources);

        Assert.Equal(new[] { "S1", "S2", "S3" }, ordered.Select(s => s.CitationId));
    }

    [Fact]
    public void Casing_and_whitespace_variants_of_one_title_group_together()
    {
        var sources = new[]
        {
            Source("S4", "Quarterly Water Report"),
            Source("S1", "  quarterly   WATER  report  "),
            Source("S3", "QUARTERLY WATER REPORT"),
            Source("S2", "Quarterly Water Report"),
        };

        var ordered = ResearchExportReferenceOrdering.OrderForExport(sources);

        // All four are the same title key, so the group is citation-ordered.
        Assert.Equal(new[] { "S1", "S2", "S3", "S4" }, ordered.Select(s => s.CitationId));
    }

    [Fact]
    public void Title_is_the_primary_key_so_later_titles_never_preempt_earlier_groups()
    {
        var sources = new[]
        {
            Source("S1", "Zeta Study"),
            Source("S9", "Alpha Study"),
            Source("S2", "Alpha Study"),
        };

        var ordered = ResearchExportReferenceOrdering.OrderForExport(sources);

        Assert.Equal(new[] { "S2", "S9", "S1" }, ordered.Select(s => s.CitationId));
    }

    [Fact]
    public void Citation_id_tiebreak_is_byte_ordinal_not_culture_or_natural_sort()
    {
        // Pins the exact sequence so no ICU/natural-sort drift can change it:
        // ordinal comparison puts S10 before S2 (and S2 before S20, S3).
        var sources = new[]
        {
            Source("S3", "Same Title"),
            Source("S20", "Same Title"),
            Source("S2", "Same Title"),
            Source("S10", "Same Title"),
        };

        var ordered = ResearchExportReferenceOrdering.OrderForExport(sources);

        Assert.Equal(new[] { "S10", "S2", "S20", "S3" }, ordered.Select(s => s.CitationId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_synthetic_arrival_permutation_renders_the_identical_reference_list(int permutation)
    {
        var canonical = new[]
        {
            Source("S1", "Beta Report", "https://example.gov/beta-1"),
            Source("S2", "Beta Report", "https://example.gov/beta-2"),
            Source("S1", "Alpha Report", "https://example.gov/alpha-1"),
            Source("S3", "Alpha Report", "https://example.gov/alpha-3"),
        };

        // Fixed, deterministic re-arrangements of the same source set.
        var arrivals = permutation switch
        {
            0 => canonical,
            1 => canonical.Reverse().ToArray(),
            2 => [canonical[2], canonical[0], canonical[3], canonical[1]],
            _ => [canonical[3], canonical[1], canonical[2], canonical[0]],
        };

        var rendered = ResearchExportReferenceOrdering.RenderReferences(arrivals);

        // The Alpha group precedes Beta; within each group the ordinal
        // citation id sequences the same-title references.
        Assert.Equal(
            [
                "[S1] Alpha Report — https://example.gov/alpha-1",
                "[S3] Alpha Report — https://example.gov/alpha-3",
                "[S1] Beta Report — https://example.gov/beta-1",
                "[S2] Beta Report — https://example.gov/beta-2",
            ],
            rendered);
    }

    [Fact]
    public void Same_title_group_order_ignores_rank_and_retrieval_metadata()
    {
        // Discovery ranks and the shared synthetic retrieval timestamp are
        // decoys: only the ordinal citation id sequences the same-title group.
        var sources = new[]
        {
            Source("S7", "Identical Title", rank: 99),
            Source("S6", "Identical Title", rank: 1),
            Source("S5", "Identical Title", rank: 50),
        };

        var ordered = ResearchExportReferenceOrdering.OrderForExport(sources);

        Assert.Equal(new[] { "S5", "S6", "S7" }, ordered.Select(s => s.CitationId));
    }

    [Fact]
    public void Reference_line_echoes_the_stored_url_verbatim_without_escaping_or_rewriting()
    {
        // This lane must not canonicalize or escape: whatever locator bytes
        // are stored are the bytes rendered (URL escaping is a separate lane).
        const string url = "https://example.gov/path%20with spaces?q=a&x=one#section";
        var rendered = ResearchExportReferenceOrdering.RenderReferences(
            [Source("S1", "Mixed Locator Report", url)]);

        Assert.Equal($"[S1] Mixed Locator Report — {url}", Assert.Single(rendered));
    }

    [Fact]
    public void Sources_without_a_url_render_the_uploaded_source_locator()
    {
        var rendered = ResearchExportReferenceOrdering.RenderReferences(
            [Source("S2", "Uploaded Brief"), Source("S1", "Uploaded Brief")]);

        Assert.Equal(
            [
                "[S1] Uploaded Brief — Uploaded source",
                "[S2] Uploaded Brief — Uploaded source",
            ],
            rendered);
    }

    [Fact]
    public void Ordered_rendering_is_byte_stable_across_repeated_calls()
    {
        var sources = new[]
        {
            Source("S3", "Delta Report", "https://example.gov/delta-3"),
            Source("S2", "alpha report", "https://example.gov/alpha-2"),
            Source("S1", "Alpha Report", "https://example.gov/alpha-1"),
            Source("S1", "Charlie Report", "https://example.gov/charlie-1"),
        };

        string JoinBytes(IReadOnlyList<string> lines)
        {
            var builder = new StringBuilder();
            foreach (var line in lines)
                builder.Append(line).Append('\n');
            return builder.ToString();
        }

        var first = JoinBytes(ResearchExportReferenceOrdering.RenderReferences(sources));
        var second = JoinBytes(ResearchExportReferenceOrdering.RenderReferences(
            sources.Reverse().ToArray()));

        Assert.Equal(
            "[S1] Alpha Report — https://example.gov/alpha-1\n" +
            "[S2] alpha report — https://example.gov/alpha-2\n" +
            "[S1] Charlie Report — https://example.gov/charlie-1\n" +
            "[S3] Delta Report — https://example.gov/delta-3\n",
            first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Ordering_is_pure_and_preserves_the_input_sequence()
    {
        var sources = new[]
        {
            Source("S9", "Zeta Study"),
            Source("S2", "Alpha Study"),
        };
        var before = sources.Select(s => s.CitationId).ToArray();

        var ordered = ResearchExportReferenceOrdering.OrderForExport(sources);

        Assert.NotSame(sources, ordered);
        Assert.Equal(before, sources.Select(s => s.CitationId).ToArray());
        Assert.Equal(new[] { "S2", "S9" }, ordered.Select(s => s.CitationId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Degenerate_lists_round_trip(int count)
    {
        var sources = count == 0
            ? Array.Empty<ResearchSourceCandidate>()
            : [Source("S1", "Only Reference", "https://example.gov/only")];

        var rendered = ResearchExportReferenceOrdering.RenderReferences(sources);

        if (count == 0)
        {
            Assert.Empty(rendered);
        }
        else
        {
            Assert.Equal("[S1] Only Reference — https://example.gov/only", Assert.Single(rendered));
        }
    }

    [Fact]
    public void Title_normalizer_collapses_interior_whitespace_and_trims()
    {
        Assert.Equal("Global Health Report", ResearchExportReferenceOrdering.NormalizeTitle("  Global   Health  Report  "));
        Assert.Equal(string.Empty, ResearchExportReferenceOrdering.NormalizeTitle(null));
        Assert.Equal(string.Empty, ResearchExportReferenceOrdering.NormalizeTitle("   "));
        Assert.Equal("a b", ResearchExportReferenceOrdering.NormalizeTitle("a\tb"));
    }

    [Fact]
    public void Sources_heading_literal_matches_the_export_section_name()
    {
        Assert.Equal("Sources", ResearchExportReferenceOrdering.SourcesHeading);
    }
}
