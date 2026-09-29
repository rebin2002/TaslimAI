using System.Text.Json;
using System.Text.RegularExpressions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class DirectorStoryActionTypes
{
    public const string DevelopPremise = "develop_premise";
    public const string ImproveLogline = "improve_logline";
    public const string ExpandSynopsis = "expand_synopsis";
    public const string DevelopSynopsis = "develop_synopsis";
    public const string CreateOrRefineTreatment = "create_refine_treatment";
    public const string ProposeScreenplayScene = "propose_screenplay_scene";
    public const string RewriteSelectedPassage = "rewrite_selected_passage";
    public const string ImproveDialogue = "improve_dialogue";
    public const string TightenPacing = "tighten_pacing";
    public const string IdentifyInconsistencies = "identify_story_inconsistencies";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        DevelopPremise, ImproveLogline, ExpandSynopsis, DevelopSynopsis, CreateOrRefineTreatment,
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
    int ContextVersion = 1,
    int DurationSeconds = 0,
    string Language = LanguageCodes.English,
    DirectorWorldContext? World = null);

public static class DirectorStoryFindingTypes
{
    public const string HardContinuityConflict = "hard_continuity_conflict";
    public const string PossibleInconsistency = "possible_inconsistency";
    public const string CreativeSuggestion = "creative_suggestion";
}
public static class DirectorStoryFindingSeverities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Suggestion = "suggestion";
    public const string Info = "info";
}
public static class DirectorStoryFindingCategories
{
    public const string ApprovedStory = "approved_story";
    public const string LockedMovieGuide = "locked_movie_guide";
    public const string CastContinuity = "cast_continuity";
    public const string CharacterPresence = "character_presence";
    public const string Wardrobe = "wardrobe";
    public const string InjuryState = "injury_state";
    public const string WorldContinuity = "world_continuity";
    public const string EnvironmentLocation = "environment_location";
    public const string TimeOfDay = "time_of_day";
    public const string Weather = "weather";
    public const string ObjectState = "object_state";
    public const string ObjectPosition = "object_position";
    public const string ProductionContinuity = "production_continuity";
    public const string CharacterState = "character_state";
    public const string Chronology = "chronology";
    public const string ScreenplayFact = "screenplay_fact";
    public const string Coverage = "coverage";
}
public sealed record DirectorStoryEvidenceDto(string Source, string? SourceType, Guid? SourceId, string? Revision, string Excerpt);
public sealed record DirectorStoryFindingTargetDto(string TargetType, Guid? TargetId, string? Label);
public sealed record DirectorStoryFindingDto(
    string FindingType,
    string Severity,
    string Category,
    IReadOnlyList<DirectorStoryEvidenceDto> Evidence,
    DirectorStoryFindingTargetDto AffectedTarget,
    string Explanation,
    string SuggestedCorrection,
    decimal Confidence,
    string? Uncertainty);

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
    bool AppliesToStory,
    MovieSynopsisDevelopmentDto? SynopsisDevelopment = null,
    IReadOnlyList<DirectorStoryFindingDto>? GroundedFindings = null);

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
    MovieSynopsisDevelopmentDto? SynopsisDevelopment = null,
    IReadOnlyList<DirectorStoryFindingDto>? GroundedFindings = null,
    string QualityLevel = DirectorQualityLevels.Fast,
    string AiCoreTier = "Fast",
    decimal? EstimatedCostUsd = null);

public sealed record DirectorStoryApplyResult(
    bool Applied,
    Guid? RevisionId,
    string SafeMessage,
    IReadOnlyList<string> Findings,
    IReadOnlyList<DirectorStoryFindingDto>? GroundedFindings = null);

public sealed record DirectorStoryProposalPlan(
    DirectorStoryActionPayload Payload,
    DirectorStoryReviewDto Review,
    string Title,
    string Summary,
    IReadOnlyList<string> Rationale,
    DirectorCreativeQualityRecommendation? Routing = null);

