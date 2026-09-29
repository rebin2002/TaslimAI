using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed record MovieProductionContinuityReviewDto(
    Guid MovieProjectId,
    Guid? SceneId,
    Guid? ShotId,
    bool ReviewOnly,
    DateTime AssembledAt,
    IReadOnlyList<DirectorStoryFindingDto> Findings);

public sealed record MovieProductionContinuitySceneContext(
    Guid Id,
    int Sequence,
    string Title,
    string Summary,
    string? ContinuityNotes,
    string? Narration,
    string? Dialogue,
    IReadOnlyList<MovieProductionContinuityShotContext> Shots);

public sealed record MovieProductionContinuityShotContext(
    Guid Id,
    int Sequence,
    string Description,
    string? Purpose,
    string? Subjects,
    IReadOnlyList<Guid> SubjectCharacterIds,
    string? LocationSet,
    string? ProductionRequirements,
    string? ContinuityReferences,
    string? CameraAndFraming,
    string? CameraMotion,
    string? Narration,
    string? Dialogue,
    string? VisualContinuityNotes);

public sealed record MovieProductionContinuityCharacterContext(
    Guid Id,
    string Name,
    string Description,
    string? Appearance,
    string? Wardrobe,
    string? ContinuityNotes,
    IReadOnlyList<MovieProductionContinuityCharacterStateContext> States,
    IReadOnlyList<MovieProductionContinuityLockContext> Locks);

public sealed record MovieProductionContinuityCharacterStateContext(
    Guid Id,
    string Key,
    string? Label,
    string? Wardrobe,
    string? AgeOrTimeState,
    string? Appearance,
    string? InjuryOrCondition,
    string? LocationOrStoryState,
    string? ContinuityNotes,
    IReadOnlyList<MovieProductionContinuityLockContext> Locks);

public sealed record MovieProductionContinuityLockContext(
    Guid Id,
    string EntityType,
    Guid? EntityId,
    string FieldName,
    string LockedValue,
    string Strength,
    string? Reason);

public sealed record MovieProductionContinuityContext(
    Guid MovieProjectId,
    Guid? SceneId,
    Guid? ShotId,
    DirectorGuideContext Guide,
    IReadOnlyList<MovieProductionContinuitySceneContext> Scenes,
    IReadOnlyList<MovieProductionContinuityCharacterContext> Characters,
    MovieWorldContinuitySnapshotDto? World,
    IReadOnlyList<MovieWorldUsage> Usages,
    IReadOnlyList<MovieWorldContinuityWarning> WorldWarnings,
    IReadOnlyList<(Guid SceneId, Guid? MovieSceneId, int? StorySequence, string Identifier, string Slugline)> StoryLinks,
    DateTime AssembledAt);

public interface IMovieProductionContinuityService
{
    Task<MovieProductionContinuityReviewDto?> ReviewProjectAsync(Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken = default);
    Task<MovieProductionContinuityReviewDto?> ReviewSceneAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken = default);
    Task<MovieProductionContinuityReviewDto?> ReviewShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default);
}

