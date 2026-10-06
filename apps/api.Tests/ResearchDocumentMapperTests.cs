using Taslim.Api.Contracts;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchDocumentMapperTests
{
    [Fact]
    public void To_document_draft_preserves_conclusion_before_sources()
    {
        var draft = new ResearchDraft
        {
            Title = "Solar energy research",
            ExecutiveSummary = "A cited summary.",
            KeyFindings = [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "A finding", CitationIds = ["S1"] }],
            Sections = [new ResearchSection { Heading = "Evidence", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Evidence", CitationIds = ["S1"] }] }],
            Conclusion = "The evidence supports a cautious investment decision.",
        };
        var sources = new[]
        {
            new ResearchSourceCandidate("S1", "https://example.gov/source", "https://example.gov/source", "Official source", "example.gov", null, null, DateTime.UtcNow, "web", "Evidence", "Evidence", null, 1, true, null),
        };

        var document = ResearchDocumentMapper.ToDocumentDraft(draft, sources);

        Assert.Equal(new[] { "Key findings", "Evidence", "Conclusion", "Sources" }, document.Sections.Select(section => section.Heading));
        var conclusion = document.Sections.Single(section => section.Heading == "Conclusion");
        Assert.Equal(DocumentBlockTypes.Paragraph, conclusion.Blocks.Single().Type);
        Assert.Equal(draft.Conclusion, conclusion.Blocks.Single().Text);
    }
}
