using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Research;

namespace Taslim.Api.Contracts;

public sealed class ResearchGenerationRequest
{
    [Required]
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    [StringLength(8_000, MinimumLength = 3)]
    public string? Question { get; set; }
    [StringLength(160)]
    public string? Title { get; set; }
    [StringLength(20)]
    public string? Depth { get; set; }
    [StringLength(40)]
    public string? ReportType { get; set; }
    [StringLength(20)]
    public string? Language { get; set; }
    [StringLength(400)]
    public string? Audience { get; set; }
    [StringLength(240)]
    public string? GeographicFocus { get; set; }
    [StringLength(120)]
    public string? TimePeriod { get; set; }
    [StringLength(3_000)]
    public string? AdditionalInstructions { get; set; }
    [StringLength(2_000)]
    public string? PreferredDomains { get; set; }
    [StringLength(2_000)]
    public string? ExcludedDomains { get; set; }
    public bool UseWebSources { get; set; } = true;
    [MaxLength(8)]
    public IReadOnlyList<Guid> AttachmentIds { get; set; } = [];
}

public sealed record ResearchGenerationInput(
    Guid WorkspaceId,
    Guid? ProjectId,
    string Question,
    string Title,
    string Depth,
    string ReportType,
    string Language,
    string? Audience,
    string? GeographicFocus,
    string? TimePeriod,
    string? AdditionalInstructions,
    IReadOnlyList<string> PreferredDomains,
    IReadOnlyList<string> ExcludedDomains,
    bool UseWebSources,
    IReadOnlyList<Guid> AttachmentIds);

public sealed record ResearchSourcePreference(
    bool UseWebSources,
    IReadOnlyList<string> PreferredDomains,
    IReadOnlyList<string> ExcludedDomains);

public sealed record ResearchProjectContext(string Name, string? Instructions, string? ContextNotes);

public sealed record ResearchPlan(
    string ResearchQuestion,
    string Objective,
    IReadOnlyList<string> SearchQueries,
    IReadOnlyList<string> KeyTopics,
    string? GeographicFocus,
    string? TimePeriod,
    IReadOnlyList<string> DesiredSourceTypes);

public sealed record ResearchSourceCandidate(
    string CitationId,
    string? Url,
    string? CanonicalUrl,
    string Title,
    string Domain,
    string? Publisher,
    DateTime? PublishedAt,
    DateTime RetrievedAt,
    string SourceType,
    string? Snippet,
    string? ExtractedText,
    string? SearchQuery,
    int Rank,
    bool IsSelected,
    string? MetadataJson);

public sealed record ResearchEvidenceCandidate(
    string CitationId,
    string Topic,
    string Excerpt,
    string? Context,
    DateTime? PublishedAt);

public sealed class ResearchDraft
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string Language { get; set; } = LanguageCodes.English;
    public string ExecutiveSummary { get; set; } = string.Empty;
    public List<ResearchReportBlock> KeyFindings { get; set; } = [];
    public List<ResearchSection> Sections { get; set; } = [];
    public string Conclusion { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = [];
}

public sealed class ResearchSection
{
    public string Heading { get; set; } = string.Empty;
    public List<ResearchReportBlock> Blocks { get; set; } = [];
}

public sealed class ResearchReportBlock
{
    public string Type { get; set; } = ResearchBlockTypes.Paragraph;
    public string? Text { get; set; }
    public List<string>? Items { get; set; }
    public List<ResearchTableRow>? Rows { get; set; }
    public List<string> CitationIds { get; set; } = [];
}

public sealed class ResearchTableRow
{
    public List<string> Cells { get; set; } = [];
}

public sealed record ResearchReportResult(
    Guid AssetId,
    IReadOnlyList<ResearchRepresentationResult> Representations,
    string Title,
    string Language,
    string ExecutiveSummary,
    IReadOnlyList<ResearchReportBlock> KeyFindings,
    int SourceCount,
    IReadOnlyList<ResearchSourceDto> Sources);

public sealed record ResearchRepresentationResult(Guid Id, string Type, string FileName, string ContentType);

