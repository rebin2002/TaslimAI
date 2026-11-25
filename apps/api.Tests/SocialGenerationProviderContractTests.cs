using System.Runtime.CompilerServices;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialGenerationProviderContractTests
{
    [Fact]
    public async Task Provider_rejects_a_structurally_valid_draft_for_a_different_requested_platform()
    {
        var input = new SocialGenerationInput(
            Guid.NewGuid(),
            null,
            "Announce the launch",
            "announcement",
            "instagram",
            "professional",
            "en",
            null,
            null,
            null,
            true,
            false,
            false,
            [],
            []);
        var prompt = new SocialGenerationPrompt(input, null, [], "system", "user");
        var provider = new AiSocialGenerationProvider(new MismatchedPlatformCompletion());

        await Assert.ThrowsAsync<SocialGenerationStageException>(() =>
            provider.GenerateAsync(prompt, new SocialGenerationOptions()));
    }

    private sealed class MismatchedPlatformCompletion : IChatCompletionService
    {
        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
            AiChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AiGenerationResult> CompleteAsync(
            AiChatRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiGenerationResult(
                """
                {"title":"Launch","platform":"linkedin","socialType":"announcement","language":"en","posts":[{"order":1,"hook":"Launch","body":"A launch update.","callToAction":null,"hashtags":[],"altText":null,"visualDirection":null,"assetRefs":[]}]}
                """,
                new AiUsageMetadata("test", "test", 1, null, 1, 0m, 0m, 1, "completed", true)));
    }
}
