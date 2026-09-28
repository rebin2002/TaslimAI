using System.Text.Json;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

/// <summary>
/// Creative work types are internal routing inputs. They are deliberately not exposed as
/// provider or model identifiers in Movie Director contracts.
/// </summary>
public static class DirectorCreativeTaskTypes
{
    public const string DevelopPremise = DirectorStoryActionTypes.DevelopPremise;
    public const string ImproveLogline = DirectorStoryActionTypes.ImproveLogline;
    public const string ExpandSynopsis = DirectorStoryActionTypes.ExpandSynopsis;
    public const string CreateOrRefineTreatment = DirectorStoryActionTypes.CreateOrRefineTreatment;
    public const string ProposeScreenplayScene = DirectorStoryActionTypes.ProposeScreenplayScene;
    public const string RewriteSelectedPassage = DirectorStoryActionTypes.RewriteSelectedPassage;
    public const string ImproveDialogue = DirectorStoryActionTypes.ImproveDialogue;
    public const string TightenPacing = DirectorStoryActionTypes.TightenPacing;
    public const string IdentifyInconsistencies = DirectorStoryActionTypes.IdentifyInconsistencies;

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        DevelopPremise, ImproveLogline, ExpandSynopsis, CreateOrRefineTreatment,
        ProposeScreenplayScene, RewriteSelectedPassage, ImproveDialogue, TightenPacing,
        IdentifyInconsistencies,
    };
}

public sealed record DirectorCreativeRoutingRequest(
    string TaskType,
    int Importance,
    int Complexity,
    int BudgetSensitivity,
    string RequestedQuality = DirectorQualityLevels.Auto,
    decimal? BudgetLimitUsd = null,
    int InputTokens = 1_000,
    int OutputTokens = 0);

public sealed record DirectorCreativeCostRequest(
    string QualityLevel,
    int InputTokens,
    int OutputTokens);

public sealed record DirectorCreativeQualityRecommendation(
    string TaskType,
    string QualityLevel,
    string SelectionMode,
    string AiCoreTier,
    decimal ImportanceScore,
    decimal ComplexityScore,
    decimal BudgetSensitivityScore,
    decimal? EstimatedCostUsd,
    IReadOnlyList<string> Reasons,
    bool BudgetConstrained);

public interface IDirectorCreativeCostEstimator
{
    DirectorCostEstimate Estimate(DirectorCreativeCostRequest request);
}

/// <summary>
/// Uses the existing AI Core router and catalog-backed cost calculator. The selected
/// provider/model never leaves this internal boundary.
/// </summary>
public sealed class DirectorCreativeCostEstimator(
    IAiModelRouter router,
    IAiCostCalculator costCalculator) : IDirectorCreativeCostEstimator
{
    public DirectorCostEstimate Estimate(DirectorCreativeCostRequest request)
    {
        var aiTier = DirectorQualityRouting.ToAiCoreTier(request.QualityLevel);
        var aiRequest = new AiChatRequest(
            [],
            "Internal Movie Director creative cost estimate.",
            aiTier,
            MaxOutputTokens: Math.Clamp(request.OutputTokens, 1, 32_000),
            JsonMode: true,
            StructuredOutput: DirectorStoryAiSchema.Spec);

        AiProviderSelection selection;
        try
        {
            selection = router.Select(aiRequest);
        }
        catch (AiProviderUnavailableException)
        {
            return new(false, null, UsageCurrencies.Usd, "ai_provider_unavailable");
        }

        var usage = new AiUsageMetadata(
            selection.ProviderKey,
            selection.ModelKey,
            Math.Max(0, request.InputTokens),
            null,
            Math.Max(0, request.OutputTokens),
            null,
            null,
            0,
            "estimate",
            selection.IsTestProvider);
        var amount = costCalculator.Calculate(usage);
        return amount.HasValue
            ? new(true, amount, UsageCurrencies.Usd, null)
            : new(false, null, UsageCurrencies.Usd, "ai_pricing_unavailable");
    }
}

