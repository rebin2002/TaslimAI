using System.Text.Json;
using Taslim.Api.Contracts;

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
    int DurationSeconds,
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
    bool AppliesToStory,
    MovieSynopsisDevelopmentDto? SynopsisDevelopment = null);

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
    MovieSynopsisDevelopmentDto? SynopsisDevelopment = null);

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
    IReadOnlyList<string> Rationale);

public sealed class DirectorStoryProposalPlanner(IMovieSynopsisDevelopmentService synopsisDevelopment)
{
    public async Task<DirectorStoryProposalPlan> BuildAsync(DirectorProposalRequest request, DirectorStoryBoundedContextDto context, CancellationToken cancellationToken = default)
    {
        var action = DirectorStoryActionTypes.Normalize(request.StoryAction);
        if (action is not (DirectorStoryActionTypes.ExpandSynopsis or DirectorStoryActionTypes.DevelopSynopsis))
            return Build(request, context);

        var source = context.CurrentRevision ?? context.ApprovedRevision;
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

        switch (action)
        {
            case DirectorStoryActionTypes.DevelopPremise:
                premise = DevelopPremise(context, source);
                changes.Add(new("premise", null, Bound(source?.Premise), premise));
                break;
            case DirectorStoryActionTypes.ImproveLogline:
                logline = ImproveLogline(context, source);
                changes.Add(new("logline", null, Bound(source?.Logline), logline));
                break;
            case DirectorStoryActionTypes.ExpandSynopsis:
            case DirectorStoryActionTypes.DevelopSynopsis:
                throw new DirectorValidationException("Synopsis development requires the structured synopsis provider.");
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
                findings.AddRange(FindInconsistencies(context, source));
                changes.Add(new("diagnostic", null, "Current story remains unchanged.", "Director findings are review-only; no story text will be changed."));
                break;
            default:
                throw new DirectorValidationException("Choose a supported Story assistance action.");
        }

        var applies = !string.Equals(action, DirectorStoryActionTypes.IdentifyInconsistencies, StringComparison.OrdinalIgnoreCase);
        var review = new DirectorStoryReviewDto(action, baseRevisionId, changes, findings, applies);
        var payload = new DirectorStoryActionPayload(action, baseRevisionId, premise, logline, synopsis, treatment, targetSceneId, targetElementId, proposedScene, replacement, findings, changes);
        var label = Label(action);
        return new DirectorStoryProposalPlan(payload, review, label, $"Review a bounded Director {label.ToLowerInvariant()} proposal before it becomes a new Story revision.", ["provider_independent_deterministic_proposal", "bounded_locked_guide_story_and_reference_context", applies ? "explicit_approval_required_before_story_apply" : "review_only_diagnostic"]);
    }

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
        var review = new DirectorStoryReviewDto(action, source?.RevisionId, changes, ["structured_synopsis_development", "new_creative_details_are_proposed_until_approved"], true, proposed);
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

    private static string DevelopPremise(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source)
    {
        var seed = !string.IsNullOrWhiteSpace(context.MovieBrief) ? context.MovieBrief : context.RelevantMovieScenes.FirstOrDefault()?.Summary ?? "The story begins with a central conflict";
        return string.IsNullOrWhiteSpace(source?.Premise)
            ? $"{seed}. The choice must carry a consequence."
            : AppendOnce(source.Premise, " The protagonist's defining choice creates a consequence that cannot be undone.");
    }

    private static string ImproveLogline(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext? source) =>
        string.IsNullOrWhiteSpace(source?.Logline)
            ? $"When the central conflict arrives in {context.RelevantMovieScenes.FirstOrDefault()?.Title ?? "an unfamiliar world"}, a determined protagonist must act before the cost becomes irreversible."
            : AppendOnce(source.Logline, " The clock, consequence, and defining choice are now explicit.");

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
        DirectorStoryActionTypes.ExpandSynopsis => "Develop synopsis",
        DirectorStoryActionTypes.DevelopSynopsis => "Develop synopsis",
        DirectorStoryActionTypes.CreateOrRefineTreatment => "Create or refine treatment",
        DirectorStoryActionTypes.ProposeScreenplayScene => "Propose screenplay scene",
        DirectorStoryActionTypes.RewriteSelectedPassage => "Rewrite selected passage",
        DirectorStoryActionTypes.ImproveDialogue => "Improve dialogue",
        DirectorStoryActionTypes.TightenPacing => "Tighten pacing",
        _ => "Identify story inconsistencies",
    };

    private static string AppendOnce(string? value, string suffix) => string.IsNullOrWhiteSpace(value) ? suffix.Trim() : value.Trim().EndsWith(suffix.Trim(), StringComparison.Ordinal) ? value.Trim() : $"{value.Trim()}{suffix}";
    private static string Bound(string? value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Length <= 6_000 ? value : $"{value[..6_000]}…";
    private static string SceneText(MovieStorySceneRequest scene) => $"{scene.Slugline}\n{scene.Synopsis}\n{string.Join("\n", scene.Elements.Select(item => $"{item.ElementType}: {item.Content}"))}";
}