public sealed class MovieProductionContinuityService(
    TaslimDbContext db,
    WorkspaceAccessService access,
    MovieWorldContinuityProjector worldContinuity) : IMovieProductionContinuityService
{
    public Task<MovieProductionContinuityReviewDto?> ReviewSceneAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken = default) =>
        ReviewTargetAsync(userId, null, sceneId, null, cancellationToken);

    public Task<MovieProductionContinuityReviewDto?> ReviewShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default) =>
        ReviewTargetAsync(userId, null, null, shotId, cancellationToken);

    public Task<MovieProductionContinuityReviewDto?> ReviewProjectAsync(Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken = default) =>
        ReviewTargetAsync(userId, movieProjectId, sceneId, shotId, cancellationToken);

    private async Task<MovieProductionContinuityReviewDto?> ReviewTargetAsync(
        Guid userId,
        Guid? requestedProjectId,
        Guid? requestedSceneId,
        Guid? requestedShotId,
        CancellationToken cancellationToken)
    {
        var projectId = requestedProjectId;
        if (!projectId.HasValue && requestedSceneId.HasValue)
            projectId = await db.MovieScenes.AsNoTracking().Where(item => item.Id == requestedSceneId.Value).Select(item => (Guid?)item.MovieProjectId).FirstOrDefaultAsync(cancellationToken);
        if (!projectId.HasValue && requestedShotId.HasValue)
            projectId = await db.MovieShots.AsNoTracking().Where(item => item.Id == requestedShotId.Value).Select(item => (Guid?)item.Scene.MovieProjectId).FirstOrDefaultAsync(cancellationToken);
        if (!projectId.HasValue) return null;
        var movie = await db.MovieProjects.AsNoTracking().Include(item => item.Guide).ThenInclude(item => item.Revisions)
            .FirstOrDefaultAsync(item => item.Id == projectId.Value, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;

        var scenesQuery = db.MovieScenes.AsNoTracking().Where(item => item.MovieProjectId == movie.Id);
        if (requestedSceneId.HasValue) scenesQuery = scenesQuery.Where(item => item.Id == requestedSceneId.Value);
        if (requestedShotId.HasValue)
        {
            var targetSceneId = await db.MovieShots.AsNoTracking().Where(item => item.Id == requestedShotId.Value).Select(item => (Guid?)item.MovieSceneId).FirstOrDefaultAsync(cancellationToken);
            if (!targetSceneId.HasValue) return null;
            scenesQuery = scenesQuery.Where(item => item.Id == targetSceneId.Value);
        }
        var scenes = await scenesQuery.Include(item => item.Shots).OrderBy(item => item.Sequence).ThenBy(item => item.Id).Take(MovieProductionContinuityLimits.MaxScenes).ToListAsync(cancellationToken);
        if (requestedSceneId.HasValue && scenes.All(item => item.Id != requestedSceneId.Value)) return null;
        if (requestedShotId.HasValue && scenes.SelectMany(item => item.Shots).All(item => item.Id != requestedShotId.Value)) return null;

        var sceneIds = scenes.Select(item => item.Id).ToArray();
        var shotIds = scenes.SelectMany(item => item.Shots).Select(item => item.Id).ToArray();
        var relevantShotIds = requestedShotId.HasValue ? new[] { requestedShotId.Value } : shotIds;
        var characters = await db.MovieCharacters.AsNoTracking().Where(item => item.MovieProjectId == movie.Id)
            .Include(item => item.States).ThenInclude(item => item.ContinuityLocks)
            .Include(item => item.ContinuityLocks)
            .OrderBy(item => item.CreatedAt).Take(MovieProductionContinuityLimits.MaxCharacters).ToListAsync(cancellationToken);
        var usages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == movie.Id && sceneIds.Contains(item.MovieSceneId) && (item.MovieShotId == null || relevantShotIds.Contains(item.MovieShotId.Value)))
            .OrderBy(item => item.MovieSceneId).ThenBy(item => item.MovieShotId).ThenBy(item => item.Id).Take(MovieProductionContinuityLimits.MaxUsages).ToListAsync(cancellationToken);
        var world = await worldContinuity.ProjectAsync(movie.Id, sceneIds.Length == 1 ? sceneIds[0] : null, requestedShotId, cancellationToken);
        var storyLinks = await db.MovieScreenplayScenes.AsNoTracking()
            .Where(item => item.MovieSceneId.HasValue && sceneIds.Contains(item.MovieSceneId.Value) && item.Revision.Status == MovieStoryRevisionStatuses.Approved)
            .Select(item => new { SceneId = item.Id, MovieSceneId = item.MovieSceneId, item.SequenceNumber, item.SceneIdentifier, item.Slugline })
            .OrderBy(item => item.SceneIdentifier).Take(MovieProductionContinuityLimits.MaxStoryLinks)
            .ToArrayAsync(cancellationToken);
        var lockedRevisionNumber = movie.Guide.LockedRevisionNumber;
        var lockedRevision = lockedRevisionNumber is int revision
            ? movie.Guide.Revisions.FirstOrDefault(item => item.RevisionNumber == revision && item.Status == MovieGuideRevisionStatuses.Locked)
            : null;
        IReadOnlyList<DirectorGuideSectionContext> guideSections = lockedRevision is null ? [] : new[]
        {
            new DirectorGuideSectionContext(MovieGuideSectionTypes.StoryBible, Limit(lockedRevision.StoryBibleJson, MovieProductionContinuityLimits.MaxGuideSection)),
            new DirectorGuideSectionContext(MovieGuideSectionTypes.WorldBibleReferences, Limit(lockedRevision.WorldBibleReferencesJson, MovieProductionContinuityLimits.MaxGuideSection)),
            new DirectorGuideSectionContext(MovieGuideSectionTypes.ContinuityBible, Limit(lockedRevision.ContinuityBibleJson, MovieProductionContinuityLimits.MaxGuideSection)),
        };
        var guide = new DirectorGuideContext(
            Limit(movie.Guide.VisualLanguage, 2_000), Limit(movie.Guide.CameraLanguage, 2_000), Limit(movie.Guide.ColorAndLighting, 2_000),
            Limit(movie.Guide.SoundAndNarration, 2_000), Limit(movie.Guide.ContinuityRules, 8_000), lockedRevision?.RevisionNumber, lockedRevision is not null,
            lockedRevision is null ? null : Limit(lockedRevision.CinematographyBibleJson, MovieProductionContinuityLimits.MaxGuideSection), guideSections);
        var reviewSceneId = requestedSceneId ?? (requestedShotId.HasValue ? scenes.FirstOrDefault()?.Id : null);
        var context = new MovieProductionContinuityContext(
            movie.Id, reviewSceneId, requestedShotId, guide,
            scenes.Select(ToSceneContext).ToArray(), characters.Select(ToCharacterContext).ToArray(), world, usages,
            world?.Warnings ?? [], storyLinks.Select(item => (item.SceneId, item.MovieSceneId, item.SequenceNumber, item.SceneIdentifier, item.Slugline)).ToArray(), DateTime.UtcNow);
        var findings = MovieProductionContinuityAnalyzer.Analyze(context);
        return new MovieProductionContinuityReviewDto(movie.Id, context.SceneId, context.ShotId, true, context.AssembledAt, findings);
    }

    private static MovieProductionContinuitySceneContext ToSceneContext(MovieScene scene) => new(
        scene.Id, scene.Sequence, Limit(scene.Title, 240), Limit(scene.Summary, 8_000), Limit(scene.ContinuityNotes, 4_000),
        Limit(scene.Narration, 8_000), Limit(scene.Dialogue, 8_000), scene.Shots.OrderBy(item => item.Sequence).ThenBy(item => item.Id).Select(ToShotContext).ToArray());

    private static MovieProductionContinuityShotContext ToShotContext(MovieShot shot) => new(
        shot.Id, shot.Sequence, Limit(shot.Description, 8_000), Limit(shot.Purpose, 2_000), Limit(shot.Subjects, 4_000),
        MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson), Limit(shot.LocationSet, 2_000), Limit(shot.ProductionRequirements, 4_000),
        Limit(shot.ContinuityReferences, 4_000), Limit(shot.CameraAndFraming, 2_000), Limit(shot.CameraMotion, 2_000), Limit(shot.Narration, 8_000),
        Limit(shot.Dialogue, 8_000), Limit(shot.VisualContinuityNotes, 4_000));

    private static MovieProductionContinuityCharacterContext ToCharacterContext(MovieCharacter character) => new(
        character.Id, Limit(character.Name, 160), Limit(character.Description, 4_000), Limit(character.Appearance, 2_000), Limit(character.Wardrobe, 2_000),
        Limit(character.ContinuityNotes, 2_000), character.States.OrderByDescending(item => item.UpdatedAt).Take(MovieProductionContinuityLimits.MaxStatesPerCharacter).Select(state =>
            new MovieProductionContinuityCharacterStateContext(state.Id, Limit(state.Key, 120), Limit(state.Label, 500), Limit(state.Wardrobe, 2_000), Limit(state.AgeOrTimeState, 1_000), Limit(state.Appearance, 2_000), Limit(state.InjuryOrCondition, 2_000), Limit(state.LocationOrStoryState, 2_000), Limit(state.ContinuityNotes, 2_000),
                state.ContinuityLocks.Select(ToLockContext).ToArray())).ToArray(), character.ContinuityLocks.Where(item => item.MovieCharacterStateId is null).Select(ToLockContext).ToArray());

    private static MovieProductionContinuityLockContext ToLockContext(MovieCharacterContinuityLock item) => new(item.Id, "character", item.MovieCharacterId, item.FieldKey, Limit(item.LockedValue, 2_000), MovieContinuityLockStrengths.Hard, null);
    private static string Limit(string? value, int max) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= max ? value : value[..max];
}

