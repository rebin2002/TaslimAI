using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Documents;

namespace Taslim.Api.Contracts;

public sealed class DocumentGenerationRequest
{
    [Required]
    public Guid WorkspaceId { get; set; }

    public Guid? ProjectId { get; set; }

    [StringLength(160)]
    public string? Title { get; set; }

    [StringLength(8_000, MinimumLength = 3)]
    public string? Description { get; set; }

    [StringLength(8_000, MinimumLength = 3)]
    public string? Prompt { get; set; }

    [StringLength(40)]
    public string? DocumentType { get; set; }

    [StringLength(20)]
    public string? Length { get; set; }

    [StringLength(400)]
    public string? Audience { get; set; }

    [StringLength(3_000)]
    public string? AdditionalInstructions { get; set; }

    [MaxLength(8)]
    public IReadOnlyList<Guid>? AttachmentIds { get; set; } = [];

    [StringLength(20)]
    public string? Language { get; set; }

    [StringLength(20)]
    public string? OutputFormat { get; set; }

    [StringLength(40)]
    public string? Tone { get; set; }

    public bool IncludeTableOfContents { get; set; } = true;
}

public sealed record DocumentGenerationInput(
    Guid WorkspaceId,
    Guid? ProjectId,
    string Title,
    string Description,
    string DocumentType,
    string Length,
    string? Audience,
    string? AdditionalInstructions,
    IReadOnlyList<Guid> AttachmentIds,
    string Language,
    string OutputFormat,
    string Tone,
    bool IncludeTableOfContents);

public sealed class DocumentDraft
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<DocumentSection> Sections { get; set; } = [];
}

public sealed class DocumentSection
{
    public string Heading { get; set; } = string.Empty;
    public List<DocumentBlock> Blocks { get; set; } = [];
}

public sealed class DocumentBlock
{
    public string Type { get; set; } = "paragraph";
    public string? Text { get; set; }
    public List<string>? Items { get; set; }
    public List<DocumentTableRow>? Rows { get; set; }
}

public sealed class DocumentTableRow
{
    public List<string> Cells { get; set; } = [];
}

public sealed record DocumentGenerationResult(
    Guid AssetId,
    IReadOnlyList<DocumentRepresentationResult> Representations,
    string Title,
    string Language,
    string Summary);

public sealed record DocumentRepresentationResult(
    Guid Id,
    string Type,
    string FileName,
    string ContentType);

public static class DocumentGenerationContractMapper
{
    public static DocumentGenerationInput ToInput(DocumentGenerationRequest request)
    {
        var language = Normalize(request.Language, DocumentGenerationDefaults.DefaultLanguage);
        var format = Normalize(request.OutputFormat, DocumentGenerationDefaults.DefaultOutputFormat);
        var tone = Normalize(request.Tone, DocumentGenerationDefaults.DefaultTone);
        var description = string.IsNullOrWhiteSpace(request.Description) ? request.Prompt?.Trim() ?? string.Empty : request.Description.Trim();
        return new DocumentGenerationInput(
            request.WorkspaceId,
            request.ProjectId,
            string.IsNullOrWhiteSpace(request.Title) ? "Taslim document" : request.Title.Trim(),
            description,
            Normalize(request.DocumentType, DocumentGenerationDefaults.DefaultDocumentType),
            Normalize(request.Length, DocumentGenerationDefaults.DefaultLength),
            NormalizeOptional(request.Audience),
            NormalizeOptional(request.AdditionalInstructions),
            request.AttachmentIds?.Distinct().ToArray() ?? [],
            language,
            format,
            tone,
            request.IncludeTableOfContents);
    }

    public static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    public static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly JsonSerializerOptions ResultJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string SerializeInput(DocumentGenerationInput input) => JsonSerializer.Serialize(input);

    /// <summary>
    /// Character ceiling for the persisted job result. The completion write stores this payload in
    /// <c>GenerationJob.ResultJson</c>, whose mapped column is limited to this length, so an
    /// unbounded canonical preview overflows the write and discards an already rendered document.
    /// </summary>
    public const int MaxResultJsonCharacters = 100_000;

    // Headroom so the measured preview never lands on the column boundary.
    private const int ResultJsonSafetyMargin = 512;

    // The DOCX and PDF artifacts stay complete. This result is only the reading preview the
    // completion screen renders, so it keeps a bounded number of whole sections and blocks.
    private const int PreviewSectionBudget = 12;
    private const int PreviewBlockBudget = 4;

