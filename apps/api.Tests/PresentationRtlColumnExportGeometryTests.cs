using System.IO.Compression;
using System.Xml.Linq;
using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// RTL export geometry regressions for synthetic Arabic and Sorani multi-column slides. When the built-in
/// PPTX renderer takes the RTL path (Arabic or Sorani draft languages, or an Arabic-script title inside an
/// otherwise LTR session), column blocks are laid out at the maximum supported column count inside the
/// full-height content area, so every derived text-box extent must stay positive and each column caption
/// must remain fully inside its card at safe lengths. This pins the export path only: the English
/// short-content-area column stress (PR #741) and the preview CSS row mirroring (PR #747) cover neither the
/// Arabic/Sorani fixture nor the RTL paragraph markers on column text.
/// </summary>
public sealed class PresentationRtlColumnExportGeometryTests
{
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const int SlideWidth = 12_192_000;
    private const int SlideHeight = 6_858_000;
    private const int ContentTop = 1_520_000;
    private const int MinimumSafeCaptionWidth = 1_000_000;
    private const int ColumnCaptionHeight = 420_000;

    private const string ArabicTitle = "خطة الإطلاق";
    private const string SoraniTitle = "پلانی دەستپێکردن";

    private static readonly IReadOnlyList<string> ArabicHeadings =
        ["السوق", "الفريق", "الميزانية", "المخاطر", "الفرص", "النتائج", "العملاء", "الموردون", "الشركاء", "القنوات", "الرسائل", "المؤشرات"];

    private static readonly IReadOnlyList<string> ArabicItems =
        ["تحليل المنافسين", "حجم الطلب", "خطة التوظيف", "برنامج التدريب", "تقدير التكاليف", "متوسط العوائد", "خارطة الطريق", "مراجعة ربع سنوية", "إجراءات التصحيح", "أولويات النمو", "توثيق النتائج", "لوحة المتابعة"];

    private static readonly IReadOnlyList<string> SoraniHeadings =
        ["بازاڕ", "تیم", "بودجە", "مەترسی", "هەلی گەشە", "دەرەنجام", "کڕیار", "داواکاری", "پێداچوونەوە", "بڕیار", "گرێبەست", "خزمەتگوزاری"];

    private static readonly IReadOnlyList<string> SoraniItems =
        ["شیکاری ڕکابەران", "قەبارەی داوا", "پلانی دامەزراندن", "بەرنامەی ڕاهێنان", "نرخی تێچوو", "ڕێژەی قازانج", "خاڵی بەرزبوونەوە", "پێوەری کارکردن", "ڕێکاری چاکسازی", "ئامانجی ساڵانە", "تۆماری ئەنجام", "پەنجەرەی داتا"];

    [Theory]
    [InlineData("ar")]
    [InlineData("ku")]
    public void Rtl_multi_column_export_keeps_every_text_extent_positive(string draftLanguage)
    {
        var (shapes, _) = Render(draftLanguage);

        Assert.All(shapes, shape =>
        {
            Assert.True(shape.Width > 0, $"{shape.Name} emitted a non-positive width cx={shape.Width}.");
            Assert.True(shape.Height > 0, $"{shape.Name} emitted a non-positive height cy={shape.Height}.");
            Assert.True(shape.X >= 0 && shape.Y >= 0, $"{shape.Name} is placed at a negative offset x={shape.X} y={shape.Y}.");
            Assert.True(shape.Right <= SlideWidth, $"{shape.Name} crosses the slide edge at x={shape.Right}.");
            Assert.True(shape.Bottom <= SlideHeight, $"{shape.Name} crosses the slide edge at y={shape.Bottom}.");
        });
    }

