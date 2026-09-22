using Taslim.Api.Ai;
using Taslim.Api.Domain;

namespace Taslim.Api.Presentations;

public static class PresentationGenerationStages
{
    public const string Validation = "validation";
    public const string Context = "context";
    public const string Provider = "provider";
    public const string DraftParse = "draft_parse";
    public const string DraftValidation = "draft_validation";
    public const string PptxRender = "pptx_render";
    public const string StoragePptx = "storage_pptx";
    public const string AssetPublish = "asset_publish";
    public const string Execution = "execution";
}

public sealed class PresentationGenerationStageException(string stage, string code, string message, AiUsageMetadata? usage = null, Exception? innerException = null) : Exception(message, innerException)
{
    public string Stage { get; } = stage;
    public string Code { get; } = code;
    public AiUsageMetadata? Usage { get; } = usage;
}

public static class PresentationGenerationFailureCodes
{
    public static string ForProvider(AiProviderException exception) => exception.FailureCategory switch
    {
        AiProviderFailureCategories.Configuration => GenerationJobErrorCodes.PresentationProviderConfiguration,
        AiProviderFailureCategories.UnsupportedRequest => GenerationJobErrorCodes.PresentationProviderUnsupportedRequest,
        AiProviderFailureCategories.RateLimited => GenerationJobErrorCodes.PresentationProviderRateLimited,
        AiProviderFailureCategories.Transient => GenerationJobErrorCodes.PresentationProviderTransientFailure,
        _ => GenerationJobErrorCodes.PresentationProviderUnavailable,
    };

    public static string ForStage(string stage) => stage switch
    {
        PresentationGenerationStages.PptxRender => GenerationJobErrorCodes.PresentationRenderFailed,
        PresentationGenerationStages.StoragePptx or PresentationGenerationStages.AssetPublish => GenerationJobErrorCodes.PresentationStorageFailed,
        PresentationGenerationStages.DraftParse or PresentationGenerationStages.DraftValidation => GenerationJobErrorCodes.PresentationOutputInvalid,
        _ => GenerationJobErrorCodes.PresentationGenerationFailed,
    };
}