public static class MovieProductionContinuityLimits
{
    public const int MaxScenes = 96;
    public const int MaxCharacters = 128;
    public const int MaxStatesPerCharacter = 16;
    public const int MaxUsages = 512;
    public const int MaxStoryLinks = 96;
    public const int MaxGuideSection = 20_000;
    public const int MaxFindings = 192;
}

public static class MovieProductionContinuityAnalyzer
{
    private static readonly (string First, string Second)[] Opposites =
    [
        ("red", "blue"), ("green", "purple"), ("black", "white"), ("yellow", "black"),
        ("day", "night"), ("daytime", "nighttime"), ("morning", "evening"), ("dawn", "dusk"),
        ("inside", "outside"), ("indoors", "outdoors"), ("interior", "exterior"),
        ("alive", "dead"), ("living", "dead"), ("injured", "uninjured"), ("wounded", "unhurt"),
        ("conscious", "unconscious"), ("wet", "dry"), ("clean", "dirty"), ("open", "closed"),
        ("locked", "unlocked"), ("left", "right"), ("present", "absent"), ("light", "dark"),
        ("rain", "sunny"), ("storm", "clear"), ("snow", "sunny"),
    ];

    public static IReadOnlyList<DirectorStoryFindingDto> Analyze(MovieProductionContinuityContext context)
    {
        var findings = new List<DirectorStoryFindingDto>();
        AnalyzeChronology(context, findings);
        foreach (var scene in context.Scenes)
        {
            AnalyzePlan(context, scene, null, SceneText(scene), findings);
            foreach (var shot in scene.Shots)
            {
                if (context.ShotId.HasValue && context.ShotId.Value != shot.Id) continue;
                AnalyzePlan(context, scene, shot, ShotText(shot), findings);
            }
        }
        AnalyzeWorldWarnings(context, findings);
        return findings.GroupBy(FindingKey, StringComparer.Ordinal).Select(item => item.First()).Take(MovieProductionContinuityLimits.MaxFindings).ToArray();
    }

