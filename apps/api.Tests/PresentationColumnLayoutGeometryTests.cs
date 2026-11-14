using System.IO.Compression;
using System.Xml.Linq;
using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic geometry regressions for the built-in PPTX renderer. A column block splits
/// the slide content area with up to four slide-level blocks, and title-style slides expose
/// only a short content area, so the derived text-box heights must stay schema-valid and
/// inside the card they are drawn on.
/// </summary>
public sealed class PresentationColumnLayoutGeometryTests
{
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const int SlideWidth = 12_192_000;
    private const int SlideHeight = 6_858_000;

    [Theory]
    [InlineData(3)]
    [InlineData(PresentationRendererLimits.MaxBlocksPerSlide)]
    public void Column_blocks_share_a_short_content_area_without_non_positive_extents(int blockCount)
    {
        var shapes = RenderSlide(PresentationSlideTypes.Section, Blocks(blockCount));

        Assert.All(shapes, shape =>
        {
            Assert.True(shape.Width > 0, $"{shape.Name} emitted a non-positive width cx={shape.Width}.");
            Assert.True(shape.Height > 0, $"{shape.Name} emitted a non-positive height cy={shape.Height}.");
        });
    }

    [Theory]
    [InlineData(3)]
    [InlineData(PresentationRendererLimits.MaxBlocksPerSlide)]
    public void Column_heading_and_body_stay_inside_their_card_on_short_blocks(int blockCount)
    {
        var shapes = RenderSlide(PresentationSlideTypes.Section, Blocks(blockCount));
        var byId = shapes.ToDictionary(shape => shape.Id);
        var cards = ColumnCards(shapes);

        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            foreach (var text in new[] { byId[card.Id + 1], byId[card.Id + 2] })
            {
                Assert.True(text.Y >= card.Y, $"{text.Name} starts at y={text.Y} above its card top y={card.Y}.");
                Assert.True(text.Y < card.Bottom, $"{text.Name} starts at y={text.Y} past its card bottom y={card.Bottom}.");
                Assert.True(text.Bottom <= card.Bottom, $"{text.Name} ends at y={text.Bottom} below its card bottom y={card.Bottom}.");
                Assert.True(text.Right <= card.Right, $"{text.Name} ends at x={text.Right} beyond its card right edge x={card.Right}.");
            }
        }
    }

    [Fact]
    public void Every_shape_stays_within_the_16_by_9_slide_bounds()
    {
        var shapes = RenderSlide(PresentationSlideTypes.Section, Blocks(PresentationRendererLimits.MaxBlocksPerSlide));

        Assert.All(shapes, shape =>
        {
            Assert.True(shape.X >= 0 && shape.Y >= 0, $"{shape.Name} is placed at a negative offset x={shape.X} y={shape.Y}.");
            Assert.True(shape.Right <= SlideWidth, $"{shape.Name} crosses the slide edge at x={shape.Right}.");
            Assert.True(shape.Bottom <= SlideHeight, $"{shape.Name} crosses the slide edge at y={shape.Bottom}.");
        });
    }

    [Fact]
    public void Full_height_column_block_keeps_its_designed_heading_and_body_geometry()
    {
        var shapes = RenderSlide(PresentationSlideTypes.Content, [ColumnBlock(1)]);
        var card = ColumnCards(shapes).First();
        var heading = shapes.Single(shape => shape.Id == card.Id + 1);
        var body = shapes.Single(shape => shape.Id == card.Id + 2);

        Assert.Equal(4_520_000, card.Height);
        Assert.Equal(160_000, heading.Y - card.Y);
        Assert.Equal(420_000, heading.Height);
        Assert.Equal(680_000, body.Y - card.Y);
        Assert.Equal(3_800_000, body.Height);
    }

    private static List<RenderedShape> ColumnCards(List<RenderedShape> shapes)
    {
        var byId = shapes.ToDictionary(shape => shape.Id);
        return shapes
            .Where(shape => shape.Kind == "Shape"
                            && byId.TryGetValue(shape.Id + 1, out var heading) && heading.Kind == "Text"
                            && byId.TryGetValue(shape.Id + 2, out var body) && body.Kind == "Text")
            .ToList();
    }

    private static List<PresentationContentBlock> Blocks(int count) => Enumerable.Range(1, count).Select(ColumnBlock).ToList();

    private static PresentationContentBlock ColumnBlock(int index) => new()
    {
        Type = PresentationBlockTypes.Columns,
        Columns =
        [
            new PresentationColumn { Heading = $"Left {index}", Items = [$"Point {index}"] },
            new PresentationColumn { Heading = $"Right {index}", Items = [$"Point {index}"] },
        ],
    };

    private static List<RenderedShape> RenderSlide(string slideType, List<PresentationContentBlock> blocks)
    {
        var options = new PresentationGenerationOptions();
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "Launch", "Plan", "general", "short", "professional", "en", null, null, null, true, true, []);
        var draft = new PresentationDraft
        {
            Title = "Launch",
            Subtitle = "Plan",
            Language = "en",
            PresentationType = "general",
            Slides = [new PresentationSlide { Order = 1, Type = slideType, Title = "Plan", Blocks = blocks }],
        };

        PresentationDraftValidator.Validate(draft, options);
        var rendered = new PresentationRenderer().Render(draft, input, options);
        PresentationOutputIntegrityValidator.Validate(rendered);

        using var archive = new ZipArchive(new MemoryStream(rendered.Content), ZipArchiveMode.Read);
        var slideEntry = archive.GetEntry("ppt/slides/slide1.xml") ?? throw new Xunit.Sdk.XunitException("The rendered package does not contain ppt/slides/slide1.xml.");
        using var reader = new StreamReader(slideEntry.Open());
        return XDocument.Parse(reader.ReadToEnd())
            .Descendants(XName.Get("sp", PresentationNamespace))
            .Select(ReadShape)
            .ToList();
    }

    private static RenderedShape ReadShape(XElement shape)
    {
        var identity = shape.Element(XName.Get("nvSpPr", PresentationNamespace))?.Element(XName.Get("cNvPr", PresentationNamespace));
        var transform = shape.Element(XName.Get("spPr", PresentationNamespace))?.Element(XName.Get("xfrm", DrawingNamespace));
        var offset = transform?.Element(XName.Get("off", DrawingNamespace));
        var extent = transform?.Element(XName.Get("ext", DrawingNamespace));
        var name = identity?.Attribute("name")?.Value ?? string.Empty;
        var kind = name.Split(' ')[0];

        return new RenderedShape(
            int.Parse(identity?.Attribute("id")?.Value ?? "0"),
            name,
            kind,
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
