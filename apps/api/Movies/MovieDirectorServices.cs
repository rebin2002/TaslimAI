using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

public interface IMovieDirectorService
{
    Task<DirectorProposalResponse?> CreateProposalAsync(Guid userId, Guid movieProjectId, DirectorProposalRequest request, CancellationToken cancellationToken = default);
    Task<DirectorProposalResponse?> GetProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectorHistoryDto>?> GetHistoryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default);
    Task<DirectorProposalDto?> ApproveProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default);
    Task<DirectorProposalDto?> RejectProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default);
    Task<DirectorActionExecutionResponse?> ExecuteActionAsync(Guid userId, Guid actionId, CancellationToken cancellationToken = default);
}

public interface IDirectorActionExecutor
{
    string ActionType { get; }
    Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, CancellationToken cancellationToken = default);
}

public sealed record DirectorActionExecution(bool Succeeded, string? FailureCode, string SafeMessage, string? ResultJson);

public sealed class MovieDirectorContextAssembler(TaslimDbContext db)
{
    private const int MaxSnapshotBytes = 100_000;
    private const int MaxOptionalBytes = 40_000;

    public Task<DirectorContextAssemblyResult?> AssembleAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default) =>
        AssembleAsync(userId, movieProjectId, new DirectorContextTargetRequest(), cancellationToken);

    public async Task<DirectorContextAssemblyResult?> AssembleAsync(Guid userId, Guid movieProjectId, DirectorContextTargetRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var targetType = NormalizeTargetType(request.TargetType);
        var movie = await db.MovieProjects.AsNoTracking().Include(item => item.Guide).ThenInclude(item => item.Revisions)
            .FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;

        var lockedRevision = movie.Guide.LockedRevisionNumber is int lockedRevisionNumber
            ? movie.Guide.Revisions.FirstOrDefault(item => item.RevisionNumber == lockedRevisionNumber && item.Status == MovieGuideRevisionStatuses.Locked)
            : null;
        if (lockedRevision is null) return null;

        var target = await ResolveTargetAsync(movieProjectId, targetType, request.TargetId, cancellationToken);
        if (target is null) throw new DirectorContextTargetException("The Director context target is not part of this movie project.");

        var approvedStoryRevision = await db.MovieStoryRevisions.AsNoTracking()
            .Include(item => item.MovieStory)
            .FirstOrDefaultAsync(item => item.MovieStory.MovieProjectId == movieProjectId && item.MovieStory.ApprovedRevisionId == item.Id && item.Status == MovieStoryRevisionStatuses.Approved, cancellationToken);
        var selectedStoryRevision = target.StoryRevisionId.HasValue
            ? await db.MovieStoryRevisions.AsNoTracking().Include(item => item.MovieStory)
                .FirstOrDefaultAsync(item => item.Id == target.StoryRevisionId && item.MovieStory.MovieProjectId == movieProjectId && item.Status == MovieStoryRevisionStatuses.Approved, cancellationToken)
            : approvedStoryRevision;
        var storyRevision = selectedStoryRevision ?? approvedStoryRevision;
        var approvedStoryContext = storyRevision is null ? null : new DirectorStoryContext(
            storyRevision.Id, storyRevision.RevisionNumber, Bounded(storyRevision.Premise, 8_000), Bounded(storyRevision.Logline, 2_000),
            Bounded(storyRevision.Synopsis, 20_000), Bounded(storyRevision.Treatment, 40_000), storyRevision.Authorship);

        var sceneIds = await SelectSceneIdsAsync(movieProjectId, target, storyRevision, cancellationToken);
        var shotIds = await SelectShotIdsAsync(target, sceneIds, cancellationToken);
        var scenes = await db.MovieScenes.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && sceneIds.Contains(item.Id))
            .Include(item => item.Shots).OrderBy(item => item.Sequence).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var shots = scenes.SelectMany(item => item.Shots).Where(item => shotIds.Count == 0 || shotIds.Contains(item.Id)).OrderBy(item => item.Sequence).ThenBy(item => item.Id).ToArray();

        var relevantCharacterIds = await SelectCharacterIdsAsync(movieProjectId, scenes, shots, storyRevision?.Id, cancellationToken);
        var relevantLocationIds = await SelectLocationIdsAsync(movieProjectId, scenes, shots, cancellationToken);
        var characters = await db.MovieCharacters.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && relevantCharacterIds.Contains(item.Id))
            .Include(item => item.ContinuityLocks).Include(item => item.States).ThenInclude(item => item.ContinuityLocks)
            .OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var locations = await db.MovieLocations.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && relevantLocationIds.Contains(item.Id))
            .OrderBy(item => item.Id).ToListAsync(cancellationToken);

        var usages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && sceneIds.Contains(item.MovieSceneId) && (item.MovieShotId == null || shotIds.Contains(item.MovieShotId.Value))).OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var relevantWorldIds = usages.Select(item => (item.EntityType, item.EntityId)).ToHashSet();
        var world = await AssembleWorldAsync(movieProjectId, sceneIds, shotIds, relevantWorldIds, cancellationToken);
        var production = await AssembleProductionAsync(target, shots, cancellationToken);
        var collaboration = await AssembleCollaborationAsync(movieProjectId, target, sceneIds, shotIds, cancellationToken);
        var provenance = BuildProvenance(lockedRevision, storyRevision, target, characters, world, production, collaboration);

        var guide = new DirectorGuideContext(
            Bounded(movie.Guide.VisualLanguage, 4_000), Bounded(movie.Guide.CameraLanguage, 4_000), Bounded(movie.Guide.ColorAndLighting, 4_000),
            Bounded(movie.Guide.SoundAndNarration, 4_000), Bounded(movie.Guide.ContinuityRules, 8_000), lockedRevision.RevisionNumber, true,
            Bounded(lockedRevision.CinematographyBibleJson, 50_000),
            [
                new(MovieGuideSectionTypes.StoryBible, Bounded(lockedRevision.StoryBibleJson, 50_000)),
                new(MovieGuideSectionTypes.CharacterBibleReferences, Bounded(lockedRevision.CharacterBibleReferencesJson, 50_000)),
                new(MovieGuideSectionTypes.WorldBibleReferences, Bounded(lockedRevision.WorldBibleReferencesJson, 50_000)),
                new(MovieGuideSectionTypes.VisualBible, Bounded(lockedRevision.VisualBibleJson, 50_000)),
                new(MovieGuideSectionTypes.CinematographyBible, Bounded(lockedRevision.CinematographyBibleJson, 50_000)),
                new(MovieGuideSectionTypes.AudioBible, Bounded(lockedRevision.AudioBibleJson, 50_000)),
                new(MovieGuideSectionTypes.ContinuityBible, Bounded(lockedRevision.ContinuityBibleJson, 50_000)),
            ]);

        var sceneContexts = scenes.Select(scene => new DirectorSceneContext(
            scene.Id, scene.Sequence, Bounded(scene.Title, 160), Bounded(scene.Summary, 8_000), scene.DurationSeconds, Bounded(scene.ContinuityNotes, 4_000),
            shots.Where(shot => shot.MovieSceneId == scene.Id).OrderBy(shot => shot.Sequence).ThenBy(shot => shot.Id).Select(ToShotContext).ToArray())).ToArray();
        var characterContexts = characters.OrderBy(item => item.Id).Select(item => new DirectorCharacterContext(
            Bounded(item.Name, 160), Bounded(item.Description, 8_000), Bounded(item.Appearance, 4_000), Bounded(item.ContinuityNotes, 4_000),
            item.ContinuityLocks.Where(lockItem => lockItem.MovieCharacterStateId is null).OrderBy(item => item.FieldKey).Select(item => new DirectorLockedFactContext(Bounded(item.FieldKey, 120), Bounded(item.LockedValue, 8_000))).Concat(
                item.States.SelectMany(state => state.ContinuityLocks.OrderBy(lockItem => lockItem.FieldKey).Select(lockItem => new DirectorLockedFactContext(Bounded(lockItem.FieldKey, 120), Bounded(lockItem.LockedValue, 8_000), state.Id)))).ToArray(), item.Id)).ToArray();
        var locationContexts = locations.OrderBy(item => item.Id).Select(item => new DirectorLocationContext(Bounded(item.Name, 160), Bounded(item.Description, 8_000), Bounded(item.VisualContinuityNotes, 4_000), item.Id)).ToArray();
        var targetDto = new DirectorContextTargetDto(target.Type, target.Id, target.StoryRevisionId, target.SceneId, target.ShotId, target.ProductionVersionId, target.TakeId);
        var contextWithoutBudget = new DirectorContextDto(
            movie.Id, movie.WorkspaceId, Bounded(movie.Title, 160), Bounded(movie.Description, 8_000), movie.DurationSeconds, Bounded(movie.AspectRatio, 20), Bounded(movie.Style, 100), Bounded(movie.Language, 5),
            guide, sceneContexts, characterContexts, locationContexts, DateTime.UnixEpoch, Target: targetDto, ApprovedStory: approvedStoryContext, World: world, Production: production, Collaboration: collaboration, Provenance: provenance);

        var criticalJson = JsonSerializer.Serialize(new { contextWithoutBudget.Guide, contextWithoutBudget.ApprovedStory, world.Locks, world.Facts, lockedCast = characterContexts.Select(item => new { item.Id, item.LockedFacts }) }, DirectorJson.Options);
        var criticalBytes = Encoding.UTF8.GetByteCount(criticalJson);
        if (criticalBytes > MaxSnapshotBytes - MaxOptionalBytes) throw new DirectorContextBudgetException("Locked Director context facts exceed the maximum context budget.");
        var context = contextWithoutBudget with { Budget = new DirectorContextBudgetDto(MaxSnapshotBytes, 0, criticalBytes, 0, true) };
        var snapshotJson = string.Empty;
        var usedBytes = 0;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            snapshotJson = JsonSerializer.Serialize(context, DirectorJson.Options);
            usedBytes = Encoding.UTF8.GetByteCount(snapshotJson);
            context = contextWithoutBudget with { Budget = new DirectorContextBudgetDto(MaxSnapshotBytes, usedBytes, criticalBytes, Math.Max(0, usedBytes - criticalBytes), true) };
        }
        snapshotJson = JsonSerializer.Serialize(context, DirectorJson.Options);
        usedBytes = Encoding.UTF8.GetByteCount(snapshotJson);
        if (usedBytes != context.Budget!.UsedBytes)
        {
            context = context with { Budget = context.Budget with { UsedBytes = usedBytes, OptionalBytes = Math.Max(0, usedBytes - criticalBytes) } };
            snapshotJson = JsonSerializer.Serialize(context, DirectorJson.Options);
            usedBytes = Encoding.UTF8.GetByteCount(snapshotJson);
        }
        if (Math.Max(0, usedBytes - criticalBytes) > MaxOptionalBytes || usedBytes > MaxSnapshotBytes) throw new DirectorContextBudgetException("Director context exceeds its deterministic bounded budget; critical locked facts were not truncated.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
        stopwatch.Stop();
        return new DirectorContextAssemblyResult(context, snapshotJson, hash, stopwatch.ElapsedMilliseconds);
    }

    private async Task<DirectorResolvedTarget?> ResolveTargetAsync(Guid movieProjectId, string targetType, Guid? targetId, CancellationToken cancellationToken)
    {
        var id = targetId ?? movieProjectId;
        return targetType switch
        {
            DirectorContextTargetTypes.Project when id == movieProjectId => new(targetType, id, null, null, null, null, null),
            DirectorContextTargetTypes.StoryRevision => await db.MovieStoryRevisions.AsNoTracking().Where(item => item.Id == id && item.MovieStory.MovieProjectId == movieProjectId && item.Status == MovieStoryRevisionStatuses.Approved).Select(item => new DirectorResolvedTarget(targetType, item.Id, item.Id, null, null, null, null)).FirstOrDefaultAsync(cancellationToken),
            DirectorContextTargetTypes.Scene => await db.MovieScenes.AsNoTracking().Where(item => item.Id == id && item.MovieProjectId == movieProjectId).Select(item => new DirectorResolvedTarget(targetType, item.Id, null, item.Id, null, null, null)).FirstOrDefaultAsync(cancellationToken),
            DirectorContextTargetTypes.Shot => await db.MovieShots.AsNoTracking().Where(item => item.Id == id && item.Scene.MovieProjectId == movieProjectId).Select(item => new DirectorResolvedTarget(targetType, item.Id, null, item.MovieSceneId, item.Id, null, null)).FirstOrDefaultAsync(cancellationToken),
            DirectorContextTargetTypes.StoryboardVersion => await db.MovieProductionVersions.AsNoTracking().Where(item => item.Id == id && (item.Stage == MovieProductionStages.StoryboardCandidate || item.Stage == MovieProductionStages.ApprovedStoryboard) && item.MovieShot.Scene.MovieProjectId == movieProjectId).Select(item => new DirectorResolvedTarget(targetType, item.Id, null, item.MovieShot.MovieSceneId, item.MovieShotId, item.Id, null)).FirstOrDefaultAsync(cancellationToken),
            DirectorContextTargetTypes.ProductionVersion => await db.MovieProductionVersions.AsNoTracking().Where(item => item.Id == id && item.MovieShot.Scene.MovieProjectId == movieProjectId).Select(item => new DirectorResolvedTarget(targetType, item.Id, null, item.MovieShot.MovieSceneId, item.MovieShotId, item.Id, null)).FirstOrDefaultAsync(cancellationToken),
            DirectorContextTargetTypes.Take => await db.MovieTakes.AsNoTracking().Where(item => item.Id == id && item.MovieShot.Scene.MovieProjectId == movieProjectId).Select(item => new DirectorResolvedTarget(targetType, item.Id, null, item.MovieShot.MovieSceneId, item.MovieShotId, null, item.Id)).FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };
    }

    private async Task<IReadOnlySet<Guid>> SelectSceneIdsAsync(Guid movieProjectId, DirectorResolvedTarget target, MovieStoryRevision? storyRevision, CancellationToken cancellationToken)
    {
        if (target.SceneId.HasValue) return new HashSet<Guid> { target.SceneId.Value };
        if (target.Type == DirectorContextTargetTypes.StoryRevision && storyRevision is not null)
        {
            var linked = await db.MovieScreenplayScenes.AsNoTracking().Where(item => item.MovieStoryRevisionId == storyRevision.Id && item.MovieSceneId.HasValue).Select(item => item.MovieSceneId!.Value).ToListAsync(cancellationToken);
            if (linked.Count > 0) return linked.ToHashSet();
        }
        return (await db.MovieScenes.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).OrderBy(item => item.Sequence).ThenBy(item => item.Id).Select(item => item.Id).ToListAsync(cancellationToken)).ToHashSet();
    }

    private async Task<IReadOnlySet<Guid>> SelectShotIdsAsync(DirectorResolvedTarget target, IReadOnlySet<Guid> sceneIds, CancellationToken cancellationToken)
    {
        if (target.ShotId.HasValue) return new HashSet<Guid> { target.ShotId.Value };
        return (await db.MovieShots.AsNoTracking().Where(item => sceneIds.Contains(item.MovieSceneId)).Select(item => item.Id).ToListAsync(cancellationToken)).ToHashSet();
    }

    private async Task<IReadOnlySet<Guid>> SelectCharacterIdsAsync(Guid movieProjectId, IReadOnlyList<MovieScene> scenes, IReadOnlyList<MovieShot> shots, Guid? storyRevisionId, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        var targetText = string.Join(" ", scenes.SelectMany(scene => new[] { scene.Title, scene.Summary, scene.Narration, scene.Dialogue }).Concat(shots.SelectMany(shot => new[] { shot.Description, shot.Narration, shot.Dialogue }))).ToLowerInvariant();
        if (storyRevisionId.HasValue)
        {
            var sceneIds = scenes.Select(item => item.Id).ToArray();
            var screenplayNames = await db.MovieScreenplayElements.AsNoTracking().Where(item => item.Scene.MovieStoryRevisionId == storyRevisionId && item.Scene.MovieSceneId.HasValue && sceneIds.Contains(item.Scene.MovieSceneId.Value) && item.CharacterName != null).Select(item => item.CharacterName!).ToListAsync(cancellationToken);
            targetText = $"{targetText} {string.Join(" ", screenplayNames)}".ToLowerInvariant();
        }
        var candidates = await db.MovieCharacters.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).Select(item => new { item.Id, item.Name }).ToListAsync(cancellationToken);
        foreach (var candidate in candidates.Where(item => targetText.Contains(item.Name.ToLowerInvariant(), StringComparison.Ordinal))) ids.Add(candidate.Id);
        var candidateIds = candidates.Select(item => item.Id).ToArray();
        var locks = await db.MovieCharacterContinuityLocks.AsNoTracking().Where(item => candidateIds.Contains(item.MovieCharacterId)).Select(item => new { item.MovieCharacterId, item.LockedValue }).ToListAsync(cancellationToken);
        ids.UnionWith(locks.Where(item => targetText.Contains(item.LockedValue, StringComparison.OrdinalIgnoreCase)).Select(item => item.MovieCharacterId));
        if (ids.Count > 0)
        {
            var related = await db.MovieCharacterRelationships.AsNoTracking().Where(item => candidateIds.Contains(item.MovieCharacterId) && (ids.Contains(item.MovieCharacterId) || ids.Contains(item.RelatedCharacterId))).Select(item => new { item.MovieCharacterId, item.RelatedCharacterId }).ToListAsync(cancellationToken);
            ids.UnionWith(related.SelectMany(item => new[] { item.MovieCharacterId, item.RelatedCharacterId }));
        }
        return ids;
    }

    private async Task<IReadOnlySet<Guid>> SelectLocationIdsAsync(Guid movieProjectId, IReadOnlyList<MovieScene> scenes, IReadOnlyList<MovieShot> shots, CancellationToken cancellationToken)
    {
        var sceneIds = scenes.Select(item => item.Id).ToHashSet();
        var shotIds = shots.Select(item => item.Id).ToHashSet();
        var usages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && sceneIds.Contains(item.MovieSceneId) && (item.MovieShotId == null || shotIds.Contains(item.MovieShotId.Value)) && item.EntityType == MovieWorldEntityTypes.Location).Select(item => item.EntityId).ToListAsync(cancellationToken);
        return usages.ToHashSet();
    }

    private async Task<DirectorWorldContext> AssembleWorldAsync(Guid movieProjectId, IReadOnlySet<Guid> sceneIds, IReadOnlySet<Guid> shotIds, IReadOnlySet<(string EntityType, Guid EntityId)> relevantWorldIds, CancellationToken cancellationToken)
    {
        var locationIds = relevantWorldIds.Where(item => item.EntityType == MovieWorldEntityTypes.Location).Select(item => item.EntityId).ToHashSet();
        var setIds = relevantWorldIds.Where(item => item.EntityType == MovieWorldEntityTypes.Set).Select(item => item.EntityId).ToHashSet();
        var propIds = relevantWorldIds.Where(item => item.EntityType == MovieWorldEntityTypes.Prop).Select(item => item.EntityId).ToHashSet();
        var entityIds = relevantWorldIds.Select(item => item.EntityId).ToArray();
        var sceneIdArray = sceneIds.ToArray();
        var shotIdArray = shotIds.ToArray();
        var locations = await db.MovieLocations.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && locationIds.Contains(item.Id)).Select(item => new DirectorWorldEntityContext(item.Id, MovieWorldEntityTypes.Location, item.Name, item.Description, item.VisualContinuityNotes, null, false)).ToListAsync(cancellationToken);
        var sets = await db.MovieSets.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && setIds.Contains(item.Id)).Select(item => new DirectorWorldEntityContext(item.Id, MovieWorldEntityTypes.Set, item.Name, item.Description, item.ContinuityNotes, null, false)).ToListAsync(cancellationToken);
        var props = await db.MovieProps.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && propIds.Contains(item.Id)).Select(item => new DirectorWorldEntityContext(item.Id, MovieWorldEntityTypes.Prop, item.Name, item.Description, item.ContinuityNotes, null, false)).ToListAsync(cancellationToken);
        var usages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && sceneIds.Contains(item.MovieSceneId) && (item.MovieShotId == null || shotIds.Contains(item.MovieShotId.Value))).ToListAsync(cancellationToken);
        var references = await db.MovieWorldReferenceLinks.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && entityIds.Contains(item.EntityId)).Select(item => new DirectorWorldEntityContext(item.Reference.Id, "reference", item.Reference.Name, item.Reference.Description ?? string.Empty, null, item.Role, false)).ToListAsync(cancellationToken);
        var entities = locations.Concat(sets).Concat(props).Concat(references).Select(entity => entity with { Role = entity.Role ?? usages.FirstOrDefault(item => item.EntityType == entity.EntityType && item.EntityId == entity.Id)?.Role }).OrderBy(item => item.EntityType).ThenBy(item => item.Id).ToArray();
        var facts = await db.MovieContinuityFacts.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && (item.ScopeType == MovieWorldScopes.Project || (item.ScopeId.HasValue && (sceneIdArray.Contains(item.ScopeId.Value) || shotIdArray.Contains(item.ScopeId.Value) || entityIds.Contains(item.ScopeId.Value))))).OrderBy(item => item.ScopeType).ThenBy(item => item.ScopeId).ThenBy(item => item.FactKey).Select(item => new DirectorContinuityFactContext(item.Id, item.ScopeType, item.ScopeId, item.FactKey, item.FactValue, item.Notes, false)).ToListAsync(cancellationToken);
        var locks = await db.MovieContinuityLocks.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && item.ReleasedAt == null && (item.EntityType == MovieWorldScopes.Project || (item.EntityId.HasValue && (sceneIdArray.Contains(item.EntityId.Value) || shotIdArray.Contains(item.EntityId.Value) || entityIds.Contains(item.EntityId.Value))))).OrderBy(item => item.EntityType).ThenBy(item => item.EntityId).ThenBy(item => item.FieldName).Select(item => new DirectorContinuityLockContext(item.Id, item.EntityType, item.EntityId, item.FieldName, item.LockedValue, item.Strength, item.Reason)).ToListAsync(cancellationToken);
        var lockedEntityIds = locks.Where(item => item.EntityId.HasValue).Select(item => item.EntityId!.Value).ToHashSet();
        return new DirectorWorldContext(entities.Select(item => item with { IsLocked = lockedEntityIds.Contains(item.Id) }).ToArray(), facts.Select(item => item with { IsLocked = item.ScopeType == MovieWorldScopes.Project || (item.ScopeId.HasValue && lockedEntityIds.Contains(item.ScopeId.Value)) }).ToArray(), locks);
    }

    private async Task<DirectorProductionContext?> AssembleProductionAsync(DirectorResolvedTarget target, IReadOnlyList<MovieShot> shots, CancellationToken cancellationToken)
    {
        var shotId = target.ShotId ?? shots.FirstOrDefault()?.Id;
        if (!shotId.HasValue) return null;
        var version = target.ProductionVersionId.HasValue ? await db.MovieProductionVersions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == target.ProductionVersionId && item.MovieShotId == shotId, cancellationToken) : null;
        var take = target.TakeId.HasValue ? await db.MovieTakes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == target.TakeId && item.MovieShotId == shotId, cancellationToken) : null;
        var shot = shots.FirstOrDefault(item => item.Id == shotId);
        return new DirectorProductionContext(shotId, shot?.ProductionStage, version?.Id, version?.Stage, version?.Status, Bounded(version?.CompositionJson, 20_000), take?.Id, take?.Status, take?.QualityLevel);
    }

    private async Task<DirectorCollaborationContext> AssembleCollaborationAsync(Guid movieProjectId, DirectorResolvedTarget target, IReadOnlySet<Guid> sceneIds, IReadOnlySet<Guid> shotIds, CancellationToken cancellationToken)
    {
        var sceneIdArray = sceneIds.ToArray();
        var shotIdArray = shotIds.ToArray();
        var reviews = await db.MovieReviews.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && ((item.TargetType == MovieCollaborationTargetTypes.Project && item.TargetId == movieProjectId) || (item.TargetType == MovieCollaborationTargetTypes.Scene && sceneIdArray.Contains(item.TargetId)) || (item.TargetType == MovieCollaborationTargetTypes.Shot && shotIdArray.Contains(item.TargetId)))).OrderBy(item => item.Id).Take(50).Select(item => new DirectorReviewContext(item.Id, item.TargetType, item.TargetId, item.Status, item.IsFinal, item.DecisionNote)).ToListAsync(cancellationToken);
        var assignments = await db.MovieProductionAssignments.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && ((item.TargetType == MovieCollaborationTargetTypes.Project && item.TargetId == movieProjectId) || (item.TargetType == MovieCollaborationTargetTypes.Scene && sceneIdArray.Contains(item.TargetId)) || (item.TargetType == MovieCollaborationTargetTypes.Shot && shotIdArray.Contains(item.TargetId)))).OrderBy(item => item.Id).Take(50).Select(item => new DirectorAssignmentContext(item.Id, item.TargetType, item.TargetId, item.Status, item.Title)).ToListAsync(cancellationToken);
        var comments = await db.MovieComments.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && ((item.TargetType == MovieCollaborationTargetTypes.Project && item.TargetId == movieProjectId) || (item.TargetType == MovieCollaborationTargetTypes.Scene && sceneIdArray.Contains(item.TargetId)) || (item.TargetType == MovieCollaborationTargetTypes.Shot && shotIdArray.Contains(item.TargetId)))).OrderBy(item => item.Id).Take(50).Select(item => new DirectorCommentContext(item.Id, item.TargetType, item.TargetId, item.ResolvedAt.HasValue)).ToListAsync(cancellationToken);
        return new DirectorCollaborationContext(reviews, assignments, comments);
    }

    private static IReadOnlyList<DirectorContextSourceDto> BuildProvenance(MovieGuideRevision guide, MovieStoryRevision? story, DirectorResolvedTarget target, IReadOnlyList<MovieCharacter> characters, DirectorWorldContext world, DirectorProductionContext? production, DirectorCollaborationContext collaboration)
    {
        var sources = new List<DirectorContextSourceDto>
        {
            new DirectorContextSourceDto("guide_revision", guide.Id, $"revision:{guide.RevisionNumber}", true, "critical"),
            new DirectorContextSourceDto("target", target.Id, $"type:{target.Type}", false, "critical"),
        };
        if (story is not null) sources.Add(new DirectorContextSourceDto("story_revision", story.Id, $"revision:{story.RevisionNumber}", true, "high"));
        sources.AddRange(characters.Select(item => new DirectorContextSourceDto("character", item.Id, $"updated:{item.UpdatedAt:O}", item.ContinuityLocks.Count > 0 || item.States.Any(state => state.ContinuityLocks.Count > 0), "relevant")));
        sources.AddRange(world.Entities.Select(item => new DirectorContextSourceDto(item.EntityType, item.Id, "current", item.IsLocked, "relevant")));
        sources.AddRange(world.Locks.Select(item => new DirectorContextSourceDto("world_lock", item.Id, "active", true, "critical")));
        if (production?.ProductionVersionId is Guid versionId) sources.Add(new DirectorContextSourceDto("production_version", versionId, production.Status ?? "current", true, "relevant"));
        if (production?.TakeId is Guid takeId) sources.Add(new DirectorContextSourceDto("take", takeId, production.TakeStatus ?? "current", true, "relevant"));
        sources.AddRange(collaboration.Reviews.Select(item => new DirectorContextSourceDto("review", item.Id, item.Status, item.IsFinal, "approval")));
        return sources;
    }

    private static DirectorShotContext ToShotContext(MovieShot shot) => new(shot.Id, shot.Sequence, Bounded(shot.Description, 8_000), Bounded(shot.CameraAndFraming, 2_000), Bounded(shot.CameraMotion, 2_000), shot.DurationSeconds, Bounded(shot.Narration, 8_000), Bounded(shot.Dialogue, 8_000), Bounded(shot.VisualContinuityNotes, 4_000), Bounded(shot.CinematographyJson, 20_000));
    private static string NormalizeTargetType(string? value) => string.IsNullOrWhiteSpace(value) ? DirectorContextTargetTypes.Project : value.Trim().ToLowerInvariant();
    private static string Bounded(string? value, int maxLength) => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? string.Empty : value[..maxLength];

    private sealed record DirectorResolvedTarget(string Type, Guid Id, Guid? StoryRevisionId, Guid? SceneId, Guid? ShotId, Guid? ProductionVersionId, Guid? TakeId);
}

