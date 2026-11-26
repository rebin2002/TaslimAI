using System.IO.Compression;
using System.Xml.Linq;
using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Contract regressions for the #741 visual-review fix: column heading/body boxes on compact
/// (title/section/closing) slides must ship an explicit &lt;a:normAutofit fontScale/&gt; whenever the
/// emitter's conservative line estimate says the authored-size ink would leave the fixed box -
/// because a self-closed bodyPr hands overflow to the application and LibreOffice/PowerPoint then
/// draw the body text across the card bottom (rendered evidence: 9-18% of column ink outside cards
/// before the fix, page-number-glyph-only residual after). The pins here are contract-level, not
/// implementation echoes: (1) any emitted scale must make the declared metrics fit, (2) the scale
/// must be tight (the previous scan step must NOT fit), (3) missing scale implies the content fits
/// at 100% under the same metrics, (4) box geometry and card containment are unchanged, (5) full
/// slides that fit at 100% keep the exact self-closed bodyPr so the shipped full-height column
/// contract (#887 caption geometry included) is byte-stable, (6) RTL direction markers survive the
/// added autofit child, and (7) no item text is truncated, split, or dropped.
/// </summary>
public sealed class PresentationColumnTextFitTests
{
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const long EmuPerPoint = 12_700;
    private const long DefaultHorizontalInsets = 182_880; // lIns + rIns (2 x 91 440)
    private const long DefaultTopInset = 45_720;          // tIns
    private const long FloorScale = 20_000;

    // Declared conservative glyph metrics (PresentationRenderer.ColumnTextFitScale):
    // 0.65 em average advance per character; line height 1.25 em (Latin) / 1.50 em (Arabic fonts).
    private const long CharAdvancePercent = 65;
    private const long LatinLinePercent = 125;
    private const long RtlLinePercent = 150;

