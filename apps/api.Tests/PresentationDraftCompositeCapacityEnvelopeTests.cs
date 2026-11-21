using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Capacity parity for a composite draft. The single-block boundary sweeps pin each collection
/// edge in isolation, but a real provider answer is a deck: many slides, the maximum number of
/// blocks per slide, and usually exactly one block that is over capacity. Nothing in the isolated
/// shape proves that the over-capacity block is still caught when it is buried among blocks the
/// renderer likes, and on a slide other than the first.
///
/// Each case below asserts three things about that deck:
/// 1. <see cref="PresentationDraftValidator"/> refuses it with the contract exception itself, at
///    every offending position, so the handler reports draft_validation /
///    PRESENTATION_OUTPUT_INVALID instead of letting the paid draft reach
///    <see cref="PresentationRenderer"/>, where the same defect would be reported as
///    pptx_render / PRESENTATION_RENDER_FAILED;
/// 2. refusing the deck is not over-rejection - swapping the one over-capacity block for its
///    at-capacity twin makes the same deck validate, render, and pass output integrity;
/// 3. the refusal is a stable envelope - deterministic, side-effect free, and typed. A draft
///    defect must surface as <see cref="PresentationOutputValidationException"/>; anything else
///    (a null reference, an index out of range) would be attributed to the job rather than to the
///    provider output, which is the misattribution this behaviour exists to prevent.
///
/// Provider-free and in-process: no provider is enabled or called, no database, no file IO.
/// </summary>
public sealed class PresentationDraftCompositeCapacityEnvelopeTests
{
    // A deck at the renderer's own slide/block shape: every slide carries MaxBlocksPerSlide blocks.
    private const int SlideCount = PresentationRendererLimits.MaxBlocksPerSlide;
    private const int BlocksPerSlide = PresentationRendererLimits.MaxBlocksPerSlide;

