namespace Taslim.Api.Ai;

/// <summary>
/// Development-only provider used to prove the end-to-end chat architecture.
/// This provider never calls a remote AI vendor and reports zero/test usage.
/// </summary>
public sealed class MockAiProvider(ILogger<MockAiProvider> logger) : IAiProvider
{
    public string Key => "mock";

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, AiProviderSelection selection, CancellationToken cancellationToken = default)
    {
        var latestUserMessage = request.Messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        if (latestUserMessage.Contains("[[mock-failure]]", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Development mock provider failure sentinel invoked. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new MockAiProviderException();
        }

        var content = latestUserMessage.Contains("hello taslim", StringComparison.OrdinalIgnoreCase)
            ? "Hello! Taslim Chat is connected and ready."
            : "Taslim Chat is connected and ready to help you shape that idea. This is a development response while the first real model provider is being prepared.";

        return Task.FromResult(new AiGenerationResult(
            content,
            new AiUsageMetadata(
                selection.ProviderKey,
                selection.ModelKey,
                InputTokens: null,
                OutputTokens: null,
                EstimatedCost: 0m,
                ActualCost: 0m,
                LatencyMs: 0,
                FinishReason: "mock-complete",
                IsTestResponse: true)));
    }
}

public sealed class MockAiProviderException : Exception;
