using System.Security.Cryptography;
using System.Text;
using Taslim.Api.Contracts;
using Taslim.Api.Documents;
using Taslim.Api.Domain;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// A generated PDF has to carry the identity of the document it exports. Without it two different documents
/// from one workspace can render byte-identical PDFs, and content-addressed generated-file storage then reuses
/// the earlier file row for the later job, so the later asset silently loses its PDF representation - and with
/// it the download button the reloaded preview is supposed to offer (pinned by
/// <see cref="DocumentPreviewDownloadIdentityRebindTests"/>).
/// </summary>
public sealed class DocumentExportIdentityTests
{
    private static DocumentRenderer Renderer { get; } = new();
    private static DocumentGenerationOptions Options { get; } = new();

    private static DocumentDraft Draft() => new()
    {
        Title = "Generated launch brief",
        Summary = "A concise brief generated from the selected source.",
        Sections =
        [
            new DocumentSection
            {
                Heading = "Key points",
                Blocks = [new DocumentBlock { Type = DocumentBlockTypes.BulletList, Items = ["Clarity is the launch goal.", "Taslim supports a calm workspace."] }],
            },
        ],
    };

    private static DocumentGenerationInput Input(string title) => new(
        Guid.NewGuid(), null, title, "Create a concise professional short document.",
        "report", "short", null, null, Array.Empty<Guid>(), "en", "both", "professional", false);

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    [Fact]
    public void Two_documents_rendered_back_to_back_never_share_one_content_hash()
    {
        // Rendered in the same instant on purpose: a second-granular internal timestamp used to make these two
        // exports byte-identical, which is exactly what let workspace-level content dedupe swallow a format.
        var first = Renderer.RenderPdf(Draft(), Input("Alpha launch brief"), Options);
        var second = Renderer.RenderPdf(Draft(), Input("Beta roadmap brief"), Options);

        Assert.NotEqual(Hash(first.Content), Hash(second.Content));
    }

    [Fact]
    public void Each_exported_pdf_carries_its_own_requested_document_title()
    {
        foreach (var title in new[] { "Alpha launch brief", "Beta roadmap brief", "کورتەی هەفتانە" })
        {
            var rendered = Renderer.RenderPdf(Draft(), Input(title), Options);
            var text = Encoding.Latin1.GetString(rendered.Content);
            Assert.True(text.Contains(title, StringComparison.Ordinal) || text.Contains(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(title)), StringComparison.OrdinalIgnoreCase),
                $"The exported PDF must embed its own document title '{title}'.");
            Assert.EndsWith(".pdf", rendered.FileName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Docx_and_pdf_exports_keep_the_same_title_derived_download_name()
    {
        var input = Input("Quarterly brief");
        var docx = Renderer.RenderDocx(Draft(), input, Options);
        var pdf = Renderer.RenderPdf(Draft(), input, Options);

        Assert.Equal(Path.GetFileNameWithoutExtension(docx.FileName), Path.GetFileNameWithoutExtension(pdf.FileName));
        Assert.EndsWith(".docx", docx.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/pdf", pdf.ContentType);
    }
}
