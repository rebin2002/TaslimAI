using System.Text.Json;
using Taslim.Api.Contracts;

namespace Taslim.Api.Movies;

public static class DirectorStoryActionTypes
{
    public const string DevelopPremise = "develop_premise";
    public const string ImproveLogline = "improve_logline";
    public const string ExpandSynopsis = "expand_synopsis";
    public const string CreateOrRefineTreatment = "create_refine_treatment";
    public const string ProposeScreenplayScene = "propose_screenplay_scene";
    public const string RewriteSelectedPassage = "rewrite_selected_passage";
    public const string ImproveDialogue = "improve_dialogue";
    public const string TightenPacing = "tighten_pacing";
    public const string IdentifyInconsistencies = "identify_story_inconsistencies";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        DevelopPremise, ImproveLogline, ExpandSynopsis, CreateOrRefineTreatment,
        ProposeScreenplayScene, RewriteSelectedPassage, ImproveDialogue, TightenPacing,
        IdentifyInconsistencies,
    };

    public static string Normalize(string? value) => All.FirstOrDefault(item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
}

public sealed record DirectorStoryRevisionContext(
    Guid RevisionId,
    int RevisionNumber,
    string Status,
    string Authorship,
    string Premise,
    string Logline,
    string Synopsis,
    string Treatment,
    IReadOnlyList<DirectorScreenplaySceneContext> Scenes);

public sealed record DirectorScreenplaySceneContext(
    Guid Id,
    Guid? MovieSceneId,
    string SceneIdentifier,
    int? ActNumber,
    int? SequenceNumber,
    string Slugline,
    string? Synopsis,
    IReadOnlyList<DirectorScreenplayElementContext> Elements);

public sealed record DirectorScreenplayElementContext(
    Guid Id,
    string ElementType,
    string Content,
    string? CharacterName,
    string? Parenthetical);

public sealed record DirectorStoryBoundedContextDto(
    Guid MovieProjectId,
    Guid WorkspaceId,
    string MovieBrief,
    DirectorGuideContext Guide,
    DirectorStoryRevisionContext? CurrentRevision,
    DirectorStoryRevisionContext? ApprovedRevision,
    DirectorScreenplaySceneContext? TargetScene,
    IReadOnlyList<DirectorSceneContext> RelevantMovieScenes,
    IReadOnlyList<DirectorCharacterContext> RelevantCharacters,
    IReadOnlyList<DirectorLocationContext> RelevantWorldReferences,
    DateTime AssembledAt,
    int ContextVersion = 1);

public sealed record DirectorStoryFieldChangeDto(
    string Field,
    string? Target,
    string ExistingContent,
    string ProposedContent);

public sealed record DirectorStoryReviewDto(
    string Action,
    Guid? BaseRevisionId,
    IReadOnlyList<DirectorStoryFieldChangeDto> Changes,
    IReadOnlyList<string> Findings,
    bool AppliesToStory);

public sealed record DirectorStoryActionPayload(
    string Action,
    Guid? BaseRevisionId,
    string? Premise,
    string? Logline,
    string? Synopsis,
    string? Treatment,
    Guid? TargetSceneId,
    Guid? TargetElementId,
    MovieStorySceneRequest? ProposedScene,
    string? ReplacementContent,
    IReadOnlyList<string> Findings,
    IReadOnlyList<DirectorStoryFieldChangeDto> Changes,
    string QualityLevel = DirectorQualityLevels.Fast,
    string AiCoreTier = "Fast",
    decimal? EstimatedCostUsd = null);

public sealed record DirectorStoryApplyResult(
    bool Applied,
    Guid? RevisionId,
    string SafeMessage,
    IReadOnlyList<string> Findings);

public sealed record DirectorStoryProposalPlan(
    DirectorStoryActionPayload Payload,
    DirectorStoryReviewDto Review,
    string Title,
    string Summary,
    IReadOnlyList<string> Rationale,
    DirectorCreativeQualityRecommendation? Routing = null);

