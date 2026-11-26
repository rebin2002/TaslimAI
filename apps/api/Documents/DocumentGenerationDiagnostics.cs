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
    /// <summary>
    /// A provider that answered with a structurally malformed body produced an
    /// invalid document output rather than suffering an outage. Collapsing that
    /// case into <see cref="GenerationJobErrorCodes.DocumentProviderUnavailable"/>
    /// reports a healthy provider as unavailable and invites an operator retry
    /// that cannot succeed, so the response-shape failure keeps the output code.
    /// </summary>
    public static string ForProvider(AiProviderException exception) => exception.FailureCategory switch
    {
        AiProviderFailureCategories.Configuration => GenerationJobErrorCodes.DocumentProviderConfiguration,
        AiProviderFailureCategories.UnsupportedRequest => GenerationJobErrorCodes.DocumentProviderUnsupportedRequest,
        AiProviderFailureCategories.RateLimited => GenerationJobErrorCodes.DocumentProviderRateLimited,
        AiProviderFailureCategories.Transient => GenerationJobErrorCodes.DocumentProviderTransientFailure,
        AiProviderFailureCategories.MalformedResponse => GenerationJobErrorCodes.DocumentOutputInvalid,
        _ => GenerationJobErrorCodes.DocumentProviderUnavailable,
    };

    public static string ForStage(string stage) => stage switch
    {
        DocumentGenerationStages.DocxRender or DocumentGenerationStages.PdfRender => GenerationJobErrorCodes.DocumentRenderFailed,
        DocumentGenerationStages.StorageDocx or DocumentGenerationStages.StoragePdf or DocumentGenerationStages.AssetPublish => GenerationJobErrorCodes.DocumentStorageFailed,
        DocumentGenerationStages.DraftParse or DocumentGenerationStages.DraftValidation => GenerationJobErrorCodes.DocumentOutputInvalid,
        _ => GenerationJobErrorCodes.DocumentGenerationFailed,
    };
}

/// <summary>
/// The single provider-neutral Document Studio kill-switch contract. Every
/// boundary that can start or resume Document provider work must fail closed
/// with this code and message, so disabling Document Studio cannot be bypassed
/// by choosing a different route and no provider detail reaches the browser.
/// </summary>
public static class DocumentStudioAvailability
{
    public const string UnavailableCode = GenerationJobErrorCodes.DocumentStudioUnavailable;
    public const string UnavailableMessage = "Document generation is not available right now.";

    public static DocumentGenerationStageException Unavailable() => new(
        DocumentGenerationStages.Validation,
        UnavailableCode,
        UnavailableMessage);
}
