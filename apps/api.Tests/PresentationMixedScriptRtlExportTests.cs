using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// RTL export regressions for Presentation drafts whose Arabic-script content is not in the slide title.
/// PresentationRenderer decides both the bidi layout flags and the typeface family from a single script
/// probe, so a mixed-script draft - an English session whose body text, subtitle, table cell or metric is
/// Arabic/Sorani - must still route every Arabic-script run through the RTL font and paragraph direction.
/// If it does not, the run is emitted with the Latin fallback family and a left-to-right base direction and
/// PowerPoint renders the Arabic unshaped and reversed, while the integrity validator accepts the package
/// because it never inspects run fonts. Latin-only slides have to keep the LTR layout untouched.
/// </summary>
public sealed class PresentationMixedScriptRtlExportTests
{
    private const string ArabicBody = "خطة إطلاق منصة تسويق المحتوى";
    private const string ArabicColumnHeading = "الفريق";
    private const string SoraniBody = "پلانی دەستپێکردنی پێشکەشکردەی بازاڕکردنی";
    private const string LatinBody = "Roll out the regional marketing platform";
    private const string RtlFontFamily = "Noto Sans Arabic";
    private const string LatinFontFamily = "Aptos";

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [Theory]
    // Every block kind the renderer can lay out, carrying the Arabic script outside of the slide title.
    [InlineData(PresentationBlockTypes.Bullets)]
    [InlineData(PresentationBlockTypes.Timeline)]
    [InlineData(PresentationBlockTypes.Process)]
    [InlineData(PresentationBlockTypes.Text)]
    [InlineData(PresentationBlockTypes.Quote)]
    [InlineData(PresentationBlockTypes.Columns)]
    [InlineData(PresentationBlockTypes.Table)]
    [InlineData(PresentationBlockTypes.Metrics)]
    public void Arabic_block_text_in_an_ltr_session_is_rendered_with_the_rtl_font_and_direction(string blockType)
    {
        var slide = new PresentationSlide
        {
            Order = 1,
            Type = PresentationSlideTypes.Content,
            Title = "Q3 roadmap",
            Blocks = [ArabicBlock(blockType)],
        };

        var runs = ArabicScriptRuns(Render("en", slide));

        Assert.NotEmpty(runs);
        Assert.All(runs, run =>
        {
            Assert.Equal(RtlFontFamily, run.Typeface);
            Assert.Equal("ar-SA", run.Language);
            Assert.Equal("1", run.ParagraphRtl);
            Assert.Equal("r", run.Alignment);
        });
    }

