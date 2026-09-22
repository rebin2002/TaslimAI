using Taslim.Api.Ai;
using Taslim.Api.Domain;

namespace Taslim.Api.Documents;

public static class DocumentGenerationStages
{
    public const string Validation = "validation";
    public const string Context = "context";
    public const string Provider = "provider";
    public const string DraftParse = "draft_parse";
    public const string DraftValidation = "draft_validation";
    public const string DocxRender = "docx_render";
    public const string PdfRender = "pdf_render";
    public const string StorageDocx = "storage_docx";
    public const string StoragePdf = "storage_pdf";
    public const string AssetPublish = "asset_publish";
    public const string UsageFinalize = "usage_finalize";
    public const string Execution = "execution";
}

public sealed class DocumentGenerationStageException(
    string stage,
    string code,
    string message,
    AiUsageMetadata? usage = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Stage { get; } = stage;
    public string Code { get; } = code;
    public AiUsageMetadata? Usage { get; } = usage;
}

public static class DocumentGenerationFailureCodes
{
    public static string ForStage(string stage) => stage switch
    {
        DocumentGenerationStages.DocxRender or DocumentGenerationStages.PdfRender => GenerationJobErrorCodes.DocumentRenderFailed,
        DocumentGenerationStages.StorageDocx or DocumentGenerationStages.StoragePdf or DocumentGenerationStages.AssetPublish => GenerationJobErrorCodes.DocumentStorageFailed,
        DocumentGenerationStages.DraftParse or DocumentGenerationStages.DraftValidation => GenerationJobErrorCodes.DocumentOutputInvalid,
        _ => GenerationJobErrorCodes.DocumentGenerationFailed,
    };
}