    [Theory]
    // Offending column block at the front, at the end, and in between.
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 3)]
    public void Composite_draft_with_one_over_capacity_columns_block_is_refused_by_validation_not_by_render(int offendingSlide, int offendingBlock)
    {
        var draft = Deck(offendingSlide, offendingBlock, ColumnsBlock(PresentationRendererLimits.MaxColumnsPerBlock + 1));

        // The refusal must come from the gate that runs before rendering, and must be the
        // contract exception itself rather than an incidental fault from walking the deck.
        var failure = Record.Exception(() => PresentationDraftValidator.Validate(draft, Options()));
        Assert.IsType<PresentationOutputValidationException>(failure);

        // The renderer would still have refused the very same draft, which is exactly why
        // validation has to be the first boundary to answer.
        var rendererRefusal = Record.Exception(() => new PresentationRenderer().Render(draft, Input(), Options()));
        var invalidOperation = Assert.IsType<InvalidOperationException>(rendererRefusal);
        Assert.Contains("columns", invalidOperation.Message, StringComparison.Ordinal);

        // Attribution: this draft fails as invalid provider output, not as a broken renderer.
        Assert.Equal(GenerationJobErrorCodes.PresentationOutputInvalid, PresentationGenerationFailureCodes.ForStage(PresentationGenerationStages.DraftValidation));
        Assert.NotEqual(GenerationJobErrorCodes.PresentationRenderFailed, PresentationGenerationFailureCodes.ForStage(PresentationGenerationStages.DraftValidation));

        // And it is the one block that is wrong, not the deck shape: the same deck with that
        // block at renderer capacity validates, renders, and passes output integrity.
        AssertDeckIsValid(Deck(offendingSlide, offendingBlock, ColumnsBlock(PresentationRendererLimits.MaxColumnsPerBlock)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    public void Composite_draft_with_one_over_capacity_metrics_block_is_refused_by_validation_not_by_render(int offendingSlide, int offendingBlock)
    {
        var draft = Deck(offendingSlide, offendingBlock, MetricsBlock(PresentationRendererLimits.MaxMetricsPerBlock + 1));

        var failure = Record.Exception(() => PresentationDraftValidator.Validate(draft, Options()));
        Assert.IsType<PresentationOutputValidationException>(failure);

        var invalidOperation = Assert.IsType<InvalidOperationException>(Record.Exception(() => new PresentationRenderer().Render(draft, Input(), Options())));
        Assert.Contains("metrics", invalidOperation.Message, StringComparison.Ordinal);

        AssertDeckIsValid(Deck(offendingSlide, offendingBlock, MetricsBlock(PresentationRendererLimits.MaxMetricsPerBlock)));
    }

    [Theory]
    // Both dimensions over capacity at once, and one of them inside a slide that also holds
    // a fully supported sibling block: the first refusal seen by the handler stays the
    // validation one, and the outcome cannot depend on how many defects the draft carries.
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    public void Composite_draft_with_several_over_capacity_blocks_fails_closed_on_the_first_gate(int offendingSlide, int offendingBlock)
    {
        var draft = Deck(offendingSlide, offendingBlock, ColumnsBlock(PresentationRendererLimits.MaxColumnsPerBlock + 5));
        draft.Slides[offendingSlide].Blocks[(offendingBlock + 1) % BlocksPerSlide] = MetricsBlock(PresentationRendererLimits.MaxMetricsPerBlock + 4);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = Record.Exception(() => PresentationDraftValidator.Validate(draft, Options()));
            Assert.IsType<PresentationOutputValidationException>(failure);
        }

        // The renderer is never reached in the handler, so its refusal must not be the
        // boundary this draft is judged by either.
        Assert.IsType<InvalidOperationException>(Record.Exception(() => new PresentationRenderer().Render(draft, Input(), Options())));
    }

    [Fact]
    public void Refusing_an_over_capacity_deck_is_deterministic_and_leaves_the_draft_untouched()
    {
        var draft = Deck(1, 1, ColumnsBlock(PresentationRendererLimits.MaxColumnsPerBlock + 1));
        var before = Fingerprint(draft);
        var options = Options();

        // Validation is a pure gate: repeated admission of the same draft gives the same answer,
        // so a retried job cannot drift between stages, and the rejected draft is preserved
        // exactly as the provider produced it for diagnosis rather than half-normalised.
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.IsType<PresentationOutputValidationException>(Record.Exception(() => PresentationDraftValidator.Validate(draft, options)));

        Assert.Equal(before, Fingerprint(draft));

        var accepted = Record.Exception(() => PresentationDraftValidator.Validate(Deck(1, 1, ColumnsBlock(PresentationRendererLimits.MaxColumnsPerBlock)), options));
        Assert.Null(accepted);
    }

    [Fact]
    public void Composite_draft_using_every_supported_capacity_renders_the_same_bytes_twice()
    {
        // Tightening the gate may not cost the success path: a deck built entirely at the
        // renderer limits still has to be admitted and drawn, and drawing it twice from one
        // renderer instance must not leak layout state between passes.
        var draft = Deck(0, 0, ColumnsBlock(PresentationRendererLimits.MaxColumnsPerBlock));
        PresentationDraftValidator.Validate(draft, Options());

        var renderer = new PresentationRenderer();
        var first = renderer.Render(draft, Input(), Options());
        var second = renderer.Render(draft, Input(), Options());

        PresentationOutputIntegrityValidator.Validate(first);
        PresentationOutputIntegrityValidator.Validate(second);
        Assert.Equal(first.Content, second.Content);
        Assert.True(first.Content.Length > 100);
    }

    private static void AssertDeckIsValid(PresentationDraft draft)
    {
        var options = Options();
        PresentationDraftValidator.Validate(draft, options);
        var rendered = new PresentationRenderer().Render(draft, Input(), options);
        PresentationOutputIntegrityValidator.Validate(rendered);
        Assert.True(rendered.Content.Length > 100);
    }

    /// <summary>
    /// A deck whose blocks are all exactly at renderer capacity, except the one at
    /// <paramref name="offendingSlide"/> / <paramref name="offendingBlock"/>.
    /// </summary>
    private static PresentationDraft Deck(int offendingSlide, int offendingBlock, PresentationContentBlock offending)
    {
        PresentationContentBlock[] supported =
        [
            BulletsBlock(),
            TableBlock(),
            ColumnsBlock(PresentationRendererLimits.MaxColumnsPerBlock),
            MetricsBlock(PresentationRendererLimits.MaxMetricsPerBlock),
        ];

        var slides = new List<PresentationSlide>();
        for (var slide = 0; slide < SlideCount; slide++)
        {
            var blocks = new List<PresentationContentBlock>();
            for (var block = 0; block < BlocksPerSlide; block++)
                blocks.Add(slide == offendingSlide && block == offendingBlock
                    ? offending
                    : supported[(slide * BlocksPerSlide + block) % supported.Length]);
            slides.Add(new PresentationSlide
            {
                Order = slide + 1,
                Type = PresentationSlideTypes.Content,
                Title = $"Slide {slide + 1}",
                Blocks = blocks,
            });
        }

        return new PresentationDraft
        {
            Title = "Quarterly review",
            Language = "en",
            PresentationType = "business",
            Slides = slides,
        };
    }

    private static PresentationContentBlock ColumnsBlock(int count) => new()
    {
        Type = PresentationBlockTypes.Columns,
        Columns = Enumerable.Range(1, count)
            .Select(index => new PresentationColumn
            {
                Heading = $"Column {index}",
                Items = Enumerable.Range(1, Options().MaxItemsPerBlock).Select(item => $"Point {index}.{item}").ToList(),
            })
            .ToList(),
    };

    private static PresentationContentBlock MetricsBlock(int count) => new()
    {
        Type = PresentationBlockTypes.Metrics,
        Metrics = Enumerable.Range(1, count)
            .Select(index => new PresentationMetric { Label = $"Metric {index}", Value = index.ToString(), Detail = $"Context {index}" })
            .ToList(),
    };

    private static PresentationContentBlock TableBlock() => new()
    {
        Type = PresentationBlockTypes.Table,
        Rows = Enumerable.Range(1, Options().MaxRowsPerBlock)
            .Select(index => new PresentationTableRow { Cells = Enumerable.Range(1, 4).Select(cell => $"R{index}C{cell}").ToList() })
            .ToList(),
    };

    private static PresentationContentBlock BulletsBlock() => new()
    {
        Type = PresentationBlockTypes.Bullets,
        Items = Enumerable.Range(1, Options().MaxItemsPerBlock).Select(index => $"Item {index}").ToList(),
    };

    private static string Fingerprint(PresentationDraft draft) => string.Join("|", draft.Slides.Select(slide =>
        $"{slide.Order}:{slide.Type}:{slide.Title}:{slide.Blocks.Count}["
        + string.Join(",", slide.Blocks.Select(block => $"{block.Type}/{block.Items.Count}/{block.Columns.Count}/{block.Rows.Count}/{block.Metrics.Count}"))
        + "]"));

    private static PresentationGenerationOptions Options() => new();

    private static PresentationGenerationInput Input() => new(Guid.NewGuid(), null, "Quarterly review", "Review the quarter", "business", "standard", "professional", "en", null, null, null, true, true, []);
}
