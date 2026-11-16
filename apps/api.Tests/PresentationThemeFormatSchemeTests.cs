using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic mock-PPTX coverage for the theme format scheme that the managed renderer ships inside every
/// exported deck. Unlike the relationship-graph checks in <see cref="PresentationOutputIntegrityTests"/>, these
/// tests look inside ppt/theme/theme1.xml, where ECMA-376 Part 1 pins CT_FillStyleList, CT_LineStyleList,
/// CT_EffectStyleList and CT_BackgroundFillStyleList to exactly three entries each. Self-closed empty lists
/// passed every part, content-type and relationship check in the export gate while PowerPoint still reported
/// the downloaded deck as damaged content, so both the renderer output and the gate are asserted here.
/// </summary>
public sealed class PresentationThemeFormatSchemeTests
{
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static readonly string[] FormatSchemeListNames = ["fillStyleLst", "lnStyleLst", "effectStyleLst", "bgFillStyleLst"];

    [Fact]
    public void Renderer_emits_three_entries_in_every_theme_format_style_list()
    {
        var scheme = FormatScheme(RenderValidPresentation());

        foreach (var listLocalName in FormatSchemeListNames)
            Assert.Equal(3, scheme.Element(XName.Get(listLocalName, DrawingNamespace))!.Elements().Count());
    }

    [Fact]
    public void Renderer_theme_effect_styles_each_declare_an_effect_list()
    {
        var scheme = FormatScheme(RenderValidPresentation());

        foreach (var effectStyle in scheme.Element(XName.Get("effectStyleLst", DrawingNamespace))!.Elements())
            Assert.NotNull(effectStyle.Element(XName.Get("effectLst", DrawingNamespace)));
    }

    [Fact]
    public void Output_integrity_validator_accepts_rendered_presentation_theme()
    {
        // The gate must keep accepting the renderer's own output, otherwise the schema fix would reject decks.
        PresentationOutputIntegrityValidator.Validate(RenderValidPresentation());
    }

    [Theory]
    [InlineData("fillStyleLst")]
    [InlineData("lnStyleLst")]
    [InlineData("effectStyleLst")]
    [InlineData("bgFillStyleLst")]
    public void Output_integrity_validator_rejects_empty_theme_format_style_list(string listLocalName)
    {
        // Reproduces the shipped defect: the renderer previously emitted every list self-closed.
        var corrupted = RewriteTheme(RenderValidPresentation(), theme =>
            Regex.Replace(theme, $"<a:{listLocalName}>[\\s\\S]*?</a:{listLocalName}>", $"<a:{listLocalName}/>"));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(
            () => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains(listLocalName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("exactly 3 entries", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_theme_format_style_list_with_too_few_entries()
    {
        var corrupted = RewriteTheme(RenderValidPresentation(), theme => theme.Replace(
            "<a:ln w=\"19050\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>",
            string.Empty, StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(
            () => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("lnStyleLst", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_theme_format_style_list_with_extra_entry()
    {
        var corrupted = RewriteTheme(RenderValidPresentation(), theme => theme.Replace(
            "</a:bgFillStyleLst>",
            "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>", StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(
            () => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("bgFillStyleLst", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_unsupported_entry_in_fill_style_list()
    {
        var corrupted = RewriteTheme(RenderValidPresentation(), theme => theme.Replace(
            "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>",
            "<a:fillStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>", StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(
            () => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("unsupported entry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_effect_style_without_effect_list()
    {
        var corrupted = RewriteTheme(RenderValidPresentation(), theme => theme.Replace(
            "<a:effectStyle><a:effectLst/></a:effectStyle>", "<a:effectStyle/>", StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(
            () => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("without an effect list", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_theme_without_format_scheme()
    {
        var corrupted = RewriteTheme(RenderValidPresentation(), theme =>
            Regex.Replace(theme, "<a:fmtScheme[\\s\\S]*?</a:fmtScheme>", string.Empty));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(
            () => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("format scheme", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_foreign_element_inside_theme_format_style_list()
    {
        // Wrapping only the first line entry keeps the list at three children, so the rejection can only come
        // from the entry guard: a foreign-namespace element is not a DrawingML line style.
        var firstLineEntry = "<a:ln w=\"6350\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>";
        var corrupted = RewriteTheme(RenderValidPresentation(), theme => theme.Replace(
            firstLineEntry,
            "<x:foreignEntry xmlns:x=\"urn:taslim:presentation-tests\">" + firstLineEntry + "</x:foreignEntry>",
            StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(
            () => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("lnStyleLst", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported entry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendered_presentation_keeps_export_filename_and_content_type_metadata()
    {
        // The schema fix must not disturb the export metadata the asset layer and download name rely on.
        var rendered = RenderValidPresentation();

        Assert.Equal(AssetRepresentationTypes.Pptx, rendered.RepresentationType);
        Assert.Equal("Launch Deck.pptx", rendered.FileName);
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", rendered.ContentType);
        Assert.True(rendered.Content.Length > 0);
    }

    private static RenderedPresentation RenderValidPresentation()
    {
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "Launch Deck", "Plan", "general", "short", "professional", "en", null, null, null, true, true, []);
        var draft = new PresentationDraft
        {
            Title = "Launch Deck",
            Subtitle = "Plan",
            Language = "en",
            PresentationType = "general",
            Slides =
            [
                new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Title, Title = "Launch" },
                new PresentationSlide { Order = 2, Type = PresentationSlideTypes.Content, Title = "Plan", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = "Ready" }] },
            ],
        };
        return new PresentationRenderer().Render(draft, input, new PresentationGenerationOptions());
    }

    private static XElement FormatScheme(RenderedPresentation rendered)
    {
        var theme = XDocument.Parse(ReadTheme(rendered));
        var elements = theme.Root!.Element(XName.Get("themeElements", DrawingNamespace))!;
        return elements.Element(XName.Get("fmtScheme", DrawingNamespace))!;
    }

    private static string ReadTheme(RenderedPresentation rendered)
    {
        using var stream = new MemoryStream(rendered.Content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("ppt/theme/theme1.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static RenderedPresentation RewriteTheme(RenderedPresentation rendered, Func<string, string> rewrite)
    {
        var entries = new List<(string Name, byte[] Data)>();
        using (var source = new MemoryStream(rendered.Content, writable: false))
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read))
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var content = reader.ReadToEnd();
                if (entry.FullName == "ppt/theme/theme1.xml")
                    content = rewrite(content);
                entries.Add((entry.FullName, Encoding.UTF8.GetBytes(content)));
            }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = entry.Open();
                writer.Write(data);
            }

        return rendered with { Content = output.ToArray() };
    }
}
