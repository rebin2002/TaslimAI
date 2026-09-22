using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentGenerationUnitTests
{
    [Fact]
    public void Prompt_builder_includes_only_selected_sources_and_enforces_context_limit()
    {
        var input = new DocumentGenerationInput(Guid.NewGuid(), null, "Brief", "Summarize the source.", "report", "standard", "operators", "Be precise.", [Guid.NewGuid()], "en", "both", "professional", true);
        var builder = new DocumentPromptBuilder();
        var prompt = builder.Build(input, new DocumentProjectContext("Launch", "Be precise", "Audience: operators"), [new DocumentSourceContext("brief.txt", ".txt", "Only selected source")], new DocumentGenerationOptions { MaxContextCharacters = 2_000 });

        Assert.Contains("Only selected source", prompt.UserInstruction, StringComparison.Ordinal);
        Assert.Contains("Audience: operators", prompt.UserInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("personal memory", prompt.UserInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<DocumentContextLimitException>(() => builder.Build(input, null, [new DocumentSourceContext("large.txt", ".txt", new string('x', 2_001))], new DocumentGenerationOptions { MaxContextCharacters = 2_000 }));
    }

    [Fact]
    public void Renderer_produces_valid_docx_and_pdf_with_unicode_content()
    {
        var input = new DocumentGenerationInput(Guid.NewGuid(), null, "ڕاپۆرتی تاقیکردنەوە", "Create a clear report.", "report", "standard", null, null, [Guid.NewGuid()], "ku", "both", "professional", true);
        var draft = new DocumentDraft
        {
            Title = input.Title,
            Summary = "پوختەی بەڵگەنامە",
            Sections = [new DocumentSection { Heading = "سەرەکی", Blocks = [new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "ناوەڕۆکی تاقیکردنەوە" }] }],
        };
        var renderer = new DocumentRenderer();
        var options = new DocumentGenerationOptions();

        var docx = renderer.RenderDocx(draft, input, options);
        var pdf = renderer.RenderPdf(draft, input, options);

        Assert.Equal(AssetRepresentationTypes.Docx, docx.RepresentationType);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", docx.ContentType);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(docx.Content, 0, 2));
        Assert.Equal(AssetRepresentationTypes.Pdf, pdf.RepresentationType);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 5));
        Assert.True(pdf.Content.Length > 500);
    }

    [Fact]
    public void Structured_document_schema_is_strict_and_matches_canonical_blocks()
    {
        var schema = DocumentDraftStructuredOutput.Spec.Schema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "title", "summary", "sections" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        var block = schema.GetProperty("properties").GetProperty("sections").GetProperty("items").GetProperty("properties").GetProperty("blocks").GetProperty("items");
        Assert.Equal(new[] { "type", "text", "items", "rows" }, block.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
    }
}
