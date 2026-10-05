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
        var input = new DocumentGenerationInput(Guid.NewGuid(), null, "Launch brief", "Create a brief.", "report", "short", null, null, [], "en", "both", "professional", true);
        var draft = new DocumentDraft
        {
            Title = "Launch brief",
            Summary = "A short brief.",
            Sections = [new DocumentSection
            {
                Heading = "Overview",
                Blocks = [new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "The launch is ready for review." }],
            }],
        };
        var renderer = new DocumentRenderer();
        var docx = renderer.RenderDocx(draft, input, new DocumentGenerationOptions());
        var pdf = renderer.RenderPdf(draft, input, new DocumentGenerationOptions());
        DocumentOutputIntegrityValidator.Validate(docx);
        DocumentOutputIntegrityValidator.Validate(pdf);
    }
    [Fact]
    public void Validator_rejects_non_docx_archive_and_missing_main_document()
    {
        var invalid = new RenderedDocument(
            AssetRepresentationTypes.Docx,
            "brief.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Encoding.UTF8.GetBytes("not a zip archive"));
        var invalidException = Assert.Throws<DocumentOutputIntegrityException>(() => DocumentOutputIntegrityValidator.Validate(invalid));
        Assert.Contains("could not be read safely", invalidException.Message, StringComparison.Ordinal);
        var missing = RewriteArchive(RenderValidDocx(), "word/document.xml");
        var missingException = Assert.Throws<DocumentOutputIntegrityException>(() => DocumentOutputIntegrityValidator.Validate(missing));
        Assert.Contains("missing required part", missingException.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Validator_rejects_truncated_or_page_less_pdf()
    {
        var valid = RenderValidPdf();
        var truncated = valid with { Content = RemoveAscii(valid.Content, "%%EOF") };
        var truncatedException = Assert.Throws<DocumentOutputIntegrityException>(() => DocumentOutputIntegrityValidator.Validate(truncated));
        Assert.Contains("end-of-file marker", truncatedException.Message, StringComparison.Ordinal);
        var pageLess = valid with { Content = ReplaceAscii(valid.Content, "/Type /Page", "/Type /NoPg") };
        var pageLessException = Assert.Throws<DocumentOutputIntegrityException>(() => DocumentOutputIntegrityValidator.Validate(pageLess));
        Assert.Contains("page object", pageLessException.Message, StringComparison.Ordinal);
    }
    private static RenderedDocument RenderValidDocx()
    {
        var input = new DocumentGenerationInput(Guid.NewGuid(), null, "Launch brief", "Create a brief.", "report", "short", null, null, [], "en", "docx", "professional", true);
        var draft = new DocumentDraft
        {
            Title = "Launch brief",
            Sections = [new DocumentSection { Heading = "Overview", Blocks = [new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "Ready." }] }],
        };
        return new DocumentRenderer().RenderDocx(draft, input, new DocumentGenerationOptions());
    }
    private static RenderedDocument RenderValidPdf()
    {
        var input = new DocumentGenerationInput(Guid.NewGuid(), null, "Launch brief", "Create a brief.", "report", "short", null, null, [], "en", "pdf", "professional", true);
        var draft = new DocumentDraft
        {
            Title = "Launch brief",
            Sections = [new DocumentSection { Heading = "Overview", Blocks = [new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = "Ready." }] }],
        };
        return new DocumentRenderer().RenderPdf(draft, input, new DocumentGenerationOptions());
    }
    private static RenderedDocument RewriteArchive(RenderedDocument rendered, string partToOmit)
    {
        using var sourceStream = new MemoryStream(rendered.Content.ToArray());
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        using var outputStream = new MemoryStream();
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (entry.FullName == partToOmit) continue;
                var destination = output.CreateEntry(entry.FullName);
                using var destinationStream = destination.Open();
                using var sourceEntryStream = entry.Open();
                sourceEntryStream.CopyTo(destinationStream);
            }
        }
        return rendered with { Content = outputStream.ToArray() };
    }
    private static byte[] ReplaceAscii(ReadOnlyMemory<byte> content, string oldValue, string newValue)
    {
        var bytes = content.ToArray();
        var oldBytes = Encoding.ASCII.GetBytes(oldValue);
        var newBytes = Encoding.ASCII.GetBytes(newValue);
        var start = 0;
        var replaced = false;
        while (start < bytes.Length)
        {
            var relativeIndex = bytes.AsSpan(start).IndexOf(oldBytes);
            if (relativeIndex < 0) break;
            var index = start + relativeIndex;
            newBytes.AsSpan().CopyTo(bytes.AsSpan(index, newBytes.Length));
            start = index + newBytes.Length;
            replaced = true;
        }
        Assert.True(replaced);
        return bytes;
    }
    private static byte[] RemoveAscii(ReadOnlyMemory<byte> content, string value)
    {
        var bytes = content.ToArray();
        var index = bytes.AsSpan().LastIndexOf(Encoding.ASCII.GetBytes(value));
        Assert.True(index >= 0);
        return bytes[..index];
    }
}
