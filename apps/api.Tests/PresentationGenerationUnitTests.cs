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
}
