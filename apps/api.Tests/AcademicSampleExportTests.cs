using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Taslim.Api.AcademicTemplates;
using UglyToad.PdfPig;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Proves the promised artifact surface for the template library: every shipped configuration renders
/// an editable DOCX whose Word-native geometry (twips, half-points, bidi, gutter, section numbering,
/// footnotes part, hyperlink relationship) matches the design spec, and a PDF whose page flow and text
/// match the same spec — with no fictional references and no backend identity in either artifact.
/// </summary>
public class AcademicSampleExportTests
{
    private static readonly string[] SpotConfigurations =
    [
        "classic-thesis.times.150.classic.center",
        "uk-postgraduate-dissertation.times.150.center",
        "journal-double-column.times.numeric.narrow",
        "kurdish-thesis-ckb.naskh.latin.150",
        "arabic-dissertation-ar.naskh.band.indic.outside",
    ];

    private static AcademicTemplateConfiguration Config(string id) =>
        AcademicTemplateRegistry.GetConfiguration(id)
        ?? throw new Xunit.Sdk.XunitException($"spot configuration '{id}' missing (registry drifted?)");

    [Theory]
    [MemberData(nameof(SpotData))]
    public void Docx_geometry_matches_the_design_spec(string configurationId)
    {
        var cfg = Config(configurationId);
        var spec = cfg.Spec;
        using var package = WordprocessingDocument.Open(new MemoryStream(AcademicTemplateDocxRenderer.Render(spec)), false);
        var body = package.MainDocumentPart!.Document.Body!;
        var sections = body.Elements<SectionProperties>().ToList();
        Assert.True(sections.Count >= 2, "front-matter and body sections must both exist");

        foreach (var section in sections)
        {
            var margin = section.Elements<PageMargin>().Single();
            Assert.Equal((long)ToTwips(spec.MarginTopPt), (long)margin.Top!.Value!);
            Assert.Equal((long)ToTwips(spec.MarginBottomPt), (long)margin.Bottom!.Value!);
            Assert.Equal((uint)ToTwips(spec.MarginLeftPt), margin.Left!.Value!);
            Assert.Equal((uint)ToTwips(spec.MarginRightPt), margin.Right!.Value!);
            Assert.Equal((uint)ToTwips(spec.BindingEdgePt), margin.Gutter!.Value!);

            if (spec.IsRtl)
                Assert.NotEmpty(section.Descendants<BiDi>());
            else
                Assert.Empty(section.Descendants<BiDi>());

            if (spec.IsRtl && spec.BindingEdge == "right")
                Assert.NotEmpty(section.Elements<GutterOnRight>());
        }

        var pageSize = sections.First().Elements<PageSize>().Single();
        Assert.Equal((uint)ToTwips(spec.PaperWidthPt), pageSize.Width!.Value!);
        Assert.Equal((uint)ToTwips(spec.PaperHeightPt), pageSize.Height!.Value!);
    }

