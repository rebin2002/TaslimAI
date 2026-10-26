using System.Runtime.CompilerServices;
using System.Text.Json;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationGenerationProviderTests
{
    [Fact]
    public async Task Late_completion_after_cancellation_does_not_return_success()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new AiPresentationGenerationProvider(new LateCompletionService(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(
            new PresentationGenerationPrompt("system", "user", "en"),
            new PresentationGenerationOptions { ProviderTimeoutSeconds = 30 },
            cancellation.Token));
    }

    private sealed class LateCompletionService(CancellationTokenSource cancellation) : IChatCompletionService
    {
        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            var draft = new PresentationDraft
            {
                Title = "Launch",
                Language = "en",
                PresentationType = "general",
                Slides = [new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Title, Title = "Launch" }],
            };
            return Task.FromResult(new AiGenerationResult(
                JsonSerializer.Serialize(draft),
                new AiUsageMetadata("test", "test", 1, null, 1, 0m, 0m, 1, "completed", true)));
        }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
            AiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
