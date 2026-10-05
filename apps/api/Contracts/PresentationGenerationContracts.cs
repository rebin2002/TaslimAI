using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;

namespace Taslim.Api.Contracts;

public sealed class PresentationGenerationRequest
{
    [Required] public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    [StringLength(160)] public string? Title { get; set; }
    [StringLength(8_000, MinimumLength = 3)] public string? Description { get; set; }
    [StringLength(40)] public string? PresentationType { get; set; }
    [StringLength(20)] public string? Length { get; set; }
    [StringLength(40)] public string? Tone { get; set; }
    [StringLength(20)] public string? Language { get; set; }
    [StringLength(400)] public string? Audience { get; set; }
    [StringLength(3_000)] public string? AdditionalInstructions { get; set; }
    [StringLength(160)] public string? BrandCompany { get; set; }
    public bool IncludeAgenda { get; set; } = true;
    public bool IncludeClosingNextSteps { get; set; } = true;
    [MaxLength(8)] public IReadOnlyList<Guid> AttachmentIds { get; set; } = [];
}

public sealed record PresentationGenerationInput(
    Guid WorkspaceId,
    Guid? ProjectId,
    string Title,
    string Description,
    string PresentationType,
    string Length,
    string Tone,
    string Language,
    string? Audience,
    string? AdditionalInstructions,
    string? BrandCompany,
    bool IncludeAgenda,
    bool IncludeClosingNextSteps,
    IReadOnlyList<Guid> AttachmentIds);

public sealed class PresentationDraft
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string Language { get; set; } = LanguageCodes.English;
    public string Theme { get; set; } = "professional-navy";
    public string PresentationType { get; set; } = "general";
    public List<PresentationSlide> Slides { get; set; } = [];
}

public sealed class PresentationSlide
{
    public int Order { get; set; }
    public string Type { get; set; } = PresentationSlideTypes.Content;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public List<PresentationContentBlock> Blocks { get; set; } = [];
    public string? Notes { get; set; }
    public string? VisualSuggestion { get; set; }
    public List<string> SourceRefs { get; set; } = [];
}

public sealed class PresentationContentBlock
{
    public string Type { get; set; } = PresentationBlockTypes.Text;
    public string? Text { get; set; }
    public List<string> Items { get; set; } = [];
    public List<PresentationColumn> Columns { get; set; } = [];
    public List<PresentationTableRow> Rows { get; set; } = [];
    public List<PresentationMetric> Metrics { get; set; } = [];
    public string? Label { get; set; }
    public string? Value { get; set; }
}

public sealed class PresentationColumn
{
    public string Heading { get; set; } = string.Empty;
    public List<string> Items { get; set; } = [];
}

public sealed class PresentationTableRow
{
    public List<string> Cells { get; set; } = [];
}

public sealed class PresentationMetric
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed record PresentationGenerationResult(
    Guid AssetId,
    IReadOnlyList<PresentationRepresentationResult> Representations,
    string Title,
    string Language,
    int SlideCount,
    string PresentationType);

public sealed record PresentationRepresentationResult(Guid Id, string Type, string FileName, string ContentType);

public static class PresentationGenerationContractMapper
{
    public static PresentationGenerationInput ToInput(PresentationGenerationRequest request) => new(
        request.WorkspaceId,
        request.ProjectId,
        NormalizeOptional(request.Title) ?? "Taslim presentation",
        (NormalizeOptional(request.Description) ?? string.Empty),
        Normalize(request.PresentationType, PresentationGenerationDefaults.DefaultPresentationType),
        Normalize(request.Length, PresentationGenerationDefaults.DefaultLength),
        Normalize(request.Tone, PresentationGenerationDefaults.DefaultTone),
        Normalize(request.Language, PresentationGenerationDefaults.DefaultLanguage),
        NormalizeOptional(request.Audience),
        NormalizeOptional(request.AdditionalInstructions),
        NormalizeOptional(request.BrandCompany),
        request.IncludeAgenda,
        request.IncludeClosingNextSteps,
        request.AttachmentIds.Distinct().ToArray());

