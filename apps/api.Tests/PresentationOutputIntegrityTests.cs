using System.IO.Compression;
using System.Text;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationOutputIntegrityTests
{
    [Fact]
    public void Output_integrity_validator_rejects_unresolved_presentation_slide_reference()
    {
        var rendered = RenderValidPresentation();
        var corrupted = RewriteArchive(rendered, "ppt/presentation.xml", content => content.Replace("r:id=\"rId2\"", "r:id=\"rId999\"", StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(() => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("rId999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_presentation_relationship_with_wrong_type()
    {
        var rendered = RenderValidPresentation();
        var corrupted = RewriteArchive(rendered, "ppt/_rels/presentation.xml.rels", content => content.Replace(
            "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\"",
            "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide\"",
            StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(() => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("slide1.xml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_integrity_validator_rejects_unresolved_slide_master_layout_reference()
    {
        var rendered = RenderValidPresentation();
        var corrupted = RewriteArchive(rendered, "ppt/slideMasters/slideMaster1.xml", content => content.Replace("r:id=\"rId2\"", "r:id=\"rId999\"", StringComparison.Ordinal));

        var exception = Assert.Throws<PresentationOutputIntegrityException>(() => PresentationOutputIntegrityValidator.Validate(corrupted));

        Assert.Contains("rId999", exception.Message, StringComparison.Ordinal);
    }

    private static RenderedPresentation RenderValidPresentation()
    {
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "Launch", "Plan", "general", "short", "professional", "en", null, null, null, true, true, []);
        var draft = new PresentationDraft
        {
            Title = "Launch",
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

    private static RenderedPresentation RewriteArchive(RenderedPresentation rendered, string part, Func<string, string> rewrite)
    {
        using var sourceStream = new MemoryStream(rendered.Content);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        using var outputStream = new MemoryStream();
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var destination = output.CreateEntry(entry.FullName);
                using var destinationStream = destination.Open();
                using var sourceEntryStream = entry.Open();
                if (entry.FullName == part)
                {
                    using var reader = new StreamReader(sourceEntryStream, Encoding.UTF8);
                    using var writer = new StreamWriter(destinationStream, new UTF8Encoding(false));
                    writer.Write(rewrite(reader.ReadToEnd()));
                }
                else
                {
                    sourceEntryStream.CopyTo(destinationStream);
                }
            }
        }
        return rendered with { Content = outputStream.ToArray() };
    }
}
