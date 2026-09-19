using System.Runtime.CompilerServices;

namespace Taslim.Api.Ai;

/// <summary>
/// Development-only provider used to prove the end-to-end chat architecture.
/// This provider never calls a remote AI vendor and reports zero/test usage.
/// </summary>
public sealed class MockAiProvider(ILogger<MockAiProvider> logger) : IAiProvider
{
    public string Key => "mock";

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiChatRequest request,
        AiProviderSelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        foreach (var chunk in Split(content, 18))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AiMessageDelta(chunk);
        }

        yield return new AiMessageCompleted(new AiUsageMetadata(
            selection.ProviderKey,
            selection.ModelKey,
            InputTokens: null,
            CachedInputTokens: null,
            OutputTokens: null,
            EstimatedCost: 0m,
            ActualCost: 0m,
            LatencyMs: 0,
            FinishReason: "mock-complete",
            IsTestResponse: true));
    }

    private static IEnumerable<string> Split(string content, int chunkSize)
    {
        for (var index = 0; index < content.Length; index += chunkSize)
            yield return content[index..Math.Min(index + chunkSize, content.Length)];
    }
}

public sealed class MockAiProviderException : Exception;
