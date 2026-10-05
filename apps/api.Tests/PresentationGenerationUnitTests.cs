using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationGenerationUnitTests
{
    [Fact]
    public void Structured_presentation_schema_is_strict_and_requires_canonical_fields()
    {
        var schema = PresentationDraftStructuredOutput.Spec.Schema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "title", "subtitle", "language", "theme", "presentationType", "slides" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        var slide = schema.GetProperty("properties").GetProperty("slides").GetProperty("items");
        Assert.False(slide.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("sourceRefs", slide.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Prompt_builder_uses_only_explicit_sources_and_bounded_project_context()
    {
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "Launch", "Present the launch plan.", "business", "standard", "professional", "en", "operators", "Keep it factual.", "Taslim", true, true, [Guid.NewGuid()]);
        var prompt = new PresentationPromptBuilder().Build(input, new PresentationProjectContext("Launch project", "Use the approved plan.", "Audience is operators."), [new PresentationSourceContext("brief.txt", ".txt", "Only selected source")], new PresentationGenerationOptions { MaxContextCharacters = 5_000 });
        Assert.Contains("Only selected source", prompt.UserInstruction, StringComparison.Ordinal);
        Assert.Contains("Audience is operators", prompt.UserInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("personal memory", prompt.UserInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<PresentationContextLimitException>(() => new PresentationPromptBuilder().Build(input, null, [new PresentationSourceContext("large.txt", ".txt", new string('x', 5_001))], new PresentationGenerationOptions { MaxContextCharacters = 5_000 }));
    }

    [Fact]
    public void Draft_validator_rejects_contentless_blocks_before_rendering()
    {
        var blocks = new[]
        {
            new PresentationContentBlock { Type = PresentationBlockTypes.Text },
            new PresentationContentBlock { Type = PresentationBlockTypes.Bullets },
            new PresentationContentBlock { Type = PresentationBlockTypes.Columns },
            new PresentationContentBlock { Type = PresentationBlockTypes.Table },
            new PresentationContentBlock { Type = PresentationBlockTypes.Metrics },
            new PresentationContentBlock { Type = PresentationBlockTypes.Quote },
        };

        foreach (var block in blocks)
            Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(DraftWith(block), new PresentationGenerationOptions()));
    }

    [Fact]
    public void Draft_validator_rejects_null_nested_collections_and_unbounded_nested_text()
    {
        var options = new PresentationGenerationOptions { MaxBlockCharacters = 8 };
        var nullItems = new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = "Safe", Items = null! };
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(DraftWith(nullItems), options));

        var oversizedBlocks = new[]
        {
            new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["123456789"] },
            new PresentationContentBlock { Type = PresentationBlockTypes.Columns, Columns = [new PresentationColumn { Heading = "Heading", Items = ["123456789"] }] },
            new PresentationContentBlock { Type = PresentationBlockTypes.Table, Rows = [new PresentationTableRow { Cells = ["123456789"] }] },
            new PresentationContentBlock { Type = PresentationBlockTypes.Metrics, Metrics = [new PresentationMetric { Label = "Label", Value = "123456789" }] },
        };

        foreach (var block in oversizedBlocks)
            Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(DraftWith(block), options));

        var oversizedSourceRef = DraftWith(new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = "Safe" });
        oversizedSourceRef.Slides[0].SourceRefs = ["123456789"];
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(oversizedSourceRef, options));
    }

    [Fact]
    public void Renderer_creates_editable_16_by_9_pptx_with_rtl_markers_and_expected_slides()
    {
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "پێشکەشکردن", "Plan", "general", "short", "professional", "ku", null, null, null, true, true, []);
        var draft = new PresentationDraft
        {
            Title = "پێشکەشکردن",
            Subtitle = "پوختە",
            Language = "ku",
            PresentationType = "general",
            Slides = [
                new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Title, Title = "پێشکەشکردن", Subtitle = "دەستپێک" },
                new PresentationSlide { Order = 2, Type = PresentationSlideTypes.Bullets, Title = "خاڵە سەرەکییەکان", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["یەکەم", "دووەم"] }] },
                new PresentationSlide { Order = 3, Type = PresentationSlideTypes.Table, Title = "خشتە", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Table, Rows = [new PresentationTableRow { Cells = ["ناو", "بڕ"] }, new PresentationTableRow { Cells = ["تاقیکردنەوە", "بێ داتا"] }] }] },
            ],
        };
        PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
        var rendered = new PresentationRenderer().Render(draft, input, new PresentationGenerationOptions());
        PresentationOutputIntegrityValidator.Validate(rendered);
        Assert.Equal(AssetRepresentationTypes.Pptx, rendered.RepresentationType);
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", rendered.ContentType);
        Assert.Equal("PK", Encoding.ASCII.GetString(rendered.Content, 0, 2));
        using var archive = new ZipArchive(new MemoryStream(rendered.Content), ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "ppt/presentation.xml");
        Assert.Equal(3, archive.Entries.Count(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)));
        foreach (var slideEntry in archive.Entries.Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)))
            XDocument.Parse(new StreamReader(slideEntry.Open()).ReadToEnd());
        var slideXml = new StreamReader(archive.GetEntry("ppt/slides/slide2.xml")!.Open()).ReadToEnd();
        Assert.Contains("rtl=\"1\"", slideXml, StringComparison.Ordinal);
        Assert.Contains("Noto Sans Arabic", slideXml, StringComparison.Ordinal);
        Assert.Contains("<a:tbl>", new StreamReader(archive.GetEntry("ppt/slides/slide3.xml")!.Open()).ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_non_zip_content()
    {
        var rendered = new RenderedPresentation(
            AssetRepresentationTypes.Pptx,
            "presentation.pptx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            Encoding.UTF8.GetBytes("not a zip archive"));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(() => PresentationOutputIntegrityValidator.Validate(rendered));

        Assert.Contains("could not be read safely", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_missing_slide_parts()
    {
        var rendered = RenderValidPresentation();
        var corrupted = RewriteArchive(rendered, "ppt/slides/slide1.xml", null);

        var exception = Assert.Throws<PresentationOutputIntegrityException>(() => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("incomplete or non-contiguous slide set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_invalid_xml_parts()
    {
        var rendered = RenderValidPresentation();
        var corrupted = RewriteArchive(rendered, null, "<p:sld>");

        var exception = Assert.Throws<PresentationOutputIntegrityException>(() => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("invalid XML", exception.Message, StringComparison.Ordinal);
    }

    private static PresentationDraft DraftWith(PresentationContentBlock block) => new()
    {
        Title = "Launch",
        Language = "en",
        PresentationType = "general",
        Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = "Overview", Blocks = [block] }],
    };

    private static RenderedPresentation RenderValidPresentation()
    {
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "Launch", "Plan", "general", "short", "professional", "en", null, null, null, true, true, []);
        var draft = new PresentationDraft
        {
            Title = "Launch",
            Subtitle = "Plan",
            Language = "en",
            PresentationType = "general",
            Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Title, Title = "Launch" }],
        };
        return new PresentationRenderer().Render(draft, input, new PresentationGenerationOptions());
    }

    private static RenderedPresentation RewriteArchive(RenderedPresentation rendered, string? partToOmit, string? replacement)
    {
        using var sourceStream = new MemoryStream(rendered.Content);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        using var outputStream = new MemoryStream();
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (partToOmit is not null && entry.FullName == partToOmit) continue;
                var destination = output.CreateEntry(entry.FullName);
                using var destinationStream = destination.Open();
                if (replacement is not null && entry.FullName == "ppt/slides/slide1.xml")
                {
                    using var writer = new StreamWriter(destinationStream, Encoding.UTF8, leaveOpen: false);
                    writer.Write(replacement);
                }
                else
                {
                    using var sourceEntryStream = entry.Open();
                    sourceEntryStream.CopyTo(destinationStream);
                }
            }
        }

        return rendered with { Content = outputStream.ToArray() };
    }
}
