namespace Taslim.Api.Ai;

public sealed record AiChatMessage(string Role, string Content);

public sealed record AiChatRequest(
    IReadOnlyList<AiChatMessage> Messages,
    string? RequestedModel = null,
    bool EnableStreaming = false);

public sealed record AiProviderSelection(
    string ProviderKey,
    string ModelKey,
    string ModelName,
    bool IsTestProvider);

public sealed record AiUsageMetadata(
    string ProviderKey,
    string ModelKey,
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCost,
    decimal? ActualCost,
    int LatencyMs,
    string FinishReason,
    bool IsTestResponse);

public sealed record AiGenerationResult(string Content, AiUsageMetadata Usage);

public interface IAiProvider
{
    string Key { get; }
    Task<AiGenerationResult> CompleteAsync(AiChatRequest request, AiProviderSelection selection, CancellationToken cancellationToken = default);
}

public interface IAiModelRouter
{
    AiProviderSelection Select(AiChatRequest request);
}

public interface IChatCompletionService
{
    Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default);
}

public sealed class AiModelRouter : IAiModelRouter
{
    public AiProviderSelection Select(AiChatRequest request) => new(
        ProviderKey: "mock",
        ModelKey: request.RequestedModel ?? "taslim-mock-chat",
        ModelName: "Taslim Mock Chat",
        IsTestProvider: true);
}

public sealed class ChatCompletionService(
    IAiModelRouter router,
    IEnumerable<IAiProvider> providers,
    ILogger<ChatCompletionService> logger) : IChatCompletionService
{
    public async Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        var selection = router.Select(request);
        var provider = providers.FirstOrDefault(item => string.Equals(item.Key, selection.ProviderKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogError("AI provider selection failed. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new InvalidOperationException("No AI provider is available for the selected model.");
        }

        return await provider.CompleteAsync(request, selection, cancellationToken);
    }
}