    private static void AnalyzePlan(MovieProductionContinuityContext context, MovieProductionContinuitySceneContext scene, MovieProductionContinuityShotContext? shot, string planText, ICollection<DirectorStoryFindingDto> findings)
    {
        if (context.ShotId.HasValue && shot is null) return;
        var target = shot is null ? Target("movie_scene", scene.Id, $"Scene {scene.Sequence}: {scene.Title}") : Target("movie_shot", shot.Id, $"Scene {scene.Sequence} · Shot {shot.Sequence}");
        var planEvidence = Evidence(shot is null ? scene.Title : $"Shot {shot.Sequence}", shot is null ? "movie_scene_plan" : "movie_shot_plan", shot?.Id ?? scene.Id, null, planText);
        AnalyzeCharacters(context, scene, shot, planText, target, planEvidence, findings);
        AnalyzeWorld(context, scene, shot, planText, target, planEvidence, findings);
        AnalyzeGuideForPlan(context, planText, target, planEvidence, findings);
        if (shot is not null && string.IsNullOrWhiteSpace(shot.ContinuityReferences) && HasRelevantCanon(context, planText))
        {
            Add(findings, Finding(DirectorStoryFindingTypes.CreativeSuggestion, DirectorStoryFindingSeverities.Suggestion, DirectorStoryFindingCategories.ProductionContinuity,
                [planEvidence], target,
                "This shot names established continuity-sensitive material but has no explicit continuity reference in its plan.",
                "Add a concise reference to the relevant Cast, World, state, or locked Guide fact so the production handoff preserves the intended canon.", .72m,
                "The omission may be intentional when the shot is covered by a scene-level continuity note."));
        }
    }

    private static void AnalyzeCharacters(MovieProductionContinuityContext context, MovieProductionContinuitySceneContext scene, MovieProductionContinuityShotContext? shot, string planText, DirectorStoryFindingTargetDto target, DirectorStoryEvidenceDto planEvidence, ICollection<DirectorStoryFindingDto> findings)
    {
        var subjectIds = shot?.SubjectCharacterIds ?? [];
        foreach (var character in context.Characters)
        {
            var named = ContainsWord(planText, character.Name);
            var explicitSubject = subjectIds.Contains(character.Id);
            if (!named && !explicitSubject) continue;
            if (shot is not null && named && !explicitSubject && (subjectIds.Count > 0 || !string.IsNullOrWhiteSpace(shot.Subjects)))
            {
                Add(findings, Finding(DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.CharacterPresence,
                    [CharacterEvidence(character, "subjectCharacterIds", string.Join(", ", subjectIds)), planEvidence], target,
                    $"The shot text names character '{character.Name}', but its explicit subject-character list does not include that character.",
                    "Confirm the intended character presence and update either the shot subject list or the descriptive shot text; no record was changed by this review.", .9m,
                    "The text may describe an off-screen or referenced character."));
            }
            foreach (var fact in CharacterFacts(character))
            {
                var contradiction = ContradictingValue(fact.Value, planText);
                if (contradiction is null) continue;
                var hard = fact.Locked;
                Add(findings, Finding(hard ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                    hard ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning, CategoryForCharacterField(fact.Field),
                    [CharacterEvidence(character, fact.Field, fact.Value, fact.LockId ?? fact.StateId), planEvidence], target,
                    $"Planned {PlanLabel(shot)} associates character '{character.Name}' with '{contradiction}', while established canon records '{fact.Value}' for {fact.Field}.",
                    $"Align the {PlanLabel(shot)} with the {(hard ? "locked " : "established ")}character fact, or revise the authoritative Cast/state record through its workflow before changing the plan.", hard ? .96m : .76m,
                    hard ? null : "The character fact is not locked and may represent an alternate state or an intentional story change."));
            }
        }
    }