/// <summary>
/// Maps the four customer-facing Movie quality tiers to the existing three AI Core
/// capability tiers. Studio is a quality tier, not a new AI Core tier or provider.
/// </summary>
public static class DirectorQualityRouting
{
    public static string ToAiCoreTier(string qualityLevel) => NormalizeQuality(qualityLevel) switch
    {
        DirectorQualityLevels.Fast => "Fast",
        DirectorQualityLevels.Standard => "Smart",
        DirectorQualityLevels.Cinematic => "Advanced",
        DirectorQualityLevels.Studio => "Advanced",
        _ => "Fast",
    };

    public static string NormalizeQuality(string? qualityLevel) => DirectorQualityLevels.QualityTiers
        .FirstOrDefault(item => string.Equals(item, qualityLevel?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? DirectorQualityLevels.Fast;

    public static int QualityRank(string qualityLevel) => NormalizeQuality(qualityLevel) switch
    {
        DirectorQualityLevels.Fast => 0,
        DirectorQualityLevels.Standard => 1,
        DirectorQualityLevels.Cinematic => 2,
        DirectorQualityLevels.Studio => 3,
        _ => 0,
    };

    public static string QualityAtRank(int rank) => Math.Clamp(rank, 0, 3) switch
    {
        0 => DirectorQualityLevels.Fast,
        1 => DirectorQualityLevels.Standard,
        2 => DirectorQualityLevels.Cinematic,
        _ => DirectorQualityLevels.Studio,
    };
}

public static class DirectorCreativeRoutingProfiles
{
    public static (int ComplexityBias, int OutputTokens) For(string taskType) => taskType.Trim().ToLowerInvariant() switch
    {
        DirectorCreativeTaskTypes.DevelopPremise => (-20, 700),
        DirectorCreativeTaskTypes.ImproveLogline => (-20, 700),
        DirectorCreativeTaskTypes.TightenPacing => (-10, 900),
        DirectorCreativeTaskTypes.IdentifyInconsistencies => (-15, 900),
        DirectorCreativeTaskTypes.ExpandSynopsis => (0, 1_400),
        DirectorCreativeTaskTypes.ImproveDialogue => (0, 1_400),
        DirectorCreativeTaskTypes.RewriteSelectedPassage => (5, 1_800),
        DirectorCreativeTaskTypes.ProposeScreenplayScene => (15, 2_400),
        DirectorCreativeTaskTypes.CreateOrRefineTreatment => (25, 3_200),
        _ => (0, 1_000),
    };
}

public sealed class DirectorCreativeQualityPlanner(IDirectorCreativeCostEstimator costEstimator)
{
    public DirectorCreativeQualityRecommendation Recommend(DirectorCreativeRoutingRequest request)
    {
        var taskType = DirectorCreativeTaskTypes.All.FirstOrDefault(item => string.Equals(item, request.TaskType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (taskType is null) throw new DirectorValidationException("Choose a supported Director creative task.");
        if (request.BudgetLimitUsd is < 0) throw new DirectorValidationException("Budget limit cannot be negative.");

        var importance = Normalize(request.Importance);
        var complexity = Normalize(request.Complexity);
        var budgetSensitivity = Normalize(request.BudgetSensitivity);
        var profile = DirectorCreativeRoutingProfiles.For(taskType);
        var adjustedComplexity = Math.Clamp(request.Complexity + profile.ComplexityBias, 0, 100);
        complexity = Normalize(adjustedComplexity);
        var score = decimal.Round((importance * 0.45m) + (complexity * 0.35m) + ((1m - budgetSensitivity) * 0.20m), 2);
        var requested = request.RequestedQuality?.Trim() ?? DirectorQualityLevels.Auto;
        var selectionMode = DirectorQualityLevels.Selectable.FirstOrDefault(item => string.Equals(item, requested, StringComparison.OrdinalIgnoreCase)) ?? DirectorQualityLevels.Auto;
        var level = selectionMode == DirectorQualityLevels.Auto
            ? score >= 0.88m ? DirectorQualityLevels.Studio
            : score >= 0.68m ? DirectorQualityLevels.Cinematic
            : score >= 0.42m ? DirectorQualityLevels.Standard
            : DirectorQualityLevels.Fast
            : selectionMode;

        var reasons = new List<string>
        {
            importance >= 0.7m ? "high_story_importance" : "lower_story_importance",
            complexity >= 0.7m ? "high_creative_complexity" : "bounded_creative_complexity",
            budgetSensitivity >= 0.7m ? "budget_sensitive" : "budget_flexible",
            $"task_{taskType}",
        };
        var estimate = Estimate(level, request, profile.OutputTokens);
        var constrained = false;
        if (request.BudgetLimitUsd.HasValue && estimate.IsKnown && estimate.AmountUsd > request.BudgetLimitUsd.Value)
        {
            for (var rank = DirectorQualityRouting.QualityRank(level) - 1; rank >= 0; rank--)
            {
                var candidate = DirectorQualityRouting.QualityAtRank(rank);
                var candidateEstimate = Estimate(candidate, request, profile.OutputTokens);
                if (!candidateEstimate.IsKnown || candidateEstimate.AmountUsd <= request.BudgetLimitUsd.Value)
                {
                    level = candidate;
                    estimate = candidateEstimate;
                    constrained = true;
                    reasons.Add("budget_limit_selected_lower_quality");
                    break;
                }
            }
        }

        return new DirectorCreativeQualityRecommendation(
            taskType,
            level,
            selectionMode,
            DirectorQualityRouting.ToAiCoreTier(level),
            importance,
            complexity,
            budgetSensitivity,
            estimate.AmountUsd,
            reasons,
            constrained);
    }

    private DirectorCostEstimate Estimate(string qualityLevel, DirectorCreativeRoutingRequest request, int profileOutputTokens) =>
        costEstimator.Estimate(new DirectorCreativeCostRequest(
            qualityLevel,
            Math.Clamp(request.InputTokens, 256, 64_000),
            Math.Clamp(request.OutputTokens > 0 ? request.OutputTokens : profileOutputTokens, 1, 32_000)));

    private static decimal Normalize(int value) => Math.Clamp(value, 0, 100) / 100m;
}

public sealed record DirectorStoryAiGeneration(DirectorStoryProposalPlan Plan, AiUsageMetadata? Usage);

/// <summary>
/// Story proposals use the existing AI Core structured-output path. A deterministic
/// proposal remains the safe development/test fallback when the mock provider or a
/// provider returns non-canonical output; no provider details are exposed.
/// </summary>
public sealed class MovieDirectorStoryAiService(
    IChatCompletionService completion,
    DirectorStoryProposalPlanner deterministicPlanner,
    DirectorCreativeQualityPlanner qualityPlanner)
{
    public async Task<DirectorStoryAiGeneration> BuildAsync(
        DirectorProposalRequest request,
        DirectorStoryBoundedContextDto context,
        CancellationToken cancellationToken = default)
    {
        var taskType = DirectorStoryActionTypes.Normalize(request.StoryAction);
        var profile = DirectorCreativeRoutingProfiles.For(taskType);
        var serializedContext = JsonSerializer.Serialize(context, DirectorJson.Options);
        var routing = qualityPlanner.Recommend(new DirectorCreativeRoutingRequest(
            taskType,
            request.Importance,
            request.Complexity,
            request.BudgetSensitivity,
            request.RequestedQuality,
            request.BudgetLimitUsd,
            Math.Clamp((serializedContext.Length + 3) / 4, 256, 64_000),
            profile.OutputTokens));
        var fallback = deterministicPlanner.Build(request, context, routing);
        var aiRequest = new AiChatRequest(
            [new AiChatMessage("user", JsonSerializer.Serialize(new
            {
                task = taskType,
                goal = request.Goal,
                context,
                deterministicReview = fallback.Review,
            }, DirectorJson.Options))],
            "You are Taslim Movie Director. Return only the bounded JSON schema. Preserve locked guide constraints, do not invent provider or model metadata, and make the requested creative change reviewable.",
            routing.AiCoreTier,
            MaxOutputTokens: profile.OutputTokens,
            JsonMode: true,
            StructuredOutput: DirectorStoryAiSchema.Spec);

        try
        {
            var generation = await completion.CompleteAsync(aiRequest, cancellationToken);
            if (DirectorStoryAiDraft.TryParse(generation.Content, out var draft))
                return new(ApplyDraft(fallback, draft!), generation.Usage);
            return new(fallback, generation.Usage);
        }
        catch (AiProviderUnavailableException)
        {
            return new(fallback, null);
        }
        catch (AiGenerationException)
        {
            return new(fallback, null);
        }
    }

    private static DirectorStoryProposalPlan ApplyDraft(DirectorStoryProposalPlan fallback, DirectorStoryAiDraft draft)
    {
        var payload = fallback.Payload;
        var action = payload.Action;
        if (action == DirectorStoryActionTypes.DevelopPremise && !string.IsNullOrWhiteSpace(draft.Premise)) payload = payload with { Premise = Bound(draft.Premise, 8_000) };
        if (action == DirectorStoryActionTypes.ImproveLogline && !string.IsNullOrWhiteSpace(draft.Logline)) payload = payload with { Logline = Bound(draft.Logline, 2_000) };
        if (action == DirectorStoryActionTypes.ExpandSynopsis && !string.IsNullOrWhiteSpace(draft.Synopsis)) payload = payload with { Synopsis = Bound(draft.Synopsis, 20_000) };
        if (action == DirectorStoryActionTypes.CreateOrRefineTreatment && !string.IsNullOrWhiteSpace(draft.Treatment)) payload = payload with { Treatment = Bound(draft.Treatment, 40_000) };
        if (action == DirectorStoryActionTypes.RewriteSelectedPassage && !string.IsNullOrWhiteSpace(draft.ReplacementContent)) payload = payload with { ReplacementContent = Bound(draft.ReplacementContent, 20_000) };
        if (action == DirectorStoryActionTypes.ImproveDialogue && !string.IsNullOrWhiteSpace(draft.ReplacementContent)) payload = payload with { ReplacementContent = Bound(draft.ReplacementContent, 20_000) };
        if (draft.Findings is { Count: > 0 } && action == DirectorStoryActionTypes.IdentifyInconsistencies) payload = payload with { Findings = draft.Findings.Take(20).Select(item => Bound(item, 500)).ToArray() };

        var changes = payload.Changes.Select(change => change with
        {
            ProposedContent = change.Field switch
            {
                "premise" => payload.Premise ?? change.ProposedContent,
                "logline" => payload.Logline ?? change.ProposedContent,
                "synopsis" => payload.Synopsis ?? change.ProposedContent,
                "treatment" => payload.Treatment ?? change.ProposedContent,
                "screenplay_passage" or "dialogue" => payload.ReplacementContent ?? change.ProposedContent,
                _ => change.ProposedContent,
            },
        }).ToArray();
        var review = fallback.Review with { Changes = changes, Findings = payload.Findings };
        return fallback with { Payload = payload with { Changes = changes }, Review = review };
    }

    private static string Bound(string value, int max) => value.Length <= max ? value.Trim() : value[..max].Trim();
}

public sealed class DirectorStoryAiDraft
{
    public string? Premise { get; set; }
    public string? Logline { get; set; }
    public string? Synopsis { get; set; }
    public string? Treatment { get; set; }
    public string? ReplacementContent { get; set; }
    public List<string>? Findings { get; set; }

    public static bool TryParse(string? content, out DirectorStoryAiDraft? draft)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(content)) return false;
        var json = content.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) json = json[(firstLine + 1)..lastFence].Trim();
        }
        try
        {
            draft = JsonSerializer.Deserialize<DirectorStoryAiDraft>(json, DirectorJson.Options);
            return draft is not null;
        }
        catch (JsonException) { return false; }
    }
}

public static class DirectorStoryAiSchema
{
    public static readonly AiStructuredOutputSpec Spec = new(
        "taslim_movie_director_story",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                premise = new { type = new[] { "string", "null" } },
                logline = new { type = new[] { "string", "null" } },
                synopsis = new { type = new[] { "string", "null" } },
                treatment = new { type = new[] { "string", "null" } },
                replacementContent = new { type = new[] { "string", "null" } },
                findings = new { type = "array", items = new { type = "string" } },
            },
        }));
}
