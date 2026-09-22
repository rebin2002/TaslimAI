using Taslim.Api.Ai;
using Taslim.Api.Domain;

namespace Taslim.Api.Research;

public static class ResearchGenerationStages
{
    public const string Validation = "validation";
    public const string Planning = "planning";
    public const string Search = "search";
    public const string SourceNormalization = "source_normalization";
    public const string Context = "context";
    public const string Report = "report";
    public const string DraftValidation = "draft_validation";
    public const string RenderDocx = "render_docx";
    public const string RenderPdf = "render_pdf";
    public const string AssetPublish = "asset_publish";
    public const string StorageDocx = "storage_docx";
    public const string StoragePdf = "storage_pdf";
}

public sealed class ResearchGenerationStageException(string stage, string code, string message, AiUsageMetadata? usage = null, Exception? inner = null) : Exception(message, inner)
{
    public string Stage { get; } = stage;
    public string Code { get; } = code;
    public AiUsageMetadata? Usage { get; } = usage;
}

public static class ResearchGenerationFailureCodes
{
    public static string ForProvider(AiProviderException exception) => exception.FailureCategory switch
    {
        AiProviderFailureCategories.Configuration => GenerationJobErrorCodes.ResearchProviderUnavailable,
        AiProviderFailureCategories.UnsupportedRequest => GenerationJobErrorCodes.ResearchProviderUnavailable,
        AiProviderFailureCategories.RateLimited => GenerationJobErrorCodes.ResearchProviderUnavailable,
        AiProviderFailureCategories.Transient => GenerationJobErrorCodes.ResearchProviderUnavailable,
        _ => GenerationJobErrorCodes.ResearchGenerationFailed,
    };
}
