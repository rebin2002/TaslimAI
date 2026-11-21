using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Documents;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Document Studio reads its completion preview back from <see cref="GenerationJob.ResultJson"/>,
/// whose mapped column is bounded. A canonical draft is validated by section, block and character
/// caps that are far larger than that column, so an unbounded preview overflowed the completion
/// write and the job failed with DOCUMENT_STORAGE_FAILED after the DOCX and PDF had already been
/// rendered and stored. These tests pin the bound provider-free.
/// </summary>
public sealed class DocumentPreviewResultBoundTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ResultJsonBound_matches_the_persisted_job_column()
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new TaslimDbContext(options);
        var column = db.Model.FindEntityType(typeof(GenerationJob))!.FindProperty(nameof(GenerationJob.ResultJson))!;

        Assert.Equal(DocumentGenerationContractMapper.MaxResultJsonCharacters, column.GetMaxLength());
    }

    [Fact]
    public void Valid_oversized_draft_preview_is_bounded_to_the_persisted_job_column()
    {
        // 20 sections x 8 blocks x the maximum block size is accepted by the draft validator, but
        // serializes to roughly 1.3 million characters - over thirteen times the result column.
        var draft = BuildDraft(sections: 20, blocksPerSection: 8, blockCharacters: 8_000);
        DocumentDraftValidator.Validate(draft, new DocumentGenerationOptions());

        var json = DocumentGenerationContractMapper.SerializeResult(
            AssetTypes.Document, draft.Title, "en", draft.Summary, draft.Sections);

        Assert.True(json.Length <= DocumentGenerationContractMapper.MaxResultJsonCharacters,
            $"Preview exceeds the persisted column: {json.Length} characters.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("previewBounded").GetBoolean());
        Assert.Equal("document", root.GetProperty("documentType").GetString());
    }

    [Fact]
    public void Bounded_preview_still_exposes_the_fields_the_completion_screen_reads_back()
    {
        var draft = BuildDraft(sections: 20, blocksPerSection: 8, blockCharacters: 8_000);
        var json = DocumentGenerationContractMapper.SerializeResult(
            AssetTypes.Document, draft.Title, "en", draft.Summary, draft.Sections);

        var result = JsonSerializer.Deserialize<DocumentPreview>(json, WebJson);
        Assert.NotNull(result);
        Assert.Equal("document", result.DocumentType);
        Assert.Equal(draft.Title, result.Title);
        Assert.Equal("en", result.Language);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        Assert.True(result.Summary!.Length <= draft.Summary.Length);

        // The reader renders canonical sections only; a bounded preview keeps whole sections in
        // order and never emits a partially rebuilt block.
        Assert.NotEmpty(result.Sections);
        Assert.True(result.Sections.Count <= 20);
        foreach (var section in result.Sections)
        {
            Assert.Equal(section.Heading, draft.Sections.First(item => item.Heading == section.Heading).Heading);
            Assert.NotEmpty(section.Blocks);
            foreach (var block in section.Blocks)
            {
                Assert.Equal(DocumentBlockTypes.Paragraph, block.Type);
                Assert.NotNull(block.Text);
                Assert.True(block.Text!.Length <= 8_000);
            }
        }
    }

    [Fact]
    public void Bound_holds_for_right_to_left_drafts_whose_text_expands_when_serialized()
    {
        // Sorani content is escaped as \uXXXX by the serializer, so the payload grows beyond its
        // source length. The bound must be measured on the serialized result.
        var draft = BuildDraft(sections: 24, blocksPerSection: 6, blockCharacters: 700, rtl: true);
        DocumentDraftValidator.Validate(draft, new DocumentGenerationOptions());

        var json = DocumentGenerationContractMapper.SerializeResult(
            AssetTypes.Document, draft.Title, "ku", draft.Summary, draft.Sections);

        Assert.True(json.Length <= DocumentGenerationContractMapper.MaxResultJsonCharacters,
            $"RTL preview exceeds the persisted column: {json.Length} characters.");
    }

    [Fact]
    public void Complete_preview_keeps_the_existing_result_shape_without_a_bounded_marker()
    {
        var draft = BuildDraft(sections: 2, blocksPerSection: 2, blockCharacters: 40);
        var json = DocumentGenerationContractMapper.SerializeResult(
            AssetTypes.Document, draft.Title, "en", draft.Summary, draft.Sections);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("previewBounded", out _));
        Assert.Equal(draft.Sections.Count, root.GetProperty("sections").GetArrayLength());
        Assert.Equal(
            draft.Sections[0].Blocks[0].Text,
            root.GetProperty("sections")[0].GetProperty("blocks")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Preview_floor_survives_a_title_that_fills_the_result_envelope()
    {
        var draft = BuildDraft(sections: 24, blocksPerSection: 6, blockCharacters: 8_000);
        var longTitle = new string('T', 255);
        DocumentDraftValidator.Validate(draft, new DocumentGenerationOptions());

        var json = DocumentGenerationContractMapper.SerializeResult(
            AssetTypes.Document, longTitle, "en", draft.Summary, draft.Sections);

        Assert.True(json.Length <= DocumentGenerationContractMapper.MaxResultJsonCharacters);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(longTitle, document.RootElement.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("sections").ValueKind);
    }

    private static DocumentDraft BuildDraft(int sections, int blocksPerSection, int blockCharacters, bool rtl = false)
    {
        var body = rtl ? new string('ک', blockCharacters) : new string('x', blockCharacters);
        return new DocumentDraft
        {
            Title = "Launch brief",
            Summary = new string('S', 2_000),
            Sections = Enumerable.Range(0, sections).Select(sectionIndex => new DocumentSection
            {
                Heading = $"Section {sectionIndex}",
                Blocks = Enumerable.Range(0, blocksPerSection).Select(blockIndex => new DocumentBlock
                {
                    Type = DocumentBlockTypes.Paragraph,
                    Text = blockIndex == 0 ? body : body[..Math.Min(body.Length, 4_000)],
                }).ToList(),
            }).ToList(),
        };
    }

    private sealed record DocumentPreview(
        string DocumentType,
        string Title,
        string Language,
        string? Summary,
        List<PreviewSection> Sections);

    private sealed record PreviewSection(string Heading, List<PreviewBlock> Blocks);

    private sealed record PreviewBlock(string Type, string? Text, List<string>? Items, List<PreviewRow>? Rows);

    private sealed record PreviewRow(List<string> Cells);
}