public sealed record ResearchSourceDto(
    string CitationId,
    string? Url,
    string Title,
    string Domain,
    string? Publisher,
    DateTime? PublishedAt,
    DateTime RetrievedAt,
    string SourceType,
    string? Snippet,
    string? SearchQuery,
    int Rank,
    bool IsSelected);

public static class ResearchGenerationContractMapper
{
    public static ResearchGenerationInput ToInput(ResearchGenerationRequest request)
    {
        return new ResearchGenerationInput(
            request.WorkspaceId,
            request.ProjectId,
            request.Question?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(request.Title) ? "Taslim research report" : request.Title.Trim(),
            Normalize(request.Depth, ResearchGenerationDefaults.DefaultDepth),
            Normalize(request.ReportType, ResearchGenerationDefaults.DefaultReportType),
            Normalize(request.Language, ResearchGenerationDefaults.DefaultLanguage),
            NormalizeOptional(request.Audience),
            NormalizeOptional(request.GeographicFocus),
            NormalizeOptional(request.TimePeriod),
            NormalizeOptional(request.AdditionalInstructions),
            ParseDomains(request.PreferredDomains),
            ParseDomains(request.ExcludedDomains),
            request.UseWebSources,
            request.AttachmentIds.Distinct().ToArray());
    }

    public static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    public static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static IReadOnlyList<string> ParseDomains(string? value) =>
        (value ?? string.Empty).Split(new[] { ',', '\n', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim().ToLowerInvariant())
            .Where(item => item.Length is >= 3 and <= 120)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

    public static string SerializeInput(ResearchGenerationInput input) => JsonSerializer.Serialize(input);

    public static bool TryDeserializeInput(string json, out ResearchGenerationInput? input)
    {
        try
        {
            input = JsonSerializer.Deserialize<ResearchGenerationInput>(json);
            return input is not null;
        }
        catch (JsonException)
        {
            input = null;
            return false;
        }
    }
}

public static class ResearchGenerationDefaults
{
    public const string DefaultDepth = "standard";
    public const string DefaultReportType = "research_report";
    public const string DefaultLanguage = "auto";
    public static readonly IReadOnlySet<string> Depths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "quick", "standard", "deep" };
    public static readonly IReadOnlySet<string> ReportTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "research_report", "market_research", "competitor_research", "company_research", "product_research", "industry_research", "general_research" };
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto", "en", "ar", "ku" };
    public static readonly IReadOnlySet<string> AttachmentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx" };

    public static int MaxQueries(string depth) => depth.ToLowerInvariant() switch { "quick" => 3, "deep" => 10, _ => 6 };
    public static int MaxSources(string depth) => depth.ToLowerInvariant() switch { "quick" => 8, "deep" => 24, _ => 16 };
}

public static class ResearchGenerationRequestValidator
{
    public static void Validate(ResearchGenerationInput input, ResearchGenerationOptions options)
    {
        if (input.WorkspaceId == Guid.Empty) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "A workspace is required.");
        if (input.Question.Length < 3 || input.Question.Length > options.MaxQuestionCharacters) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "The research question is too short or too long.");
        if (input.Title.Length is < 1 or > 160) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "The report title is invalid.");
        if (!ResearchGenerationDefaults.Depths.Contains(input.Depth)) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "The selected research depth is not supported.");
        if (!ResearchGenerationDefaults.ReportTypes.Contains(input.ReportType)) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "The selected report type is not supported.");
        if (!ResearchGenerationDefaults.Languages.Contains(input.Language)) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "The selected research language is not supported.");
        if (input.AttachmentIds.Count > options.MaxAttachments) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "Choose no more than the allowed number of source files.");
        if (input.PreferredDomains.Count > 100 || input.ExcludedDomains.Count > 100) Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "Too many domain preferences were supplied.");
        if (input.Audience?.Length > 400 || input.GeographicFocus?.Length > 240 || input.TimePeriod?.Length > 120 || input.AdditionalInstructions?.Length > 3_000)
            Invalid(GenerationJobErrorCodes.ResearchRequestInvalid, "Optional research guidance is too long.");
        if (!input.UseWebSources && input.AttachmentIds.Count == 0) Invalid(GenerationJobErrorCodes.ResearchSourceUnavailable, "Choose a source file or enable web sources.");

        static void Invalid(string code, string message) => throw new ResearchRequestValidationException(code, message);
    }
}