    [Theory]
    [InlineData("ar")]
    [InlineData("ku")]
    public void Rtl_column_captions_stay_inside_their_card_at_safe_lengths(string draftLanguage)
    {
        var (shapes, _) = Render(draftLanguage);
        var byId = shapes.ToDictionary(shape => shape.Id);
        var cards = ColumnCards(shapes);

        var expectedCards = PresentationRendererLimits.MaxBlocksPerSlide * PresentationRendererLimits.MaxColumnsPerBlock;
        Assert.Equal(expectedCards, cards.Count);
        foreach (var card in cards)
        {
            foreach (var text in new[] { byId[card.Id + 1], byId[card.Id + 2] })
            {
                Assert.True(text.X >= card.X, $"{text.Name} starts at x={text.X} left of its card edge x={card.X}.");
                Assert.True(text.Right <= card.Right, $"{text.Name} ends at x={text.Right} beyond its card right edge x={card.Right}.");
                Assert.True(text.Y >= card.Y, $"{text.Name} starts at y={text.Y} above its card top y={card.Y}.");
                Assert.True(text.Bottom <= card.Bottom, $"{text.Name} ends at y={text.Bottom} below its card bottom y={card.Bottom}.");
            }

            var caption = byId[card.Id + 1];
            Assert.True(caption.Width >= MinimumSafeCaptionWidth, $"The RTL column caption {caption.Name} collapses to cx={caption.Width}.");
            Assert.Equal(ColumnCaptionHeight, caption.Height);
        }
    }

    [Theory]
    [InlineData("ar", "ar-SA")]
    [InlineData("ku", "ku-Arab")]
    public void Rtl_column_paragraphs_carry_bidi_markers_and_survive_export_verbatim(string draftLanguage, string expectedLangTag)
    {
        var (_, slideXml) = Render(draftLanguage);

        Assert.Contains("rtl=\"1\"", slideXml, StringComparison.Ordinal);
        Assert.Contains("rtlCol=\"1\"", slideXml, StringComparison.Ordinal);
        Assert.Contains("algn=\"r\"", slideXml, StringComparison.Ordinal);
        Assert.Contains($"lang=\"{expectedLangTag}\"", slideXml, StringComparison.Ordinal);
        Assert.Contains("Noto Sans Arabic", slideXml, StringComparison.Ordinal);
        Assert.Contains(RtlTitle(draftLanguage), slideXml, StringComparison.Ordinal);
        Assert.All(RtlHeadings(draftLanguage), heading => Assert.Contains(heading, slideXml, StringComparison.Ordinal));
    }

    [Fact]
    public void Arabic_script_title_forces_rtl_column_geometry_in_an_ltr_session()
    {
        // Direction is derived from the Arabic-script slide title while the session language stays English:
        // the RTL layout path must still produce contained, positive-extent column captions.
        var draft = BuildDraft("en", SoraniTitle);
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
        var rendered = new PresentationRenderer().Render(draft, Input("en"), new PresentationGenerationOptions());
        PresentationOutputIntegrityValidator.Validate(rendered);

        var (shapes, slideXml) = ReadShapes(rendered);
        Assert.Contains("rtl=\"1\"", slideXml, StringComparison.Ordinal);
        Assert.Contains("rtlCol=\"1\"", slideXml, StringComparison.Ordinal);
        Assert.All(shapes, shape => Assert.True(shape.Width > 0 && shape.Height > 0, $"{shape.Name} emitted a non-positive extent cx={shape.Width} cy={shape.Height}."));
        var byId = shapes.ToDictionary(shape => shape.Id);
        var cards = ColumnCards(shapes);
        Assert.NotEmpty(cards);
        Assert.All(cards, card =>
        {
            foreach (var text in new[] { byId[card.Id + 1], byId[card.Id + 2] })
                Assert.True(text.Right <= card.Right && text.Bottom <= card.Bottom, $"{text.Name} escapes its RTL card.");
        });
    }

    private static (List<RenderedShape> Shapes, string SlideXml) Render(string draftLanguage)
    {
        var draft = BuildDraft(draftLanguage, RtlTitle(draftLanguage));
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
        var rendered = new PresentationRenderer().Render(draft, Input("auto"), new PresentationGenerationOptions());
        PresentationOutputIntegrityValidator.Validate(rendered);
        return ReadShapes(rendered);
    }