/// <summary>
/// Converts a validated AI Story response into the existing review/action contract.
/// This class performs only normalization, bounds, and validation; it never invents
/// creative prose or screenplay content.
/// </summary>
public sealed class DirectorStoryProposalPlanner
{
    public DirectorStoryProposalPlan BuildFromAiDraft(
        DirectorProposalRequest request,
        DirectorStoryBoundedContextDto context,
        DirectorCreativeQualityRecommendation routing,
        DirectorStoryAiDraft draft)
    {
        var action = DirectorStoryActionTypes.Normalize(request.StoryAction);
        if (action.Length == 0) throw new DirectorValidationException("Choose a supported Story assistance action.");
        if (!string.Equals(action, routing.TaskType, StringComparison.OrdinalIgnoreCase))
            throw new DirectorStoryCreativeException(DirectorStoryCreativeFailureCodes.Invalid, "The Story AI response did not match the requested creative task.");

        var source = context.CurrentRevision ?? context.ApprovedRevision;
        var baseRevisionId = source?.RevisionId;
        var premise = Resolve(draft.Premise, source?.Premise);
        var logline = Resolve(draft.Logline, source?.Logline);
        var synopsis = Resolve(draft.Synopsis, source?.Synopsis);
        var treatment = Resolve(draft.Treatment, source?.Treatment);
        Guid? targetSceneId = request.TargetSceneId;
        Guid? targetElementId = request.TargetElementId;
        MovieStorySceneRequest? proposedScene = null;
        string? replacement = null;
        var findings = draft.Findings ?? [];

        switch (action)
        {
            case DirectorStoryActionTypes.DevelopPremise:
                Require(draft.Premise, "premise");
                break;
            case DirectorStoryActionTypes.ImproveLogline:
                Require(draft.Logline, "logline");
                break;
            case DirectorStoryActionTypes.ExpandSynopsis:
                Require(draft.Synopsis, "synopsis");
                break;
            case DirectorStoryActionTypes.CreateOrRefineTreatment:
                Require(draft.Treatment, "treatment");
                break;
            case DirectorStoryActionTypes.ProposeScreenplayScene:
                proposedScene = ToSceneRequest(draft.ProposedScene);
                break;
            case DirectorStoryActionTypes.RewriteSelectedPassage:
                ValidatePassageTarget(request, source, out targetSceneId, out targetElementId);
                replacement = Require(draft.ReplacementContent, "replacementContent");
                break;
            case DirectorStoryActionTypes.ImproveDialogue:
                (targetSceneId, targetElementId) = ValidateDialogueTarget(request, context, source);
                replacement = Require(draft.ReplacementContent, "replacementContent");
                break;
            case DirectorStoryActionTypes.TightenPacing:
                Require(draft.Synopsis, "synopsis");
                break;
            case DirectorStoryActionTypes.IdentifyInconsistencies:
                if (draft.Findings is null) throw Invalid("findings");
                break;
            default:
                throw new DirectorValidationException("Choose a supported Story assistance action.");
        }

        if (!string.Equals(action, DirectorStoryActionTypes.IdentifyInconsistencies, StringComparison.OrdinalIgnoreCase))
        {
            Require(premise, "premise");
            Require(logline, "logline");
            Require(synopsis, "synopsis");
            Require(treatment, "treatment");
        }

        var changes = BuildChanges(action, source, premise, logline, synopsis, treatment, proposedScene, replacement, targetSceneId, findings);
        var applies = !string.Equals(action, DirectorStoryActionTypes.IdentifyInconsistencies, StringComparison.OrdinalIgnoreCase);
        var review = new DirectorStoryReviewDto(action, baseRevisionId, changes, findings, applies);
        var payload = new DirectorStoryActionPayload(
            action,
            baseRevisionId,
            premise,
            logline,
            synopsis,
            treatment,
            targetSceneId,
            targetElementId,
            proposedScene,
            replacement,
            findings,
            changes,
            routing.QualityLevel,
            routing.AiCoreTier,
            routing.EstimatedCostUsd);
        var label = Label(action);
        var rationale = new List<string>
        {
            "ai_core_structured_output_validated",
            $"director_quality_{routing.QualityLevel.ToLowerInvariant()}",
            "bounded_locked_guide_story_and_reference_context",
            applies ? "explicit_approval_required_before_story_apply" : "review_only_diagnostic",
        };
        return new DirectorStoryProposalPlan(payload, review, label, $"Review a bounded Director {label.ToLowerInvariant()} proposal before it becomes a new Story revision.", rationale, routing);
    }

