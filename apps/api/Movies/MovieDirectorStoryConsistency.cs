using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taslim.Api.Movies;

public static class DirectorStoryConsistencyAnalyzer
{
    private const int MaxFindings = 96;
    private static readonly (string First, string Second)[] Opposites =
    [
        ("red", "blue"), ("green", "purple"), ("black", "white"), ("yellow", "black"),
        ("day", "night"), ("daytime", "nighttime"), ("morning", "evening"), ("dawn", "dusk"),
        ("inside", "outside"), ("indoors", "outdoors"), ("interior", "exterior"),
        ("alive", "dead"), ("living", "dead"), ("injured", "uninjured"), ("wounded", "unhurt"),
        ("conscious", "unconscious"), ("wet", "dry"), ("clean", "dirty"),
        ("open", "closed"), ("locked", "unlocked"), ("left", "right"),
        ("young", "old"), ("present", "absent"), ("light", "dark"),
    ];

    public static IReadOnlyList<DirectorStoryFindingDto> Analyze(DirectorStoryBoundedContextDto context)
    {
        var findings = new List<DirectorStoryFindingDto>();
        var current = context.CurrentRevision ?? context.ApprovedRevision;
        var approved = context.ApprovedRevision;

        if (current is null)
        {
            Add(findings, Finding(
                DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning,
                DirectorStoryFindingCategories.Coverage,
                [Evidence("Story context", "story", null, null, "No current or approved Story revision was available.")],
                Target("project", context.MovieProjectId, "Story"),
                "There is no Story revision to compare with the locked Guide or continuity records. This is a coverage gap, not proof of a contradiction.",
                "Create a Story revision and run the consistency check again.", 1m,
                "No Story text was available."));
            return findings;
        }

        if (approved is null && context.CurrentRevision is not null)
        {
            Add(findings, Finding(
                DirectorStoryFindingTypes.CreativeSuggestion, DirectorStoryFindingSeverities.Info,
                DirectorStoryFindingCategories.ApprovedStory,
                [StoryEvidence(context.CurrentRevision, "Current Story revision", StoryText(context.CurrentRevision))],
                Target("story_revision", context.CurrentRevision.RevisionId, $"Current revision {context.CurrentRevision.RevisionNumber}"),
                "The current Story is being checked without an approved Story baseline. Findings below are grounded in the locked Guide and persisted continuity records only.",
                "Approve a reviewed Story revision when it becomes the authoritative screenplay baseline.", .99m,
                "Intentional draft changes cannot be distinguished from contradictions against an approved baseline."));
        }

        AnalyzeChronology(context, current, findings);
        AnalyzeCoverage(current, findings);
        AnalyzeApprovedStory(context, current, approved, findings);
        AnalyzeGuide(context, current, findings);
        AnalyzeCharacters(context, current, findings);
        AnalyzeWorld(context, current, findings);

        return findings
            .GroupBy(FindingKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(MaxFindings)
            .ToArray();
    }

    private static void AnalyzeChronology(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext current, ICollection<DirectorStoryFindingDto> findings)
    {
        var scenes = current.Scenes;
        foreach (var duplicate in scenes.Where(scene => !string.IsNullOrWhiteSpace(scene.SceneIdentifier))
                     .GroupBy(scene => scene.SceneIdentifier.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var affected = duplicate.ToArray();
            Add(findings, Finding(
                DirectorStoryFindingTypes.HardContinuityConflict, DirectorStoryFindingSeverities.Error,
                DirectorStoryFindingCategories.Chronology,
                affected.Select(scene => StoryEvidence(current, scene.SceneIdentifier, SceneText(scene))).ToArray(),
                Target("screenplay_scene", affected[0].Id, string.Join(" / ", affected.Select(scene => scene.SceneIdentifier))),
                $"The current Story contains {affected.Length} screenplay scenes with the same scene identifier '{duplicate.Key}'. Scene identity is ambiguous for downstream chronology and production linking.",
                "Give each screenplay scene a unique stable identifier, then rerun the check.", .99m, null));
        }

        var previousSequence = (int?)null;
        var previousAct = (int?)null;
        DirectorScreenplaySceneContext? previous = null;
        foreach (var scene in scenes)
        {
            if (scene.SequenceNumber is int sequence && previousSequence is int priorSequence && sequence < priorSequence)
            {
                Add(findings, Finding(
                    DirectorStoryFindingTypes.HardContinuityConflict, DirectorStoryFindingSeverities.Error,
                    DirectorStoryFindingCategories.Chronology,
                    [StoryEvidence(current, previous?.SceneIdentifier ?? "previous scene", previous is null ? string.Empty : SceneText(previous)), StoryEvidence(current, scene.SceneIdentifier, SceneText(scene))],
                    Target("screenplay_scene", scene.Id, scene.SceneIdentifier),
                    $"Screenplay sequence {sequence} appears after sequence {priorSequence} in the persisted Story order.",
                    "Reconcile the scene sequence numbers or the screenplay order; do not silently reorder an approved scene.", .98m, null));
            }
            if (scene.ActNumber is int act && previousAct is int priorAct && act < priorAct)
            {
                Add(findings, Finding(
                    DirectorStoryFindingTypes.HardContinuityConflict, DirectorStoryFindingSeverities.Error,
                    DirectorStoryFindingCategories.Chronology,
                    [StoryEvidence(current, previous?.SceneIdentifier ?? "previous scene", previous is null ? string.Empty : SceneText(previous)), StoryEvidence(current, scene.SceneIdentifier, SceneText(scene))],
                    Target("screenplay_scene", scene.Id, scene.SceneIdentifier),
                    $"Act {act} appears after Act {priorAct} in the persisted Story order.",
                    "Reconcile the act numbers with the screenplay order.", .98m, null));
            }
            previousSequence = scene.SequenceNumber ?? previousSequence;
            previousAct = scene.ActNumber ?? previousAct;
            previous = scene;
        }

        foreach (var scene in scenes.Where(item => item.MovieSceneId is not null))
        {
            var movieScene = context.RelevantMovieScenes.FirstOrDefault(item => item.Id == scene.MovieSceneId);
            if (movieScene is null || scene.SequenceNumber is not int screenplaySequence || screenplaySequence == movieScene.Sequence) continue;
            Add(findings, Finding(
                DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning,
                DirectorStoryFindingCategories.Chronology,
                [StoryEvidence(current, scene.SceneIdentifier, SceneText(scene)), MovieSceneEvidence(movieScene)],
                Target("screenplay_scene", scene.Id, scene.SceneIdentifier),
                $"The screenplay links '{scene.SceneIdentifier}' to production scene '{movieScene.Title}', but their sequence numbers are {screenplaySequence} and {movieScene.Sequence}.",
                "Confirm whether the link or one of the sequence numbers is stale before production mapping.", .86m,
                "Production sequence and screenplay sequence may intentionally use different numbering systems."));
        }
    }

    private static void AnalyzeCoverage(DirectorStoryRevisionContext current, ICollection<DirectorStoryFindingDto> findings)
    {
        foreach (var scene in current.Scenes.Where(scene => scene.Elements.Count == 0))
        {
            Add(findings, Finding(
                DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning,
                DirectorStoryFindingCategories.Coverage,
                [StoryEvidence(current, scene.SceneIdentifier, SceneText(scene))],
                Target("screenplay_scene", scene.Id, scene.SceneIdentifier),
                $"Screenplay scene '{scene.SceneIdentifier}' has no screenplay elements. Its slugline or synopsis alone cannot establish the intended action and dialogue facts.",
                "Add the screenplay elements or mark the scene as intentionally outline-only.", .98m,
                "An outline-only scene may be intentional."));
        }
    }

    private static void AnalyzeApprovedStory(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext current, DirectorStoryRevisionContext? approved, ICollection<DirectorStoryFindingDto> findings)
    {
        if (approved is null || approved.RevisionId == current.RevisionId) return;

        var currentByKey = current.Scenes.ToDictionary(SceneKey, StringComparer.OrdinalIgnoreCase);
        foreach (var approvedScene in approved.Scenes)
        {
            if (!currentByKey.TryGetValue(SceneKey(approvedScene), out var currentScene))
            {
                if (approvedScene.MovieSceneId is null) continue;
                Add(findings, Finding(
                    DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning,
                    DirectorStoryFindingCategories.ApprovedStory,
                    [StoryEvidence(approved, approvedScene.SceneIdentifier, SceneText(approvedScene)), StoryEvidence(current, "Current Story", StoryText(current))],
                    Target("screenplay_scene", approvedScene.Id, approvedScene.SceneIdentifier),
                    $"The current Story does not contain the approved screenplay scene '{approvedScene.SceneIdentifier}' for linked production scene {approvedScene.MovieSceneId}.",
                    "Confirm whether the scene was intentionally removed or restore the approved scene link before production.", .84m,
                    "A draft may intentionally remove or merge an approved scene."));
                continue;
            }

            var approvedStructure = StructuralTokens(approvedScene.Slugline);
            var currentStructure = StructuralTokens(currentScene.Slugline);
            if (approvedStructure.Count > 0 && currentStructure.Count > 0 && !approvedStructure.SequenceEqual(currentStructure, StringComparer.OrdinalIgnoreCase))
            {
                Add(findings, Finding(
                    DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning,
                    DirectorStoryFindingCategories.ScreenplayFact,
                    [StoryEvidence(approved, approvedScene.SceneIdentifier, approvedScene.Slugline), StoryEvidence(current, currentScene.SceneIdentifier, currentScene.Slugline)],
                    Target("screenplay_scene", currentScene.Id, currentScene.SceneIdentifier),
                    $"The current slugline changes the structural scene facts from '{approvedScene.Slugline}' to '{currentScene.Slugline}'.",
                    "Confirm the intended location, interior/exterior, and time-of-day change and update the approved Story only after review.", .9m,
                    "A deliberate rewrite can legitimately change a slugline."));
            }

            for (var elementIndex = 0; elementIndex < currentScene.Elements.Count; elementIndex++)
            {
                var currentElement = currentScene.Elements[elementIndex];
                var approvedElement = approvedScene.Elements.ElementAtOrDefault(elementIndex);
                if (approvedElement is not null && !approvedElement.ElementType.Equals(currentElement.ElementType, StringComparison.OrdinalIgnoreCase)) approvedElement = null;
                if (approvedElement is null) continue;
                var contradiction = ContradictingValue(approvedElement.Content, currentElement.Content);
                if (contradiction is null) continue;
                Add(findings, Finding(
                    DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning,
                    DirectorStoryFindingCategories.ScreenplayFact,
                    [StoryEvidence(approved, approvedScene.SceneIdentifier, approvedElement.Content), StoryEvidence(current, currentScene.SceneIdentifier, currentElement.Content)],
                    Target("screenplay_element", currentElement.Id, $"{currentScene.SceneIdentifier} element {elementIndex + 1}"),
                    $"The current screenplay passage contains '{contradiction}' where the approved passage establishes a different explicit fact.",
                    "Confirm the intended rewrite and update any dependent scene, Cast, World, or production records if the fact changed.", .78m,
                    "The detector is lexical and only reports opposing explicit terms; it does not infer unstated plot meaning."));
            }
        }
    }

    private static void AnalyzeGuide(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext current, ICollection<DirectorStoryFindingDto> findings)
    {
        var guideParts = new List<string> { context.Guide.ContinuityRules };
        guideParts.AddRange((context.Guide.LockedSections ?? []).Where(section => section.Type.Equals(MovieGuideSectionTypes.ContinuityBible, StringComparison.OrdinalIgnoreCase) || section.Type.Equals(MovieGuideSectionTypes.StoryBible, StringComparison.OrdinalIgnoreCase)).Select(section => ExtractJsonText(section.ContentJson)));
        var guideText = string.Join(" ", guideParts.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(guideText)) return;

        var storyText = string.Join(" ", current.Scenes.Select(SceneText));
        foreach (var (expected, opposite) in Opposites)
        {
            var guideExpected = ContainsWord(guideText, expected) && ContainsWord(storyText, opposite);
            var reverseExpected = ContainsWord(guideText, opposite) && ContainsWord(storyText, expected);
            if (!guideExpected && !reverseExpected) continue;
            var isPrescriptive = Regex.IsMatch(guideText, "\\b(must|always|locked|never|only|authoritative)\\b", RegexOptions.IgnoreCase);
            var guideTerm = guideExpected ? expected : opposite;
            var storyTerm = guideExpected ? opposite : expected;
            Add(findings, Finding(
                isPrescriptive ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                isPrescriptive ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning,
                DirectorStoryFindingCategories.LockedMovieGuide,
                [Evidence("Locked Movie Guide", "guide_revision", null, context.Guide.RevisionNumber is int revision ? $"revision:{revision}" : null, Excerpt(guideText)), StoryEvidence(current, "Current Story", Excerpt(storyText))],
                Target("story_revision", current.RevisionId, $"Current revision {current.RevisionNumber}"),
                $"The locked Movie Guide contains the explicit term '{guideTerm}', while the current Story contains the opposing term '{storyTerm}'.",
                $"Confirm the authoritative Guide rule and align the Story's explicit {guideTerm}/{storyTerm} fact before approval.", isPrescriptive ? .9m : .68m,
                isPrescriptive ? null : "The Guide text may be descriptive rather than a hard rule."));
        }
    }

    private static void AnalyzeCharacters(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext current, ICollection<DirectorStoryFindingDto> findings)
    {
        foreach (var character in context.RelevantCharacters)
        {
            if (string.IsNullOrWhiteSpace(character.Name)) continue;
            foreach (var scene in current.Scenes.Where(scene => ContainsWord(SceneText(scene), character.Name)))
            {
                foreach (var fact in CharacterFacts(character))
                {
                    var contradiction = ContradictingValue(fact.Value, SceneText(scene));
                    if (contradiction is null) continue;
                    var isLocked = fact.LockId.HasValue || fact.StateId.HasValue && fact.Locked;
                    Add(findings, Finding(
                        isLocked ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                        isLocked ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning,
                        fact.Category,
                        [CharacterEvidence(character, fact), StoryEvidence(current, scene.SceneIdentifier, SceneText(scene))],
                        Target("screenplay_scene", scene.Id, scene.SceneIdentifier),
                        $"Character '{character.Name}' is associated with '{fact.Value}' in the Cast record, but the scene contains the opposing explicit term '{contradiction}'.",
                        $"Align the scene with the {(isLocked ? "locked " : "established ")}Cast fact, or formally revise the Cast/state record before changing the Story.", isLocked ? .94m : .72m,
                        isLocked ? null : "The Cast value is not locked and may be an intentional story change."));
                }
            }
        }
    }

    private static void AnalyzeWorld(DirectorStoryBoundedContextDto context, DirectorStoryRevisionContext current, ICollection<DirectorStoryFindingDto> findings)
    {
        var world = context.World;
        if (world is null) return;

        foreach (var warning in world.Warnings ?? [])
        {
            var linkedScene = warning.Target.SceneId is Guid warningSceneId
                ? current.Scenes.FirstOrDefault(scene => scene.MovieSceneId == warningSceneId)
                : null;
            var target = linkedScene is not null
                ? Target("screenplay_scene", linkedScene.Id, linkedScene.SceneIdentifier)
                : Target("project", context.MovieProjectId, "World continuity");
            Add(findings, Finding(
                warning.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                warning.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning,
                DirectorStoryFindingCategories.WorldContinuity,
                [Evidence("World continuity engine", warning.Source.EntityType, warning.Source.RecordId ?? warning.Source.EntityId, null, warning.Message)],
                target,
                $"The existing World continuity engine reported: {warning.Message}",
                "Resolve the persisted World fact/lock conflict or revise the Story only after the authoritative World record is clear.", warning.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? .98m : .86m,
                null));
        }

        var storyTextByScene = current.Scenes.ToDictionary(scene => scene.Id, SceneText);
        foreach (var lockFact in world.Locks)
        {
            var entityName = world.Entities.FirstOrDefault(entity => entity.Id == lockFact.EntityId)?.Name;
            var scenes = storyTextByScene.Where(pair => string.IsNullOrWhiteSpace(entityName) || ContainsWord(pair.Value, entityName)).ToArray();
            foreach (var scene in scenes)
            {
                var contradiction = ContradictingValue(lockFact.LockedValue, scene.Value);
                if (contradiction is null) continue;
                var hard = lockFact.Strength.Equals(MovieContinuityLockStrengths.Hard, StringComparison.OrdinalIgnoreCase);
                Add(findings, Finding(
                    hard ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                    hard ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning,
                    DirectorStoryFindingCategories.WorldContinuity,
                    [Evidence("World continuity lock", "continuity_lock", lockFact.Id, null, $"{lockFact.FieldName} = {lockFact.LockedValue}"), StoryEvidence(current, scene.Key.ToString(), scene.Value)],
                    Target("screenplay_scene", scene.Key, scene.Key.ToString()),
                    $"The Story scene contains '{contradiction}' for a World record locked as '{lockFact.LockedValue}'.",
                    "Align the Story with the active World lock or release/revise the lock through the World workflow before changing the scene.", hard ? .94m : .78m,
                    hard ? null : "The World lock is soft and may be intentionally overridden after review."));
            }
        }

        foreach (var fact in world.Facts)
        {
            if (fact.IsLocked) continue;
            var entityName = world.Entities.FirstOrDefault(entity => entity.Id == fact.ScopeId)?.Name;
            foreach (var scene in storyTextByScene.Where(pair => string.IsNullOrWhiteSpace(entityName) || ContainsWord(pair.Value, entityName)))
            {
                var contradiction = ContradictingValue(fact.FactValue, scene.Value);
                if (contradiction is null) continue;
                Add(findings, Finding(
                    DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning,
                    DirectorStoryFindingCategories.WorldContinuity,
                    [Evidence("World continuity fact", "continuity_fact", fact.Id, null, $"{fact.FactKey} = {fact.FactValue}"), StoryEvidence(current, scene.Key.ToString(), scene.Value)],
                    Target("screenplay_scene", scene.Key, scene.Key.ToString()),
                    $"The Story scene contains '{contradiction}' where the current World fact records '{fact.FactValue}'.",
                    "Confirm whether the World fact or the Story is authoritative, then update the appropriate record through review.", .7m,
                    "The fact is not locked and may have become stale."));
            }
        }
    }

    private static IEnumerable<CharacterFact> CharacterFacts(DirectorCharacterContext character)
    {
        foreach (var locked in character.LockedFacts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(locked.LockedValue)) yield return new CharacterFact(locked.FieldKey, locked.LockedValue, true, locked.StateId, locked.LockId, DirectorStoryFindingCategories.CastContinuity);
        }
        foreach (var state in character.States ?? [])
        {
            foreach (var item in new[]
                     {
                         ("wardrobe", state.Wardrobe), ("ageOrTimeState", state.AgeOrTimeState), ("appearance", state.Appearance),
                         ("injuryOrCondition", state.InjuryOrCondition), ("locationOrStoryState", state.LocationOrStoryState), ("continuityNotes", state.ContinuityNotes),
                     })
            {
                if (!string.IsNullOrWhiteSpace(item.Item2)) yield return new CharacterFact(item.Item1, item.Item2!, false, state.Id, null, DirectorStoryFindingCategories.CharacterState);
            }
        }
        foreach (var item in new[] { ("description", character.Description), ("appearance", character.Appearance), ("continuityNotes", character.ContinuityNotes) })
        {
            if (!string.IsNullOrWhiteSpace(item.Item2)) yield return new CharacterFact(item.Item1, item.Item2!, false, null, null, DirectorStoryFindingCategories.CastContinuity);
        }
    }

    private static string? ContradictingValue(string expectedText, string actualText)
    {
        if (string.IsNullOrWhiteSpace(expectedText) || string.IsNullOrWhiteSpace(actualText)) return null;
        foreach (var (first, second) in Opposites)
        {
            if (ContainsWord(expectedText, first) && ContainsWord(actualText, second)) return second;
            if (ContainsWord(expectedText, second) && ContainsWord(actualText, first)) return first;
        }
        var phrase = expectedText.Trim();
        if (phrase.Length >= 3)
        {
            var pattern = $"\\b(?:not|never|without|no)\\s+(?:{Regex.Escape(phrase).Replace("\\ ", "\\s+")})\\b";
            if (Regex.IsMatch(actualText, pattern, RegexOptions.IgnoreCase)) return $"not {phrase}";
        }
        return null;
    }

    private static IReadOnlyList<string> StructuralTokens(string? slugline) => (slugline ?? string.Empty).ToLowerInvariant().Split(new[] { ' ', '\t', '-', '–', '—', '.', ':' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token is "int" or "ext" or "interior" or "exterior" or "day" or "night" or "dawn" or "dusk" or "morning" or "evening")
        .ToArray();

    private static string ExtractJsonText(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var values = new List<string>();
            Walk(document.RootElement, values);
            return string.Join(" ", values);
        }
        catch (JsonException) { return json; }
    }

    private static void Walk(JsonElement element, ICollection<string> values)
    {
        if (element.ValueKind == JsonValueKind.String) { values.Add(element.GetString() ?? string.Empty); return; }
        if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) Walk(property.Value, values);
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Walk(item, values);
    }

