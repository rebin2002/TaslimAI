using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorScenePlanValidationTests
{
    [Fact]
    public void Runtime_validator_rejects_a_plan_that_exceeds_the_thirty_second_target()
    {
        var validator = CreateValidator();
        var context = CreateContext();
        var scene = Scene(31, "[insert a scene here]");
        var payload = new DirectorScenePlanActionPayload(DirectorScenePlanActionTypes.PlanScenes, context.CurrentRevision!.RevisionId, "snapshot", "en", "16:9", 30, 31, DirectorQualityLevels.Fast, "Fast", null, [scene]);

        var result = validator.Validate(payload, context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorScenePlanValidationReasonCodes.Placeholder);
    }

    [Fact]
    public async Task Unavailable_scene_plan_ai_fails_honestly_without_a_placeholder_plan()
    {
        var service = CreateService(new UnavailableScenePlanCompletion());

        var exception = await Assert.ThrowsAsync<DirectorScenePlanCreativeException>(() => service.BuildAsync(new DirectorProposalRequest { PlanScenes = true }, CreateContext(), "snapshot"));

        Assert.Equal(DirectorScenePlanCreativeFailureCodes.Unavailable, exception.Code);
        Assert.DoesNotContain("placeholder", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_scene_plan_ai_is_rejected_without_a_deterministic_fallback()
    {
        var service = CreateService(new MalformedScenePlanCompletion());

        var exception = await Assert.ThrowsAsync<DirectorScenePlanCreativeException>(() => service.BuildAsync(new DirectorProposalRequest { PlanScenes = true }, CreateContext(), "snapshot"));

        Assert.Equal(DirectorScenePlanCreativeFailureCodes.Invalid, exception.Code);
        Assert.DoesNotContain("Mara", exception.Message, StringComparison.Ordinal);
    }

    private static MovieDirectorScenePlanningAiService CreateService(IChatCompletionService completion) => new(
        completion,
        new DirectorCreativeQualityPlanner(new FixedCreativeCostEstimator()),
        CreateValidator());

    private static IMovieDirectorScenePlanValidator CreateValidator() => new MovieDirectorScenePlanValidator(
        Options.Create(new MovieDirectorScenePlanningOptions()),
        NullLogger<MovieDirectorScenePlanValidator>.Instance);

    private static DirectorSceneProposal Scene(int duration, string title = "Seed at Dawn") => new(
        1, title, "Mara protects the last seed.", "Mara discovers the seed at the dry riverbed.", "Dry riverbed", "Dawn", ["Mara"], "Move from fear to responsibility.", duration, "Open from silence into wind.", ["The last seed remains in Mara's left hand."], "Use blue hour naturalism.");

    private static DirectorStoryBoundedContextDto CreateContext() => new(
        Guid.NewGuid(), Guid.NewGuid(), "Mara protects the last seed at the dry riverbed.",
        new DirectorGuideContext("blue hour naturalism", "slow camera", "blue hour light", "wind", "The seed remains in Mara's left hand."),
        new DirectorStoryRevisionContext(Guid.NewGuid(), 1, MovieStoryRevisionStatuses.Approved, MovieStoryAuthorship.Human, "Mara protects the last seed.", "Mara carries the seed.", "The last seed crosses the dry riverbed.", "Mara chooses renewal.", []),
        null,
        null,
        [],
        [new DirectorCharacterContext("Mara", "A seed keeper.", null, null)],
        [new DirectorLocationContext("Dry riverbed", "A dry riverbed.", null)],
        DateTime.UnixEpoch,
        DurationSeconds: 30,
        Language: "en",
        AspectRatio: "16:9");

    private sealed class FixedCreativeCostEstimator : IDirectorCreativeCostEstimator
    {
        public DirectorCostEstimate Estimate(DirectorCreativeCostRequest request) => new(true, 0.01m, "USD", null);
    }

    private sealed class UnavailableScenePlanCompletion : IChatCompletionService
    {
        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => throw new AiProviderUnavailableException();
    }

    private sealed class MalformedScenePlanCompletion : IChatCompletionService
    {
        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AiGenerationResult("{\"scenes\":[{\"sequence\":1}]}", new AiUsageMetadata("test", "test", 1, null, 1, 0m, 0m, 1, "completed", true)));
    }
}