    private static PresentationGenerationInput Input(string language) => new(Guid.NewGuid(), null, "Launch plan", "Plan", "general", "short", "professional", language, null, null, null, true, true, []);

    private static string RtlTitle(string language) => language == "ku" ? SoraniTitle : ArabicTitle;
    private static IReadOnlyList<string> RtlHeadings(string language) => language == "ku" ? SoraniHeadings : ArabicHeadings;
    private static IReadOnlyList<string> RtlItems(string language) => language == "ku" ? SoraniItems : ArabicItems;

    private static PresentationDraft BuildDraft(string language, string title)
    {
        var headings = language == "ku" || language == "en" ? SoraniHeadings : ArabicHeadings;
        var items = language == "ku" || language == "en" ? SoraniItems : ArabicItems;
        return new PresentationDraft
        {
            Title = title,
            Language = language,
            PresentationType = "general",
            Slides =
            [
                new PresentationSlide
                {
                    Order = 1,
                    Type = PresentationSlideTypes.Content,
                    Title = title,
                    Blocks = [.. Enumerable.Range(0, PresentationRendererLimits.MaxBlocksPerSlide).Select(blockIndex => new PresentationContentBlock
                    {
                        Type = PresentationBlockTypes.Columns,
                        Columns = [.. Enumerable.Range(0, PresentationRendererLimits.MaxColumnsPerBlock).Select(columnIndex =>
                        {
                            var index = blockIndex * PresentationRendererLimits.MaxColumnsPerBlock + columnIndex;
                            return new PresentationColumn
                            {
                                Heading = headings[index % headings.Count],
                                Items = [items[index % items.Count], items[(index + 1) % items.Count]],
                            };
                        })],
                    })],
                },
            ],
        };
    }

    private static List<RenderedShape> ColumnCards(List<RenderedShape> shapes)
    {
        var byId = shapes.ToDictionary(shape => shape.Id);
        return shapes
            .Where(shape => shape.Kind == "Shape"
                            && shape.Y >= ContentTop
                            && byId.TryGetValue(shape.Id + 1, out var heading) && heading.Kind == "Text"
                            && byId.TryGetValue(shape.Id + 2, out var body) && body.Kind == "Text")
            .ToList();
    }

    private static (List<RenderedShape> Shapes, string SlideXml) ReadShapes(RenderedPresentation rendered)
    {
        using var archive = new ZipArchive(new MemoryStream(rendered.Content), ZipArchiveMode.Read);
        var slideEntry = archive.GetEntry("ppt/slides/slide1.xml") ?? throw new Xunit.Sdk.XunitException("The rendered package does not contain ppt/slides/slide1.xml.");
        using var reader = new StreamReader(slideEntry.Open());
        var slideXml = reader.ReadToEnd();
        return (XDocument.Parse(slideXml).Descendants(XName.Get("sp", PresentationNamespace)).Select(ReadShape).ToList(), slideXml);
    }

    private static RenderedShape ReadShape(XElement shape)
    {
        var identity = shape.Element(XName.Get("nvSpPr", PresentationNamespace))?.Element(XName.Get("cNvPr", PresentationNamespace));
        var transform = shape.Element(XName.Get("spPr", PresentationNamespace))?.Element(XName.Get("xfrm", DrawingNamespace));
        var offset = transform?.Element(XName.Get("off", DrawingNamespace));
        var extent = transform?.Element(XName.Get("ext", DrawingNamespace));
        var name = identity?.Attribute("name")?.Value ?? string.Empty;

        return new RenderedShape(
            int.Parse(identity?.Attribute("id")?.Value ?? "0"),
            name,
            name.Split(' ')[0],
            ReadCoordinate(offset?.Attribute("x")),
            ReadCoordinate(offset?.Attribute("y")),
            ReadCoordinate(extent?.Attribute("cx")),
            ReadCoordinate(extent?.Attribute("cy")));
    }

    private static int ReadCoordinate(XAttribute? attribute) => int.TryParse(attribute?.Value, out var value) ? value : 0;

    private sealed record RenderedShape(int Id, string Name, string Kind, int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }
}
