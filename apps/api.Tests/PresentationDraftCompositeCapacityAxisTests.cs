using System.IO.Compression;
using System.Text.RegularExpressions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Capacity parity for the composite axes the single-block sweeps and the mixed-deck envelope
/// leave open. A provider deck is judged by one rule: <see cref="PresentationDraftValidator"/>
/// must refuse every draft <see cref="PresentationRenderer"/> cannot draw, so the job is reported
/// at draft_validation / PRESENTATION_OUTPUT_INVALID instead of pptx_render /
/// PRESENTATION_RENDER_FAILED. Columns, metrics, rows and slide-level blocks obey that rule
/// because the renderer throws when it meets them.
///
/// Table cell width is different. The renderer never refuses it: <c>RenderTable</c> clamps the
/// grid to eight columns (<see cref="MaxTableCells"/>) and drops whatever the provider put in
/// column nine and beyond. The package that comes out is well formed and passes
/// <see cref="PresentationOutputIntegrityValidator"/> - it is simply missing provider data, and
/// nothing downstream can tell. On that axis the draft validator is not merely the first
/// boundary, it is the only one, so its <c>row.Cells.Count &gt; 8</c> check is load bearing in a
/// way the other checks are not. These tests pin it, and pin the duplicated literal that keeps
/// the two sides honest, so neither half of the pair can be edited alone.
///
/// Everything here is deterministic, provider-free and in-process: no provider is enabled or
/// called, no database, no file IO, no paid round-trip, and no rendering geometry is changed.
/// </summary>
public sealed class PresentationDraftCompositeCapacityAxisTests
{
    // The renderer clamps a table to this many grid columns and the validator refuses above it.
    // The two literals live in different files (PresentationGenerationContracts and
    // PresentationRenderers), so the shared value is named once here.
    private const int MaxTableCells = 8;
    private const int SlideCount = 4;
    private const int BlocksPerSlide = PresentationRendererLimits.MaxBlocksPerSlide;

    // Cell text carries the block it came from, so a slide holding more than one table can still
    // be read unambiguously: O = the one over-capacity block, S = a filler at capacity.
    private const string OffendingTag = "O";
    private const string SupportedTag = "S";

    private const string ColumnsAxis = "columns";
    private const string MetricsAxis = "metrics";
    private const string RowsAxis = "rows";
    private const string CellsAxis = "cells";

