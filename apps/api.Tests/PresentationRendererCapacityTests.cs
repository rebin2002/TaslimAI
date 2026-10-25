using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationRendererCapacityTests
{
    [Fact]
    public void Renderer_rejects_columns_that_would_be_dropped()
    {
        var block = new PresentationContentBlock
        {
            Type = PresentationBlockTypes.Columns,
            Columns = Enumerable.Range(1, PresentationRendererLimits.MaxColumnsPerBlock + 1)
                .Select(index => new PresentationColumn { Heading = $"Column {index}", Items = [$"Value {index}"] })
                .ToList(),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Render(block));

        Assert.Contains("columns", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_rejects_metrics_that_would_be_dropped()
    {
        var block = new PresentationContentBlock
        {
            Type = PresentationBlockTypes.Metrics,
            Metrics = Enumerable.Range(1, PresentationRendererLimits.MaxMetricsPerBlock + 1)
                .Select(index => new PresentationMetric { Label = $"Metric {index}", Value = index.ToString() })
                .ToList(),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Render(block));

        Assert.Contains("metrics", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_rejects_table_rows_above_the_configured_safe_capacity()
    {
        var block = new PresentationContentBlock
        {
            Type = PresentationBlockTypes.Table,
            Rows = Enumerable.Range(1, PresentationRendererLimits.MaxRowsPerBlock + 1)
                .Select(index => new PresentationTableRow { Cells = [$"Row {index}"] })
                .ToList(),
        };

        var options = new PresentationGenerationOptions { MaxRowsPerBlock = PresentationRendererLimits.MaxRowsPerBlock + 1 };
        var exception = Assert.Throws<InvalidOperationException>(() => Render(block, options));

        Assert.Contains("rows", exception.Message, StringComparison.Ordinal);
    }

    private static RenderedPresentation Render(PresentationContentBlock block, PresentationGenerationOptions? options = null)
    {
        var input = new PresentationGenerationInput(Guid.NewGuid(), null, "Launch", "Plan", "general", "short", "professional", "en", null, null, null, true, true, []);
        var draft = new PresentationDraft
        {
            Title = "Launch",
            Language = "en",
            PresentationType = "general",
            Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = "Plan", Blocks = [block] }],
        };
        return new PresentationRenderer().Render(draft, input, options ?? new PresentationGenerationOptions());
    }
}
