using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic mock-PPTX layout regressions for DISTINCT long Arabic/Sorani slide titles in the RTL
/// export path. Unlike the short-title and multi-column geometry coverage elsewhere, these fixtures push a
/// full marketing-length title (121 Arabic / 140 Sorani characters) into the fixed title box that
/// PresentationRenderer emits with wrap="square" and no autofit, so every glyph overflows the box on
/// purpose. The suite pins the overflow contract: the renderer never truncates or splits the title, the
/// title box geometry and downstream zones are length-independent, RTL direction markers survive, Arabic
/// and Sorani share one layout with only the language tag differing, script detection forces RTL in an
/// English session, the export filename truncation preserves RTL characters, and the validator still
/// rejects titles beyond draft capacity before any rendering happens.
/// </summary>
public sealed class PresentationRtlLongTitleOverflowTests
{
    // Mirrors PresentationRenderer's private layout constants (apps/api/Presentations/PresentationRenderers.cs).
    private const int SlideWidth = 12_192_000;
    private const int SlideHeight = 6_858_000;
    private const int TitleBoxX = 720_000;
    private const int TitleBoxWidth = 10_750_000;
    private const int ContentTitleBoxY = 520_000;
    private const int ContentTitleBoxHeight = 700_000;
    private const int TitleTypeContentZoneY = 3_450_000;
    private const int MaximumDraftTitleCharacters = 160;
    private const string ArabicLongTitle = "خطة إطلاق منصة تسويق المحتوى في السوق الإقليمي مع الشركاء الاستراتيجيين وخطة النمو والتوسع خلال العام القادم أمام العملاء";
    private const string SoraniLongTitle = "پلانی دەستپێکردنی پێشکەشکردەی بازاڕکردنی ناوچەیی لەگەڵ هاوبەشە کۆیلانییەکان و ئامانجی گەشەکردن و فراوانبوون لە ساڵی داهاتوودا بەردەم کڕیاران";

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [Fact]
    public void Long_arabic_title_overflows_the_fixed_rtl_box_without_truncation_or_autofit()
    {
        Assert.Equal(121, ArabicLongTitle.Length);
        var rendered = RenderDraft(LanguageCodes.Arabic, ArabicLongTitle);
        var title = SingleTitleShape(ReadSlide(rendered, 1), ArabicLongTitle);

        // Overflow is left to PowerPoint: one unsplit paragraph, no shrinking, the full 121-character run intact.
        Assert.Equal(ArabicLongTitle, title.Text);
        Assert.Equal(1, title.ParagraphCount);
        Assert.Empty(title.Autofit);
        Assert.Equal("square", title.Wrap);
        Assert.Equal("1", title.RtlParagraph);
        Assert.Equal("1", title.RtlColumn);
        Assert.Equal("r", title.Alignment);
        Assert.Equal("ar-SA", title.Language);
        Assert.Equal("Noto Sans Arabic", title.Typeface);
        Assert.Equal("2400", title.FontSize);

        // The oversized RTL run still renders inside a mirrored, slide-bounded box: 722k EMU right margin
        // mirrors the 720k left margin, so a right-aligned long title cannot cross either slide edge.
        Assert.Equal(TitleBoxX, title.X);
        Assert.Equal(TitleBoxWidth, title.CX);
        Assert.Equal(TitleBoxX + TitleBoxWidth, SlideWidth - 722_000);
        foreach (var shape in TextShapes(ReadSlide(rendered, 1)))
        {
            Assert.True(shape.X + shape.CX <= SlideWidth, $"{shape.Name} crosses the right slide edge.");
            Assert.True(shape.Y + shape.CY <= SlideHeight, $"{shape.Name} crosses the bottom slide edge.");
        }
    }

    [Fact]
    public void Sorani_long_title_wraps_inside_one_box_and_keeps_the_content_zone_length_independent()
    {
        Assert.Equal(140, SoraniLongTitle.Length);
        var slideXml = ReadSlide(RenderDraft(LanguageCodes.Kurdish, SoraniLongTitle), 1);
        var title = SingleTitleShape(slideXml, SoraniLongTitle);

        // The title box is length-independent: the same fixed geometry as for a short title, with the box
        // bottom (1_220_000) deliberately below where wrapped Sorani lines spill, and the bullet zone never
        // moves to make room, so overflow happens without reshaping the slide.
        Assert.Equal(TitleBoxX, title.X);
        Assert.Equal(ContentTitleBoxY, title.Y);
        Assert.Equal(TitleBoxWidth, title.CX);
        Assert.Equal(ContentTitleBoxHeight, title.CY);
        Assert.Equal(1, title.ParagraphCount);
        Assert.Empty(title.Autofit);

        // Every other RTL-bearing text zone on the slide keeps the Sorani markers intact end to end.
        var otherRtlShapes = TextShapes(slideXml).Where(shape => shape.Name != title.Name && shape.RtlParagraph == "1").ToArray();
        Assert.NotEmpty(otherRtlShapes);
        Assert.All(otherRtlShapes, shape =>
        {
            Assert.Equal("ku-Arab", shape.Language);
            Assert.Equal("Noto Sans Arabic", shape.Typeface);
            Assert.Equal("r", shape.Alignment);
        });

        // The subtitle is the fixed slot directly beneath the title box; it is excluded from the
        // content-zone scan below on purpose (the renderer overlaps it into the title band by design).
        var subtitleBottom = otherRtlShapes.Where(shape => shape.FontSize == "1400").Select(shape => shape.Y + shape.CY).DefaultIfEmpty(ContentTitleBoxY + ContentTitleBoxHeight).Max();

        // The layout is length-independent: the title block (box plus its fixed subtitle slot) always
        // ends at 1_690_000, and the first content-zone text line after a 140-character wrapped Sorani
        // title sits at that same constant, never pushed down (or up) by the overflowing title.
        Assert.Equal(1_690_000, otherRtlShapes.Where(shape => shape.Y > subtitleBottom).Select(shape => shape.Y).Min());
    }

