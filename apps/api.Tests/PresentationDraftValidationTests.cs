using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationDraftValidationTests
{
    [Fact]
    public void Rejects_contentless_blocks_before_rendering()
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
    public void Rejects_null_nested_collections_and_unbounded_nested_text()
    {
        var options = new PresentationGenerationOptions { MaxBlockCharacters = 8 };
        var nullItems = new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = "Safe", Items = null! };
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(DraftWith(nullItems), options));

        var malformedBlocks = new[]
        {
            new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["123456789"] },
            new PresentationContentBlock { Type = PresentationBlockTypes.Columns, Columns = [new PresentationColumn { Heading = "Heading", Items = ["123456789"] }] },
            new PresentationContentBlock { Type = PresentationBlockTypes.Table, Rows = [new PresentationTableRow { Cells = ["123456789"] }] },
            new PresentationContentBlock { Type = PresentationBlockTypes.Metrics, Metrics = [new PresentationMetric { Label = "Label", Value = "123456789" }] },
        };

        foreach (var block in malformedBlocks)
            Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(DraftWith(block), options));

        var oversizedSourceRef = DraftWith(new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = "Safe" });
        oversizedSourceRef.Slides[0].SourceRefs = ["123456789"];
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(oversizedSourceRef, options));
    }

    [Fact]
    public void Rejects_null_slide_and_nested_elements_from_provider_json()
    {
        var nullSlide = DraftWith(new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = "Safe" });
        nullSlide.Slides = [null!];
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(nullSlide, new PresentationGenerationOptions()));

        var nullColumn = DraftWith(new PresentationContentBlock { Type = PresentationBlockTypes.Columns, Columns = [null!] });
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(nullColumn, new PresentationGenerationOptions()));

        var nullRow = DraftWith(new PresentationContentBlock { Type = PresentationBlockTypes.Table, Rows = [null!] });
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(nullRow, new PresentationGenerationOptions()));

        var nullMetric = DraftWith(new PresentationContentBlock { Type = PresentationBlockTypes.Metrics, Metrics = [null!] });
        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(nullMetric, new PresentationGenerationOptions()));
    }

    private static PresentationDraft DraftWith(PresentationContentBlock block) => new()
    {
        Title = "Launch",
        Language = "en",
        PresentationType = "general",
        Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = "Overview", Blocks = [block] }],
    };
}
