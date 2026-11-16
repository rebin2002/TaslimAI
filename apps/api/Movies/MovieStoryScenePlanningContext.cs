using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

/// <summary>
/// Request for a bounded, read-only Story/screenplay context used by a future scene planner.
/// It never creates or mutates MovieScene records.
/// </summary>
public sealed class MovieStoryScenePlanningContextRequest
{
    public Guid? StoryRevisionId { get; set; }
    public Guid? TargetSceneId { get; set; }
    public string? TargetSection { get; set; }
    public int SurroundingSceneRadius { get; set; } = 2;
    public int? TargetRuntimeSeconds { get; set; }
    public string? Language { get; set; }
    public string? AspectRatio { get; set; }
}

public sealed record MovieStoryScenePlanningGuideSectionContext(string Type, string ContentJson);

public sealed record MovieStoryScenePlanningGuideContext(
    int RevisionNumber,
    Guid RevisionId,
    string VisualLanguage,
    string CameraLanguage,
    string ColorAndLighting,
    string SoundAndNarration,
    string ContinuityRules,
    IReadOnlyList<MovieStoryScenePlanningGuideSectionContext> Sections);

public sealed record MovieStoryScenePlanningElementContext(
    Guid Id,
    int Ordinal,
    string ElementType,
    string Content,
    string? CharacterName,
    string? Parenthetical);

public sealed record MovieStoryScenePlanningSceneContext(
    Guid Id,
    int Ordinal,
    string SceneIdentifier,
    int? ActNumber,
    int? SequenceNumber,
    string Slugline,
    string? Synopsis,
    IReadOnlyList<MovieStoryScenePlanningElementContext> Elements,
    bool IsTarget,
    bool IsSurrounding);

public sealed record MovieStoryScenePlanningRevisionContext(
    Guid RevisionId,
    int RevisionNumber,
    string Status,
    string Authorship,
    string Premise,
    string Logline,
    string Synopsis,
    string Treatment,
    IReadOnlyList<MovieStoryScenePlanningSceneContext> Scenes);

public sealed record MovieStoryScenePlanningTargetContext(
    Guid RevisionId,
    int RevisionNumber,
    Guid SceneId,
    string SceneIdentifier,
    int Ordinal);

public sealed record MovieStoryScenePlanningContextDto(
    Guid MovieProjectId,
    Guid WorkspaceId,
    string MovieBrief,
    int TargetRuntimeSeconds,
    string Language,
    string AspectRatio,
    MovieStoryScenePlanningGuideContext LockedGuide,
    MovieStoryScenePlanningRevisionContext? CurrentStory,
    MovieStoryScenePlanningRevisionContext? ApprovedStory,
    MovieStoryScenePlanningTargetContext? Target,
    IReadOnlyList<string> MissingSections);

public sealed record MovieStoryScenePlanningContextDiagnostics(
    int MaxBytes,
    int UsedBytes,
    int CriticalBytes,
    int OptionalBytes,
    bool CriticalFactsComplete,
    bool OptionalMaterialTrimmed,
    bool TargetResolved,
    int RevisionCount,
    int ScreenplaySceneCount,
    int ScreenplayElementCount,
    int GuideSectionCount,
    IReadOnlyList<string> MissingSections,
    IReadOnlyList<string> IncludedSourceKinds);

public sealed record MovieStoryScenePlanningContextAssemblyResult(
    MovieStoryScenePlanningContextDto Context,
    string SnapshotJson,
    string SnapshotHash,
    MovieStoryScenePlanningContextDiagnostics Diagnostics,
    long AssemblyMilliseconds = 0);

public sealed class MovieStoryScenePlanningContextAssembler(TaslimDbContext db)
{
    public const int MaxSnapshotBytes = 100_000;
    private const int MaxBriefCharacters = 6_000;
    private const int MaxNarrativePremiseCharacters = 2_000;
    private const int MaxNarrativeLoglineCharacters = 1_000;
    private const int MaxNarrativeSynopsisCharacters = 3_000;
    private const int MaxNarrativeTreatmentCharacters = 4_000;
    private const int MaxScreenplayScenesPerRevision = 5;
    private const int MaxScreenplayElementsPerScene = 6;
    private const int MaxElementCharacters = 500;
    private const int MaxSluglineCharacters = 240;
    private const int MaxSceneSynopsisCharacters = 1_000;
    private const int MaxGuideFieldCharacters = 1_800;
    private const int MaxGuideSectionCharacters = 2_500;
    private const int MaxRadius = 3;

