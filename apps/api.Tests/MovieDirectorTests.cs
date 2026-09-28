using System.Runtime.CompilerServices;
using Taslim.Api.Ai;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorTests
{
    [Fact]
    public void Auto_director_selects_studio_for_important_complex_shot()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(2m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 100, Complexity: 100, BudgetSensitivity: 0, DurationSeconds: 10,
            RequiresContinuity: true, RequestedQuality: DirectorQualityLevels.Auto));

        Assert.Equal(DirectorQualityLevels.Studio, recommendation.QualityLevel);
        Assert.Equal(DirectorQualityLevels.Auto, recommendation.SelectionMode);
        Assert.Contains("high_story_importance", recommendation.Reasons);
        Assert.Contains("high_shot_complexity", recommendation.Reasons);
    }

    [Fact]
    public void Auto_director_selects_fast_for_low_importance_budget_sensitive_shot()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(1m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 0, Complexity: 0, BudgetSensitivity: 100, DurationSeconds: 10,
            RequiresContinuity: false, RequestedQuality: DirectorQualityLevels.Auto));

        Assert.Equal(DirectorQualityLevels.Fast, recommendation.QualityLevel);
        Assert.True(recommendation.BudgetSensitivityScore > 0.99m);
    }

    [Fact]
    public void Budget_limit_downgrades_auto_recommendation_without_spending()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(12m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 100, Complexity: 100, BudgetSensitivity: 0, DurationSeconds: 10,
            RequiresContinuity: true, RequestedQuality: DirectorQualityLevels.Auto, BudgetLimitUsd: 5m));

        Assert.Equal(DirectorQualityLevels.Fast, recommendation.QualityLevel);
        Assert.True(recommendation.BudgetConstrained);
        Assert.Contains("budget_limit_selected_lower_quality", recommendation.Reasons);
    }

    [Fact]
    public void Explicit_quality_remains_a_user_choice()
    {
        var planner = new DirectorQualityPlanner(new FixedCostEstimator(4m));

        var recommendation = planner.Recommend(new DirectorShotPlanningRequest(
            Guid.NewGuid(), Importance: 0, Complexity: 0, BudgetSensitivity: 100, DurationSeconds: 10,
            RequiresContinuity: false, RequestedQuality: DirectorQualityLevels.Cinematic));

        Assert.Equal(DirectorQualityLevels.Cinematic, recommendation.QualityLevel);
        Assert.Equal(DirectorQualityLevels.Cinematic, recommendation.SelectionMode);
    }

    [Theory]
    [InlineData(DirectorQualityLevels.Fast, "Fast")]
    [InlineData(DirectorQualityLevels.Standard, "Smart")]
    [InlineData(DirectorQualityLevels.Cinematic, "Advanced")]
    [InlineData(DirectorQualityLevels.Studio, "Advanced")]
    public void Creative_quality_tiers_reuse_existing_ai_core_tiers(string quality, string aiCoreTier)
    {
        Assert.Contains(quality, DirectorQualityLevels.QualityTiers);
        Assert.Equal(aiCoreTier, DirectorQualityRouting.ToAiCoreTier(quality));
    }

    [Fact]
    public void Auto_story_routing_uses_fast_for_a_simple_budget_sensitive_operation()
    {
        var planner = new DirectorCreativeQualityPlanner(new TieredCreativeCostEstimator());

        var recommendation = planner.Recommend(new DirectorCreativeRoutingRequest(
            DirectorCreativeTaskTypes.ImproveLogline,
            Importance: 0,
            Complexity: 0,
            BudgetSensitivity: 100,
            RequestedQuality: DirectorQualityLevels.Auto,
            InputTokens: 300,
            OutputTokens: 500));

        Assert.Equal(DirectorQualityLevels.Fast, recommendation.QualityLevel);
        Assert.Equal(DirectorQualityLevels.Auto, recommendation.SelectionMode);
        Assert.Equal("Fast", recommendation.AiCoreTier);
    }

    [Fact]
    public void Auto_story_routing_can_select_studio_for_complex_important_treatment()
    {
        var planner = new DirectorCreativeQualityPlanner(new TieredCreativeCostEstimator());

        var recommendation = planner.Recommend(new DirectorCreativeRoutingRequest(
            DirectorCreativeTaskTypes.CreateOrRefineTreatment,
            Importance: 100,
            Complexity: 100,
            BudgetSensitivity: 0,
            RequestedQuality: DirectorQualityLevels.Auto,
            InputTokens: 8_000,
            OutputTokens: 3_200));

        Assert.Equal(DirectorQualityLevels.Studio, recommendation.QualityLevel);
        Assert.Equal("Advanced", recommendation.AiCoreTier);
        Assert.Contains("high_creative_complexity", recommendation.Reasons);
    }

    [Fact]
    public void Budget_limit_downgrades_creative_quality_without_adding_a_new_tier()
    {
        var planner = new DirectorCreativeQualityPlanner(new TieredCreativeCostEstimator());

        var recommendation = planner.Recommend(new DirectorCreativeRoutingRequest(
            DirectorCreativeTaskTypes.CreateOrRefineTreatment,
            Importance: 100,
            Complexity: 100,
            BudgetSensitivity: 0,
            RequestedQuality: DirectorQualityLevels.Auto,
            BudgetLimitUsd: 0.05m,
            InputTokens: 1_000,
            OutputTokens: 1_000));

        Assert.Equal(DirectorQualityLevels.Standard, recommendation.QualityLevel);
        Assert.True(recommendation.BudgetConstrained);
        Assert.DoesNotContain(DirectorQualityLevels.Auto, DirectorQualityLevels.QualityTiers);
        Assert.Contains("budget_limit_selected_lower_quality", recommendation.Reasons);
    }

    [Fact]
    public async Task Story_ai_service_routes_treatment_to_existing_ai_core_without_exposing_provider_metadata()
    {
        var completion = new RecordingDirectorCompletion();
        var service = new MovieDirectorStoryAiService(
            completion,
            new DirectorStoryProposalPlanner(),
            new DirectorCreativeQualityPlanner(new TieredCreativeCostEstimator()));
        var request = new DirectorProposalRequest
        {
            StoryAction = DirectorStoryActionTypes.CreateOrRefineTreatment,
            Goal = "Strengthen the treatment arc.",
            RequestedQuality = DirectorQualityLevels.Auto,
            Importance = 100,
            Complexity = 100,
            BudgetSensitivity = 0,
        };
        var projectId = Guid.NewGuid();
        var context = new DirectorStoryBoundedContextDto(
            projectId,
            Guid.NewGuid(),
            "A locked movie brief.",
            new DirectorGuideContext("visual", "camera", "color", "sound", "continuity"),
            null,
            null,
            null,
            [],
            [],
            [],
            DateTime.UtcNow);

        var result = await service.BuildAsync(request, context);

        Assert.Equal("Advanced", completion.LastRequest!.RequestedTier);
        Assert.NotNull(completion.LastRequest.StructuredOutput);
        Assert.Equal(DirectorQualityLevels.Studio, result.Plan.Payload.QualityLevel);
        Assert.DoesNotContain("ProviderKey", completion.LastRequest.SystemInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ModelKey", completion.LastRequest.SystemInstruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unavailable_story_ai_fails_without_a_deterministic_creative_plan()
    {
        var service = CreateStoryAiService(new UnavailableDirectorCompletion());

        var exception = await Assert.ThrowsAsync<DirectorStoryCreativeException>(() => service.BuildAsync(CreateTreatmentRequest(), CreateContext()));

        Assert.Equal(DirectorStoryCreativeFailureCodes.Unavailable, exception.Code);
        Assert.DoesNotContain("protagonist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_story_ai_fails_without_a_deterministic_creative_plan()
    {
        var service = CreateStoryAiService(new MalformedDirectorCompletion());

        var exception = await Assert.ThrowsAsync<DirectorStoryCreativeException>(() => service.BuildAsync(CreateTreatmentRequest(), CreateContext()));

        Assert.Equal(DirectorStoryCreativeFailureCodes.Invalid, exception.Code);
        Assert.DoesNotContain("protagonist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MovieDirectorStoryAiService CreateStoryAiService(IChatCompletionService completion) => new(
        completion,
        new DirectorStoryProposalPlanner(),
        new DirectorCreativeQualityPlanner(new TieredCreativeCostEstimator()));

    private static DirectorProposalRequest CreateTreatmentRequest() => new()
    {
        StoryAction = DirectorStoryActionTypes.CreateOrRefineTreatment,
        RequestedQuality = DirectorQualityLevels.Auto,
        Importance = 100,
        Complexity = 100,
        BudgetSensitivity = 0,
    };

    private static DirectorStoryBoundedContextDto CreateContext() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "A locked movie brief.",
        new DirectorGuideContext("visual", "camera", "color", "sound", "continuity"),
        null,
        null,
        null,
        [],
        [],
        [],
        DateTime.UtcNow);

    private sealed class FixedCostEstimator(decimal amount) : IDirectorCostEstimator
    {
        public DirectorCostEstimate Estimate(DirectorCostRequest request) => new(true, amount, "USD", null);
    }

    private sealed class TieredCreativeCostEstimator : IDirectorCreativeCostEstimator
    {
        public DirectorCostEstimate Estimate(DirectorCreativeCostRequest request) => new(
            true,
            request.QualityLevel switch
            {
                DirectorQualityLevels.Fast => 0.01m,
                DirectorQualityLevels.Standard => 0.05m,
                DirectorQualityLevels.Cinematic => 0.10m,
                DirectorQualityLevels.Studio => 0.20m,
                _ => 1m,
            },
            "USD",
            null);
    }

    private sealed class RecordingDirectorCompletion : IChatCompletionService
    {
        public AiChatRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AiGenerationResult("{\"premise\":\"AI premise\",\"logline\":\"AI logline\",\"synopsis\":\"AI synopsis\",\"treatment\":\"AI treatment\",\"findings\":[]}", new AiUsageMetadata("internal-test", "internal-test", 20, null, 10, 0m, 0m, 1, "completed", true)));
        }
    }

    private sealed class UnavailableDirectorCompletion : IChatCompletionService
    {
        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => throw new AiProviderUnavailableException();
    }

    private sealed class MalformedDirectorCompletion : IChatCompletionService
    {
        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiGenerationResult("{}", new AiUsageMetadata("test", "test", 1, null, 1, 0m, 0m, 1, "completed", true)));
    }
}
