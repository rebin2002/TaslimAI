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
    public void Request_mapping_defaults_null_attachment_ids_and_validation_rejects_malformed_persisted_input()
    {
        var request = new DocumentGenerationRequest
        {
            WorkspaceId = Guid.NewGuid(),
            Description = "Create a useful brief.",
            AttachmentIds = null!,
        };

        var mapped = DocumentGenerationContractMapper.ToInput(request);
        Assert.Empty(mapped.AttachmentIds);

        var malformed = new DocumentGenerationInput(
            Guid.NewGuid(), null, null!, "Create a useful brief.", "report", "standard", null, null, null!, "en", "pdf", "professional", true);
        var exception = Assert.Throws<DocumentRequestValidationException>(() => DocumentGenerationRequestValidator.Validate(malformed, new DocumentGenerationOptions()));
        Assert.Equal(GenerationJobErrorCodes.DocumentRequestInvalid, exception.Code);

        var missingAttachments = new DocumentGenerationInput(
            Guid.NewGuid(), null, "Report", "Create a useful brief.", "report", "standard", null, null, null!, "en", "pdf", "professional", true);
        var attachmentsException = Assert.Throws<DocumentRequestValidationException>(() => DocumentGenerationRequestValidator.Validate(missingAttachments, new DocumentGenerationOptions()));
        Assert.Equal(GenerationJobErrorCodes.DocumentRequestInvalid, attachmentsException.Code);
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

    [Fact]
    public void Draft_validator_rejects_null_nested_values_and_unbounded_cells()
    {
        var options = new DocumentGenerationOptions { MaxBlockCharacters = 20 };
        var nullSection = new DocumentDraft
        {
            Title = "Report",
            Summary = string.Empty,
            Sections = [new DocumentSection { Heading = "Overview", Blocks = null! }],
        };
        var oversizedCell = new DocumentDraft
        {
            Title = "Report",
            Summary = string.Empty,
            Sections = [new DocumentSection
            {
                Heading = "Overview",
                Blocks = [new DocumentBlock
                {
                    Type = DocumentBlockTypes.Table,
                    Rows = [new DocumentTableRow { Cells = [new string('x', 21)] }],
                }],
            }],
        };

        Assert.Throws<DocumentOutputValidationException>(() => DocumentDraftValidator.Validate(nullSection, options));
        Assert.Throws<DocumentOutputValidationException>(() => DocumentDraftValidator.Validate(oversizedCell, options));
    }

    [Fact]
    public void Draft_validator_rejects_xml_unsafe_text_before_rendering()
    {
        var draft = new DocumentDraft
        {
            Title = "Report",
            Summary = "Summary",
            Sections = [new DocumentSection
            {
                Heading = "Overview",
                Blocks = [new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "Safe prefix\u0001unsafe suffix" }],
            }],
        };

        Assert.Throws<DocumentOutputValidationException>(() => DocumentDraftValidator.Validate(draft, new DocumentGenerationOptions()));
    }

    [Fact]
    public void Draft_validator_accepts_canonical_paragraph_list_and_table_blocks()
    {
        var draft = new DocumentDraft
        {
            Title = "Report",
            Summary = "Summary",
            Sections = [new DocumentSection
            {
                Heading = "Overview",
                Blocks =
                [
                    new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "Body" },
                    new DocumentBlock { Type = DocumentBlockTypes.BulletList, Items = ["One", "Two"] },
                    new DocumentBlock { Type = DocumentBlockTypes.Table, Rows = [new DocumentTableRow { Cells = ["A", "B"] }] },
                ],
            }],
        };

        DocumentDraftValidator.Validate(draft, new DocumentGenerationOptions());
    }
}
