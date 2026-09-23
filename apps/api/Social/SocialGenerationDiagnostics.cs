using Taslim.Api.Ai;
using Taslim.Api.Domain;

namespace Taslim.Api.Social;

public static class SocialGenerationStages
{
    public const string Validation = "validation";
    public const string Context = "context";
    public const string Provider = "provider";
    public const string DraftParse = "draft_parse";
    public const string DraftValidation = "draft_validation";
    public const string Storage = "storage";
    public const string AssetPublish = "asset_publish";
    public const string Execution = "execution";
}

public sealed class SocialGenerationStageException(string stage, string code, string message, AiUsageMetadata? usage = null, Exception? innerException = null) : Exception(message, innerException)
{
    public string Stage { get; } = stage;
    public string Code { get; } = code;
    public AiUsageMetadata? Usage { get; } = usage;
}

public static class SocialGenerationFailureCodes
{
    public static string ForProvider(AiProviderException exception) => exception.FailureCategory switch
    {
        AiProviderFailureCategories.Configuration => GenerationJobErrorCodes.SocialProviderConfiguration,
        AiProviderFailureCategories.UnsupportedRequest => GenerationJobErrorCodes.SocialProviderUnsupportedRequest,
        AiProviderFailureCategories.RateLimited => GenerationJobErrorCodes.SocialProviderRateLimited,
        AiProviderFailureCategories.Transient => GenerationJobErrorCodes.SocialProviderTransientFailure,
        _ => GenerationJobErrorCodes.SocialProviderUnavailable,
    };
}