    private static string SceneKey(DirectorScreenplaySceneContext scene) => scene.MovieSceneId?.ToString("N") ?? scene.SceneIdentifier.Trim();
    private static string SceneText(DirectorScreenplaySceneContext scene) => string.Join(" ", scene.Slugline, scene.Synopsis, string.Join(" ", scene.Elements.Select(element => string.Join(" ", element.CharacterName, element.Parenthetical, element.Content))));
    private static string StoryText(DirectorStoryRevisionContext revision) => string.Join(" ", revision.Premise, revision.Logline, revision.Synopsis, revision.Treatment, string.Join(" ", revision.Scenes.Select(SceneText)));
    private static bool ContainsWord(string? text, string? value) => !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(text, $"(?<![\\p{{L}}\\p{{N}}]){Regex.Escape(value.Trim())}(?![\\p{{L}}\\p{{N}}])", RegexOptions.IgnoreCase);
    private static string Excerpt(string value) => value.Length <= 800 ? value : value[..800] + "…";
    private static string FindingKey(DirectorStoryFindingDto finding) => $"{finding.FindingType}|{finding.Category}|{finding.AffectedTarget.TargetType}|{finding.AffectedTarget.TargetId}|{finding.Explanation}";
    private static void Add(ICollection<DirectorStoryFindingDto> findings, DirectorStoryFindingDto finding) => findings.Add(finding);
    private static DirectorStoryFindingDto Finding(string type, string severity, string category, IReadOnlyList<DirectorStoryEvidenceDto> evidence, DirectorStoryFindingTargetDto target, string explanation, string correction, decimal confidence, string? uncertainty) => new(type, severity, category, evidence, target, explanation, correction, Math.Clamp(confidence, 0m, 1m), uncertainty);
    private static DirectorStoryEvidenceDto Evidence(string source, string? sourceType, Guid? sourceId, string? revision, string excerpt) => new(source, sourceType, sourceId, revision, Excerpt(excerpt));
    private static DirectorStoryEvidenceDto StoryEvidence(DirectorStoryRevisionContext revision, string source, string excerpt) => Evidence(source, "story_revision", revision.RevisionId, $"revision:{revision.RevisionNumber}", excerpt);
    private static DirectorStoryEvidenceDto MovieSceneEvidence(DirectorSceneContext scene) => Evidence(scene.Title, "movie_scene", scene.Id, $"sequence:{scene.Sequence}", scene.Summary);
    private static DirectorStoryEvidenceDto CharacterEvidence(DirectorCharacterContext character, CharacterFact fact) => Evidence($"Cast: {character.Name} · {fact.Field}", "character_continuity", fact.LockId ?? character.Id, fact.StateId is Guid stateId ? $"state:{stateId}" : null, fact.Value);
    private static DirectorStoryFindingTargetDto Target(string type, Guid? id, string? label) => new(type, id, label);

    private sealed record CharacterFact(string Field, string Value, bool Locked, Guid? StateId, Guid? LockId, string Category);
}
