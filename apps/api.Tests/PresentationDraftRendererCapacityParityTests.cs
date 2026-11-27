using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Structural admission and PPTX layout must agree on one capacity vocabulary.
/// <see cref="PresentationDraftValidator"/> is the fail-closed gate that runs on provider
/// output before <see cref="PresentationRenderer"/> is asked to lay the deck out, so every
/// column or metric count it accepts has to be a count the renderer can actually draw.
/// The validator still carries the bounds it had before the renderer capacity guard existed
/// (4 columns, 6 metrics) while <see cref="PresentationRendererLimits"/> allows 3 and 4.
/// A draft inside that gap is charged for provider usage, passes structure validation, and
/// then dies in the render stage with PRESENTATION_RENDER_FAILED instead of
/// PRESENTATION_OUTPUT_INVALID. These regressions pin the two boundaries together at the
/// edges so neither side can drift again.
/// </summary>
public sealed class PresentationDraftRendererCapacityParityTests
{
    [Theory]
    // Every column count up to the renderer capacity must survive validation and render.
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    // One over capacity and further out must be refused by validation itself.
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    public void Draft_validator_admits_exactly_the_column_counts_the_renderer_can_draw(int columnCount, bool rendererSupports)
    {
        var draft = DraftWith(ColumnsBlock(columnCount));

        var validatorAccepted = ValidatorAccepts(draft, out var validationFailure);
        Assert.Equal(rendererSupports, validatorAccepted);

        if (validatorAccepted)
        {
            // Admitted output must render; a validator that admits what the renderer refuses
            // is the drift this regression exists to prevent.
            var rendered = new PresentationRenderer().Render(draft, Input(), new PresentationGenerationOptions());
            PresentationOutputIntegrityValidator.Validate(rendered);
            Assert.True(rendered.Content.Length > 100);
            return;
        }

        Assert.IsType<PresentationOutputValidationException>(validationFailure);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(8, false)]
    public void Draft_validator_admits_exactly_the_metric_counts_the_renderer_can_draw(int metricCount, bool rendererSupports)
    {
        var draft = DraftWith(MetricsBlock(metricCount));

        var validatorAccepted = ValidatorAccepts(draft, out var validationFailure);
        Assert.Equal(rendererSupports, validatorAccepted);

        if (validatorAccepted)
        {
            var rendered = new PresentationRenderer().Render(draft, Input(), new PresentationGenerationOptions());
            PresentationOutputIntegrityValidator.Validate(rendered);
            Assert.True(rendered.Content.Length > 100);
            return;
        }

        Assert.IsType<PresentationOutputValidationException>(validationFailure);
    }

    [Theory]
    [InlineData(PresentationBlockTypes.Columns)]
    [InlineData(PresentationBlockTypes.Metrics)]
    public void Renderer_refusal_is_always_preceded_by_a_validation_refusal(string blockType)
    {
        // Walk the whole capacity range: the renderer must never be the first boundary to
        // reject a draft. Any count where the validator says "yes" and the renderer says "no"
        // is a paid provider round-trip that produces the wrong failure code.
        var upperBound = (blockType == PresentationBlockTypes.Columns
            ? PresentationRendererLimits.MaxColumnsPerBlock
            : PresentationRendererLimits.MaxMetricsPerBlock) + 3;

        for (var count = 1; count <= upperBound; count++)
        {
            var draft = DraftWith(blockType == PresentationBlockTypes.Columns ? ColumnsBlock(count) : MetricsBlock(count));
            var accepted = ValidatorAccepts(draft, out _);
            var refusedByRenderer = Record.Exception(() => new PresentationRenderer().Render(draft, Input(), new PresentationGenerationOptions())) is InvalidOperationException;

            Assert.False(accepted && refusedByRenderer,
                $"{blockType} count {count} passed draft validation but the renderer could not draw it.");
        }
    }

    private static bool ValidatorAccepts(PresentationDraft draft, out Exception? failure)
    {
        try
        {
            PresentationDraftValidator.Validate(draft, new PresentationGenerationOptions());
            failure = null;
            return true;
        }
        catch (PresentationOutputValidationException exception)
        {
            failure = exception;
            return false;
        }
    }

    private static PresentationContentBlock ColumnsBlock(int count) => new()
    {
        Type = PresentationBlockTypes.Columns,
        Columns = Enumerable.Range(1, count)
            .Select(index => new PresentationColumn { Heading = $"Column {index}", Items = [$"Value {index}"] })
            .ToList(),
    };

    private static PresentationContentBlock MetricsBlock(int count) => new()
    {
        Type = PresentationBlockTypes.Metrics,
        Metrics = Enumerable.Range(1, count)
            .Select(index => new PresentationMetric { Label = $"Metric {index}", Value = index.ToString() })
            .ToList(),
    };

    private static PresentationDraft DraftWith(PresentationContentBlock block) => new()
    {
        Title = "Launch",
        Language = "en",
        PresentationType = "general",
        Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Content, Title = "Plan", Blocks = [block] }],
    };

    private static PresentationGenerationInput Input() => new(Guid.NewGuid(), null, "Launch", "Plan", "general", "short", "professional", "en", null, null, null, true, true, []);
}
