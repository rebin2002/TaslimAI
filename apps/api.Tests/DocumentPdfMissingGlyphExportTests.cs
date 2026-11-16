using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Research;
using UglyToad.PdfPig;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// PDF output integrity and readability for Document Studio and Research exports.
///
/// QuestPDF 2026.9 defaults to aborting GeneratePdf() on the first glyph that none of the configured
/// fonts can draw. The text that reaches this renderer is user-authored titles and AI-written report
/// content, and both draft validators admit characters outside the shipped Latin/Arabic coverage on
/// purpose (emoji, arrows, CJK): the DOCX representation renders them with the reader's own fonts, and
/// no admission rule rejects them for the PDF. With the strict default, a perfectly valid export
/// request therefore died permanently at the last stage of the job - after the provider spend for a
/// Research report - with "The PDF document could not be rendered" while its DOCX sibling rendered
/// fine. The export must instead always produce a valid, parseable, readable PDF.
/// </summary>
public sealed class DocumentPdfMissingGlyphExportTests
{
    private static DocumentRenderer Renderer { get; } = new();
    private static DocumentGenerationOptions Options { get; } = new();

    private const string Rocket = "\U0001F680";
    private const string Check = "\u2705";
    private const string Arrow = "\u2192";

    private static DocumentGenerationInput Input(string title, string language = "en") => new(
        Guid.NewGuid(), null, title, "Create a concise professional short document.",
        "report", "short", null, null, Array.Empty<Guid>(), language, "pdf", "professional", true);

