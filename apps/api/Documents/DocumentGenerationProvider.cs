using System.Text.Json;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;

namespace Taslim.Api.Documents;

public sealed record DocumentProviderResult(DocumentDraft Draft, AiUsageMetadata Usage);

public interface IDocumentGenerationProvider
{
    Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default);
}

public sealed class AiDocumentGenerationProvider(IChatCompletionService completion) : IDocumentGenerationProvider
{
    public async Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ProviderTimeoutSeconds)));
        AiGenerationResult result;
        try
        {
            result = await completion.CompleteAsync(
                new AiChatRequest(
                    [new AiChatMessage("user", prompt.UserInstruction)],
                    prompt.SystemInstruction,
                    options.RequestedTier,
                    EnableStreaming: false,
                    MaxOutputTokens: options.MaxOutputTokens,
                    JsonMode: true),
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderTimeoutException();
        }
        try
        {
            var draft = JsonSerializer.Deserialize<DocumentDraft>(result.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new DocumentOutputValidationException();
            DocumentDraftValidator.Validate(draft, options);
            return new DocumentProviderResult(draft, result.Usage);
        }
        catch (JsonException)
        {
            throw new DocumentOutputValidationException();
        }
    }
}