    private static IReadOnlyList<DirectorStoryFieldChangeDto> BuildChanges(
        string action,
        DirectorStoryRevisionContext? source,
        string? premise,
        string? logline,
        string? synopsis,
        string? treatment,
        MovieStorySceneRequest? proposedScene,
        string? replacement,
        Guid? targetSceneId,
        IReadOnlyList<string> findings)
    {
        return action switch
        {
            DirectorStoryActionTypes.DevelopPremise => [new("premise", null, Bound(source?.Premise), premise!)],
            DirectorStoryActionTypes.ImproveLogline => [new("logline", null, Bound(source?.Logline), logline!)],
            DirectorStoryActionTypes.ExpandSynopsis or DirectorStoryActionTypes.TightenPacing => [new("synopsis", null, Bound(source?.Synopsis), synopsis!)],
            DirectorStoryActionTypes.CreateOrRefineTreatment => [new("treatment", null, Bound(source?.Treatment), treatment!)],
            DirectorStoryActionTypes.ProposeScreenplayScene => [new("screenplay_scene", proposedScene!.SceneIdentifier, "No scene in the current revision.", SceneText(proposedScene))],
            DirectorStoryActionTypes.RewriteSelectedPassage => [new("screenplay_passage", targetSceneId?.ToString(), ExistingPassage(source, targetSceneId, replacement), replacement!)],
            DirectorStoryActionTypes.ImproveDialogue => [new("dialogue", targetSceneId?.ToString(), ExistingDialogue(source, targetSceneId), replacement!)],
            _ => [new("diagnostic", null, "Current story remains unchanged.", findings.Count == 0 ? "No bounded inconsistency was returned." : string.Join("; ", findings))],
        };
    }