    /// <summary>
    /// Serializes the canonical Document Studio preview alongside the asset references the
    /// completion screen reads back. A preview larger than the persisted column is trimmed to the
    /// largest measured size that still fits, so the job stays succeedable and downloadable instead
    /// of failing at publication.
    /// </summary>
    public static string SerializeResult(string documentType, string title, string language, string summary, IReadOnlyList<DocumentSection> sections)
    {
        var budget = MaxResultJsonCharacters - ResultJsonSafetyMargin;
        var preview = sections ?? [];
        var complete = Write(documentType, title, language, summary ?? string.Empty, preview, bounded: false);
        if (complete.Length <= budget) return complete;

        // Draft text is escaped when serialized, so non-ASCII previews expand beyond their source
        // length. The retained size is therefore chosen by measuring the payload, not by counting
        // source characters. The ceiling is the largest field that trimming can still shorten;
        // scale 0 drops the preview body entirely and is the guaranteed floor.
        var ceiling = new[] { summary?.Length ?? 0 }
            .Concat(preview.SelectMany(section => section.Blocks).Select(BlockLength))
            .DefaultIfEmpty(0)
            .Max();
        var low = 0;
        var high = ceiling;
        var best = Write(documentType, title, language, string.Empty, [], bounded: true);
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = BuildBoundedPreview(documentType, title, language, summary ?? string.Empty, preview, middle);
            if (candidate.Length <= budget)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private static string BuildBoundedPreview(
        string documentType,
        string title,
        string language,
        string summary,
        IReadOnlyList<DocumentSection> sections,
        int scale)
    {
        if (scale <= 0) return Write(documentType, title, language, string.Empty, [], bounded: true);
        var boundedSummary = Truncate(summary, scale);
        if (sections.Count == 0) return Write(documentType, title, language, boundedSummary, sections, bounded: true);

        var kept = new List<DocumentSection>(Math.Min(sections.Count, PreviewSectionBudget));
        foreach (var section in sections.Take(PreviewSectionBudget))
        {
            var blocks = new List<DocumentBlock>();
            foreach (var block in section.Blocks.Take(PreviewBlockBudget))
            {
                blocks.Add(new DocumentBlock
                {
                    Type = block.Type,
                    Text = Truncate(block.Text, scale),
                    Items = block.Items is null ? null : [.. block.Items.Select(item => Truncate(item, scale))],
                    Rows = block.Rows is null
                        ? null
                        : [.. block.Rows.Select(row => new DocumentTableRow { Cells = [.. row.Cells.Select(cell => Truncate(cell, scale))] })],
                });
            }

            kept.Add(new DocumentSection { Heading = section.Heading, Blocks = blocks });
        }

        return Write(documentType, title, language, boundedSummary, kept, bounded: true);
    }

    private static int BlockLength(DocumentBlock block) =>
        new[] { block.Text?.Length ?? 0 }
            .Concat(block.Items is null ? [] : block.Items.Select(item => item?.Length ?? 0))
            .Concat(block.Rows is null ? [] : block.Rows.SelectMany(row => row.Cells).Select(cell => cell?.Length ?? 0))
            .DefaultIfEmpty(0)
            .Max();

    private static string Truncate(string? value, int maximum)
    {
        if (value is null || maximum < 0 || value.Length <= maximum) return value ?? string.Empty;
        var length = maximum;
        // Never split a surrogate pair, which would emit invalid JSON string content.
        if (length > 0 && char.IsHighSurrogate(value[length - 1]) && length < value.Length && char.IsLowSurrogate(value[length]))
            length -= 1;
        return value[..length];
    }

    private static string Write(
        string documentType,
        string title,
        string language,
        string summary,
        IReadOnlyList<DocumentSection> sections,
        bool bounded)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["documentType"] = documentType,
            ["title"] = title,
            ["language"] = language,
            ["summary"] = summary,
            ["sections"] = sections,
        };
        if (bounded) result["previewBounded"] = true;
        return JsonSerializer.Serialize(result, ResultJsonOptions);
    }

    public static bool TryDeserializeInput(string json, out DocumentGenerationInput? input)
    {
        try
        {
            input = JsonSerializer.Deserialize<DocumentGenerationInput>(json);
            return input is not null;
        }
        catch (JsonException)
        {
            input = null;
            return false;
        }
    }
}

public static class DocumentGenerationDefaults
{
    public const string DefaultLanguage = "auto";
    public const string DefaultOutputFormat = "both";
    public const string DefaultTone = "professional";
    public const string DefaultDocumentType = "auto";
    public const string DefaultLength = "standard";
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto", "en", "ar", "ku" };
    public static readonly IReadOnlySet<string> OutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "docx", "pdf", "both" };
    public static readonly IReadOnlySet<string> Tones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "professional", "formal", "friendly", "persuasive", "neutral", "concise", "academic" };
    public static readonly IReadOnlySet<string> DocumentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto", "report", "proposal", "business_letter", "company_profile", "meeting_minutes", "article", "general" };
    public static readonly IReadOnlySet<string> Lengths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "short", "standard", "detailed" };
    public static readonly IReadOnlySet<string> AttachmentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx" };
}