public sealed class MovieDirectorService(
    TaslimDbContext db,
    WorkspaceAccessService access,
    MovieDirectorContextAssembler assembler,
    DirectorQualityPlanner qualityPlanner,
    IEnumerable<IDirectorActionExecutor> executors) : IMovieDirectorService
{
    public async Task<DirectorProposalResponse?> CreateProposalAsync(Guid userId, Guid movieProjectId, DirectorProposalRequest request, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        var contextTarget = new DirectorContextTargetRequest
        {
            TargetType = request.ContextTargetType ?? (request.ShotId.HasValue ? DirectorContextTargetTypes.Shot : DirectorContextTargetTypes.Project),
            TargetId = request.ContextTargetId ?? request.ShotId,
        };
        var context = await assembler.AssembleAsync(userId, movieProjectId, contextTarget, cancellationToken);
        if (context is null) return null;
        var shot = request.ShotId.HasValue
            ? context.Context.Scenes.SelectMany(item => item.Shots).FirstOrDefault(item => item.Id == request.ShotId.Value)
            : context.Context.Scenes.SelectMany(item => item.Shots).FirstOrDefault();
        if (shot is null) throw new DirectorValidationException("Select a shot before creating a Director proposal.");
        if (request.BudgetLimitUsd is < 0) throw new DirectorValidationException("Budget limit cannot be negative.");

        var recommendation = qualityPlanner.Recommend(new DirectorShotPlanningRequest(
            shot.Id, request.Importance, request.Complexity, request.BudgetSensitivity,
            shot.DurationSeconds ?? Math.Min(movie.DurationSeconds, 60), context.Context.Guide.ContinuityRules.Length > 0,
            request.RequestedQuality, request.BudgetLimitUsd));
        var rationale = recommendation.Reasons.ToArray();
        var now = DateTime.UtcNow;
        var directorContext = await GetOrCreateContextAsync(movie, context, cancellationToken);
        var proposal = new DirectorProposal
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, DirectorProjectContextId = directorContext.Id,
            CreatedByUserId = userId, Status = DirectorProposalStatuses.PendingApproval,
            Title = string.IsNullOrWhiteSpace(request.Goal) ? $"Plan shot {shot.Sequence}" : request.Goal.Trim(),
            Summary = $"Prepare shot {shot.Sequence} at {recommendation.QualityLevel} quality.",
            RationaleJson = JsonSerializer.Serialize(rationale), CreatedAt = now,
        };
        var action = new DirectorAction
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, DirectorProposalId = proposal.Id,
            ActionType = DirectorActionTypes.GenerateShot, Status = DirectorActionStatuses.PendingApproval, ApprovalRequired = true,
            PayloadJson = JsonSerializer.Serialize(new DirectorGenerateShotPayload(shot.Id, recommendation.QualityLevel, shot.DurationSeconds ?? 60, recommendation.EstimatedCostUsd)), CreatedAt = now,
        };
        proposal.Actions.Add(action);
        db.DirectorProposals.Add(proposal);
        db.DirectorDecisions.Add(new DirectorDecision
        {
            Id = Guid.NewGuid(), DirectorProjectContextId = directorContext.Id, MovieShotId = shot.Id, DecisionType = "quality_recommendation",
            QualityLevel = recommendation.QualityLevel, RationaleJson = JsonSerializer.Serialize(rationale), EstimatedCostUsd = recommendation.EstimatedCostUsd, CreatedAt = now,
        });
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, DirectorProposalId = proposal.Id, EventType = DirectorHistoryEventTypes.ContextAssembled,
            SafeDetailsJson = JsonSerializer.Serialize(new { contextVersion = directorContext.ContextVersion, snapshotHash = context.SnapshotHash, contextTarget = context.Context.Target?.Type, assemblyMilliseconds = context.AssemblyMilliseconds }), CreatedAt = now,
        });
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent
        {
            Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, DirectorProposalId = proposal.Id, EventType = DirectorHistoryEventTypes.ProposalCreated,
            SafeDetailsJson = JsonSerializer.Serialize(new { qualityLevel = recommendation.QualityLevel, estimatedCostUsd = recommendation.EstimatedCostUsd }), CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new DirectorProposalResponse(ToDto(proposal, rationale, [new DirectorPlanItemDto(shot.Id, shot.Sequence, shot.Description, recommendation)]), context.Context with { ContextVersion = directorContext.ContextVersion });
    }

    public async Task<DirectorProposalResponse?> GetProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = await QueryProposal().FirstOrDefaultAsync(item => item.Id == proposalId, cancellationToken);
        if (proposal is null || !await access.IsMemberAsync(userId, proposal.WorkspaceId, cancellationToken)) return null;
        var target = await db.DirectorProjectContexts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == proposal.DirectorProjectContextId && item.MovieProjectId == proposal.MovieProjectId && item.WorkspaceId == proposal.WorkspaceId, cancellationToken);
        var context = target is null ? null : await assembler.AssembleAsync(userId, proposal.MovieProjectId, new DirectorContextTargetRequest { TargetType = target.TargetType, TargetId = target.TargetId }, cancellationToken);
        return context is null ? null : new DirectorProposalResponse(ToDto(proposal), context.Context with { ContextVersion = target!.ContextVersion });
    }

    public async Task<IReadOnlyList<DirectorHistoryDto>?> GetHistoryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        return await db.DirectorHistoryEvents.AsNoTracking().Where(item => item.WorkspaceId == movie.WorkspaceId && (item.Proposal == null || item.Proposal.MovieProjectId == movieProjectId))
            .OrderByDescending(item => item.CreatedAt).Take(200).Select(item => new DirectorHistoryDto(item.Id, item.EventType, item.SafeDetailsJson, item.CreatedAt)).ToListAsync(cancellationToken);
    }

    public async Task<DirectorProposalDto?> ApproveProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default) => await SetProposalStatusAsync(userId, proposalId, true, cancellationToken);
    public async Task<DirectorProposalDto?> RejectProposalAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken = default) => await SetProposalStatusAsync(userId, proposalId, false, cancellationToken);

    public async Task<DirectorActionExecutionResponse?> ExecuteActionAsync(Guid userId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var action = await db.DirectorActions.Include(item => item.Proposal).Include(item => item.Results).FirstOrDefaultAsync(item => item.Id == actionId, cancellationToken);
        if (action is null || !await access.IsMemberAsync(userId, action.WorkspaceId, cancellationToken)) return null;
        if (action.Status != DirectorActionStatuses.Ready) throw new DirectorActionNotApprovedException();
        var executor = executors.FirstOrDefault(item => string.Equals(item.ActionType, action.ActionType, StringComparison.OrdinalIgnoreCase));
        if (executor is null) throw new DirectorActionExecutionException("DIRECTOR_ACTION_UNSUPPORTED", "This Director action is not available.");
        action.Status = DirectorActionStatuses.Running;
        action.StartedAt = DateTime.UtcNow;
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = action.WorkspaceId, DirectorActionId = action.Id, DirectorProposalId = action.DirectorProposalId, EventType = DirectorHistoryEventTypes.ActionStarted, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);

        DirectorActionExecution execution;
        try { execution = await executor.ExecuteAsync(action, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { execution = new(false, "DIRECTOR_ACTION_FAILED", "The Director action could not be completed.", null); }
        var now = DateTime.UtcNow;
        action.Status = execution.Succeeded ? DirectorActionStatuses.Succeeded : DirectorActionStatuses.Failed;
        action.FailureCode = execution.FailureCode;
        action.CompletedAt = now;
        var result = new DirectorActionResult { Id = Guid.NewGuid(), DirectorActionId = action.Id, Status = action.Status, SafeMessage = execution.SafeMessage, ResultJson = execution.ResultJson, CreatedAt = now };
        db.DirectorActionResults.Add(result);
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = action.WorkspaceId, DirectorActionId = action.Id, DirectorProposalId = action.DirectorProposalId, EventType = execution.Succeeded ? DirectorHistoryEventTypes.ActionSucceeded : DirectorHistoryEventTypes.ActionFailed, SafeDetailsJson = JsonSerializer.Serialize(new { execution.FailureCode }), CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return new DirectorActionExecutionResponse(ToDto(action), ToDto(result));
    }

    private async Task<DirectorProposalDto?> SetProposalStatusAsync(Guid userId, Guid proposalId, bool approve, CancellationToken cancellationToken)
    {
        var proposal = await db.DirectorProposals.Include(item => item.Actions).Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == proposalId, cancellationToken);
        if (proposal is null || !await access.IsMemberAsync(userId, proposal.WorkspaceId, cancellationToken)) return null;
        if (proposal.Status != DirectorProposalStatuses.PendingApproval) throw new DirectorValidationException("This proposal is no longer awaiting approval.");
        var now = DateTime.UtcNow;
        proposal.Status = approve ? DirectorProposalStatuses.Approved : DirectorProposalStatuses.Rejected;
        proposal.ApprovedAt = approve ? now : null;
        proposal.RejectedAt = approve ? null : now;
        foreach (var action in proposal.Actions) { action.Status = approve ? DirectorActionStatuses.Ready : DirectorActionStatuses.Cancelled; action.ApprovedAt = approve ? now : null; }
        db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = proposal.WorkspaceId, DirectorProposalId = proposal.Id, EventType = approve ? DirectorHistoryEventTypes.ProposalApproved : DirectorHistoryEventTypes.ProposalRejected, CreatedAt = now });
        if (approve) foreach (var action in proposal.Actions) db.DirectorHistoryEvents.Add(new DirectorHistoryEvent { Id = Guid.NewGuid(), WorkspaceId = proposal.WorkspaceId, DirectorProposalId = proposal.Id, DirectorActionId = action.Id, EventType = DirectorHistoryEventTypes.ActionReady, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(proposal);
    }

    private async Task<DirectorProjectContext> GetOrCreateContextAsync(MovieProject movie, DirectorContextAssemblyResult assembled, CancellationToken cancellationToken)
    {
        var target = assembled.Context.Target ?? new DirectorContextTargetDto(DirectorContextTargetTypes.Project, movie.Id, null, null, null, null, null);
        var existing = await db.DirectorProjectContexts.FirstOrDefaultAsync(item => item.MovieProjectId == movie.Id && item.WorkspaceId == movie.WorkspaceId && item.TargetType == target.Type && item.TargetId == target.Id, cancellationToken);
        if (existing is null)
        {
            existing = new DirectorProjectContext { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, MovieProjectId = movie.Id, TargetType = target.Type, TargetId = target.Id, SnapshotJson = assembled.SnapshotJson, SnapshotHash = assembled.SnapshotHash, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.DirectorProjectContexts.Add(existing);
        }
        else if (!string.Equals(existing.SnapshotHash, assembled.SnapshotHash, StringComparison.Ordinal))
        {
            existing.ContextVersion++;
            existing.SnapshotJson = assembled.SnapshotJson;
            existing.SnapshotHash = assembled.SnapshotHash;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        return existing;
    }

    private IQueryable<DirectorProposal> QueryProposal() => db.DirectorProposals.AsNoTracking().Include(item => item.Actions).ThenInclude(item => item.Results).Include(item => item.MovieProject);

    private static DirectorProposalDto ToDto(DirectorProposal proposal, IReadOnlyList<string>? rationale = null, IReadOnlyList<DirectorPlanItemDto>? plan = null) =>
        new(proposal.Id, proposal.MovieProjectId, proposal.Status, proposal.Title, proposal.Summary, rationale ?? ParseRationale(proposal.RationaleJson), plan ?? [], proposal.Actions.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(), proposal.CreatedAt, proposal.ApprovedAt);
    private static DirectorActionDto ToDto(DirectorAction action) => new(action.Id, action.DirectorProposalId, action.ActionType, action.Status, action.ApprovalRequired, action.FailureCode, action.CreatedAt, action.ApprovedAt, action.StartedAt, action.CompletedAt, action.Results.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray());
    private static DirectorActionResultDto ToDto(DirectorActionResult result) => new(result.Id, result.Status, result.SafeMessage, result.ResultJson, result.CreatedAt);
    private static IReadOnlyList<string> ParseRationale(string json) { try { return JsonSerializer.Deserialize<string[]>(json) ?? []; } catch (JsonException) { return []; } }
}

public sealed class MovieDirectorActionExecutor(IMovieStudioService movies, IMovieVideoProvider provider) : IDirectorActionExecutor
{
    public string ActionType => DirectorActionTypes.GenerateShot;
    public async Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, CancellationToken cancellationToken = default)
    {
        if (!provider.IsAvailable) return new(false, GenerationJobErrorCodes.MovieProviderUnavailable, "The movie generation capability is not available.", null);
        DirectorGenerateShotPayload? payload;
        try { payload = JsonSerializer.Deserialize<DirectorGenerateShotPayload>(action.PayloadJson, DirectorJson.Options); }
        catch (JsonException) { payload = null; }
        if (payload is null) return new(false, "DIRECTOR_ACTION_INVALID", "The Director action payload is invalid.", null);
        var result = await movies.GenerateShotAsync(action.Proposal.CreatedByUserId, payload.ShotId, new MovieStudioGenerationRequest($"Director: {payload.QualityLevel}", payload.EstimatedCostUsd), cancellationToken, action.IdempotencyKey ?? $"director:{action.Id:N}");
        return result is null
            ? new(false, "DIRECTOR_SHOT_NOT_FOUND", "The Director shot could not be found.", null)
            : new(true, null, "The Director queued the shot for generation.", JsonSerializer.Serialize(new { result.Job.Id, result.ClipId }));
    }
}
