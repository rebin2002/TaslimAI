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

    public static string SerializeInput(DocumentGenerationInput input) => JsonSerializer.Serialize(input);

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
            || draft.Sections is null || draft.Sections.Count < 1 || draft.Sections.Count > options.MaxSections
            || draft.Summary is null || draft.Summary.Length > options.MaxSummaryCharacters)
            throw new DocumentOutputValidationException();

        var blocks = 0;
        foreach (var section in draft.Sections)
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Heading) || section.Heading.Length > options.MaxHeadingCharacters
                || section.Blocks is null || section.Blocks.Count == 0)
                throw new DocumentOutputValidationException();

            blocks += section.Blocks.Count;
            foreach (var block in section.Blocks)
            {
                if (block is null || string.IsNullOrWhiteSpace(block.Type) || !DocumentBlockTypes.Supported.Contains(block.Type)
                    || (block.Text?.Length ?? 0) > options.MaxBlockCharacters
                    || block.Items is { Count: > 40 } || block.Rows is { Count: > 100 })
                    throw new DocumentOutputValidationException();

                if (block.Items?.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > options.MaxBlockCharacters) == true
                    || block.Rows?.Any(row => row is null || row.Cells is null || row.Cells.Count is < 1 or > 8
                        || row.Cells.Any(cell => cell is null || cell.Length > options.MaxBlockCharacters)) == true)
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