    public static string Normalize(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    public static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string SerializeInput(PresentationGenerationInput input) => JsonSerializer.Serialize(input);

    public static bool TryDeserializeInput(string json, out PresentationGenerationInput? input)
    {
        try
        {
            input = JsonSerializer.Deserialize<PresentationGenerationInput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return input is not null;
        }
        catch (JsonException)
        {
            input = null;
            return false;
        }
    }
}

public static class PresentationGenerationDefaults
{
    public const string DefaultLanguage = "auto";
    public const string DefaultTone = "professional";
    public const string DefaultPresentationType = "general";
    public const string DefaultLength = "standard";
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto", "en", "ar", "ku" };
    public static readonly IReadOnlySet<string> Tones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "professional", "formal", "friendly", "persuasive", "neutral", "concise", "academic" };
    public static readonly IReadOnlySet<string> Lengths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "short", "standard", "detailed" };
    public static readonly IReadOnlySet<string> PresentationTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto", "business", "company_profile", "sales", "investor", "proposal", "training", "project_update", "report", "educational", "general" };
    public static readonly IReadOnlySet<string> AttachmentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx" };
}

public static class PresentationGenerationRequestValidator
{
    public static void Validate(PresentationGenerationInput input, PresentationGenerationOptions options)
    {
        if (input.WorkspaceId == Guid.Empty) Invalid("WORKSPACE_REQUIRED", "A workspace is required.");
        if (input.Title.Length is < 1 or > 160) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, "Presentation title must be between 1 and 160 characters.");
        if (input.Description.Length < 3 || input.Description.Length > options.MaxPromptCharacters) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, $"Presentation description must be between 3 and {options.MaxPromptCharacters} characters.");
        if (input.AttachmentIds.Count > options.MaxAttachments) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, $"Choose no more than {options.MaxAttachments} source documents.");
        if (!PresentationGenerationDefaults.Languages.Contains(input.Language)) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, "The selected presentation language is not supported.");
        if (!PresentationGenerationDefaults.Tones.Contains(input.Tone)) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, "The selected tone is not supported.");
        if (!PresentationGenerationDefaults.PresentationTypes.Contains(input.PresentationType)) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, "The selected presentation type is not supported.");
        if (!PresentationGenerationDefaults.Lengths.Contains(input.Length)) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, "The selected presentation length is not supported.");
        if (input.Audience?.Length > 400 || input.AdditionalInstructions?.Length > 3_000 || input.BrandCompany?.Length > 160) Invalid(GenerationJobErrorCodes.PresentationRequestInvalid, "Optional presentation guidance is too long.");

        static void Invalid(string code, string message) => throw new PresentationRequestValidationException(code, message);
    }
}

public sealed class PresentationRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PresentationGenerationCostEstimator
{
    public static decimal? Estimate(PresentationGenerationOptions options) => options.EstimatedOutputUsdPer1KTokens.HasValue
        ? Math.Round(Math.Max(0m, options.MaxOutputTokens / 1_000m * options.EstimatedOutputUsdPer1KTokens.Value), 8)
        : null;
}