public static class DocumentGenerationRequestValidator
{
    public static void Validate(DocumentGenerationInput input, DocumentGenerationOptions options)
    {
        if (input is null) throw new DocumentRequestValidationException(GenerationJobErrorCodes.DocumentRequestInvalid, "The document request is invalid.");
        if (input.WorkspaceId == Guid.Empty) Invalid("WORKSPACE_REQUIRED", "A workspace is required.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length is < 1 or > 160) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, "Document title must be between 1 and 160 characters.");
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length < 3 || input.Description.Length > options.MaxPromptCharacters) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, $"Document description must be between 3 and {options.MaxPromptCharacters} characters.");
        if (input.AttachmentIds is null || input.AttachmentIds.Count > options.MaxAttachments) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, $"Choose no more than {options.MaxAttachments} source documents.");
        if (string.IsNullOrWhiteSpace(input.Language) || !DocumentGenerationDefaults.Languages.Contains(input.Language)) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, "The selected document language is not supported.");
        if (string.IsNullOrWhiteSpace(input.OutputFormat) || !DocumentGenerationDefaults.OutputFormats.Contains(input.OutputFormat)) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, "The selected output format is not supported.");
        if (string.IsNullOrWhiteSpace(input.Tone) || !DocumentGenerationDefaults.Tones.Contains(input.Tone)) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, "The selected tone is not supported.");
        if (string.IsNullOrWhiteSpace(input.DocumentType) || !DocumentGenerationDefaults.DocumentTypes.Contains(input.DocumentType)) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, "The selected document type is not supported.");
        if (string.IsNullOrWhiteSpace(input.Length) || !DocumentGenerationDefaults.Lengths.Contains(input.Length)) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, "The selected document length is not supported.");
        if (input.Audience?.Length > 400 || input.AdditionalInstructions?.Length > 3_000) Invalid(GenerationJobErrorCodes.DocumentRequestInvalid, "Optional document guidance is too long.");
        return;

        static void Invalid(string code, string message) => throw new DocumentRequestValidationException(code, message);
    }
}

public sealed class DocumentRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class DocumentGenerationCostEstimator
{
    public static decimal? Estimate(DocumentGenerationOptions options) => options.EstimatedOutputUsdPer1KTokens.HasValue
        ? Math.Round(Math.Max(0m, options.MaxOutputTokens / 1_000m * options.EstimatedOutputUsdPer1KTokens.Value), 8)
        : null;
}

public static class DocumentDraftValidator
{
    public static void Validate(DocumentDraft draft, DocumentGenerationOptions options)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 255
            || HasUnsafeText(draft.Title)
            || draft.Sections is null || draft.Sections.Count < 1 || draft.Sections.Count > options.MaxSections
            || draft.Summary is null || draft.Summary.Length > options.MaxSummaryCharacters || HasUnsafeText(draft.Summary))
            throw new DocumentOutputValidationException();

        var blocks = 0;
        foreach (var section in draft.Sections)
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Heading) || section.Heading.Length > options.MaxHeadingCharacters
                || HasUnsafeText(section.Heading)
                || section.Blocks is null || section.Blocks.Count == 0)
                throw new DocumentOutputValidationException();

            blocks += section.Blocks.Count;
            foreach (var block in section.Blocks)
            {
                if (block is null || string.IsNullOrWhiteSpace(block.Type) || !DocumentBlockTypes.Supported.Contains(block.Type)
                    || (block.Text?.Length ?? 0) > options.MaxBlockCharacters || HasUnsafeText(block.Text)
                    || block.Items is { Count: > 40 } || block.Rows is { Count: > 100 })
                    throw new DocumentOutputValidationException();

                if (block.Items?.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > options.MaxBlockCharacters || HasUnsafeText(item)) == true
                    || block.Rows?.Any(row => row is null || row.Cells is null || row.Cells.Count is < 1 or > 8
                        || row.Cells.Any(cell => cell is null || cell.Length > options.MaxBlockCharacters || HasUnsafeText(cell))) == true)
                    throw new DocumentOutputValidationException();

                switch (block.Type.ToLowerInvariant())
                {
                    case DocumentBlockTypes.Paragraph:
                    case DocumentBlockTypes.Heading:
                        if (string.IsNullOrWhiteSpace(block.Text) || block.Items is { Count: > 0 } || block.Rows is { Count: > 0 })
                            throw new DocumentOutputValidationException();
                        break;
                    case DocumentBlockTypes.BulletList:
                    case DocumentBlockTypes.NumberedList:
                        if (block.Items is not { Count: > 0 } || block.Rows is { Count: > 0 })
                            throw new DocumentOutputValidationException();
                        break;
                    case DocumentBlockTypes.Table:
                        if (block.Rows is not { Count: > 0 } || block.Items is { Count: > 0 })
                            throw new DocumentOutputValidationException();
                        break;
                }
            }
        }

        if (blocks < 1 || blocks > options.MaxBlocks)
            throw new DocumentOutputValidationException();
    }

    private static bool HasUnsafeText(string? value)
    {
        if (value is null) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) && character is not '\t' and not '\n' and not '\r') return true;
            if (character is '\uFFFE' or '\uFFFF') return true;
            if (!char.IsSurrogate(character)) continue;
            if (!char.IsHighSurrogate(character) || index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return true;
        }
        return false;
    }
}

public static class DocumentBlockTypes
{
    public const string Paragraph = "paragraph";
    public const string Heading = "heading";
    public const string BulletList = "bullet_list";
    public const string NumberedList = "numbered_list";
    public const string Table = "table";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Paragraph, Heading, BulletList, NumberedList, Table };
}

public sealed class DocumentOutputValidationException : Exception;