public sealed class DirectorStoryProposalPlanner(IMovieSynopsisDevelopmentService? synopsisDevelopment = null, IMovieDirectorCreativeOutputValidator? outputValidator = null)
{
    private readonly IMovieDirectorCreativeOutputValidator validator = outputValidator ?? new MovieDirectorCreativeOutputValidator(
        Microsoft.Extensions.Options.Options.Create(new MovieDirectorCreativeOutputValidationOptions()),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MovieDirectorCreativeOutputValidator>.Instance);
    public async Task<DirectorStoryProposalPlan> BuildAsync(DirectorProposalRequest request, DirectorStoryBoundedContextDto context, CancellationToken cancellationToken = default)
    {
        var action = DirectorStoryActionTypes.Normalize(request.StoryAction);
        if (action is not (DirectorStoryActionTypes.ExpandSynopsis or DirectorStoryActionTypes.DevelopSynopsis))
            return Build(request, context);

        var source = context.CurrentRevision ?? context.ApprovedRevision;
        if (synopsisDevelopment is null) throw new DirectorSynopsisGenerationException("The synopsis development provider is not configured.");
        var draft = await synopsisDevelopment.GenerateAsync(context, source, cancellationToken);
        return BuildSynopsisPlan(action, source, draft, context.DurationSeconds);
    }