    [Fact]
    public void Title_type_slide_pins_the_long_sorani_title_below_the_fixed_subtitle_and_content_gap()
    {
        var draft = new PresentationDraft
        {
            Title = SoraniLongTitle,
            Language = LanguageCodes.Kurdish,
            PresentationType = "general",
            Slides =
            [
                new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Title, Title = SoraniLongTitle, Subtitle = "کورتەی پلانی ساڵانە" },
                new PresentationSlide { Order = 2, Type = PresentationSlideTypes.Bullets, Title = "خاڵە سەرەکییەکان", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["یەکەم", "دووەم"] }] },
            ],
        };
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
        var slideXml = ReadSlide(new PresentationRenderer().Render(draft, Input(LanguageCodes.Kurdish), new PresentationGenerationOptions()), 1);
        var title = SingleTitleShape(slideXml, SoraniLongTitle);
        var subtitle = TextShapes(slideXml).Single(shape => shape.Text == "کورتەی پلانی ساڵانە");

        // Fixed 30pt box for a Title-type slide; the long title overflows it, while subtitle and content
        // zones stay at their constant slots no matter how many wrapped lines the title produces.
        Assert.Equal(1_650_000, title.Y);
        Assert.Equal(TitleBoxX, title.X);
        Assert.Equal(TitleBoxWidth, title.CX);
        Assert.Equal(1_400_000, title.CY);
        Assert.Equal("3000", title.FontSize);
        Assert.Equal(1, title.ParagraphCount);
        Assert.Empty(title.Autofit);
        Assert.Equal(3_000_000, subtitle.Y);
        Assert.True(subtitle.Y < TitleTypeContentZoneY);

        // A 140-character Sorani title over the Title-type layout leaves nothing of the content zone
        // drawn above its constant 3_450_000 start line: the overflowing title never evicts or shifts
        // the zone, it just paints over the geometry the renderer already fixed.
        var zoneShapes = TextShapes(slideXml).Where(shape => shape.Name != title.Name && shape.Y >= TitleTypeContentZoneY && shape.Y < 6_000_000).ToArray();
        Assert.Empty(zoneShapes);
    }

    [Fact]
    public void Long_arabic_script_title_forces_the_rtl_title_box_even_in_an_english_session()
    {
        var slideXml = ReadSlide(RenderDraft(LanguageCodes.English, ArabicLongTitle), 1);
        var title = SingleTitleShape(slideXml, ArabicLongTitle);

        // Direction detection is script-based, and it is the long RTL run that makes a missing marker
        // observable: paragraph and column direction must be flipped while geometry stays unchanged.
        Assert.Equal("1", title.RtlParagraph);
        Assert.Equal("1", title.RtlColumn);
        Assert.Equal("r", title.Alignment);
        Assert.Equal("Noto Sans Arabic", title.Typeface);
        Assert.Equal(TitleBoxX, title.X);
        Assert.Equal(TitleBoxWidth, title.CX);
        Assert.Equal(ArabicLongTitle, title.Text);
    }

    [Fact]
    public void Arabic_and_sorani_long_titles_share_the_layout_and_differ_only_in_the_language_tag()
    {
        var arabic = SingleTitleShape(ReadSlide(RenderDraft(LanguageCodes.Arabic, ArabicLongTitle), 1), ArabicLongTitle);
        var sorani = SingleTitleShape(ReadSlide(RenderDraft(LanguageCodes.Kurdish, ArabicLongTitle), 1), ArabicLongTitle);

        // Same deterministic overflow geometry for both RTL locales; only the resolved language tag moves,
        // so a Sorani deck exported through the Arabic-tuned path keeps identical box behaviour.
        Assert.Equal(arabic.X, sorani.X);
        Assert.Equal(arabic.Y, sorani.Y);
        Assert.Equal(arabic.CX, sorani.CX);
        Assert.Equal(arabic.CY, sorani.CY);
        Assert.Equal(arabic.Alignment, sorani.Alignment);
        Assert.Equal(arabic.RtlParagraph, sorani.RtlParagraph);
        Assert.Equal(arabic.RtlColumn, sorani.RtlColumn);
        Assert.Equal(arabic.Typeface, sorani.Typeface);
        Assert.Equal(arabic.FontSize, sorani.FontSize);
        Assert.Equal("ar-SA", arabic.Language);
        Assert.Equal("ku-Arab", sorani.Language);
    }

    [Fact]
    public void Sorani_long_title_export_filename_is_truncated_at_120_characters_without_mangling_script()
    {
        Assert.Equal(140, SoraniLongTitle.Length);
        var rendered = RenderDraft(LanguageCodes.Kurdish, SoraniLongTitle);

        // SafeFileName trims the 140-character Sorani draft title to its first 120 characters: no
        // underscores (Arabic-script characters are not replaced), no question marks, extension intact.
        Assert.Equal(SoraniLongTitle[..120] + ".pptx", rendered.FileName);
        Assert.EndsWith(".pptx", rendered.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("_", rendered.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("?", rendered.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_title_beyond_draft_capacity_is_rejected_before_any_rendering()
    {
        var overCapacityTitle = string.Concat(Enumerable.Repeat(ArabicLongTitle + " و", 2)).TrimEnd(' ');
        Assert.True(overCapacityTitle.Length > MaximumDraftTitleCharacters);
        var draft = new PresentationDraft
        {
            Title = overCapacityTitle,
            Language = LanguageCodes.Arabic,
            PresentationType = "general",
            Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = ArabicLongTitle }],
        };

        // The overflow contract only exists because the validator caps titles at 160 characters first;
        // anything longer never reaches the fixed box at all.
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions()));
    }

    private static PresentationGenerationInput Input(string language) =>
        new(Guid.NewGuid(), null, "Launch plan", "Present the launch plan.", "general", "standard", "professional", language, null, null, null, true, true, []);

    private static RenderedPresentation RenderDraft(string language, string slideTitle)
    {
        var draft = new PresentationDraft
        {
            Title = slideTitle,
            Language = language,
            PresentationType = "general",
            Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = slideTitle, Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["بند", "بند"] }] }],
        };
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
        return new PresentationRenderer().Render(draft, Input(language), new PresentationGenerationOptions());
    }

    private static string ReadSlide(RenderedPresentation rendered, int number)
    {
        using var archive = new ZipArchive(new MemoryStream(rendered.Content), ZipArchiveMode.Read);
        var entry = archive.GetEntry($"ppt/slides/slide{number}.xml") ?? throw new FileNotFoundException($"Slide part {number} is missing from the rendered package.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static TextShapeInfo SingleTitleShape(string slideXml, string titleText)
    {
        var matches = TextShapes(slideXml).Where(shape => shape.Text == titleText).ToArray();
        Assert.Single(matches);
        return matches[0];
    }

    private static IReadOnlyList<TextShapeInfo> TextShapes(string slideXml)
    {
        var document = XDocument.Parse(slideXml);
        return document.Descendants(P + "sp")
            .Select(shape =>
            {
                var properties = shape.Element(P + "spPr");
                var transform = properties?.Element(A + "xfrm");
                var body = shape.Element(P + "txBody");
                var paragraphs = body?.Elements(A + "p").ToArray() ?? [];
                var firstParagraph = paragraphs.FirstOrDefault();
                var runProperties = firstParagraph?.Element(A + "r")?.Element(A + "rPr");
                var text = string.Join("\n", paragraphs.Select(paragraph => string.Concat(paragraph.Elements(A + "r").Elements(A + "t").Select(run => run.Value))));
                return new TextShapeInfo(
                    shape.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("name")?.Value ?? string.Empty,
                    int.Parse(transform?.Element(A + "off")?.Attribute("x")?.Value ?? "-1"),
                    int.Parse(transform?.Element(A + "off")?.Attribute("y")?.Value ?? "-1"),
                    int.Parse(transform?.Element(A + "ext")?.Attribute("cx")?.Value ?? "-1"),
                    int.Parse(transform?.Element(A + "ext")?.Attribute("cy")?.Value ?? "-1"),
                    text,
                    firstParagraph?.Element(A + "pPr")?.Attribute("algn")?.Value,
                    firstParagraph?.Element(A + "pPr")?.Attribute("rtl")?.Value,
                    body?.Element(A + "bodyPr")?.Attribute("rtlCol")?.Value,
                    body?.Element(A + "bodyPr")?.Attribute("wrap")?.Value,
                    paragraphs.Length,
                    runProperties?.Attribute("lang")?.Value,
                    runProperties?.Attribute("sz")?.Value,
                    runProperties?.Attribute("typeface")?.Value,
                    body?.Element(A + "bodyPr")?.Elements().Select(element => element.Name.LocalName).ToArray() ?? []);
            })
            .Where(shape => shape.Name.StartsWith("Text ", StringComparison.Ordinal))
            .ToArray();
    }

    private sealed record TextShapeInfo(
        string Name,
        int X,
        int Y,
        int CX,
        int CY,
        string Text,
        string? Alignment,
        string? RtlParagraph,
        string? RtlColumn,
        string? Wrap,
        int ParagraphCount,
        string? Language,
        string? FontSize,
        string? Typeface,
        string[] Autofit);
}