    [Theory]
    [InlineData("title", 2, "en")]
    [InlineData("title", 3, "en")]
    [InlineData("title", 4, "en")]
    [InlineData("title", 2, "ar")]
    [InlineData("title", 3, "ar")]
    [InlineData("title", 4, "ar")]
    [InlineData("section", 3, "ku")]
    [InlineData("section", 4, "ku")]
    [InlineData("closing", 3, "ar")]
    [InlineData("closing", 4, "ku")]
    public void Column_boxes_that_cannot_hold_authored_size_ship_a_fitting_font_scale(string slideType, int blockCount, string lang)
    {
        var boxes = RenderBoxes(slideType, blockCount, lang);
        Assert.NotEmpty(boxes);
        foreach (var box in boxes)
        {
            var fullScaleFits = EstimatedInkHeight(box.Lines, 100_000, box.SizePt, box.BoxCx, box.BoxCy, box.Rtl) <= box.BoxCy - DefaultTopInset;
            if (box.FontScale is null)
            {
                // Only the self-closed bodyPr on boxes that fit at 100% may omit the autofit element.
                Assert.True(fullScaleFits, $"{box.Name} omits normAutofit but its {box.Lines.Count} {box.SizePt}pt line(s) exceed the box at 100%.");
                continue;
            }

            Assert.True(box.FontScale >= FloorScale && box.FontScale < 100_000, $"{box.Name} emitted fontScale={box.FontScale}.");
            var fitsAtScale = EstimatedInkHeight(box.Lines, box.FontScale.Value, box.SizePt, box.BoxCx, box.BoxCy, box.Rtl) <= box.BoxCy - DefaultTopInset;
            if (box.FontScale > FloorScale)
                Assert.True(fitsAtScale, $"{box.Name} fontScale={box.FontScale} still leaves the declared metrics outside the box.");

            // Tightness: the previous scan step (5 000 above 50 000, 2 500 below) must not fit,
            // so the renderer may not over-shrink beyond its own scan resolution.
            var previous = box.FontScale.Value + (box.FontScale.Value >= 50_000 ? 5_000 : 2_500);
            if (previous < 100_000)
                Assert.True(EstimatedInkHeight(box.Lines, previous, box.SizePt, box.BoxCx, box.BoxCy, box.Rtl) > box.BoxCy - DefaultTopInset,
                    $"{box.Name} fontScale={box.FontScale} is looser than its scan step.");

            // RTL direction markers must survive the added bodyPr child.
            if (box.Rtl)
            {
                Assert.Equal("1", box.RtlColumn);
                Assert.All(box.Lines, _ => Assert.Equal("1", box.RtlParagraph));
            }
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Compact_slides_keep_every_column_box_inside_its_card_and_slide(string lang)
    {
        foreach (var blockCount in new[] { 2, 3, 4 })
        {
            var (shapes, boxes, _) = Render("title", blockCount, lang, Columns(blockCount));
            var cards = shapes.Where(shape => shape.Kind == "Shape" && shape.Width < 11_000_000).ToList();
            Assert.NotEmpty(cards);
            foreach (var box in boxes.Where(box => box.SizePt is 13 or 15))
            {
                Assert.True(cards.Any(card => box.X >= card.X && box.Right <= card.Right && box.Y >= card.Y && box.Bottom <= card.Bottom),
                    $"Column box {box.Name} left its card at block count {blockCount}.");
            }

            Assert.All(shapes, shape =>
            {
                Assert.True(shape.X >= 0 && shape.Y >= 0 && shape.Width > 0 && shape.Height > 0);
                Assert.True(shape.Right <= 12_192_000 && shape.Bottom <= 6_858_000);
            });
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Full_height_column_boxes_that_fit_keep_the_self_closed_body_pr(string lang)
    {
        var full = Render("content", 1, lang, Columns(blockCount: 1, items: 1, longItem: false));
        var boxes = full.Boxes;
        var xml = full.Xml;
        Assert.All(boxes, box =>
        {
            Assert.Null(box.FontScale);
            Assert.True(EstimatedInkHeight(box.Lines, 100_000, box.SizePt, box.BoxCx, box.BoxCy, box.Rtl) <= box.BoxCy - DefaultTopInset);
        });
        // #887 shipped invariant: the full-height heading caption box keeps its designed 420 000 EMU height.
        var heading = boxes.First(box => box.SizePt == 15);
        Assert.Equal(420_000, heading.BoxCy);
        // The bodyPr is emitted exactly as before the fix (self-closed, no autofit child).
        Assert.Contains($"<a:bodyPr wrap=\"square\" rtlCol=\"{(lang is "ar" ? "1" : "0")}\"/>", xml);
    }

    [Theory]
    [InlineData("title", 4, "en")]
    [InlineData("title", 4, "ar")]
    public void Autofit_never_truncates_splits_or_drops_item_text(string slideType, int blockCount, string lang)
    {
        var rendered = RenderDraft(slideType, blockCount, lang);
        var xml = ReadSlideXml(rendered);
        foreach (var item in ExpectedItems(lang))
            Assert.Contains(EscapeXml(item), xml, StringComparison.Ordinal);
        // And the authored 13pt size stays on every body run: only the box-level scale shrinks rendering.
        Assert.Contains("sz=\"1300\"", xml, StringComparison.Ordinal);
        Assert.Contains("sz=\"1500\"", xml, StringComparison.Ordinal);
    }

    private record TextBoxInfo(string Name, int X, int Y, int BoxCx, int BoxCy, int SizePt, List<string> Lines, long? FontScale, string? RtlColumn, string? RtlParagraph, string? FontScaleKey)
    {
        public int Right => X + BoxCx;
        public int Bottom => Y + BoxCy;
        public bool Rtl => RtlColumn == "1";
    }

    private static List<TextBoxInfo> RenderBoxes(string slideType, int blockCount, string lang) => Render(slideType, blockCount, lang, Columns(blockCount)).Boxes;

    private static (List<ShapeInfo> Shapes, List<TextBoxInfo> Boxes, string Xml) Render(string slideType, int blockCount, string lang, List<PresentationContentBlock> blocks)
    {
        var rendered = RenderDraft(slideType, blockCount, lang, blocks);
        var xml = ReadSlideXml(rendered);
        var document = XDocument.Parse(xml);
        var shapes = new List<ShapeInfo>();
        var boxes = new List<TextBoxInfo>();
        foreach (var sp in document.Descendants(XName.Get("sp", PresentationNamespace)))
        {
            var identity = sp.Element(XName.Get("nvSpPr", PresentationNamespace))?.Element(XName.Get("cNvPr", PresentationNamespace));
            var xfrm = sp.Element(XName.Get("spPr", PresentationNamespace))?.Element(XName.Get("xfrm", DrawingNamespace));
            var off = xfrm?.Element(XName.Get("off", DrawingNamespace));
            var ext = xfrm?.Element(XName.Get("ext", DrawingNamespace));
            var name = identity?.Attribute("name")?.Value ?? string.Empty;
            var x = int.Parse(off?.Attribute("x")?.Value ?? "-1");
            var y = int.Parse(off?.Attribute("y")?.Value ?? "-1");
            var cx = int.Parse(ext?.Attribute("cx")?.Value ?? "-1");
            var cy = int.Parse(ext?.Attribute("cy")?.Value ?? "-1");
            shapes.Add(new ShapeInfo(name, name.Split(' ')[0], x, y, cx, cy));
            var body = sp.Element(XName.Get("txBody", PresentationNamespace));
            var bodyPr = body?.Element(XName.Get("bodyPr", DrawingNamespace));
            if (bodyPr is null || !name.StartsWith("Text ", StringComparison.Ordinal)) continue;
            var scale = bodyPr.Element(XName.Get("normAutofit", DrawingNamespace))?.Attribute("fontScale")?.Value;
            var size = body.Descendants(XName.Get("rPr", DrawingNamespace))
                .Select(run => int.TryParse(run.Attribute("sz")?.Value, out var value) ? value : 0)
                .Where(value => value > 0).Select(value => value / 100).DefaultIfEmpty(0).Min();
            var paragraphs = body.Descendants(XName.Get("p", DrawingNamespace))
                .Select(paragraph => string.Concat(paragraph.Elements(XName.Get("r", DrawingNamespace)).Select(run => run.Element(XName.Get("t", DrawingNamespace))?.Value ?? string.Empty)))
                .ToList();
            boxes.Add(new TextBoxInfo(name, x, y, cx, cy, size, paragraphs,
                scale is null ? null : long.Parse(scale),
                bodyPr.Attribute("rtlCol")?.Value,
                body.Element(XName.Get("p", DrawingNamespace))?.Element(XName.Get("pPr", DrawingNamespace))?.Attribute("rtl")?.Value,
                scale is null ? null : scale));
        }

        return (shapes, boxes, xml);
    }

    private static RenderedPresentation RenderDraft(string slideType, int blockCount, string lang, List<PresentationContentBlock>? blocks = null)
    {
        blocks ??= Columns(blockCount);
        var options = new PresentationGenerationOptions();
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "Launch", "Plan", "general", "short", "professional", lang, null, null, null, true, true, []);
        var rtl = lang is "ar" or "ku";
        var draft = new PresentationDraft
        {
            Title = rtl ? "خطة الإطلاق" : "Launch",
            Subtitle = rtl ? "ملخص" : "Plan",
            Language = lang,
            PresentationType = "general",
            Slides = [new PresentationSlide
            {
                Order = 1,
                Type = slideType,
                Title = rtl ? "الخطة" : "The Plan",
                Blocks = blocks,
            }],
        };
        PresentationDraftValidator.Validate(draft, options);
        var rendered = new PresentationRenderer().Render(draft, input, options);
        PresentationOutputIntegrityValidator.Validate(rendered);
        return rendered;
    }

    private static List<PresentationContentBlock> Columns(int blockCount, int items = 3, bool longItem = true)
        => Enumerable.Range(1, blockCount).Select(index => new PresentationContentBlock
        {
            Type = PresentationBlockTypes.Columns,
            Columns =
            [
                new PresentationColumn { Heading = longItem ? $"Column {index}A heading" : $"Col {index}A", Items = [$"Short {index}"] },
                new PresentationColumn { Heading = $"Col {index}B", Items = longItem ? [$"Point {index} one", "Alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november"] : [$"Point {index} two"] },
                new PresentationColumn { Heading = $"Col {index}C", Items = items switch { 1 => [$"Point {index}"], 2 => [$"Point {index} a", $"Point {index} b"], _ => [$"Point {index} a", $"Point {index} b", $"Point {index} c"] } },
            ],
        }).ToList();

    // The Columns() helper writes Latin item text in every language; the draft only flips direction.
    private static List<string> ExpectedItems(string lang) => ["Short", "Point", "Alpha bravo"];

    private static long EstimatedInkHeight(List<string> lines, long scale, int sizePt, int boxCx, int boxCy, bool rtl)
    {
        var textWidth = boxCx - DefaultHorizontalInsets;
        if (textWidth <= 0) return long.MaxValue;
        var emu = (long)sizePt * EmuPerPoint;
        var charWidth = emu * CharAdvancePercent * scale / 10_000_000L;
        var lineHeight = emu * (rtl ? RtlLinePercent : LatinLinePercent) * scale / 10_000_000L;
        long total = 0;
        foreach (var line in lines)
            total += Math.Max(1L, (line.Length * charWidth + textWidth - 1) / textWidth) * lineHeight;
        return total;
    }

    private static string EscapeXml(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    private static string ReadSlideXml(RenderedPresentation rendered)
    {
        using var archive = new ZipArchive(new MemoryStream(rendered.Content), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("ppt/slides/slide1.xml")!.Open());
        return reader.ReadToEnd();
    }

    private sealed record ShapeInfo(string Name, string Kind, int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }
}
