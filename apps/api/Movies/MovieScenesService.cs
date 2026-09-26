using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieScenesService
{
    Task<MovieScenesWorkspaceDto?> GetWorkspaceAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieScenesBreakdownDto?> BreakDownApprovedScreenplayAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
}

public sealed class MovieScenesService(TaslimDbContext db, MovieCollaborationAccess access) : IMovieScenesService
{
    public async Task<MovieScenesWorkspaceDto?> GetWorkspaceAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking()
            .Include(item => item.Acts).ThenInclude(item => item.Sequences).ThenInclude(item => item.Scenes).ThenInclude(item => item.Shots)
            .SingleOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await access.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var story = await ApprovedStoryAsync(movieProjectId, cancellationToken);
        var world = await LoadWorldAsync(movieProjectId, cancellationToken);
        return BuildWorkspace(movie, story, world);
    }

    public async Task<MovieScenesBreakdownDto?> BreakDownApprovedScreenplayAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var movie = await db.MovieProjects.SingleOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null) return null;

        var story = await ApprovedStoryAsync(movieProjectId, cancellationToken);
        if (story is null || story.Revision is null) throw new MovieScenesWorkflowException("MOVIE_SCREENPLAY_NOT_APPROVED", "Approve a screenplay revision before breaking it down into production scenes.");

        var existingSceneIds = await db.MovieScenes.Where(item => item.MovieProjectId == movieProjectId).Select(item => item.Id).ToHashSetAsync(cancellationToken);
        var trackedScreenplayScenes = await db.MovieScreenplayScenes
            .Where(item => item.MovieStoryRevisionId == story.Revision.Id)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var created = 0;
        var alreadyLinked = 0;
        var now = DateTime.UtcNow;
        var acts = await db.MovieActs.Where(item => item.MovieProjectId == movieProjectId).ToListAsync(cancellationToken);
        var sequences = await db.MovieSequences.Where(item => acts.Select(act => act.Id).Contains(item.MovieActId)).ToListAsync(cancellationToken);
        var scenes = await db.MovieScenes.Where(item => item.MovieProjectId == movieProjectId).ToListAsync(cancellationToken);

        foreach (var screenplayScene in story.Revision.Scenes.OrderBy(item => item.Ordinal))
        {
            if (screenplayScene.MovieSceneId.HasValue && existingSceneIds.Contains(screenplayScene.MovieSceneId.Value))
            {
                alreadyLinked++;
                continue;
            }
            if (screenplayScene.MovieSceneId.HasValue)
                throw new MovieScenesWorkflowException("MOVIE_SCREENPLAY_LINK_INVALID", "The approved screenplay contains a production scene link outside this movie project.");

            var actPosition = screenplayScene.ActNumber.GetValueOrDefault(1);
            var sequencePosition = screenplayScene.SequenceNumber.GetValueOrDefault(1);
            var act = acts.SingleOrDefault(item => item.Sequence == actPosition);
            if (act is null)
            {
                act = new MovieAct { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, Sequence = actPosition, Title = $"Act {actPosition}", Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now };
                acts.Add(act);
                db.MovieActs.Add(act);
            }

            var sequence = sequences.SingleOrDefault(item => item.MovieActId == act.Id && item.Sequence == sequencePosition);
            if (sequence is null)
            {
                sequence = new MovieSequence { Id = Guid.NewGuid(), MovieActId = act.Id, Sequence = sequencePosition, Title = $"Sequence {sequencePosition}", Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now };
                sequences.Add(sequence);
                db.MovieSequences.Add(sequence);
            }

            var title = string.IsNullOrWhiteSpace(screenplayScene.Slugline) ? $"Scene {screenplayScene.SceneIdentifier}" : screenplayScene.Slugline.Trim();
            var summary = string.IsNullOrWhiteSpace(screenplayScene.Synopsis) ? screenplayScene.Slugline.Trim() : screenplayScene.Synopsis.Trim();
            var scene = new MovieScene
            {
                Id = Guid.NewGuid(), MovieProjectId = movieProjectId, MovieSequenceId = sequence.Id,
                Sequence = scenes.Where(item => item.MovieSequenceId == sequence.Id).Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 1,
                Title = title[..Math.Min(title.Length, 160)], Summary = summary[..Math.Min(summary.Length, 8_000)],
                Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now,
            };
            scenes.Add(scene);
            db.MovieScenes.Add(scene);
            trackedScreenplayScenes[screenplayScene.Id].MovieSceneId = scene.Id;
            existingSceneIds.Add(scene.Id);
            created++;
        }

        await db.SaveChangesAsync(cancellationToken);
        var workspace = await GetWorkspaceAsync(userId, movieProjectId, cancellationToken) ?? throw new InvalidOperationException("Movie scenes workspace disappeared after breakdown.");
        return new MovieScenesBreakdownDto(movieProjectId, story.Revision.Id, story.Revision.RevisionNumber, created, alreadyLinked, workspace);
    }

    private async Task<ApprovedStorySnapshot?> ApprovedStoryAsync(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var story = await db.MovieStories.AsNoTracking()
            .Include(item => item.Revisions).ThenInclude(item => item.Scenes).ThenInclude(item => item.Elements)
            .SingleOrDefaultAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        if (story is null || !story.ApprovedRevisionId.HasValue) return story is null ? null : new ApprovedStorySnapshot(story, null);
        var revision = story.Revisions.SingleOrDefault(item => item.Id == story.ApprovedRevisionId.Value && item.Status == MovieStoryRevisionStatuses.Approved);
        return new ApprovedStorySnapshot(story, revision);
    }

    private async Task<WorldSnapshot> LoadWorldAsync(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var usages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).ToListAsync(cancellationToken);
        var locations = await db.MovieLocations.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).ToDictionaryAsync(item => item.Id, cancellationToken);
        var sets = await db.MovieSets.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).ToDictionaryAsync(item => item.Id, cancellationToken);
        var facts = await db.MovieContinuityFacts.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && item.ScopeType == MovieWorldScopes.Scene).ToListAsync(cancellationToken);
        return new WorldSnapshot(usages, locations, sets, facts);
    }

    private static MovieScenesWorkspaceDto BuildWorkspace(MovieProject movie, ApprovedStorySnapshot? story, WorldSnapshot world)
    {
        var approvedRevision = story?.Revision;
        var screenplayByProductionScene = approvedRevision?.Scenes.Where(item => item.MovieSceneId.HasValue).ToDictionary(item => item.MovieSceneId!.Value) ?? [];
        var acts = movie.Acts.OrderBy(item => item.Sequence).Select(act => new MovieScenesActDto(
            act.Id, act.Sequence, act.Title, act.Summary, act.Status,
            act.Sequences.OrderBy(item => item.Sequence).Select(sequence => new MovieScenesSequenceDto(
                sequence.Id, sequence.Sequence, sequence.Title, sequence.Summary, sequence.Status,
                sequence.Scenes.OrderBy(item => item.Sequence).Select(scene => ToScene(scene, act, sequence, screenplayByProductionScene.GetValueOrDefault(scene.Id), approvedRevision, world)).ToArray())).ToArray())).ToArray();
        var sceneCount = acts.Sum(act => act.Sequences.Sum(sequence => sequence.Scenes.Count));
        var linkedCount = acts.Sum(act => act.Sequences.Sum(sequence => sequence.Scenes.Count(scene => scene.ScreenplaySceneId.HasValue)));
        var shotCount = acts.Sum(act => act.Sequences.Sum(sequence => sequence.Scenes.Sum(scene => scene.ShotCount)));
        return new MovieScenesWorkspaceDto(movie.Id, movie.Title, movie.ProductionStatus, story?.Story.ApprovalState ?? "NotAvailable", story?.Story.ApprovedRevisionId, approvedRevision?.RevisionNumber, acts, sceneCount, linkedCount, shotCount);
    }

    private static MovieSceneWorkspaceDto ToScene(MovieScene scene, MovieAct act, MovieSequence sequence, MovieScreenplayScene? screenplay, MovieStoryRevision? approvedRevision, WorldSnapshot world)
    {
        var usages = world.Usages.Where(item => item.MovieSceneId == scene.Id).ToArray();
        var locations = usages.Where(item => item.EntityType.Equals(MovieWorldEntityTypes.Location, StringComparison.OrdinalIgnoreCase) && world.Locations.TryGetValue(item.EntityId, out _)).Select(item => new MovieSceneWorldReferenceDto(item.EntityId, world.Locations[item.EntityId].Name, item.Role)).DistinctBy(item => item.Id).ToArray();
        var sets = usages.Where(item => item.EntityType.Equals(MovieWorldEntityTypes.Set, StringComparison.OrdinalIgnoreCase) && world.Sets.TryGetValue(item.EntityId, out _)).Select(item => new MovieSceneWorldReferenceDto(item.EntityId, world.Sets[item.EntityId].Name, item.Role)).DistinctBy(item => item.Id).ToArray();
        var characters = screenplay?.Elements.Where(item => !string.IsNullOrWhiteSpace(item.CharacterName)).Select(item => item.CharacterName!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToArray() ?? [];
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(scene.ContinuityNotes)) warnings.Add(scene.ContinuityNotes.Trim());
        warnings.AddRange(world.Facts.Where(item => item.ScopeId == scene.Id).Select(item => $"{item.FactKey}: {item.FactValue}"));
        var slug = screenplay?.Slugline ?? scene.Title;
        var description = string.IsNullOrWhiteSpace(scene.Summary) ? screenplay?.Synopsis ?? string.Empty : scene.Summary;
        return new MovieSceneWorkspaceDto(
            scene.Id, scene.Sequence, scene.Title, slug, description, screenplay?.Synopsis, scene.DurationSeconds,
            scene.Status, screenplay is null ? "Unlinked" : "Linked", $"Act {act.Sequence} · Sequence {sequence.Sequence} · Scene {scene.Sequence}",
            act.Sequence, sequence.Sequence, scene.MovieSequenceId, screenplay?.Id, screenplay?.SceneIdentifier, screenplay?.Slugline,
            screenplay?.Synopsis, approvedRevision?.RevisionNumber, characters, locations, sets, warnings.Distinct().ToArray(), scene.Shots.Count,
            scene.ArchivedAt, scene.CreatedAt, scene.UpdatedAt);
    }

    private sealed record ApprovedStorySnapshot(MovieStory Story, MovieStoryRevision? Revision);
    private sealed record WorldSnapshot(IReadOnlyList<MovieWorldUsage> Usages, IReadOnlyDictionary<Guid, MovieLocation> Locations, IReadOnlyDictionary<Guid, MovieSet> Sets, IReadOnlyList<MovieContinuityFact> Facts);
}

public sealed class MovieScenesWorkflowException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
