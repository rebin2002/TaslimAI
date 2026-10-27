using System.Runtime.CompilerServices;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Documents;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentGenerationProviderContractTests
{
    [Fact]
    public async Task Provider_sends_non_streaming_structured_request_and_accepts_valid_draft()
    {
        var completion = new CapturingCompletion(ValidDraftJson);
        var provider = new AiDocumentGenerationProvider(completion);

        var result = await provider.GenerateAsync(Prompt(), Options());

        Assert.Equal("Launch brief", result.Draft.Title);
        Assert.NotNull(completion.Request);
        Assert.False(completion.Request!.EnableStreaming);
        Assert.Equal("Smart", completion.Request.RequestedTier);
        Assert.Equal(123, completion.Request.MaxOutputTokens);
        Assert.Equal(DocumentDraftStructuredOutput.Spec.Name, completion.Request.StructuredOutput?.Name);
    }

    [Fact]
    public async Task Provider_maps_its_internal_timeout_to_provider_timeout()
    {
        var provider = new AiDocumentGenerationProvider(new WaitForCancellationCompletion());

        await Assert.ThrowsAsync<AiProviderTimeoutException>(() => provider.GenerateAsync(Prompt(), Options(timeoutSeconds: 1)));
    }

    [Fact]
    public async Task Provider_does_not_relabel_provider_owned_cancellation_as_timeout()
    {
        var provider = new AiDocumentGenerationProvider(new ProviderOwnedCancellationCompletion());

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GenerateAsync(Prompt(), Options()));

        Assert.IsNotType<AiProviderTimeoutException>(exception);
    }

    private static DocumentGenerationPrompt Prompt() => new(
        "Return only the required JSON document draft.",
        "Create a concise launch brief.",
        "en");

    private static DocumentGenerationOptions Options(int timeoutSeconds = 30) => new()
    {
        RequestedTier = "Smart",
        MaxOutputTokens = 123,
        ProviderTimeoutSeconds = timeoutSeconds,
        MaxSections = 4,
        MaxBlocks = 8,
        MaxHeadingCharacters = 100,
        MaxBlockCharacters = 1_000,
        MaxSummaryCharacters = 500,
    };

    private const string ValidDraftJson = """
        {
          "title": "Launch brief",
          "summary": "A concise launch summary.",
          "sections": [
            {
              "heading": "Overview",
              "blocks": [
                {
                  "type": "paragraph",
                  "text": "The launch prioritizes clarity.",
                  "items": null,
                  "rows": null
                }
              ]
            }
          ]
        }
        """;

    private static AiUsageMetadata Usage() => new("document-test", "document-test", 10, null, 12, 0m, 0m, 1, "completed", true);

    private sealed class CapturingCompletion(string content) : IChatCompletionService
    {
        public AiChatRequest? Request { get; private set; }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new AiGenerationResult(content, Usage()));
        }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
            AiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class WaitForCancellationCompletion : IChatCompletionService
    {
        public async Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation token completed without cancelling the task.");
        }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
            AiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ProviderOwnedCancellationCompletion : IChatCompletionService
    {
        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<AiGenerationResult>(new OperationCanceledException("The provider aborted this request before the Document timeout."));

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
            AiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