    [Theory]
    // The offending table buried among blocks the renderer likes, on slides other than the first.
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 3)]
    public void Composite_draft_with_one_over_cell_width_table_is_refused_by_validation_and_by_nothing_else(int offendingSlide, int offendingBlock)
    {
        var draft = Deck(offendingSlide, offendingBlock, Table(OffendingTag, 2, MaxTableCells + 1));

        // Gate one: validation refuses the deck. This is the behaviour under test.
        var failure = Record.Exception(() => PresentationDraftValidator.Validate(draft, Options()));
        Assert.IsType<PresentationOutputValidationException>(failure);

        // Gate two: the renderer does NOT refuse it. That asymmetry is the whole point - the
        // columns, metrics and rows axes have a second boundary to fall back on and this one does
        // not, so widening the cell bound would ship a deck with lost provider data instead of
        // failing the job.
        var renderer = Record.Exception(() => new PresentationRenderer().Render(draft, Input(), Options()));
        Assert.Null(renderer);

        // And the loss is real: column nine is clamped out of the grid and never reaches the
        // slide, while column eight still does.
        Assert.False(CellSurvives(draft, offendingSlide, marker: 9), "the over-capacity cell survived, so the renderer no longer clamps and this test's premise changed");
        Assert.True(CellSurvives(draft, offendingSlide, marker: 8));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(12)]
    public void Over_cell_width_table_reaches_the_asset_silently_when_the_draft_gate_is_bypassed(int columns)
    {
        // What the guard exists to prevent, shown end to end without a provider: the same draft
        // the validator refuses renders to a package that passes output integrity. Integrity can
        // prove the file is well formed; it cannot prove the file is complete.
        var draft = Deck(1, 2, Table(OffendingTag, 2, columns));
        var rendered = new PresentationRenderer().Render(draft, Input(), Options());

        PresentationOutputIntegrityValidator.Validate(rendered);
        Assert.True(rendered.Content.Length > 100);
        // The grid is the requested width clamped to capacity, and RenderTable pads every row to
        // that width, so the cell count that reaches the file is rows x min(requested, capacity).
        var drawnColumns = Math.Min(columns, MaxTableCells);
        Assert.Equal(drawnColumns, GridColumns(rendered, 1, OffendingTag));
        Assert.Equal(2 * drawnColumns, CellCount(rendered, 1, OffendingTag));
        for (var marker = MaxTableCells + 1; marker <= columns; marker++)
            Assert.False(CellSurvives(rendered, 1, marker, OffendingTag), $"column {marker} should have been clamped away");
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    public void Composite_draft_with_one_over_row_table_is_refused_before_the_renderer_is_blamed(int offendingSlide, int offendingBlock)
    {
        var draft = Deck(offendingSlide, offendingBlock, Table(OffendingTag, PresentationRendererLimits.MaxRowsPerBlock + 1, 3));

        Assert.IsType<PresentationOutputValidationException>(Record.Exception(() => PresentationDraftValidator.Validate(draft, Options())));

        // Rows do have a second boundary, so the ordering claim - not the existence of one - is
        // what this pins: validation answers first and names it invalid provider output.
        var invalidOperation = Assert.IsType<InvalidOperationException>(Record.Exception(() => new PresentationRenderer().Render(draft, Input(), Options())));
        Assert.Contains("rows", invalidOperation.Message, StringComparison.Ordinal);
        Assert.Equal(GenerationJobErrorCodes.PresentationOutputInvalid, PresentationGenerationFailureCodes.ForStage(PresentationGenerationStages.DraftValidation));
        Assert.NotEqual(GenerationJobErrorCodes.PresentationRenderFailed, PresentationGenerationFailureCodes.ForStage(PresentationGenerationStages.DraftValidation));

        AssertDeckIsValid(Deck(offendingSlide, offendingBlock, Table(OffendingTag, PresentationRendererLimits.MaxRowsPerBlock, 3)));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    public void Composite_slide_holding_one_block_too_many_is_refused_before_the_renderer_is_blamed(int offendingSlide, int offendingBlock)
    {
        var draft = Deck(offendingSlide, offendingBlock, BulletsBlock());
        draft.Slides[offendingSlide].Blocks.Add(TextBlock("Overflow block"));

        Assert.IsType<PresentationOutputValidationException>(Record.Exception(() => PresentationDraftValidator.Validate(draft, Options())));
        var invalidOperation = Assert.IsType<InvalidOperationException>(Record.Exception(() => new PresentationRenderer().Render(draft, Input(), Options())));
        Assert.Contains("blocks", invalidOperation.Message, StringComparison.Ordinal);

        AssertDeckIsValid(Deck(offendingSlide, offendingBlock, BulletsBlock()));
    }

    [Theory]
    [InlineData(ColumnsAxis)]
    [InlineData(MetricsAxis)]
    [InlineData(RowsAxis)]
    [InlineData(CellsAxis)]
    public void Validator_never_admits_a_draft_the_renderer_cannot_draw(string axis)
    {
        // The one direction that has to hold for every numeric axis: anything admitted has to be
        // drawable. Refusing more than the renderer needs (the cell axis) is safe; admitting more
        // is the paid, misattributed failure this behaviour exists to prevent.
        var capacity = Capacity(axis);
        for (var count = 1; count <= capacity + 2; count++)
        {
            var draft = Deck(1, 1, OffendingBlock(axis, count));
            var accepted = Record.Exception(() => PresentationDraftValidator.Validate(draft, Options())) is null;
            var refusedByRenderer = Record.Exception(() => new PresentationRenderer().Render(draft, Input(), Options())) is InvalidOperationException;

            Assert.False(accepted && refusedByRenderer, $"{axis} count {count} passed draft validation but the renderer could not draw it.");
        }
    }

    [Fact]
    public void Composite_draft_at_every_capacity_survives_the_round_trip_without_loss()
    {
        // The guard must not cost the success path. A deck whose tables, columns, metrics and
        // slide blocks all sit exactly on capacity is admitted, drawn twice from one renderer
        // instance, and keeps every provider cell it was given.
        var draft = Deck(0, 0, Table(OffendingTag, PresentationRendererLimits.MaxRowsPerBlock, MaxTableCells));
        PresentationDraftValidator.Validate(draft, Options());

        var renderer = new PresentationRenderer();
        var first = renderer.Render(draft, Input(), Options());
        var second = renderer.Render(draft, Input(), Options());

        PresentationOutputIntegrityValidator.Validate(first);
        PresentationOutputIntegrityValidator.Validate(second);
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(MaxTableCells, GridColumns(first, 0, OffendingTag));
        for (var marker = 1; marker <= MaxTableCells; marker++)
            Assert.True(CellSurvives(first, 0, marker, OffendingTag), $"column {marker} was dropped at capacity");
        // The offending block here is a full-capacity table: MaxRowsPerBlock rows x capacity
        // columns, every one of which has to reach the file.
        Assert.Equal(PresentationRendererLimits.MaxRowsPerBlock * MaxTableCells, CellCount(first, 0, OffendingTag));
    }

    [Fact]
    public void Refusing_a_mixed_capacity_deck_is_deterministic_and_leaves_the_draft_untouched()
    {
        var draft = Deck(2, 1, Table(OffendingTag, 2, MaxTableCells + 1));
        var before = Fingerprint(draft);
        var options = Options();

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.IsType<PresentationOutputValidationException>(Record.Exception(() => PresentationDraftValidator.Validate(draft, options)));

        // The rejected draft is preserved exactly as the provider produced it, so the over-capacity
        // table stays inspectable rather than half-normalised toward the renderer's clamp.
        Assert.Equal(before, Fingerprint(draft));

        var accepted = Record.Exception(() => PresentationDraftValidator.Validate(Deck(2, 1, Table(OffendingTag, 2, MaxTableCells)), options));
        Assert.Null(accepted);
    }

    private static int Capacity(string axis) => axis switch
    {
        ColumnsAxis => PresentationRendererLimits.MaxColumnsPerBlock,
        MetricsAxis => PresentationRendererLimits.MaxMetricsPerBlock,
        RowsAxis => PresentationRendererLimits.MaxRowsPerBlock,
        CellsAxis => MaxTableCells,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown capacity axis."),
    };

    private static PresentationContentBlock OffendingBlock(string axis, int count) => axis switch
    {
        ColumnsAxis => ColumnsBlock(count),
        MetricsAxis => MetricsBlock(count),
        RowsAxis => Table(OffendingTag, count, 3),
        CellsAxis => Table(OffendingTag, 2, count),
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown capacity axis."),
    };

    private static void AssertDeckIsValid(PresentationDraft draft)
    {
        var options = Options();
        PresentationDraftValidator.Validate(draft, options);
        var rendered = new PresentationRenderer().Render(draft, Input(), options);
        PresentationOutputIntegrityValidator.Validate(rendered);
        Assert.True(rendered.Content.Length > 100);
    }

    /// <summary>
    /// A deck whose blocks all sit exactly at renderer capacity except the one at
    /// <paramref name="offendingSlide"/> / <paramref name="offendingBlock"/>. That block lands on
    /// a slide other than the first for part of the sweep, so the gate cannot pass by answering on
    /// slide one alone.
    /// </summary>
    private static PresentationDraft Deck(int offendingSlide, int offendingBlock, PresentationContentBlock offending)
    {
        PresentationContentBlock[] supported =
        [
            BulletsBlock(),
            Table(SupportedTag, PresentationRendererLimits.MaxRowsPerBlock, MaxTableCells),
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
                    : supported[block % supported.Length]);
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

    private static PresentationContentBlock Table(string tag, int rows, int cells) => new()
    {
        Type = PresentationBlockTypes.Table,
        Rows = Enumerable.Range(1, rows)
            .Select(index => new PresentationTableRow { Cells = Enumerable.Range(1, cells).Select(cell => $"{tag}{index}x{cell}").ToList() })
            .ToList(),
    };

    private static PresentationContentBlock BulletsBlock() => new()
    {
        Type = PresentationBlockTypes.Bullets,
        Items = Enumerable.Range(1, Options().MaxItemsPerBlock).Select(index => $"Item {index}").ToList(),
    };

    private static PresentationContentBlock TextBlock(string text) => new()
    {
        Type = PresentationBlockTypes.Text,
        Text = text,
    };

    private static bool CellSurvives(PresentationDraft draft, int slide, int marker, string tag = OffendingTag)
        => CellSurvives(new PresentationRenderer().Render(draft, Input(), Options()), slide, marker, tag);

    private static bool CellSurvives(RenderedPresentation rendered, int slide, int marker, string tag = OffendingTag)
        => OffendingTableXml(SlideXml(rendered, slide), tag).Contains($"{tag}1x{marker}<", StringComparison.Ordinal);

    private static int CellCount(RenderedPresentation rendered, int slide, string tag)
        => Regex.Matches(OffendingTableXml(SlideXml(rendered, slide), tag), "<a:tc>").Count;

    private static int GridColumns(RenderedPresentation rendered, int slide, string tag)
        => Regex.Matches(OffendingTableXml(SlideXml(rendered, slide), tag), "<a:gridCol\\b").Count;

    /// <summary>
    /// A slide can hold more than one table, so the assertions are scoped to the graphic frame
    /// carrying the tagged block rather than the whole slide part.
    /// </summary>
    private static string OffendingTableXml(string slideXml, string tag)
    {
        var frames = Regex.Matches(slideXml, "<p:graphicFrame>.*?</p:graphicFrame>", RegexOptions.Singleline);
        var match = frames.FirstOrDefault(frame => frame.Value.Contains($"{tag}1x1<", StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"no table carrying tag '{tag}' was rendered on the slide; frames seen: {frames.Count}");
        return match.Value;
    }

    private static string SlideXml(RenderedPresentation rendered, int slide)
    {
        using var archive = new ZipArchive(new MemoryStream(rendered.Content, writable: false));
        var entry = archive.GetEntry($"ppt/slides/slide{slide + 1}.xml")
            ?? throw new Xunit.Sdk.XunitException($"slide{slide + 1}.xml is missing from the rendered package.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string Fingerprint(PresentationDraft draft) => string.Join("|", draft.Slides.Select(slide =>
        $"{slide.Order}:{slide.Type}:{slide.Title}:{slide.Blocks.Count}["
        + string.Join(",", slide.Blocks.Select(block => $"{block.Type}/{block.Items.Count}/{block.Columns.Count}/{block.Rows.Count}/{block.Metrics.Count}/{block.Rows.Sum(row => row.Cells.Count)}"))
        + "]"));

    private static PresentationGenerationOptions Options() => new();

    private static PresentationGenerationInput Input() => new(Guid.NewGuid(), null, "Quarterly review", "Review the quarter", "business", "standard", "professional", "en", null, null, null, true, true, []);
}
