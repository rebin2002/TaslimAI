using Taslim.Api.Documents;
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void To_document_draft_omits_empty_conclusion_without_a_blank_section(string? conclusion)
    {
        var draft = new ResearchDraft
        {
            Title = "Research without a conclusion",
            ExecutiveSummary = "Summary",
            Conclusion = conclusion!,
        };

        var document = ResearchDocumentMapper.ToDocumentDraft(draft, []);

        Assert.Equal(new[] { "Key findings", "Sources" }, document.Sections.Select(section => section.Heading));
    }

    [Fact]
    public void Conclusion_is_in_a_real_docx_and_both_exports_pass_integrity_validation()
    {
        const string conclusion = "The evidence supports a cautious investment decision.";
        var research = new ResearchDraft
        {
            Title = "Solar energy research",
            ExecutiveSummary = "Summary",
            Conclusion = conclusion,
            Sections =
            [
                new ResearchSection
                {
                    Heading = "Evidence",
                    Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Evidence gathered." }],
                },
            ],
        };
        var document = ResearchDocumentMapper.ToDocumentDraft(research, []);
        var input = new DocumentGenerationInput(
            Guid.NewGuid(), null, research.Title, "Produce a research report.", "report",
            "short", null, null, [], "en", "both", "professional", true);
        var renderer = new DocumentRenderer();
        var docx = renderer.RenderDocx(document, input, new DocumentGenerationOptions());
        var pdf = renderer.RenderPdf(document, input, new DocumentGenerationOptions());

        DocumentOutputIntegrityValidator.Validate(docx);
        DocumentOutputIntegrityValidator.Validate(pdf);
        using var package = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(docx.Content), false);
        var renderedText = package.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains(conclusion, renderedText, StringComparison.Ordinal);
        Assert.True(
            renderedText.IndexOf(conclusion, StringComparison.Ordinal) <
            renderedText.LastIndexOf("Sources", StringComparison.Ordinal),
            "The research conclusion must appear before the source appendix in the actual DOCX.");
    }
}
