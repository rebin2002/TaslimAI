using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationGenerationOptionsTests
{
    [Fact]
    public void Default_block_limit_matches_the_renderer_capacity()
    {
        var options = new PresentationGenerationOptions();

        Assert.Equal(PresentationRendererLimits.MaxBlocksPerSlide, options.MaxBlocksPerSlide);
        Assert.Equal(4, options.MaxBlocksPerSlide);
        Assert.True(PresentationGenerationOptionsValidatorResult(options).Succeeded);
    }

    [Fact]
    public void Configuration_above_renderer_capacity_fails_closed()
    {
        var options = new PresentationGenerationOptions { MaxBlocksPerSlide = PresentationRendererLimits.MaxBlocksPerSlide + 1 };

        var result = PresentationGenerationOptionsValidatorResult(options);

        Assert.False(result.Succeeded);
        Assert.Contains("MaxBlocksPerSlide", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_rows_above_renderer_capacity_fail_closed()
    {
        var options = new PresentationGenerationOptions { MaxRowsPerBlock = PresentationRendererLimits.MaxRowsPerBlock + 1 };

        var result = PresentationGenerationOptionsValidatorResult(options);

        Assert.False(result.Succeeded);
        Assert.Contains("MaxRowsPerBlock", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Drafts_above_renderer_capacity_are_rejected_before_rendering()
    {
        var options = new PresentationGenerationOptions();
        var draft = new PresentationDraft
        {
            Title = "Launch",
            Language = "en",
            PresentationType = "general",
            Slides =
            [
                new PresentationSlide
                {
                    Order = 1,
                    Type = PresentationSlideTypes.Content,
                    Title = "Plan",
                    Blocks = Enumerable.Range(1, PresentationRendererLimits.MaxBlocksPerSlide + 1)
                        .Select(index => new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = $"Block {index}" })
                        .ToList(),
                },
            ],
        };

        Assert.Throws<PresentationOutputValidationException>(() => PresentationDraftValidator.Validate(draft, options));
    }

    private static (bool Succeeded, string FailureMessage) PresentationGenerationOptionsValidatorResult(PresentationGenerationOptions options)
    {
        var result = new PresentationGenerationOptionsValidator().Validate(Options.DefaultName, options);
        return (result.Succeeded, result.FailureMessage ?? string.Empty);
    }
}
