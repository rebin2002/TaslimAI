using System.Runtime.CompilerServices;
using Taslim.Api.Ai;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSynopsisMalformedOutputTests
{
    [Fact]
    public async Task Missing_complications_array_returns_safe_generation_error_instead_of_null_reference()
    {
        var service = new AiMovieSynopsisDevelopmentService(new StubCompletionService("""
        {
          "premise": "A courier protects a fragile secret.",
          "logline": "A courier must choose between speed and trust.",
          "treatment": "The courier crosses a guarded city.",
          "synopsis": "The courier crosses the city, faces a blockade, and chooses to help a stranger.",
          "setup": "The courier receives a sealed package.",
          "protagonistMotivation": "The courier wants to keep a promise.",
          "incitingEvent": "The route is unexpectedly closed.",
          "escalation": "A stranger asks for help.",
          "climaxChoice": "The courier gives up the fastest route.",
          "resolution": "The package reaches its destination.",
          "emotionalArc": "Duty becomes chosen compassion.",
          "canonAnchors": [],
          "proposedElements": ["The guarded city"],
          "beatCount": 1
        }
        """));
        var context = new DirectorStoryBoundedContextDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "A bounded movie brief.",
            new DirectorGuideContext("visual", "camera", "color", "sound", "continuity"),
            null,
            null,
            null,
            [],
            [],
            [],
            DateTime.UtcNow,
            DurationSeconds: 30);

        await Assert.ThrowsAsync<DirectorSynopsisGenerationException>(() => service.GenerateAsync(context, null));
    }

    private sealed class StubCompletionService(string content) : IChatCompletionService
    {
        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiGenerationResult(content, new AiUsageMetadata("test", "test", 1, null, 1, 0m, 0m, 0, "completed", true)));

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
            AiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