    [Fact]
    public void Arabic_subtitle_under_a_latin_title_uses_the_rtl_font_and_direction()
    {
        var slide = new PresentationSlide
        {
            Order = 1,
            Type = PresentationSlideTypes.Content,
            Title = "Q3 roadmap",
            Subtitle = ArabicBody,
            Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = LatinBody }],
        };

        var runs = ArabicScriptRuns(Render("en", slide));

        Assert.NotEmpty(runs);
        Assert.All(runs, run =>
        {
            Assert.Equal(RtlFontFamily, run.Typeface);
            Assert.Equal("1", run.ParagraphRtl);
        });
    }

    [Fact]
    public void Sorani_body_text_in_an_ltr_session_is_rendered_rtl_instead_of_the_latin_fallback()
    {
        var slide = new PresentationSlide
        {
            Order = 1,
            Type = PresentationSlideTypes.Bullets,
            Title = "Highlights",
            Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = [SoraniBody, "Second point"] }],
        };

        var runs = ArabicScriptRuns(Render("en", slide));

        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.Equal(RtlFontFamily, run.Typeface));
    }

    [Fact]
    public void Provider_language_auto_resolves_to_the_draft_language_and_still_covers_body_only_arabic()
    {
        // The draft reports "en", so the resolved session language is LTR and only the script probe can
        // notice the Arabic body. This is the shape a mixed-language provider draft actually arrives in.
        var draft = new PresentationDraft
        {
            Title = "Launch plan",
            Language = "en",
            PresentationType = "general",
            Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = "Market entry", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = ArabicBody }] }],
        };
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());

        var rendered = new PresentationRenderer().Render(draft, Input("auto"), new PresentationGenerationOptions());
        var runs = ArabicScriptRuns(rendered, 1);

        Assert.NotEmpty(runs);
        Assert.All(runs, run =>
        {
            Assert.Equal(RtlFontFamily, run.Typeface);
            Assert.Equal("1", run.ParagraphRtl);
        });
    }

    [Fact]
    public void Arabic_table_cell_reaches_the_rtl_font_and_column_direction_in_the_editable_table()
    {
        // Tables lay text out through a:tc/a:bodyPr, so rtlCol has to follow the script too.
        var slide = new PresentationSlide
        {
            Order = 1,
            Type = PresentationSlideTypes.Table,
            Title = "Budget",
            Blocks = [new PresentationContentBlock
            {
                Type = PresentationBlockTypes.Table,
                Rows =
                [
                    new PresentationTableRow { Cells = ["Region", "Spend"] },
                    new PresentationTableRow { Cells = [ArabicBody, "120000"] },
                ],
            }],
        };

        var slideXml = ReadSlide(Render("en", slide), 1);
        var runs = ArabicScriptRuns(Render("en", slide));

        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.Equal(RtlFontFamily, run.Typeface));
        Assert.Contains("rtlCol=\"1\"", slideXml, StringComparison.Ordinal);
    }

    [Fact]
    public void Latin_only_deck_in_an_ltr_session_keeps_the_latin_font_and_left_to_right_layout()
    {
        var slide = new PresentationSlide
        {
            Order = 1,
            Type = PresentationSlideTypes.Content,
            Title = "Q3 roadmap",
            Subtitle = "Sequenced delivery",
            Blocks =
            [
                new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["Scope the pilot", "Measure adoption"] },
                new PresentationContentBlock
                {
                    Type = PresentationBlockTypes.Columns,
                    Columns =
                    [
                        new PresentationColumn { Heading = "Team", Items = ["Delivery"] },
                        new PresentationColumn { Heading = "Budget", Items = ["Forecast"] },
                    ],
                },
                new PresentationContentBlock { Type = PresentationBlockTypes.Table, Rows = [new PresentationTableRow { Cells = ["Region", "Spend"] }] },
                new PresentationContentBlock { Type = PresentationBlockTypes.Metrics, Metrics = [new PresentationMetric { Label = "Growth", Value = "42%", Detail = "versus last quarter" }] },
            ],
        };

        var runs = TextRuns(Render("en", slide));

        Assert.NotEmpty(runs);
        // Nothing on this slide is Arabic script, so the probe must leave the LTR layout alone.
        Assert.DoesNotContain(runs, run => ContainsArabicScript(run.Text));
        Assert.All(runs, run =>
        {
            Assert.Equal(LatinFontFamily, run.Typeface);
            Assert.Equal("en-US", run.Language);
            Assert.Equal("0", run.ParagraphRtl);
            Assert.Equal("l", run.Alignment);
        });
    }

    [Fact]
    public void Script_probe_stays_per_slide_so_an_english_slide_keeps_the_latin_font()
    {
        var draft = new PresentationDraft
        {
            Title = "Launch plan",
            Language = "en",
            PresentationType = "general",
            Slides =
            [
                new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = "Market entry", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = LatinBody }] },
                new PresentationSlide { Order = 2, Type = PresentationSlideTypes.Content, Title = "Market entry", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = ArabicBody }] },
            ],
        };
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
        var rendered = new PresentationRenderer().Render(draft, Input("en"), new PresentationGenerationOptions());

        Assert.All(TextRuns(rendered, 1), run => Assert.Equal(LatinFontFamily, run.Typeface));
        Assert.Contains(ArabicScriptRuns(rendered, 2), run => run.Typeface == RtlFontFamily);
    }

    [Fact]
    public void Mixed_script_slide_still_produces_an_integrity_valid_package()
    {
        var rendered = Render("en", ArabicColumnSlide());

        PresentationOutputIntegrityValidator.Validate(rendered);
        Assert.Equal(AssetRepresentationTypes.Pptx, rendered.RepresentationType);
    }

    [Fact]
    public void Column_heading_script_reaches_the_rtl_font_even_when_items_stay_latin()
    {
        var runs = ArabicScriptRuns(Render("en", ArabicColumnSlide()));

        Assert.NotEmpty(runs);
        Assert.Contains(runs, run => run.Text.Contains(ArabicColumnHeading, StringComparison.Ordinal) && run.Typeface == RtlFontFamily);
    }

    private static PresentationSlide ArabicColumnSlide() => new()
    {
        Order = 1,
        Type = PresentationSlideTypes.Content,
        Title = "Operating model",
        Blocks =
        [
            new PresentationContentBlock
            {
                Type = PresentationBlockTypes.Columns,
                Columns =
                [
                    new PresentationColumn { Heading = ArabicColumnHeading, Items = ["Delivery pod"] },
                    new PresentationColumn { Heading = "Budget", Items = [ArabicBody] },
                ],
            },
        ],
    };

    private static PresentationContentBlock ArabicBlock(string blockType) => blockType switch
    {
        PresentationBlockTypes.Columns => new PresentationContentBlock
        {
            Type = PresentationBlockTypes.Columns,
            Columns = [new PresentationColumn { Heading = ArabicColumnHeading, Items = [ArabicBody] }],
        },
        PresentationBlockTypes.Table => new PresentationContentBlock
        {
            Type = PresentationBlockTypes.Table,
            Rows = [new PresentationTableRow { Cells = [ArabicBody, "120000"] }],
        },
        PresentationBlockTypes.Metrics => new PresentationContentBlock
        {
            Type = PresentationBlockTypes.Metrics,
            Metrics = [new PresentationMetric { Label = ArabicColumnHeading, Value = "42%", Detail = ArabicBody }],
        },
        PresentationBlockTypes.Text => new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = ArabicBody },
        PresentationBlockTypes.Quote => new PresentationContentBlock { Type = PresentationBlockTypes.Quote, Text = ArabicBody },
        _ => new PresentationContentBlock { Type = blockType, Items = [ArabicBody, "Second point"] },
    };

    private static RenderedPresentation Render(string sessionLanguage, PresentationSlide slide)
    {
        var draft = new PresentationDraft
        {
            Title = "Launch plan",
            Language = "en",
            PresentationType = "general",
            Slides = [slide],
        };
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
        return new PresentationRenderer().Render(draft, Input(sessionLanguage), new PresentationGenerationOptions());
    }

    private static PresentationGenerationInput Input(string language) =>
        new(Guid.NewGuid(), null, "Launch plan", "Present the launch plan.", "general", "standard", "professional", language, null, null, null, true, true, []);

    private static IReadOnlyList<TextRun> ArabicScriptRuns(RenderedPresentation rendered, int slideNumber = 1) =>
        TextRuns(rendered, slideNumber).Where(run => ContainsArabicScript(run.Text)).ToArray();

    private static IReadOnlyList<TextRun> TextRuns(RenderedPresentation rendered, int slideNumber = 1)
    {
        var document = XDocument.Parse(ReadSlide(rendered, slideNumber));
        return document.Descendants(A + "r")
            .Select(run =>
            {
                var paragraph = run.Parent;
                var runProperties = run.Element(A + "rPr");
                var paragraphProperties = paragraph?.Element(A + "pPr");
                return new TextRun(
                    run.Element(A + "t")?.Value ?? string.Empty,
                    runProperties?.Attribute("typeface")?.Value,
                    runProperties?.Attribute("lang")?.Value,
                    paragraphProperties?.Attribute("rtl")?.Value,
                    paragraphProperties?.Attribute("algn")?.Value);
            })
            .ToArray();
    }

    private static string ReadSlide(RenderedPresentation rendered, int slideNumber)
    {
        using var archive = new ZipArchive(new MemoryStream(rendered.Content), ZipArchiveMode.Read);
        var entry = archive.GetEntry($"ppt/slides/slide{slideNumber}.xml")
            ?? throw new FileNotFoundException($"Slide part {slideNumber} is missing from the rendered package.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static bool ContainsArabicScript(string value) => value.Any(character => character >= '\u0600' && character <= '\u08ff');

    private sealed record TextRun(string Text, string? Typeface, string? Language, string? ParagraphRtl, string? Alignment);
}