    private static void AnalyzeWorld(MovieProductionContinuityContext context, MovieProductionContinuitySceneContext scene, MovieProductionContinuityShotContext? shot, string planText, DirectorStoryFindingTargetDto target, DirectorStoryEvidenceDto planEvidence, ICollection<DirectorStoryFindingDto> findings)
    {
        if (context.World is null) return;
        var usages = context.Usages.Where(item => item.MovieSceneId == scene.Id && (item.MovieShotId is null || item.MovieShotId == shot?.Id)).ToArray();
        var entityIds = usages.Select(item => (item.EntityType, item.EntityId)).ToHashSet();
        foreach (var entity in context.World.Locations.Select(item => new WorldEntity(item.Id, MovieWorldEntityTypes.Location, item.Name, item.Description, null, null, null, null, item.VisualContinuityNotes))
            .Concat(context.World.Sets.Select(item =>
            {
                var variation = item.Variations.FirstOrDefault(item => item.IsDefault);
                return new WorldEntity(item.Id, MovieWorldEntityTypes.Set, item.Name, item.Description, item.MovieLocationId,
                    item.TimeOfDay ?? variation?.TimeOfDay, item.Weather ?? variation?.Weather, variation, item.ContinuityNotes);
            }))
            .Concat(context.World.Props.Select(item => new WorldEntity(item.Id, MovieWorldEntityTypes.Prop, item.Name, item.Description, null, null, null, null, item.ContinuityNotes)))
            .Where(item => entityIds.Contains((item.Type, item.Id))))
        {
            var mentioned = ContainsWord(planText, entity.Name) || string.Equals(shot?.LocationSet?.Trim(), entity.Name, StringComparison.OrdinalIgnoreCase);
            if (!mentioned && entity.Type != MovieWorldEntityTypes.Prop) continue;
            if (entity.Type is MovieWorldEntityTypes.Location or MovieWorldEntityTypes.Set)
            {
                CompareWorldField(context, entity, "timeOfDay", entity.TimeOfDay, planText, target, planEvidence, findings);
                CompareWorldField(context, entity, "weather", entity.Weather, planText, target, planEvidence, findings);
            }
            if (entity.Type == MovieWorldEntityTypes.Prop)
            {
                foreach (var fact in ApplicableWorldFacts(context, entity.Id, scene.Id, shot?.Id).Where(item => IsStateOrPosition(item.FactKey)))
                    CompareWorldFact(context, entity, fact.FactKey, fact.FactValue, planText, target, planEvidence, findings, IsFactLocked(context, fact), fact.Id, "continuity_fact");
                foreach (var lockFact in context.World.Locks.Where(item => item.EntityId == entity.Id && IsStateOrPosition(item.FieldName)))
                    CompareWorldFact(context, entity, lockFact.FieldName, lockFact.LockedValue, planText, target, planEvidence, findings,
                        lockFact.Strength.Equals(MovieContinuityLockStrengths.Hard, StringComparison.OrdinalIgnoreCase), lockFact.Id, "continuity_lock");
            }
        }
        if (shot is not null && !string.IsNullOrWhiteSpace(shot.LocationSet))
        {
            var usedNames = context.World.Locations.Where(item => usages.Any(usage => usage.EntityType == MovieWorldEntityTypes.Location && usage.EntityId == item.Id)).Select(item => item.Name)
                .Concat(context.World.Sets.Where(item => usages.Any(usage => usage.EntityType == MovieWorldEntityTypes.Set && usage.EntityId == item.Id)).Select(item => item.Name)).ToArray();
            if (usedNames.Length > 0 && !usedNames.Any(name => string.Equals(name, shot.LocationSet.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var hard = usages.Any(item => context.World.Locks.Any(lockItem => lockItem.EntityId == item.EntityId && lockItem.FieldName.Equals("location", StringComparison.OrdinalIgnoreCase) && lockItem.Strength.Equals(MovieContinuityLockStrengths.Hard, StringComparison.OrdinalIgnoreCase)));
                Add(findings, Finding(hard ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                    hard ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.EnvironmentLocation,
                    [Evidence("Planned world usage", "world_usage", usages.FirstOrDefault()?.Id, null, string.Join(", ", usedNames)), planEvidence], target,
                    $"The planned shot selects location/set '{shot.LocationSet}', while the established target usage is '{string.Join(" or ", usedNames)}'.",
                    "Confirm the intended location/set and reconcile the shot's Location / Set field with the World usage before production.", hard ? .95m : .78m,
                    hard ? null : "World usage is persisted planning context but is not itself a hard lock."));
            }
        }
        foreach (var fact in ApplicableWorldFacts(context, null, scene.Id, shot?.Id))
        {
            var entity = fact.ScopeId is Guid id ? context.World.Locations.Select(item => (item.Id, item.Name)).Concat(context.World.Sets.Select(item => (item.Id, item.Name))).Concat(context.World.Props.Select(item => (item.Id, item.Name))).FirstOrDefault(item => item.Id == id) : default;
            if (fact.ScopeId.HasValue && string.IsNullOrWhiteSpace(entity.Name) && !planText.Contains(fact.FactValue, StringComparison.OrdinalIgnoreCase)) continue;
            CompareWorldFact(context, new WorldEntity(fact.ScopeId ?? context.MovieProjectId, fact.ScopeType, entity.Name ?? "target", string.Empty, null, null, null, null, null), fact.FactKey, fact.FactValue, planText, target, planEvidence, findings, IsFactLocked(context, fact), fact.Id, "continuity_fact");
        }
    }

    private static void CompareWorldField(MovieProductionContinuityContext context, WorldEntity entity, string field, string? expected, string planText, DirectorStoryFindingTargetDto target, DirectorStoryEvidenceDto planEvidence, ICollection<DirectorStoryFindingDto> findings)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;
        var lockFact = context.World?.Locks.FirstOrDefault(item => item.EntityId == entity.Id && item.FieldName.Equals(field, StringComparison.OrdinalIgnoreCase));
        CompareWorldFact(context, entity, field, lockFact?.LockedValue ?? expected, planText, target, planEvidence, findings, lockFact is not null && lockFact.Strength.Equals(MovieContinuityLockStrengths.Hard, StringComparison.OrdinalIgnoreCase), lockFact?.Id, lockFact is null ? "world_record" : "continuity_lock");
    }

    private static void CompareWorldFact(MovieProductionContinuityContext context, WorldEntity entity, string field, string expected, string planText, DirectorStoryFindingTargetDto target, DirectorStoryEvidenceDto planEvidence, ICollection<DirectorStoryFindingDto> findings, bool locked, Guid? sourceId, string sourceType)
    {
        var contradiction = ContradictingValue(expected, planText);
        if (contradiction is null) return;
        var category = CategoryForWorldField(field);
        Add(findings, Finding(locked ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
            locked ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning, category,
            [Evidence($"World: {entity.Name}", sourceType, sourceId ?? entity.Id, null, $"{field} = {expected}"), planEvidence], target,
            $"Planned {(target.TargetType == "movie_shot" ? "shot" : "scene")} text contains '{contradiction}', while the World record establishes '{expected}' for {field}.",
            $"Align the plan with the {(locked ? "locked " : "established ")}World {field} fact, or revise the authoritative World record through its workflow before production.", locked ? .96m : .74m,
            locked ? null : "The World fact is not hard-locked and may be stale or intentionally overridden."));
    }

    private static void AnalyzeGuideForPlan(MovieProductionContinuityContext context, string planText, DirectorStoryFindingTargetDto target, DirectorStoryEvidenceDto planEvidence, ICollection<DirectorStoryFindingDto> findings)
    {
        if (!context.Guide.IsAuthoritative) return;
        var guideText = string.Join(" ", context.Guide.ContinuityRules, (context.Guide.LockedSections ?? []).Where(item => item.Type is MovieGuideSectionTypes.ContinuityBible or MovieGuideSectionTypes.StoryBible or MovieGuideSectionTypes.WorldBibleReferences).Select(item => ExtractJsonText(item.ContentJson)));
        if (string.IsNullOrWhiteSpace(guideText)) return;
        foreach (var (expected, opposite) in Opposites)
        {
            if (!ContainsWord(guideText, expected) || !ContainsWord(planText, opposite)) continue;
            var prescriptive = Regex.IsMatch(guideText, "\\b(must|always|locked|never|only|authoritative)\\b", RegexOptions.IgnoreCase);
            Add(findings, Finding(prescriptive ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                prescriptive ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.LockedMovieGuide,
                [Evidence("Locked Movie Guide", "guide_revision", null, context.Guide.RevisionNumber is int revision ? $"revision:{revision}" : null, Excerpt(guideText)), planEvidence], target,
                $"The locked Guide establishes '{expected}', while the planned text contains the opposing explicit term '{opposite}'.",
                "Confirm the locked Guide rule and align the scene/shot plan before production; this review does not change the Guide or plan.", prescriptive ? .94m : .78m,
                prescriptive ? null : "The Guide text may be descriptive rather than prescriptive."));
        }
    }

    private static void AnalyzeChronology(MovieProductionContinuityContext context, ICollection<DirectorStoryFindingDto> findings)
    {
        foreach (var sceneGroup in context.Scenes.GroupBy(item => item.Sequence).Where(item => item.Count() > 1))
            Add(findings, Finding(DirectorStoryFindingTypes.HardContinuityConflict, DirectorStoryFindingSeverities.Error, DirectorStoryFindingCategories.Chronology,
                sceneGroup.Select(item => Evidence(item.Title, "movie_scene", item.Id, $"sequence:{item.Sequence}", SceneText(item))).ToArray(), Target("movie_project", context.MovieProjectId, "Movie scene order"),
                $"Multiple planned scenes use sequence {sceneGroup.Key}; production chronology is ambiguous.", "Assign unique scene sequence values before production review proceeds.", .98m, null));
        foreach (var scene in context.Scenes)
        {
            foreach (var shotGroup in scene.Shots.GroupBy(item => item.Sequence).Where(item => item.Count() > 1))
                Add(findings, Finding(DirectorStoryFindingTypes.HardContinuityConflict, DirectorStoryFindingSeverities.Error, DirectorStoryFindingCategories.Chronology,
                    shotGroup.Select(item => Evidence($"Shot {item.Sequence}", "movie_shot", item.Id, $"scene:{scene.Id}", ShotText(item))).ToArray(), Target("movie_scene", scene.Id, scene.Title),
                    $"Multiple planned shots in scene '{scene.Title}' use sequence {shotGroup.Key}; shot chronology is ambiguous.", "Assign unique shot sequence values within the scene before production.", .98m, null));
        }
        foreach (var link in context.StoryLinks)
        {
            var scene = context.Scenes.FirstOrDefault(item => item.Id == link.MovieSceneId);
            if (scene is null || link.StorySequence is not int storySequence || storySequence == scene.Sequence) continue;
            Add(findings, Finding(DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.Chronology,
                [Evidence($"Approved Story: {link.Identifier}", "story_screenplay_scene", link.SceneId, null, link.Slugline), Evidence(scene.Title, "movie_scene", scene.Id, $"sequence:{scene.Sequence}", SceneText(scene))], Target("movie_scene", scene.Id, scene.Title),
                $"The planned production scene sequence is {scene.Sequence}, while its approved Story link uses sequence {storySequence}.", "Confirm whether the Story link or production sequence is stale before shot execution.", .86m,
                "Story and production may intentionally use different numbering systems."));
        }
    }

    private static void AnalyzeWorldWarnings(MovieProductionContinuityContext context, ICollection<DirectorStoryFindingDto> findings)
    {
        foreach (var warning in context.WorldWarnings)
        {
            if (context.ShotId.HasValue && warning.Target.ShotId.HasValue && warning.Target.ShotId != context.ShotId) continue;
            var target = warning.Target.ShotId is Guid shotId ? Target("movie_shot", shotId, $"Shot {shotId}") : warning.Target.SceneId is Guid sceneId ? Target("movie_scene", sceneId, $"Scene {sceneId}") : Target("movie_project", context.MovieProjectId, "World continuity");
            var hard = warning.Severity.Equals("error", StringComparison.OrdinalIgnoreCase);
            Add(findings, Finding(hard ? DirectorStoryFindingTypes.HardContinuityConflict : DirectorStoryFindingTypes.PossibleInconsistency,
                hard ? DirectorStoryFindingSeverities.Error : DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.WorldContinuity,
                [Evidence("World continuity engine", warning.Source.EntityType, warning.Source.RecordId ?? warning.Source.EntityId, null, warning.Message)], target,
                $"The existing World continuity engine reported: {warning.Message}", "Resolve the persisted World fact, lock, usage, or object-state conflict before production; no record was changed.", hard ? .98m : .84m, null));
        }
    }

    private static IEnumerable<MovieProductionContinuityLockContext> CharacterLocks(MovieProductionContinuityCharacterContext character) => character.Locks.Concat(character.States.SelectMany(item => item.Locks));
    private static IEnumerable<(string Field, string Value, bool Locked, Guid? LockId, Guid? StateId)> CharacterFacts(MovieProductionContinuityCharacterContext character)
    {
        foreach (var lockFact in CharacterLocks(character)) yield return (lockFact.FieldName, lockFact.LockedValue, true, lockFact.Id, lockFact.EntityId);
        foreach (var state in character.States)
        {
            foreach (var item in new[] { ("wardrobe", state.Wardrobe), ("ageOrTimeState", state.AgeOrTimeState), ("appearance", state.Appearance), ("injuryOrCondition", state.InjuryOrCondition), ("locationOrStoryState", state.LocationOrStoryState), ("continuityNotes", state.ContinuityNotes) })
                if (!string.IsNullOrWhiteSpace(item.Item2)) yield return (item.Item1, item.Item2!, false, null, state.Id);
        }
        foreach (var item in new[] { ("description", character.Description), ("appearance", character.Appearance), ("wardrobe", character.Wardrobe), ("continuityNotes", character.ContinuityNotes) })
            if (!string.IsNullOrWhiteSpace(item.Item2)) yield return (item.Item1, item.Item2!, false, null, null);
    }

    private static IEnumerable<MovieWorldContinuityFactSnapshot> ApplicableWorldFacts(MovieProductionContinuityContext context, Guid? entityId, Guid sceneId, Guid? shotId) => context.World?.Facts.Where(item =>
        item.ScopeType == MovieWorldScopes.Project || item.ScopeType == MovieWorldScopes.Scene && item.ScopeId == sceneId || shotId.HasValue && item.ScopeType == MovieWorldScopes.Shot && item.ScopeId == shotId || entityId.HasValue && item.ScopeId == entityId) ?? [];
    private static bool IsFactLocked(MovieProductionContinuityContext context, MovieWorldContinuityFactSnapshot fact) => context.World?.Locks.Any(item =>
        item.FieldName.Equals(fact.FactKey, StringComparison.OrdinalIgnoreCase) &&
        (item.EntityId == fact.ScopeId || fact.ScopeType == MovieWorldScopes.Project && item.EntityType == MovieWorldScopes.Project && (!item.EntityId.HasValue || item.EntityId == context.MovieProjectId)) &&
        item.Strength.Equals(MovieContinuityLockStrengths.Hard, StringComparison.OrdinalIgnoreCase)) == true;
    private static bool IsStateOrPosition(string key) => key.Contains("state", StringComparison.OrdinalIgnoreCase) || key.Contains("condition", StringComparison.OrdinalIgnoreCase) || key.Contains("position", StringComparison.OrdinalIgnoreCase) || key.Contains("location", StringComparison.OrdinalIgnoreCase);
    private static string CategoryForCharacterField(string field) => field.Contains("wardrobe", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.Wardrobe : field.Contains("injury", StringComparison.OrdinalIgnoreCase) || field.Contains("condition", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.InjuryState : field.Contains("location", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.CharacterPresence : DirectorStoryFindingCategories.CastContinuity;
    private static string CategoryForWorldField(string field) => field.Contains("weather", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.Weather : field.Contains("time", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.TimeOfDay : field.Contains("position", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.ObjectPosition : field.Contains("state", StringComparison.OrdinalIgnoreCase) || field.Contains("condition", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.ObjectState : field.Contains("location", StringComparison.OrdinalIgnoreCase) ? DirectorStoryFindingCategories.EnvironmentLocation : DirectorStoryFindingCategories.WorldContinuity;
    private static bool HasRelevantCanon(MovieProductionContinuityContext context, string planText) => context.Characters.Any(item => ContainsWord(planText, item.Name) && (item.Wardrobe is not null || item.States.Any(state => state.Wardrobe is not null || state.InjuryOrCondition is not null))) || context.World?.Facts.Any(item => planText.Contains(item.FactValue, StringComparison.OrdinalIgnoreCase)) == true;
    private static string PlanLabel(MovieProductionContinuityShotContext? shot) => shot is null ? "scene plan" : $"shot {shot.Sequence} plan";
    private static string SceneText(MovieProductionContinuitySceneContext scene) => string.Join(" ", scene.Title, scene.Summary, scene.ContinuityNotes, scene.Narration, scene.Dialogue);
    private static string ShotText(MovieProductionContinuityShotContext shot) => string.Join(" ", shot.Description, shot.Purpose, shot.Subjects, shot.LocationSet, shot.ProductionRequirements, shot.ContinuityReferences, shot.CameraAndFraming, shot.CameraMotion, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes);
    private static string FindingKey(DirectorStoryFindingDto finding) => $"{finding.FindingType}|{finding.Category}|{finding.AffectedTarget.TargetType}|{finding.AffectedTarget.TargetId}|{finding.Explanation}";
    private static void Add(ICollection<DirectorStoryFindingDto> findings, DirectorStoryFindingDto finding) => findings.Add(finding);
    private static DirectorStoryFindingDto Finding(string type, string severity, string category, IReadOnlyList<DirectorStoryEvidenceDto> evidence, DirectorStoryFindingTargetDto target, string explanation, string correction, decimal confidence, string? uncertainty) => new(type, severity, category, evidence, target, explanation, correction, Math.Clamp(confidence, 0m, 1m), uncertainty);
    private static DirectorStoryEvidenceDto Evidence(string source, string? sourceType, Guid? sourceId, string? revision, string excerpt) => new(source, sourceType, sourceId, revision, Excerpt(excerpt));
    private static DirectorStoryEvidenceDto CharacterEvidence(MovieProductionContinuityCharacterContext character, string field, string value, Guid? sourceId) => Evidence($"Cast: {character.Name} · {field}", "character_continuity", sourceId ?? character.Id, null, value);
    private static DirectorStoryEvidenceDto CharacterEvidence(MovieProductionContinuityCharacterContext character, string field, string value) => CharacterEvidence(character, field, value, null);
    private static DirectorStoryFindingTargetDto Target(string type, Guid? id, string? label) => new(type, id, label);
    private sealed record WorldEntity(Guid Id, string Type, string Name, string Description, Guid? LocationId, string? TimeOfDay, string? Weather, MovieWorldContinuityVariationSnapshot? Variation, string? Notes);
    private static string? ContradictingValue(string expectedText, string actualText)
    {
        if (string.IsNullOrWhiteSpace(expectedText) || string.IsNullOrWhiteSpace(actualText)) return null;
        foreach (var (first, second) in Opposites)
        {
            if (ContainsWord(expectedText, first) && ContainsWord(actualText, second)) return second;
            if (ContainsWord(expectedText, second) && ContainsWord(actualText, first)) return first;
        }
        var phrase = expectedText.Trim();
        if (phrase.Length >= 3 && Regex.IsMatch(actualText, $"\\b(?:not|never|without|no)\\s+(?:{Regex.Escape(phrase).Replace("\\ ", "\\s+")})\\b", RegexOptions.IgnoreCase)) return $"not {phrase}";
        return null;
    }
    private static bool ContainsWord(string? text, string? value) => !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(text, $"(?<![\\p{{L}}\\p{{N}}]){Regex.Escape(value.Trim())}(?![\\p{{L}}\\p{{N}}])", RegexOptions.IgnoreCase);
    private static string Excerpt(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= 800 ? value : value[..800] + "…";
    private static string ExtractJsonText(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try { using var document = JsonDocument.Parse(json); var values = new List<string>(); Walk(document.RootElement, values); return string.Join(" ", values); }
        catch (JsonException) { return json; }
    }
    private static void Walk(JsonElement element, ICollection<string> values)
    {
        if (element.ValueKind == JsonValueKind.String) { values.Add(element.GetString() ?? string.Empty); return; }
        if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) Walk(property.Value, values);
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Walk(item, values);
    }
}