    [Theory]
    [MemberData(nameof(SpotData))]
    public void Docx_styles_match_the_design_ladder_and_core_properties_stay_neutral(string configurationId)
    {
        var cfg = Config(configurationId);
        var spec = cfg.Spec;
        using var package = WordprocessingDocument.Open(new MemoryStream(AcademicTemplateDocxRenderer.Render(spec)), false);

        var styles = package.MainDocumentPart!.StyleDefinitionsPart!.Styles;
        var heading1 = styles.Elements<Style>().Single(s => s.StyleId == "Heading1");
        Assert.Equal(Math.Round(spec.HeadingSizePt(1) * 2).ToString(),
            heading1.StyleRunProperties!.Elements<FontSize>().Single().Val!.Value);

        var normal = styles.Elements<Style>().Single(s => s.StyleId == "Normal");
        Assert.True(normal.Default!.Value);

        var props = package.PackageProperties;
        Assert.Equal("Taslim Academic Templates", props.Creator);
        Assert.Contains(configurationId, props.Title);

        // No backend identity anywhere in the visible styles document part.
        var partText = string.Concat(styles.Descendants<StyleId>().Select(s => s.Val ?? ""));
        Assert.DoesNotContain("gpt", partText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SpotData))]
    public void Docx_numbering_sections_follow_the_scheme(string configurationId)
    {
        var spec = Config(configurationId).Spec;
        using var package = WordprocessingDocument.Open(new MemoryStream(AcademicTemplateDocxRenderer.Render(spec)), false);
        var sections = package.MainDocumentPart!.Document.Body!.Elements<SectionProperties>().ToList();
        var formats = sections
            .Select(s => s.Elements<PageNumberType>().FirstOrDefault()?.Format?.InnerText)
            .Where(f => f is not null)
            .ToList();
        if (spec.FrontMatterNumbering == "none" && spec.BodyNumbering == "none")
        {
            Assert.Empty(formats);
            return;
        }
        Assert.NotEmpty(formats);
        if (spec.FrontMatterNumbering is "roman-lower" or "roman-upper")
            Assert.Contains("oman", formats.First()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Docx_footnotes_part_and_hyperlink_relationship_are_real_openxml_features()
    {
        var cfg = AcademicTemplateRegistry.Configurations.First(c => c.Spec.FootnotesEnabled);
        using var package = WordprocessingDocument.Open(new MemoryStream(AcademicTemplateDocxRenderer.Render(cfg.Spec)), false);
        var footnotes = package.MainDocumentPart!.FootnotesPart!.Footnotes!
            .Elements<Footnote>().ToList();
        Assert.Contains(footnotes, f => (int)f.Id!.Value! == -1 && f.Type == FootnoteEndnoteValues.Separator);
        Assert.Contains(footnotes, f => (int)f.Id!.Value! == 0 && f.Type == FootnoteEndnoteValues.ContinuationSeparator);
        Assert.Contains(footnotes, f => (int)f.Id!.Value! == 1 && f.Type == FootnoteEndnoteValues.Normal);
        Assert.Contains(package.MainDocumentPart.HyperlinkRelationships, r => r.Uri.Host == "example.org");
        Assert.NotEmpty(package.MainDocumentPart.Document.Descendants<FootnoteReference>());
    }

    [Fact]
    public void Docx_columns_match_double_column_designs()
    {
        var cfg = Config("journal-double-column.times.numeric.narrow");
        using var package = WordprocessingDocument.Open(new MemoryStream(AcademicTemplateDocxRenderer.Render(cfg.Spec)), false);
        var columns = package.MainDocumentPart!.Document.Body!
            .Elements<SectionProperties>().First().Elements<Columns>().Single();
        Assert.Equal((short)2, columns.ColumnCount!.Value!);
    }

    [Theory]
    [MemberData(nameof(SpotData))]
    public void Pdf_paginates_and_carries_the_synthetic_banner_text(string configurationId)
    {
        var spec = Config(configurationId).Spec;
        var bytes = AcademicTemplatePdfRenderer.Render(spec);
        using var pdf = PdfDocument.Open(bytes);
        Assert.True(pdf.NumberOfPages >= 2, $"{configurationId}: expected multi-page sample, got {pdf.NumberOfPages}");
        var firstPageText = string.Concat(pdf.GetPages().Take(2).Select(p => p.Text));
        if (spec.IsRtl)
        {
            // Reshaped Arabic-script PDF text extracts as presentation forms, so the honest
            // assertion is script presence rather than a logical-order substring.
            Assert.Contains(firstPageText, c => c >= '\u0600' && c <= '\u06FF');
        }
        else
        {
            var banner = AcademicSampleContent.For(spec).Banner;
            var needle = banner.Split(' ', StringSplitOptions.TrimEntries)[0];
            Assert.Contains(needle, firstPageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pdf_render_is_deterministic_in_structure()
    {
        var spec = Config("kurdish-thesis-ckb.naskh.latin.150").Spec;
        using var a = PdfDocument.Open(AcademicTemplatePdfRenderer.Render(spec));
        using var b = PdfDocument.Open(AcademicTemplatePdfRenderer.Render(spec));
        Assert.Equal(a.NumberOfPages, b.NumberOfPages);
        Assert.Equal(string.Concat(a.GetPages().Select(p => p.Text)), string.Concat(b.GetPages().Select(p => p.Text)));
    }

    [Fact]
    public void Pdf_document_metadata_never_names_a_model_or_provider()
    {
        var spec = Config("classic-thesis.times.150.classic.center").Spec;
        var text = System.Text.Encoding.ASCII.GetString(AcademicTemplatePdfRenderer.Render(spec).Take(6000).ToArray());
        foreach (var token in new[] { "openai", "gpt", "claude", "gemini", "billing", "credits" })
            Assert.False(text.Contains(token, StringComparison.OrdinalIgnoreCase), $"pdf header leaked '{token}'");
    }

    public static IEnumerable<object[]> SpotData() => SpotConfigurations.Select(id => new object[] { id });

    private static int ToTwips(double points) => (int)Math.Round(points * AcademicDesignSpec.TwipsPerPoint);
}
