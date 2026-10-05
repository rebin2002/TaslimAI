using System.IO.Compression;
using System.Text;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Xunit;
namespace Taslim.Api.Tests;
public sealed class DocumentOutputIntegrityTests
{
    [Fact]
    public void Renderer_outputs_pass_docx_and_pdf_integrity_validation()
    {
        var input = new DocumentGenerationInput(Guid.NewGuid(), null, "Launch brief", "Create a clear brief.", "report", "standard", null, null, [], "en", "both", "professional", true);
        var draft = new DocumentDraft
        {
            Title = "Launch brief",
            Summary = "A short summary.",
            Sections = [new DocumentSection { Heading = "Overview", Blocks = [new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "The launch is ready." }] }],
        };
        var renderer = new DocumentRenderer();
        var docx = renderer.RenderDocx(draft, input, new DocumentGenerationOptions());
        var pdf = renderer.RenderPdf(draft, input, new DocumentGenerationOptions());
        DocumentOutputIntegrityValidator.Validate(docx);
        DocumentOutputIntegrityValidator.Validate(pdf);
    }
    [Fact]
    public void Validator_rejects_a_pdf_that_only_has_header_and_eof_marker()
    {
        var rendered = new RenderedDocument(AssetRepresentationTypes.Pdf, "brief.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF"));
        var exception = Assert.Throws<DocumentOutputIntegrityException>(() => DocumentOutputIntegrityValidator.Validate(rendered));
        Assert.Contains("could not be read safely", exception.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Validator_rejects_a_docx_with_malformed_main_document_xml()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
            AddEntry(archive, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
            AddEntry(archive, "word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
        }
        var rendered = new RenderedDocument(AssetRepresentationTypes.Docx, "brief.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", output.ToArray());
        var exception = Assert.Throws<DocumentOutputIntegrityException>(() => DocumentOutputIntegrityValidator.Validate(rendered));
        Assert.Contains("could not be read safely", exception.Message, StringComparison.Ordinal);
    }
    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(content);
    }
}