    public DirectorStoryProposalPlan Build(DirectorProposalRequest request, DirectorStoryBoundedContextDto context)
    {
        var action = DirectorStoryActionTypes.Normalize(request.StoryAction);
        if (action.Length == 0) throw new DirectorValidationException("Choose a supported Story assistance action.");

        var source = context.CurrentRevision ?? context.ApprovedRevision;
        var baseRevisionId = source?.RevisionId;
        var changes = new List<DirectorStoryFieldChangeDto>();
        var findings = new List<string>();
        string? premise = null;
        string? logline = null;
        string? synopsis = null;
        string? treatment = null;
        Guid? targetSceneId = request.TargetSceneId;
        Guid? targetElementId = request.TargetElementId;
        MovieStorySceneRequest? proposedScene = null;
        string? replacement = null;
        IReadOnlyList<DirectorStoryFindingDto>? groundedFindings = null;

        switch (action)
        {
            case DirectorStoryActionTypes.DevelopPremise:
                var premiseAnalysis = AnalyzePremise(context, source);
                premise = RenderPremise(context, premiseAnalysis);
                changes.Add(new("premise", null, Bound(source?.Premise), premise));
                findings.Add("premise_source_facts_preserved");
                if (premiseAnalysis.SourceFacts.Count > 0 && premiseAnalysis.ProposedAdditions.Count > 0) findings.Add("premise_creative_additions_explicit");
                findings.Add("creative_additions_goal_stakes_choice_emotional_engine_theme");
                if (IsGenericBoilerplate(source?.Premise)) findings.Add("generic_premise_boilerplate_replaced");
                findings.Add($"premise_language_{NormalizeLanguage(context.Language)}");
                findings.Add(context.DurationSeconds is > 0 and <= 90 ? "premise_duration_short_form" : "premise_duration_full_arc");
                break;
            case DirectorStoryActionTypes.ImproveLogline:
                logline = ImproveLogline(context, source);
                changes.Add(new("logline", null, Bound(source?.Logline), logline));
                break;
            case DirectorStoryActionTypes.ExpandSynopsis:
                synopsis = ExpandSynopsis(context, source);
                changes.Add(new("synopsis", null, Bound(source?.Synopsis), synopsis));
                break;
            case DirectorStoryActionTypes.CreateOrRefineTreatment:
                treatment = RefineTreatment(context, source);
                changes.Add(new("treatment", null, Bound(source?.Treatment), treatment));
                break;
            case DirectorStoryActionTypes.ProposeScreenplayScene:
                proposedScene = ProposeScene(context, source, request.Goal);
                changes.Add(new("screenplay_scene", proposedScene.SceneIdentifier, "No scene in the current revision.", SceneText(proposedScene)));
                break;
            case DirectorStoryActionTypes.RewriteSelectedPassage:
                (targetSceneId, targetElementId, replacement) = RewritePassage(context, request, source);
                var existingPassage = source?.Scenes.SelectMany(item => item.Elements).FirstOrDefault(item => item.Id == targetElementId)?.Content ?? string.Empty;
                changes.Add(new("screenplay_passage", targetSceneId?.ToString(), Bound(existingPassage), replacement));
                break;
            case DirectorStoryActionTypes.ImproveDialogue:
                (targetSceneId, replacement) = ImproveDialogue(context, request, source);
                var dialogue = source?.Scenes.FirstOrDefault(item => item.Id == targetSceneId)?.Elements.Where(item => item.ElementType.Equals(MovieScreenplayElementTypes.Dialogue, StringComparison.OrdinalIgnoreCase)).Select(item => item.Content).FirstOrDefault() ?? string.Empty;
                changes.Add(new("dialogue", targetSceneId?.ToString(), Bound(dialogue), replacement));
                break;
            case DirectorStoryActionTypes.TightenPacing:
                synopsis = TightenPacing(context, source);
                changes.Add(new("synopsis", null, Bound(source?.Synopsis), synopsis));
                findings.Add("pacing_pass_reduces_repetition_and_surfaces_the_next_turn");
                break;
            case DirectorStoryActionTypes.IdentifyInconsistencies:
                groundedFindings = DirectorStoryConsistencyAnalyzer.Analyze(context);
                findings.AddRange(groundedFindings.Select(item => item.Explanation));
                changes.Add(new("diagnostic", null, "Current story remains unchanged.", "Director findings are review-only; no story text will be changed."));
                break;
            default:
                throw new DirectorValidationException("Choose a supported Story assistance action.");
        }

        var rawPayload = new DirectorStoryActionPayload(action, baseRevisionId, premise, logline, synopsis, treatment, targetSceneId, targetElementId, proposedScene, replacement, findings, changes, null, groundedFindings);
        var payloadValidation = validator.ValidateAndRepair(rawPayload, context);
        if (!payloadValidation.IsValid || payloadValidation.Output is null)
            throw new DirectorCreativeOutputValidationException(payloadValidation);
        var payload = payloadValidation.Output;
        changes = payload.Changes.ToList();
        var applies = !string.Equals(action, DirectorStoryActionTypes.IdentifyInconsistencies, StringComparison.OrdinalIgnoreCase);
        var review = new DirectorStoryReviewDto(action, baseRevisionId, changes, payload.Findings, applies, payload.SynopsisDevelopment, payload.GroundedFindings);
        var label = Label(action);
        return new DirectorStoryProposalPlan(payload, review, label, $"Review a bounded Director {label.ToLowerInvariant()} proposal before it becomes a new Story revision.", ["provider_independent_deterministic_proposal", "bounded_locked_guide_story_and_reference_context", applies ? "explicit_approval_required_before_story_apply" : "review_only_diagnostic"]);
    }

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
        IReadOnlyList<DirectorStoryFindingDto>? groundedFindings = action == DirectorStoryActionTypes.IdentifyInconsistencies
            ? DirectorStoryConsistencyAnalyzer.Analyze(context)
            : null;
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
            null,
            groundedFindings,
            routing.QualityLevel,
            routing.AiCoreTier,
            routing.EstimatedCostUsd);
        var validation = validator.ValidateAndRepair(payload, context);
        if (!validation.IsValid || validation.Output is null)
            throw new DirectorCreativeOutputValidationException(validation);
        payload = validation.Output;
        changes = payload.Changes.ToArray();
        var review = new DirectorStoryReviewDto(action, baseRevisionId, changes, payload.Findings, applies, payload.SynopsisDevelopment, payload.GroundedFindings);
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

    private static DirectorStoryProposalPlan BuildSynopsisPlan(string action, DirectorStoryRevisionContext? source, MovieSynopsisDevelopmentDraft draft, int durationSeconds)
    {
        var proposed = new MovieSynopsisDevelopmentDto(
            "proposed",
            MovieSynopsisScopes.ForDuration(durationSeconds).Scope,
            durationSeconds,
            draft.Synopsis,
            draft.Setup,
            draft.ProtagonistMotivation,
            draft.IncitingEvent,
            draft.Escalation,
            draft.Complications,
            draft.ClimaxChoice,
            draft.Resolution,
            draft.EmotionalArc,
            draft.CanonAnchors,
            draft.ProposedElements,
            draft.BeatCount);
        var changes = new[]
        {
            new DirectorStoryFieldChangeDto(
                "synopsis",
                null,
                Bound(source?.Synopsis),
                $"[PROPOSED — not canon until approved]\n{draft.Synopsis}"),
        };
        var review = new DirectorStoryReviewDto(action, source?.RevisionId, changes, ["structured_synopsis_development", "new_creative_details_explicitly_proposed_until_approved"], true, proposed);
        var payload = new DirectorStoryActionPayload(
            action,
            source?.RevisionId,
            source is null ? draft.Premise : null,
            source is null ? draft.Logline : null,
            draft.Synopsis,
            source is null ? draft.Treatment : null,
            null,
            null,
            null,
            null,
            ["structured_synopsis_development", "duration_bounded", "canon_preserved", "proposed_material_explicit"],
            changes,
            proposed);
        return new DirectorStoryProposalPlan(payload, review, "Develop synopsis", "Review a duration-aware synopsis proposal with a complete narrative arc before it becomes a new Story revision.", ["structured_ai_synopsis_output", "runtime_bounded_narrative_scope", "approved_and_locked_canon_preserved", "explicit_approval_required_before_story_apply"]);
    }

    private static string DevelopPremise(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source) => RenderPremise(context, AnalyzePremise(context, source));

    private static PremiseDevelopmentAnalysis AnalyzePremise(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source)
    {
        var sourceFacts = new List<string>();
        AddFact(sourceFacts, context.MovieBrief);
        AddFact(sourceFacts, source?.Premise);
        foreach (var character in context.RelevantCharacters.Take(4))
        {
            AddFact(sourceFacts, character.Name);
            AddFact(sourceFacts, character.Description);
            AddFact(sourceFacts, character.ContinuityNotes);
        }
        foreach (var location in context.RelevantWorldReferences.Take(3))
        {
            AddFact(sourceFacts, location.Name);
            AddFact(sourceFacts, location.Description);
        }

        var sourceText = string.Join(" ", sourceFacts);
        var scarceObject = MatchValue(sourceText, @"\b(?:the|a|an|her|his|their|its|my|our)\s+(?:(?:[a-z][a-z-]*['’]s)\s+)?(?:last|only|final|remaining)\s+[a-z][a-z-]*(?:\s+[a-z][a-z-]*){0,2}\b(?=\s+(?:while|but|and|because|that|who|offers?|waits?)\b|[,.!?;]|$)");
        var relationship = MatchValue(sourceText, @"\b(?:grandfather|grandmother|father|mother|parent|brother|sister|child|mentor|ancestor)\b");
        var weatherTurn = MatchValue(sourceText, @"\b(?:rain|rains|storm|snow|flood|sun|sunlight|dawn|winter|summer)\b")
            ?? MatchValue(sourceText, @"\b(?:drought|dry)\b");
        var protagonist = ResolveProtagonist(context, sourceText);
        var goal = ResolveGoal(sourceText, scarceObject);
        var setting = ResolveSetting(context, sourceText);
        var opposition = ResolveOpposition(context, sourceText);
        var stakes = ResolveStakes(sourceText, scarceObject, weatherTurn);
        var emotionalEngine = relationship is not null
            ? $"the responsibility carried through the {relationship} bond"
            : HasAny(sourceText, "community", "village", "belong", "together")
                ? "the need to belong without surrendering conviction"
                : "the pressure between safety and responsibility";
        var theme = HasAny(sourceText, "hope", "rain", "community", "village", "together")
            ? "whether hope is protected by one person or made real through collective risk"
            : "the cost of choosing responsibility over safety";
        var sourceHasSpecificAnchor = context.RelevantCharacters.Count > 0
            || context.RelevantWorldReferences.Count > 0
            || context.RelevantMovieScenes.Count > 0
            || scarceObject is not null
            || relationship is not null
            || weatherTurn is not null
            || Regex.IsMatch(sourceText, @"\b(?:young|old|child|farmer|worker|doctor|teacher|parent|sibling|leader|stranger|courier|artist|soldier)\b", RegexOptions.IgnoreCase);
        var proposedAdditions = new List<string>
        {
            $"goal: {goal}",
            $"stakes: {stakes}",
            $"emotional_engine: {emotionalEngine}",
            $"theme: {theme}",
        };
        if (weatherTurn is not null) proposedAdditions.Add($"pressure_turn: {weatherTurn} changes the meaning of the choice");
        return new PremiseDevelopmentAnalysis(sourceFacts, sourceText, protagonist, goal, setting, opposition, stakes, emotionalEngine, theme, scarceObject, relationship, weatherTurn, sourceHasSpecificAnchor, proposedAdditions);
    }

    private static string RenderPremise(DirectorStoryBoundedContextDto context, PremiseDevelopmentAnalysis analysis)
    {
        var language = NormalizeLanguage(context.Language);
        if (language == LanguageCodes.Arabic) return RenderArabicPremise(context, analysis);
        if (language == LanguageCodes.Kurdish) return RenderKurdishPremise(context, analysis);

        var subject = analysis.Protagonist.Contains(", ", StringComparison.Ordinal) ? $"{analysis.Protagonist}," : analysis.Protagonist;
        var opening = $"{subject} must {analysis.Goal} in {analysis.Setting}, while {analysis.Opposition}.";
        var pressure = analysis.WeatherTurn is not null
            ? $"As {analysis.WeatherTurn} changes the possibility of renewal, failure could mean {analysis.Stakes}."
            : $"If the plan fails, {analysis.Stakes}.";
        var choice = context.DurationSeconds is > 0 and <= 90
            ? $"That choice exposes {analysis.EmotionalEngine} and asks {analysis.Theme}."
            : $"The emotional engine is {analysis.EmotionalEngine}: the defining choice is whether to accept a shared risk rather than protect a narrower safety. The story's thematic direction is {analysis.Theme}.";
        var premise = $"{opening} {pressure} {choice}";
        if (!analysis.SourceHasSpecificAnchor && !string.IsNullOrWhiteSpace(context.MovieBrief))
            premise = $"{context.MovieBrief.Trim().TrimEnd('.')}. {premise}";
        if (IsGenericBoilerplate(premise) && analysis.SourceFacts.Count > 0)
        {
            var brief = context.MovieBrief.Trim();
            premise = $"{(brief.Length <= 280 ? brief : brief[..280]).TrimEnd('.')}. {opening} {pressure} {choice}";
        }
        return LimitPremise(premise);
    }

    private static string RenderArabicPremise(DirectorStoryBoundedContextDto context, PremiseDevelopmentAnalysis analysis)
    {
        var brief = !string.IsNullOrWhiteSpace(context.MovieBrief) ? context.MovieBrief.Trim().TrimEnd('.') : analysis.SourceText.Trim().TrimEnd('.');
        return LimitPremise($"{brief}. يتطور الصراع حول هدف الشخصية الرئيسية في مواجهة ضغط الجماعة أو القوة التي تعيقها، وتصبح المخاطرة مرتبطة بما قد يخسره من يعتمدون على القرار. يدفعها الارتباط العاطفي والمسؤولية إلى اختيار واضح، فيما يختبر الموضوع معنى الأمل حين يتحول الأمان الفردي إلى مخاطرة مشتركة.");
    }

    private static string RenderKurdishPremise(DirectorStoryBoundedContextDto context, PremiseDevelopmentAnalysis analysis)
    {
        var brief = !string.IsNullOrWhiteSpace(context.MovieBrief) ? context.MovieBrief.Trim().TrimEnd('.') : analysis.SourceText.Trim().TrimEnd('.');
        return LimitPremise($"{brief}. ملمڵانێکەکە لەسەر ئامانجی کەسایەتیی سەرەکی و ئەو بەربەستەی ڕێگری لێ دەکات دروست دەبێت، و شکستهێنان نرخێکی هەیە بۆ ئەوانەی پشت بەو بڕیارە دەبەستن. پەیوەندیی هەستی و بەرپرسیارێتییەکەی ناچار دەکات هەڵبژاردنێکی ڕوون بکات؛ چیرۆکەکەش دەپرسی ئایا هیوا بە پاراستنی تاکەکەس دەمێنێتەوە یان بە مەترسیی هاوبەش دەبێتە ڕاستی.");
    }

    private sealed record PremiseDevelopmentAnalysis(
        IReadOnlyList<string> SourceFacts,
        string SourceText,
        string Protagonist,
        string Goal,
        string Setting,
        string Opposition,
        string Stakes,
        string EmotionalEngine,
        string Theme,
        string? ScarceObject,
        string? Relationship,
        string? WeatherTurn,
        bool SourceHasSpecificAnchor,
        IReadOnlyList<string> ProposedAdditions);

    private static string ResolveProtagonist(DirectorStoryBoundedContextDto context, string sourceText)
    {
        var character = context.RelevantCharacters.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Name));
        if (character is not null)
        {
            var descriptor = MatchValue($"{character.Description} {character.ContinuityNotes}", @"\b(?:a|an|the)?\s*((?:(?:young|old|elderly|reluctant|ambitious|孤独)\s+)?[a-z][a-z-]*)\b");
            return descriptor is not null && !descriptor.Equals(character.Name, StringComparison.OrdinalIgnoreCase)
                ? $"{character.Name}, {descriptor}"
                : character.Name;
        }

        var articlePhrase = MatchValue(sourceText, @"\b(?:a|an|the)\s+((?:(?:young|old|elderly|reluctant|ambitious)\s+)?[a-z][a-z-]*(?:\s+[a-z][a-z-]*){0,2})\s+(?=(?:in|from|who|must|needs|tries|seeks|discovers|finds|works|struggles))");
        return articlePhrase ?? "the central character";
    }

    private static string ResolveGoal(string sourceText, string? scarceObject)
    {
        var modalGoal = MatchValue(sourceText, @"\b(?:must|needs to|tries to|seeks to|wants to|hopes to|works to|is determined to|struggles to|efforts to)\s+([^,.!?;]{2,100})");
        var directGoal = MatchValue(sourceText, @"\b(?:protect|save|find|deliver|escape|return|plant|defend|unite|survive|restore|build|reclaim|convince|stop|reach|choose|keep)\w*\s+(?:the|a|an|her|his|their|it|them)?\s*[a-z][^,.!?;]{0,70}");
        var goal = modalGoal is not null && !modalGoal.Contains("decide what to do", StringComparison.OrdinalIgnoreCase)
            ? modalGoal
            : directGoal ?? modalGoal;
        if (string.IsNullOrWhiteSpace(goal)) return "make the defining choice suggested by the brief";
        goal = Regex.Replace(goal.Trim(), @"^(?:her|his|their|the protagonist's)\s+(?:effort|efforts|plan|attempt|attempts)\s+to\s+", string.Empty, RegexOptions.IgnoreCase);
        if (scarceObject is not null && Regex.IsMatch(goal, @"\b(?:it|them)\b", RegexOptions.IgnoreCase))
            goal = Regex.Replace(goal, @"\b(?:it|them)\b", scarceObject, RegexOptions.IgnoreCase);
        return goal.Trim().TrimEnd('.', ',', ';');
    }

    private static string ResolveSetting(DirectorStoryBoundedContextDto context, string sourceText)
    {
        var setting = MatchValue(sourceText, @"\b(?:in|within|among|inside|across|under)\s+((?:the|a|an)?\s*[a-z][a-z-]*(?:\s+[a-z][a-z-]*){0,4})(?=\s+(?:guards?|holds?|faces?|must|while|where|as|that|who|opposes?|waits?|offers?)\b|[,.!?;]|$)");
        if (setting is not null) return setting;
        var location = context.RelevantWorldReferences.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Name));
        return location?.Name ?? "the world defined by the brief";
    }

    private static string ResolveOpposition(DirectorStoryBoundedContextDto context, string sourceText)
    {
        if (HasAny(sourceText, "community", "village", "council", "crowd", "people") && HasAny(sourceText, "oppos", "against", "refus", "block", "pressure", "threat"))
            return "the community opposing the plan";
        var opposingClause = MatchValue(sourceText, @"\b(?:but|while|even as|against)\s+([^,.!?;]{2,100})");
        if (opposingClause is not null) return opposingClause;
        var character = context.RelevantCharacters.Skip(1).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Name));
        if (character is not null) return $"the pressure exerted by {character.Name}";
        if (HasAny(sourceText, "drought", "scarcity", "war", "storm", "illness", "deadline", "famine")) return "a force that makes delay costly";
        return "opposition that tests the protagonist's plan";
    }

    private static string ResolveStakes(string sourceText, string? scarceObject, string? weatherTurn)
    {
        if (scarceObject is not null)
            return $"losing {scarceObject} could erase the remaining chance of renewal for those who depend on it";
        if (HasAny(sourceText, "survival", "death", "life", "future", "family", "home", "hope"))
            return "the future, safety, or belonging the protagonist is trying to protect";
        return weatherTurn is not null
            ? "the chance to turn a brief opening into a future for the people at risk"
            : "the trust, safety, or future the protagonist will lose if the choice fails";
    }

    private static bool IsGenericBoilerplate(string? premise)
    {
        if (string.IsNullOrWhiteSpace(premise)) return false;
        var normalized = premise.Trim().ToLowerInvariant();
        var fragments = new[]
        {
            "the choice must carry a consequence",
            "the protagonist's defining choice creates a consequence",
            "a protagonist faces a defining choice",
            "the central conflict",
            "the choice lands with a clear consequence",
        };
        return fragments.Any(normalized.Contains);
    }

    private static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        LanguageCodes.Arabic => LanguageCodes.Arabic,
        LanguageCodes.Kurdish => LanguageCodes.Kurdish,
        _ => LanguageCodes.English,
    };

    private static string LimitPremise(string value) => value.Trim().Length <= 1_800 ? value.Trim() : $"{value.Trim()[..1_797]}…";

    private static string? MatchValue(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var result = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private static bool HasAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void AddFact(ICollection<string> facts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) facts.Add(value.Trim());
    }

    private static string ImproveLogline(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source) =>
        string.IsNullOrWhiteSpace(source?.Logline)
            ? $"When the central conflict arrives in {context.RelevantMovieScenes.FirstOrDefault()?.Title ?? "an unfamiliar world"}, a determined protagonist must act before the cost becomes irreversible."
            : AppendOnce(source.Logline, " The clock, consequence, and defining choice are now explicit.");

    private static string ExpandSynopsis(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source) =>
        AppendOnce(source?.Synopsis, $" The locked guide keeps the dramatic turn grounded in {context.Guide.ContinuityRules}.");

    private static string RefineTreatment(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source) =>
        AppendOnce(source?.Treatment, $" The treatment is organized around setup, escalation, and a consequential final turn while preserving the locked visual language: {context.Guide.VisualLanguage}.");

    private static string TightenPacing(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source) =>
        AppendOnce(source?.Synopsis, " Each beat should enter later, turn sooner, and leave on the next necessary question.");

    private static MovieStorySceneRequest ProposeScene(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source, string? goal)
    {
        var number = (source?.Scenes.Count ?? 0) + 1;
        var movieScene = context.RelevantMovieScenes.LastOrDefault();
        var identifier = $"DIRECTOR-PROPOSAL-{number}";
        return new MovieStorySceneRequest
        {
            SceneIdentifier = identifier,
            ActNumber = 1,
            SequenceNumber = number,
            MovieSceneId = movieScene?.Id,
            Slugline = $"INT. {movieScene?.Title.ToUpperInvariant() ?? "STORY ROOM"} - NIGHT",
            Synopsis = goal?.Trim() is { Length: > 0 } ? goal.Trim() : "The protagonist faces the next irreversible choice.",
            Elements =
            [
                new MovieScreenplayElementRequest { ElementType = MovieScreenplayElementTypes.Action, Content = "The room holds its breath as the choice becomes unavoidable." },
                new MovieScreenplayElementRequest { ElementType = MovieScreenplayElementTypes.Dialogue, CharacterName = context.RelevantCharacters.FirstOrDefault()?.Name ?? "PROTAGONIST", Content = "Then we do it now." },
            ],
        };
    }

    private static (Guid? SceneId, Guid? ElementId, string Replacement) RewritePassage(DirectorStoryBoundedContextDto context, DirectorProposalRequest request, DirectorStoryRevisionContext? source)
    {
        if (!request.TargetSceneId.HasValue || !request.TargetElementId.HasValue) throw new DirectorValidationException("Select a screenplay scene and passage before requesting a rewrite.");
        var element = source?.Scenes.SelectMany(item => item.Elements).FirstOrDefault(item => item.Id == request.TargetElementId.Value);
        if (element is null) throw new DirectorValidationException("The selected screenplay passage is not in the bounded Story context.");
        return (request.TargetSceneId, request.TargetElementId, string.IsNullOrWhiteSpace(request.SelectedPassage) ? AppendOnce(element.Content, " The choice lands with a clear consequence.") : $"{request.SelectedPassage.Trim()} The choice lands with a clear consequence.");
    }

    private static (Guid? SceneId, string Replacement) ImproveDialogue(DirectorStoryBoundedContextDto context, DirectorProposalRequest request, DirectorStoryRevisionContext? source)
    {
        var scene = request.TargetSceneId.HasValue ? source?.Scenes.FirstOrDefault(item => item.Id == request.TargetSceneId.Value) : context.TargetScene ?? source?.Scenes.FirstOrDefault();
        if (scene is null) throw new DirectorValidationException("Select a screenplay scene before requesting dialogue improvement.");
        var dialogue = scene.Elements.FirstOrDefault(item => item.ElementType.Equals(MovieScreenplayElementTypes.Dialogue, StringComparison.OrdinalIgnoreCase));
        if (dialogue is null) throw new DirectorValidationException("The selected screenplay scene does not contain dialogue to improve.");
        return (scene.Id, AppendOnce(dialogue.Content, " We have one chance, and I am choosing it."));
    }

    private static IReadOnlyList<string> FindInconsistencies(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source)
    {
        var results = new List<string>();
        if (source is null) results.Add("story_has_no_current_revision");
        if (source is not null && source.Scenes.Count == 0) results.Add("story_has_no_screenplay_scenes");
        if (source is not null && source.Scenes.Any(scene => scene.Elements.Count == 0)) results.Add("one_or_more_screenplay_scenes_have_no_elements");
        if (context.RelevantCharacters.Count == 0) results.Add("story_has_no_bounded_cast_reference");
        if (results.Count == 0) results.Add("no_bounded_inconsistency_found");
        return results;
    }

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

    private static string AppendOnce(string? value, string suffix) => string.IsNullOrWhiteSpace(value) ? suffix.Trim() : value.Trim().EndsWith(suffix.Trim(), StringComparison.Ordinal) ? value.Trim() : $"{value.Trim()}{suffix}";
    private static string Bound(string? value, int max = 6_000) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Length <= max ? value.Trim() : $"{value[..max].Trim()}…";
    private static string SceneText(MovieStorySceneRequest scene) => $"{scene.Slugline}\n{scene.Synopsis}\n{string.Join("\n", scene.Elements.Select(item => $"{item.ElementType}: {item.Content}"))}";
}