public static class PresentationDraftValidator
{
    public static void Validate(PresentationDraft draft, PresentationGenerationOptions options)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > options.MaxTitleCharacters || draft.Slides is null || draft.Slides.Count < 1 || draft.Slides.Count > options.MaxSlides)
            throw new PresentationOutputValidationException();
        if (draft.Language is null || (!PresentationGenerationDefaults.Languages.Contains(draft.Language) && !string.Equals(draft.Language, "auto", StringComparison.OrdinalIgnoreCase))) throw new PresentationOutputValidationException();
        var expectedOrder = 1;
        foreach (var slide in draft.Slides)
        {
            if (slide is null || slide.Order != expectedOrder++ || slide.Type is null || !PresentationSlideTypes.Supported.Contains(slide.Type) || string.IsNullOrWhiteSpace(slide.Title) || slide.Title.Length > options.MaxTitleCharacters || slide.Blocks is null || slide.SourceRefs is null)
                throw new PresentationOutputValidationException();
            if (slide.Subtitle?.Length > options.MaxSubtitleCharacters || slide.Notes?.Length > options.MaxNotesCharacters || slide.VisualSuggestion?.Length > options.MaxVisualSuggestionCharacters || slide.SourceRefs.Count > options.MaxSourceRefsPerSlide || slide.SourceRefs.Any(reference => string.IsNullOrWhiteSpace(reference) || reference.Length > options.MaxBlockCharacters))
                throw new PresentationOutputValidationException();
            if (slide.Blocks.Count > options.MaxBlocksPerSlide) throw new PresentationOutputValidationException();
            foreach (var block in slide.Blocks)
                ValidateBlock(block, options);
        }

        static void ValidateBlock(PresentationContentBlock block, PresentationGenerationOptions options)
        {
            if (block is null || block.Type is null || !PresentationBlockTypes.Supported.Contains(block.Type) || block.Items is null || block.Columns is null || block.Rows is null || block.Metrics is null)
                throw new PresentationOutputValidationException();
            if (block.Text?.Length > options.MaxBlockCharacters || block.Label?.Length > options.MaxBlockCharacters || block.Value?.Length > options.MaxBlockCharacters)
                throw new PresentationOutputValidationException();
            if (block.Items.Count > options.MaxItemsPerBlock || block.Columns.Count > 4 || block.Rows.Count > options.MaxRowsPerBlock || block.Metrics.Count > 6)
                throw new PresentationOutputValidationException();
            if (block.Items.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > options.MaxBlockCharacters)
                || block.Columns.Any(column => column is null
                    || string.IsNullOrWhiteSpace(column.Heading)
                    || column.Heading.Length > options.MaxBlockCharacters
                    || column.Items is null
                    || column.Items.Count == 0
                    || column.Items.Count > options.MaxItemsPerBlock
                    || column.Items.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > options.MaxBlockCharacters))
                || block.Rows.Any(row => row is null
                    || row.Cells is null
                    || row.Cells.Count == 0
                    || row.Cells.Count > 8
                    || row.Cells.Any(cell => cell is null || cell.Length > options.MaxBlockCharacters))
                || block.Metrics.Any(metric => metric is null
                    || string.IsNullOrWhiteSpace(metric.Label)
                    || string.IsNullOrWhiteSpace(metric.Value)
                    || metric.Label.Length > options.MaxBlockCharacters
                    || metric.Value.Length > options.MaxBlockCharacters
                    || metric.Detail?.Length > options.MaxBlockCharacters))
                throw new PresentationOutputValidationException();
            var type = block.Type.ToLowerInvariant();
            var hasRenderableContent = type switch
            {
                PresentationBlockTypes.Text => !string.IsNullOrWhiteSpace(block.Text) || block.Items.Count > 0,
                PresentationBlockTypes.Bullets or PresentationBlockTypes.Timeline or PresentationBlockTypes.Process => block.Items.Count > 0 || !string.IsNullOrWhiteSpace(block.Text),
                PresentationBlockTypes.Columns => block.Columns.Count > 0,
                PresentationBlockTypes.Table => block.Rows.Count > 0,
                PresentationBlockTypes.Metrics => block.Metrics.Count > 0,
                PresentationBlockTypes.Quote => !string.IsNullOrWhiteSpace(block.Text ?? block.Value),
                _ => false,
            };
            if (!hasRenderableContent || AllText(block).Any(text => text.Contains('<') || text.Contains('>')))
                throw new PresentationOutputValidationException();
        }

        static IEnumerable<string> AllText(PresentationContentBlock block)
        {
            if (block.Text is not null) yield return block.Text;
            if (block.Label is not null) yield return block.Label;
            if (block.Value is not null) yield return block.Value;
            foreach (var item in block.Items) yield return item;
            foreach (var column in block.Columns)
            {
                yield return column.Heading;
                foreach (var item in column.Items) yield return item;
            }
            foreach (var row in block.Rows) foreach (var cell in row.Cells) yield return cell;
            foreach (var metric in block.Metrics)
            {
                yield return metric.Label;
                yield return metric.Value;
                if (metric.Detail is not null) yield return metric.Detail;
            }
        }
    }
}

public static class PresentationSlideTypes
{
    public const string Title = "title";
    public const string Agenda = "agenda";
    public const string Section = "section";
    public const string Content = "content";
    public const string Bullets = "bullets";
    public const string TwoColumn = "two_column";
    public const string Comparison = "comparison";
    public const string Metrics = "metrics";
    public const string Table = "table";
    public const string Timeline = "timeline";
    public const string Process = "process";
    public const string Quote = "quote";
    public const string Summary = "summary";
    public const string NextSteps = "next_steps";
    public const string Closing = "closing";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Title, Agenda, Section, Content, Bullets, TwoColumn, Comparison, Metrics, Table, Timeline, Process, Quote, Summary, NextSteps, Closing };
}

public static class PresentationBlockTypes
{
    public const string Text = "text";
    public const string Bullets = "bullets";
    public const string Columns = "columns";
    public const string Table = "table";
    public const string Metrics = "metrics";
    public const string Timeline = "timeline";
    public const string Process = "process";
    public const string Quote = "quote";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Text, Bullets, Columns, Table, Metrics, Timeline, Process, Quote };
}

public sealed class PresentationOutputValidationException : Exception;