    public async Task<MovieStoryScenePlanningContextAssemblyResult?> AssembleAsync(
        Guid movieProjectId,
        MovieStoryScenePlanningContextRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new MovieStoryScenePlanningContextRequest();
        var stopwatch = Stopwatch.StartNew();
        var movie = await db.MovieProjects.AsNoTracking()
            .Include(item => item.Guide).ThenInclude(item => item.Revisions)
            .Where(item => item.Id == movieProjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (movie is null) return null;

        var lockedGuide = movie.Guide.LockedRevisionNumber is int lockedRevisionNumber
            ? movie.Guide.Revisions.FirstOrDefault(item =>
                item.RevisionNumber == lockedRevisionNumber && item.Status == MovieGuideRevisionStatuses.Locked)
            : null;
        if (lockedGuide is null) return null;

        var storyPointer = await db.MovieStories.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId)
            .Select(item => new { item.Id, item.CurrentRevisionId, item.ApprovedRevisionId })
            .FirstOrDefaultAsync(cancellationToken);

        var selectedRevisionIds = storyPointer is null
            ? []
            : new[] { storyPointer.CurrentRevisionId, storyPointer.ApprovedRevisionId }
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .Distinct()
                .ToArray();
        var revisions = selectedRevisionIds.Length == 0
            ? []
            : await db.MovieStoryRevisions.AsNoTracking()
                .Where(item => selectedRevisionIds.Contains(item.Id) && item.MovieStoryId == storyPointer!.Id)
                .Include(item => item.Scenes).ThenInclude(item => item.Elements)
                .ToListAsync(cancellationToken);
        var current = FindRevision(revisions, storyPointer?.CurrentRevisionId);
        var approved = FindRevision(revisions, storyPointer?.ApprovedRevisionId, requireApproved: true);

        ValidateRevisionTarget(request, storyPointer?.Id, current, approved, revisions);
        var target = ResolveTarget(request, current, approved);
        if (request.TargetSceneId.HasValue && target is null)
        {
            var staleRevisionId = storyPointer is null
                ? (Guid?)null
                : await db.MovieScreenplayScenes.AsNoTracking()
                    .Where(item => item.Id == request.TargetSceneId.Value && item.Revision.MovieStoryId == storyPointer.Id)
                    .Select(item => (Guid?)item.MovieStoryRevisionId)
                    .FirstOrDefaultAsync(cancellationToken);
            if (staleRevisionId.HasValue)
                throw new MovieStoryScenePlanningTargetException("MOVIE_SCENE_PLANNING_STALE_TARGET", "The selected screenplay scene belongs to an older Story revision and cannot ground a new scene plan.");
            throw new MovieStoryScenePlanningTargetException("MOVIE_SCENE_PLANNING_TARGET_NOT_FOUND", "The selected screenplay scene is not present in the current or approved Story revision.");
        }
        if (!string.IsNullOrWhiteSpace(request.TargetSection) && target is null)
            throw new MovieStoryScenePlanningTargetException("MOVIE_SCENE_PLANNING_SECTION_NOT_FOUND", "The requested screenplay section is not present in the current or approved Story revision.");

        var targetText = TargetText(target);
        var guide = BuildGuideContext(movie, lockedGuide, targetText);
        var currentContext = BuildRevisionContext(current, target, request.SurroundingSceneRadius);
        var approvedContext = BuildRevisionContext(approved, target, request.SurroundingSceneRadius);
        var targetContext = target is null
            ? null
            : new MovieStoryScenePlanningTargetContext(target.Value.Revision.Id, target.Value.Revision.RevisionNumber, target.Value.Scene.Id, Bound(target.Value.Scene.SceneIdentifier, 80), target.Value.Scene.Ordinal);

        var missing = new List<string>();
        if (storyPointer is null) missing.Add("story");
        if (current is null) missing.Add("current_story");
        if (approved is null) missing.Add("approved_story");
        if ((current?.Scenes.Count ?? 0) == 0 && (approved?.Scenes.Count ?? 0) == 0) missing.Add("screenplay");
        if (target is null) missing.Add("target_scene");

        var context = new MovieStoryScenePlanningContextDto(
            movie.Id,
            movie.WorkspaceId,
            Bound(movie.Description, MaxBriefCharacters),
            Math.Clamp(request.TargetRuntimeSeconds ?? movie.DurationSeconds, 1, 86_400),
            Bound(string.IsNullOrWhiteSpace(request.Language) ? movie.Language : request.Language, 20),
            Bound(string.IsNullOrWhiteSpace(request.AspectRatio) ? movie.AspectRatio : request.AspectRatio, 20),
            guide,
            currentContext,
            approvedContext,
            targetContext,
            missing);

        var snapshotJson = JsonSerializer.Serialize(context, DirectorJson.Options);
        var usedBytes = Encoding.UTF8.GetByteCount(snapshotJson);
        if (usedBytes > MaxSnapshotBytes)
            throw new MovieStoryScenePlanningContextBudgetException($"Story scene planning context exceeds the deterministic {MaxSnapshotBytes}-byte budget.");

        var criticalJson = JsonSerializer.Serialize(new
        {
            context.MovieBrief,
            context.TargetRuntimeSeconds,
            context.Language,
            context.AspectRatio,
            context.LockedGuide,
            context.Target,
        }, DirectorJson.Options);
        var criticalBytes = Encoding.UTF8.GetByteCount(criticalJson);
        var elementCount = CountElements(currentContext) + CountElements(approvedContext);
        var sceneCount = CountScenes(currentContext) + CountScenes(approvedContext);
        var sources = new List<string> { "movie_brief", "locked_guide", "runtime", "language", "aspect_ratio" };
        if (currentContext is not null) sources.Add("current_story");
        if (approvedContext is not null) sources.Add("approved_story");
        if (target is not null) sources.Add("target_scene");
        if (sceneCount > (target is null ? 0 : 1)) sources.Add("surrounding_screenplay");
        var diagnostics = new MovieStoryScenePlanningContextDiagnostics(
            MaxSnapshotBytes,
            usedBytes,
            criticalBytes,
            Math.Max(0, usedBytes - criticalBytes),
            criticalBytes <= MaxSnapshotBytes,
            false,
            target is not null,
            (current is null ? 0 : 1) + (approved is null ? 0 : 1),
            sceneCount,
            elementCount,
            guide.Sections.Count,
            missing,
            sources);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
        stopwatch.Stop();
        return new MovieStoryScenePlanningContextAssemblyResult(context, snapshotJson, hash, diagnostics, stopwatch.ElapsedMilliseconds);
    }