    private static MovieStorySceneRequest ToSceneRequest(DirectorStoryAiSceneDraft? draft)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.SceneIdentifier) || string.IsNullOrWhiteSpace(draft.Slugline) || string.IsNullOrWhiteSpace(draft.Synopsis) || draft.Elements is null || draft.Elements.Count == 0)
            throw Invalid("proposedScene");
        if (draft.Elements.Any(item => item is null || string.IsNullOrWhiteSpace(item.Content) || string.IsNullOrWhiteSpace(item.ElementType)))
            throw Invalid("proposedScene.elements");
        return new MovieStorySceneRequest
        {
            SceneIdentifier = Bound(draft.SceneIdentifier, 160),
            ActNumber = draft.ActNumber,
            SequenceNumber = draft.SequenceNumber,
            MovieSceneId = draft.MovieSceneId,
            Slugline = Bound(draft.Slugline, 500),
            Synopsis = Bound(draft.Synopsis, 8_000),
            Elements = draft.Elements.Take(80).Select(item => new MovieScreenplayElementRequest
            {
                ElementType = Bound(item.ElementType, 80),
                Content = Bound(item.Content, 8_000),
                CharacterName = string.IsNullOrWhiteSpace(item.CharacterName) ? null : Bound(item.CharacterName, 160),
                Parenthetical = string.IsNullOrWhiteSpace(item.Parenthetical) ? null : Bound(item.Parenthetical, 500),
            }).ToList(),
        };
    }

    private static void ValidatePassageTarget(DirectorProposalRequest request, DirectorStoryRevisionContext? source, out Guid? sceneId, out Guid? elementId)
    {
        if (!request.TargetSceneId.HasValue || !request.TargetElementId.HasValue) throw new DirectorValidationException("Select a screenplay scene and passage before requesting a rewrite.");
        var scene = source?.Scenes.FirstOrDefault(item => item.Id == request.TargetSceneId.Value);
        if (scene is null || scene.Elements.All(item => item.Id != request.TargetElementId.Value)) throw new DirectorValidationException("The selected screenplay passage is not in the bounded Story context.");
        sceneId = request.TargetSceneId;
        elementId = request.TargetElementId;
    }

    private static (Guid? SceneId, Guid? ElementId) ValidateDialogueTarget(DirectorProposalRequest request, DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source)
    {
        var scene = request.TargetSceneId.HasValue ? source?.Scenes.FirstOrDefault(item => item.Id == request.TargetSceneId.Value) : context.TargetScene ?? source?.Scenes.FirstOrDefault();
        if (scene is null) throw new DirectorValidationException("Select a screenplay scene before requesting dialogue improvement.");
        var dialogue = scene.Elements.FirstOrDefault(item => item.ElementType.Equals(MovieScreenplayElementTypes.Dialogue, StringComparison.OrdinalIgnoreCase));
        if (dialogue is null) throw new DirectorValidationException("The selected screenplay scene does not contain dialogue to improve.");
        return (scene.Id, dialogue.Id);
    }

    private static string ExistingPassage(DirectorStoryRevisionContext? source, Guid? sceneId, string? replacement) => Bound(source?.Scenes.FirstOrDefault(item => item.Id == sceneId)?.Elements.FirstOrDefault(item => item.Content != replacement)?.Content);
    private static string ExistingDialogue(DirectorStoryRevisionContext? source, Guid? sceneId) => Bound(source?.Scenes.FirstOrDefault(item => item.Id == sceneId)?.Elements.FirstOrDefault(item => item.ElementType.Equals(MovieScreenplayElementTypes.Dialogue, StringComparison.OrdinalIgnoreCase))?.Content);
    private static string? Resolve(string? generated, string? existing) => string.IsNullOrWhiteSpace(generated) ? existing : Bound(generated, 40_000);
    private static string Require(string? value, string field) => string.IsNullOrWhiteSpace(value) ? throw Invalid(field) : Bound(value, 40_000);
    private static DirectorStoryCreativeException Invalid(string field) => new(DirectorStoryCreativeFailureCodes.Invalid, $"The Story AI response was invalid or incomplete ({field}). Please try again.");
    private static string Label(string action) => action switch
    {
        DirectorStoryActionTypes.DevelopPremise => "Develop premise",
        DirectorStoryActionTypes.ImproveLogline => "Improve logline",
        DirectorStoryActionTypes.ExpandSynopsis => "Expand synopsis",
        DirectorStoryActionTypes.CreateOrRefineTreatment => "Create or refine treatment",
        DirectorStoryActionTypes.ProposeScreenplayScene => "Propose screenplay scene",
        DirectorStoryActionTypes.RewriteSelectedPassage => "Rewrite selected passage",
        DirectorStoryActionTypes.ImproveDialogue => "Improve dialogue",
        DirectorStoryActionTypes.TightenPacing => "Tighten pacing",
        _ => "Identify story inconsistencies",
    };
    private static string Bound(string? value, int max = 6_000) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Length <= max ? value.Trim() : $"{value[..max].Trim()}…";
    private static string SceneText(MovieStorySceneRequest scene) => $"{scene.Slugline}\n{scene.Synopsis}\n{string.Join("\n", scene.Elements.Select(item => $"{item.ElementType}: {item.Content}"))}";
}