    private static DocumentDraft Draft(string title, string summary, string body) => new()
    {
        Title = title,
        Summary = summary,
        Sections =
        [
            new DocumentSection
            {
                Heading = $"Outlook {Arrow}",
                Blocks =
                [
                    new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = body },
                    new DocumentBlock { Type = DocumentBlockTypes.BulletList, Items = [$"Adopted {Check}", "Plain bullet"] },
                ],
            },
        ],
    };

    /// <summary>
    /// Opens the export with a real PDF reader and returns (extracted page text, /Title metadata).
    /// A file that only satisfies the byte-signature gate is not a readable export, so every case here
    /// is read back through PdfPig as well as through the pipeline's own integrity validator.
    /// </summary>
    private static (string Text, string? Title) AssertValidReadablePdf(RenderedDocument rendered)
    {
        DocumentOutputIntegrityValidator.Validate(rendered);
        using var pdf = PdfDocument.Open(new MemoryStream(rendered.Content));
        Assert.Equal(1, pdf.NumberOfPages);
        var text = string.Concat(pdf.GetPages().Select(page => page.Text));
        Assert.Contains("Taslim", text, StringComparison.Ordinal);
        Assert.Equal("application/pdf", rendered.ContentType);
        Assert.EndsWith(".pdf", rendered.FileName, StringComparison.OrdinalIgnoreCase);
        return (text, pdf.Information.Title);
    }

    [Fact]
    public void Document_pdf_export_survives_an_emoji_in_the_title()
    {
        var title = $"Quarterly review {Rocket}";
        var draft = Draft(title, "Leadership summary.", "Revenue grew across all segments.");

        // RED for the shipped strict default: DocumentDrawingException, whole export lost.
        RenderedDocument rendered;
        try
        {
            rendered = Renderer.RenderPdf(draft, Input(title), Options);
        }
        catch (Exception exception)
        {
            Assert.Fail($"The PDF export must render a title containing an emoji instead of failing the job ({exception.GetType().Name}: {exception.Message}).");
            return;
        }

        var (text, metadataTitle) = AssertValidReadablePdf(rendered);
        Assert.Contains("Quarterly review", text, StringComparison.Ordinal);
        // Readability: the rest of the document must still be drawn and extractable.
        Assert.Contains("Revenue grew across all segments.", text, StringComparison.Ordinal);
        Assert.Contains("Outlook", text, StringComparison.Ordinal);
        Assert.Equal(title, metadataTitle);
    }

    [Fact]
    public void Document_pdf_export_survives_out_of_coverage_glyphs_in_the_body()
    {
        var draft = Draft("Roadmap brief", $"Phase 1 {Check} complete", $"Scoping {Arrow} build {Arrow} ship.");
        var rendered = Renderer.RenderPdf(draft, Input("Roadmap brief"), Options);

        var (text, _) = AssertValidReadablePdf(rendered);
        Assert.Contains("Roadmap brief", text, StringComparison.Ordinal);
        Assert.Contains("Scoping", text, StringComparison.Ordinal);
        Assert.Contains("ship.", text, StringComparison.Ordinal);
        Assert.Contains("Adopted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_pdf_export_survives_a_non_latin_script_title()
    {
        // CJK has no coverage in either shipped font family; it must not sink the export either.
        var title = "\u4e2d\u6587\u5831\u544a quarterly";
        var rendered = Renderer.RenderPdf(Draft(title, "s", "body"), Input(title), Options);

        var (text, metadataTitle) = AssertValidReadablePdf(rendered);
        Assert.Contains("quarterly", text, StringComparison.Ordinal);
        Assert.Equal(title, metadataTitle);
    }

    [Fact]
    public void Research_report_export_survives_glyphs_outside_the_shipped_font_coverage()
    {
        // The Research job handler maps its draft through ResearchDocumentMapper and shares this exact
        // renderer, so the report that was already paid for must still come out as a valid PDF.
        var researchDraft = new ResearchDraft
        {
            Title = $"Market scan {Rocket}",
            ExecutiveSummary = $"Momentum is positive {Arrow} margins steady.",
            Language = "en",
            KeyFindings = [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = $"Adoption grew {Check}", CitationIds = ["S1"] }],
            Sections = [new ResearchSection { Heading = "Evidence", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Two of three vendors raised prices.", CitationIds = ["S1"] }] }],
            Conclusion = "A cautious expansion is supported.",
            Sources = ["S1"],
        };
        // The same gate the handler runs before rendering accepts this draft unchanged.
        ResearchDraftValidator.Validate(researchDraft, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S1" }, new ResearchGenerationOptions());

        var sources = new[]
        {
            new ResearchSourceCandidate("S1", "https://example.gov/source", "https://example.gov/source", "Official source", "example.gov", null, null, DateTime.UtcNow, "web", "Evidence", "Evidence", null, 1, true, null),
        };
        var documentDraft = ResearchDocumentMapper.ToDocumentDraft(researchDraft, sources);
        // The handler passes the report title as the export identity, same as the document lane.
        var rendered = Renderer.RenderPdf(documentDraft, Input(documentDraft.Title), Options);

        var (text, metadataTitle) = AssertValidReadablePdf(rendered);
        Assert.Contains("Market scan", text, StringComparison.Ordinal);
        Assert.Contains("A cautious expansion is supported.", text, StringComparison.Ordinal);
        Assert.Contains("Two of three vendors raised prices.", text, StringComparison.Ordinal);
        Assert.Contains("[S1]", text, StringComparison.Ordinal);
        Assert.Equal(researchDraft.Title, metadataTitle);
    }

    [Fact]
    public void Rtl_document_pdf_export_survives_an_emoji_in_the_title()
    {
        var title = $"\u062a\u0642\u0631\u064a\u0631 \u0631\u0628\u0639\u064a {Rocket}";
        var draft = Draft(title, "\u0645\u0644\u062e\u0635.", "\u0646\u0635 \u0627\u0644\u062a\u0642\u0631\u064a\u0631.");
        var rendered = Renderer.RenderPdf(draft, Input(title, "ar"), Options);

        // Glyph shaping/routing for RTL body text is owned by the RTL font lane; the contract pinned
        // here is only that an emoji can no longer destroy the whole RTL export.
        var (text, metadataTitle) = AssertValidReadablePdf(rendered);
        Assert.Equal(title, metadataTitle);
        Assert.Contains("Taslim", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_identity_still_separates_exports_whose_titles_differ_only_by_an_out_of_coverage_glyph()
    {
        // Content-addressed generated-file storage reuses an earlier file row for byte-identical
        // output, so relaxing the glyph check must not collapse distinct exports: the /Title entry
        // still carries the exact requested identity, surrogate pair included.
        var shared = Draft("Launch plan", "Same body.", "Same content.");
        var plainTitle = "Launch plan";
        var emojiTitle = $"Launch plan {Rocket}";
        var first = Renderer.RenderPdf(shared, Input(plainTitle), Options);
        var second = Renderer.RenderPdf(shared, Input(emojiTitle), Options);

        Assert.NotEqual(
            Convert.ToHexString(SHA256.HashData(first.Content)),
            Convert.ToHexString(SHA256.HashData(second.Content)));
        Assert.Equal(plainTitle, AssertValidReadablePdf(first).Title);
        Assert.Equal(emojiTitle, AssertValidReadablePdf(second).Title);
    }

    [Fact]
    public void Missing_font_family_remains_a_loud_deployment_failure()
    {
        // The relaxation covers un-drawable glyphs only. A configured font family that does not exist
        // is a deployment error and must keep failing loudly instead of silently painting every page
        // in a fallback font.
        var options = new DocumentGenerationOptions { DefaultFontFamily = "Totally Absent Family", RtlFontFamily = "Also Absent Family" };
        var exception = Assert.ThrowsAny<Exception>(() => Renderer.RenderPdf(Draft("Font gate", "s", "body"), Input("Font gate"), options));
        Assert.Contains("font families", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not available in the configured fonts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