    private static MovieStoryRevision? FindRevision(IReadOnlyList<MovieStoryRevision> revisions, Guid? id, bool requireApproved = false)
    {
        if (!id.HasValue) return null;
        return revisions.FirstOrDefault(item => item.Id == id.Value && (!requireApproved || item.Status == MovieStoryRevisionStatuses.Approved));
    }

    private static void ValidateRevisionTarget(
        MovieStoryScenePlanningContextRequest request,
        Guid? storyId,
        MovieStoryRevision? current,
        MovieStoryRevision? approved,
        IReadOnlyList<MovieStoryRevision> revisions)
    {
        if (!request.StoryRevisionId.HasValue) return;
        var selected = revisions.FirstOrDefault(item => item.Id == request.StoryRevisionId.Value);
        if (selected is null || (selected != current && selected != approved))
            throw new MovieStoryScenePlanningTargetException("MOVIE_SCENE_PLANNING_STALE_TARGET", "Scene planning must use the current or approved Story revision; the requested base is stale.");
        if (selected.Status != MovieStoryRevisionStatuses.Approved && selected != current)
            throw new MovieStoryScenePlanningTargetException("MOVIE_SCENE_PLANNING_STALE_TARGET", "Scene planning cannot use a non-current Story revision.");
    }

    private static (MovieStoryRevision Revision, MovieScreenplayScene Scene)? ResolveTarget(
        MovieStoryScenePlanningContextRequest request,
        MovieStoryRevision? current,
        MovieStoryRevision? approved)
    {
        var candidates = request.StoryRevisionId.HasValue
            ? new[] { current, approved }.Where(item => item?.Id == request.StoryRevisionId).Select(item => item!).ToArray()
            : new[] { current, approved }.Where(item => item is not null).Select(item => item!).ToArray();
        foreach (var revision in candidates)
        {
            MovieScreenplayScene? scene = null;
            if (request.TargetSceneId is Guid targetId)
                scene = revision.Scenes.FirstOrDefault(item => item.Id == targetId);
            else if (!string.IsNullOrWhiteSpace(request.TargetSection))
            {
                var section = request.TargetSection.Trim();
                scene = revision.Scenes.FirstOrDefault(item =>
                    item.SceneIdentifier.Equals(section, StringComparison.OrdinalIgnoreCase) ||
                    item.Slugline.Equals(section, StringComparison.OrdinalIgnoreCase));
            }
            else
                scene = revision.Scenes.OrderBy(item => item.Ordinal).FirstOrDefault();
            if (scene is not null) return (revision, scene);
        }
        return null;
    }