public sealed class ResearchRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ResearchOutputValidationException : Exception;
public sealed class ResearchCitationValidationException : Exception;
public sealed class ResearchContextLimitException : Exception;

public static class ResearchGenerationCostEstimator
{
    public static decimal? Estimate(ResearchGenerationOptions options) => options.EstimatedOutputUsdPer1KTokens.HasValue
        ? decimal.Round(Math.Max(0m, options.MaxOutputTokens / 1_000m * options.EstimatedOutputUsdPer1KTokens.Value), 8, MidpointRounding.AwayFromZero)
        : null;
}

public static class ResearchBlockTypes
{
    public const string Paragraph = "paragraph";
    public const string Bullets = "bullets";
    public const string NumberedList = "numbered_list";
    public const string Table = "table";
    public const string KeyFinding = "key_finding";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Paragraph, Bullets, NumberedList, Table, KeyFinding };
}

public static class ResearchDraftValidator
{
    public static void Validate(ResearchDraft draft, IReadOnlySet<string> sourceCitationIds, ResearchGenerationOptions options)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 255 || string.IsNullOrWhiteSpace(draft.ExecutiveSummary) || draft.ExecutiveSummary.Length > options.MaxReportBlockCharacters)
            throw new ResearchOutputValidationException();
        if (!ResearchGenerationDefaults.Languages.Contains(draft.Language) || draft.Sections.Count < 1 || draft.Sections.Count > options.MaxSections || draft.KeyFindings.Count > options.MaxKeyFindings)
            throw new ResearchOutputValidationException();
        if (draft.Conclusion.Length > options.MaxReportBlockCharacters || draft.Sources.Count > options.MaxSourceCount)
            throw new ResearchOutputValidationException();
        var blocks = draft.Sections.Sum(section => section.Blocks.Count) + draft.KeyFindings.Count;
        if (blocks < 1 || blocks > options.MaxBlocks) throw new ResearchOutputValidationException();
        foreach (var citation in draft.Sources.Concat(draft.KeyFindings.SelectMany(item => item.CitationIds)).Concat(draft.Sections.SelectMany(section => section.Blocks.SelectMany(item => item.CitationIds))))
            if (!sourceCitationIds.Contains(citation)) throw new ResearchCitationValidationException();
        foreach (var section in draft.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Heading) || section.Heading.Length > options.MaxHeadingCharacters) throw new ResearchOutputValidationException();
            foreach (var block in section.Blocks) ValidateBlock(block, options);
        }
        foreach (var block in draft.KeyFindings) ValidateBlock(block, options);
    }

    private static void ValidateBlock(ResearchReportBlock block, ResearchGenerationOptions options)
    {
        if (!ResearchBlockTypes.Supported.Contains(block.Type)) throw new ResearchOutputValidationException();
        if ((block.Text?.Length ?? 0) > options.MaxReportBlockCharacters) throw new ResearchOutputValidationException();
        if (block.Items is { Count: > 40 } || block.Rows is { Count: > 100 } || block.CitationIds.Count > options.MaxCitationsPerBlock) throw new ResearchOutputValidationException();
        if (block.Items?.Any(item => item.Length > options.MaxReportBlockCharacters) == true) throw new ResearchOutputValidationException();
        if (block.Rows?.Any(row => row.Cells.Count > 12 || row.Cells.Any(cell => cell.Length > options.MaxReportBlockCharacters)) == true) throw new ResearchOutputValidationException();
        if (ContainsMarkup(block.Text) || block.Items?.Any(ContainsMarkup) == true || block.Rows?.SelectMany(row => row.Cells).Any(ContainsMarkup) == true) throw new ResearchOutputValidationException();
    }

    private static bool ContainsMarkup(string? value) => value?.Contains('<') == true || value?.Contains('>') == true;
}
