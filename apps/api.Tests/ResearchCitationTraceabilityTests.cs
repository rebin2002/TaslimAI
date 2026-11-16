using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Generated-report citation traceability. The structured `citationIds` arrays are not the only
/// citation surface: the provider also writes `[S1]`-style markers inline in the text it supplies,
/// and the executive summary and conclusion have no structured citation field at all. Every marker
/// that survives into the persisted report result, the CSV citation manifest and the DOCX/PDF
/// Sources appendix must resolve to a server-normalized source, so an unknown marker fails the
/// draft at validation time instead of being exported as if it were traceable.
/// </summary>
public sealed class ResearchCitationTraceabilityTests
{
    private static ResearchDraft BuildDraft(
        string executiveSummary = "A cited summary.",
        string conclusion = "A cited conclusion.",
        IReadOnlyList<ResearchReportBlock>? keyFindings = null,
        IReadOnlyList<ResearchSection>? sections = null) => new()
    {
        Title = "Solar energy research",
        Language = "en",
        ExecutiveSummary = executiveSummary,
        KeyFindings = keyFindings is null
            ? [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "Official evidence identifies the opportunity.", CitationIds = ["S1"] }]
            : [.. keyFindings],
        Sections = sections is null
            ? [new ResearchSection { Heading = "Evidence", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Independent market evidence adds context.", CitationIds = ["S2"] }] }]
            : [.. sections],
        Conclusion = conclusion,
        Sources = ["S1", "S2"],
    };

    private static HashSet<string> KnownCitations() => new(StringComparer.OrdinalIgnoreCase) { "S1", "S2" };

    [Theory]
    [InlineData("The summary cites [S9] for the headline claim.")]
    [InlineData("The conclusion relies on [S3] and nothing else.")]
    public void Rejects_unknown_inline_markers_outside_the_structured_citation_fields(string conclusion)
    {
        var isSummary = conclusion.StartsWith("The summary", StringComparison.Ordinal);
        var draft = BuildDraft(
            executiveSummary: isSummary ? conclusion : "A cited summary.",
            conclusion: isSummary ? "A cited conclusion." : conclusion);

        Assert.Throws<ResearchCitationValidationException>(
            () => ResearchDraftValidator.Validate(draft, KnownCitations(), new ResearchGenerationOptions()));
    }

    [Theory]
    [InlineData(ResearchBlockTypes.Paragraph, "A dangling paragraph marker [S7].", null)]
    [InlineData(ResearchBlockTypes.Bullets, null, new[] { "Backed claim.", "Unsupported claim [S7]." })]
    [InlineData(ResearchBlockTypes.NumberedList, null, new[] { "First [S1].", "Second [S12]." })]
    public void Rejects_unknown_inline_markers_in_block_body_and_list_items(
        string blockType, string? text, string[]? items)
    {
        var draft = BuildDraft(keyFindings:
        [
            new ResearchReportBlock { Type = blockType, Text = text, Items = items is null ? null : new List<string>(items), CitationIds = ["S1"] },
        ]);

        Assert.Throws<ResearchCitationValidationException>(
            () => ResearchDraftValidator.Validate(draft, KnownCitations(), new ResearchGenerationOptions()));
    }

    [Fact]
    public void Rejects_unknown_inline_markers_in_table_cells()
    {
        var draft = BuildDraft(sections:
        [
            new ResearchSection
            {
                Heading = "Comparison",
                Blocks =
                [
                    new ResearchReportBlock
                    {
                        Type = ResearchBlockTypes.Table,
                        Rows = [new ResearchTableRow { Cells = ["Capacity", "1.2 GW [S4]"] }],
                        CitationIds = ["S2"],
                    },
                ],
            },
        ]);

        Assert.Throws<ResearchCitationValidationException>(
            () => ResearchDraftValidator.Validate(draft, KnownCitations(), new ResearchGenerationOptions()));
    }

    [Fact]
    public void Accepts_inline_markers_that_resolve_to_a_server_normalized_source()
    {
        var draft = BuildDraft(
            executiveSummary: "The opportunity is real [S1].",
            conclusion: "Weigh the evidence before deciding [S2].",
            keyFindings:
            [
                new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "Official evidence identifies the opportunity. [s1]", CitationIds = ["S1"] },
            ],
            sections:
            [
                new ResearchSection
                {
                    Heading = "Comparison",
                    Blocks =
                    [
                        new ResearchReportBlock { Type = ResearchBlockTypes.Bullets, Items = ["Backed claim [S2].", "Also backed [S1]."], CitationIds = ["S1", "S2"] },
                        new ResearchReportBlock { Type = ResearchBlockTypes.Table, Rows = [new ResearchTableRow { Cells = ["Capacity", "1.2 GW [S2]"] }], CitationIds = ["S2"] },
                    ],
                },
            ]);

        ResearchDraftValidator.Validate(draft, KnownCitations(), new ResearchGenerationOptions());
    }

    [Fact]
    public void Leaves_non_citation_bracketed_prose_untouched()
    {
        var draft = BuildDraft(
            executiveSummary: "Iraq's 2024 [baseline] solar capacity is [TBD].",
            conclusion: "See the 2030 [target] figures.");

        ResearchDraftValidator.Validate(draft, KnownCitations(), new ResearchGenerationOptions());
    }
}