    private static MovieStoryScenePlanningRevisionContext? BuildRevisionContext(
        MovieStoryRevision? revision,
        (MovieStoryRevision Revision, MovieScreenplayScene Scene)? target,
        int requestedRadius)
    {
        if (revision is null) return null;
        var targetScene = target?.Revision.Id == revision.Id ? target.Value.Scene : null;
        var scenes = revision.Scenes.OrderBy(item => item.Ordinal).ToArray();
        var radius = Math.Clamp(requestedRadius, 0, MaxRadius);
        var targetIndex = targetScene is null
            ? (scenes.Length == 0 ? -1 : 0)
            : Array.FindIndex(scenes, item => item.Id == targetScene.Id);
        var start = targetIndex < 0 ? 0 : Math.Max(0, targetIndex - radius);
        var end = targetIndex < 0 ? Math.Min(scenes.Length, 1) : Math.Min(scenes.Length, targetIndex + radius + 1);
        var selected = scenes.Skip(start).Take(Math.Max(0, end - start)).Take(MaxScreenplayScenesPerRevision).ToArray();
        return new MovieStoryScenePlanningRevisionContext(
            revision.Id,
            revision.RevisionNumber,
            revision.Status,
            Bound(revision.Authorship, 40),
            Bound(revision.Premise, MaxNarrativePremiseCharacters),
            Bound(revision.Logline, MaxNarrativeLoglineCharacters),
            Bound(revision.Synopsis, MaxNarrativeSynopsisCharacters),
            Bound(revision.Treatment, MaxNarrativeTreatmentCharacters),
            selected.Select(scene => ToSceneContext(scene, targetScene, targetIndex >= 0 && scene.Ordinal != targetScene?.Ordinal)).ToArray());
    }

    private static MovieStoryScenePlanningSceneContext ToSceneContext(MovieScreenplayScene scene, MovieScreenplayScene? target, bool surrounding)
    {
        return new MovieStoryScenePlanningSceneContext(
            scene.Id,
            scene.Ordinal,
            Bound(scene.SceneIdentifier, 80),
            scene.ActNumber,
            scene.SequenceNumber,
            Bound(scene.Slugline, MaxSluglineCharacters),
            BoundNullable(scene.Synopsis, MaxSceneSynopsisCharacters),
            scene.Elements.OrderBy(item => item.Ordinal).Take(MaxScreenplayElementsPerScene).Select(item =>
                new MovieStoryScenePlanningElementContext(item.Id, item.Ordinal, Bound(item.ElementType, 30), Bound(item.Content, MaxElementCharacters), BoundNullable(item.CharacterName, 160), BoundNullable(item.Parenthetical, 300))).ToArray(),
            target?.Id == scene.Id,
            surrounding);
    }

    private static MovieStoryScenePlanningGuideContext BuildGuideContext(MovieProject movie, MovieGuideRevision revision, string targetText)
    {
        var sections = new[]
        {
            (MovieGuideSectionTypes.StoryBible, revision.StoryBibleJson, false),
            (MovieGuideSectionTypes.CharacterBibleReferences, revision.CharacterBibleReferencesJson, false),
            (MovieGuideSectionTypes.WorldBibleReferences, revision.WorldBibleReferencesJson, false),
            (MovieGuideSectionTypes.VisualBible, revision.VisualBibleJson, true),
            (MovieGuideSectionTypes.CinematographyBible, revision.CinematographyBibleJson, true),
            (MovieGuideSectionTypes.AudioBible, revision.AudioBibleJson, true),
            (MovieGuideSectionTypes.ContinuityBible, revision.ContinuityBibleJson, true),
        };
        var anchorTokens = targetText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken).Where(item => item.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = sections.Where(item => item.Item3 || anchorTokens.Length == 0 || anchorTokens.Any(token => item.Item2.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(item => new MovieStoryScenePlanningGuideSectionContext(item.Item1, Bound(item.Item2, MaxGuideSectionCharacters)))
            .ToArray();
        return new MovieStoryScenePlanningGuideContext(
            revision.RevisionNumber,
            revision.Id,
            Bound(movie.Guide.VisualLanguage, MaxGuideFieldCharacters),
            Bound(movie.Guide.CameraLanguage, MaxGuideFieldCharacters),
            Bound(movie.Guide.ColorAndLighting, MaxGuideFieldCharacters),
            Bound(movie.Guide.SoundAndNarration, MaxGuideFieldCharacters),
            Bound(movie.Guide.ContinuityRules, MaxGuideFieldCharacters),
            selected);
    }

    private static string TargetText((MovieStoryRevision Revision, MovieScreenplayScene Scene)? target) => target is null
        ? string.Empty
        : string.Join(" ", target.Value.Scene.SceneIdentifier, target.Value.Scene.Slugline, target.Value.Scene.Synopsis, target.Value.Scene.Elements.SelectMany(item => new[] { item.Content, item.CharacterName, item.Parenthetical }));

    private static int CountScenes(MovieStoryScenePlanningRevisionContext? context) => context?.Scenes.Count ?? 0;
    private static int CountElements(MovieStoryScenePlanningRevisionContext? context) => context?.Scenes.Sum(item => item.Elements.Count) ?? 0;
    private static string NormalizeToken(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    private static string Bound(string? value, int maximum) => string.IsNullOrEmpty(value) || value.Length <= maximum ? value ?? string.Empty : value[..maximum];
    private static string? BoundNullable(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : Bound(value, maximum);
}

public sealed class MovieStoryScenePlanningTargetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MovieStoryScenePlanningContextBudgetException(string message) : Exception(message);
