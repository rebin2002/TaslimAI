using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed class MovieDirectorScenePlanActionExecutor(
    TaslimDbContext db,
    MovieDirectorContextAssembler assembler,
    IMovieDirectorScenePlanValidator validator) : IDirectorActionExecutor
{
    public string ActionType => DirectorActionTypes.ScenePlanning;

    public async Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, Guid executingUserId, CancellationToken cancellationToken = default)
    {
        DirectorScenePlanActionPayload? payload;
        try { payload = JsonSerializer.Deserialize<DirectorScenePlanActionPayload>(action.PayloadJson, DirectorJson.Options); }
        catch (JsonException) { payload = null; }
        if (payload is null || !DirectorScenePlanActionTypes.IsSupported(payload.Action))
            return new(false, "DIRECTOR_SCENE_PLAN_ACTION_INVALID", "The scene-plan proposal payload is invalid.", null);

        var assembled = await assembler.AssembleStoryAsync(action.MovieProjectId, null, null, cancellationToken);
        if (assembled is null)
            return new(false, "DIRECTOR_SCENE_PLAN_CONTEXT_UNAVAILABLE", "The current locked Story context is unavailable. Create a new proposal after checking the Movie Guide.", null);

        if (!string.Equals(payload.ContextSnapshotHash, assembled.Value.SnapshotHash, StringComparison.Ordinal))
            return new(false, "DIRECTOR_SCENE_PLAN_STALE", "The Story, Guide, Cast, World, or continuity context changed after this proposal was created. Create a fresh scene plan.", null);

        var validation = validator.Validate(payload, assembled.Value.Context, assembled.Value.SnapshotHash);
        if (!validation.IsValid || validation.Output is null)
            return new(false, "DIRECTOR_SCENE_PLAN_INVALID", "The scene-plan proposal no longer passes the current runtime and grounding checks. Create a fresh scene plan.", null, validation.ReasonCodes);
        payload = validation.Output;

        var screenplayScenes = (assembled.Value.Context.CurrentRevision?.Scenes ?? [])
            .Concat(assembled.Value.Context.ApprovedRevision?.Scenes ?? [])
            .GroupBy(item => item.Id)
            .Select(item => item.First())
            .ToDictionary(item => item.Id);
        var existingScenes = await db.MovieScenes.Where(item => item.MovieProjectId == action.MovieProjectId).ToListAsync(cancellationToken);
        var existingById = existingScenes.ToDictionary(item => item.Id);
        var sceneIds = new List<Guid>();

        foreach (var proposal in payload.Scenes.OrderBy(item => item.Sequence))
        {
            MovieScene? scene = null;
            if (proposal.ExistingMovieSceneId is Guid existingMovieSceneId)
                existingById.TryGetValue(existingMovieSceneId, out scene);
            if (scene is null && proposal.SourceScreenplaySceneId is Guid sourceId && screenplayScenes.TryGetValue(sourceId, out var screenplay))
            {
                if (screenplay.MovieSceneId is Guid linkedId)
                    existingById.TryGetValue(linkedId, out scene);
            }

            if (scene is null)
            {
                scene = new MovieScene
                {
                    Id = Guid.NewGuid(),
                    MovieProjectId = action.MovieProjectId,
                    Sequence = proposal.Sequence,
                    CreatedAt = DateTime.UtcNow,
                };
                db.MovieScenes.Add(scene);
                existingById[scene.Id] = scene;
            }

            scene.Sequence = proposal.Sequence;
            scene.Title = proposal.Title.Trim();
            scene.Summary = RenderSummary(proposal);
            scene.DurationSeconds = proposal.EstimatedDurationSeconds;
            scene.ContinuityNotes = RenderContinuity(proposal);
            scene.UpdatedAt = DateTime.UtcNow;
            sceneIds.Add(scene.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        var result = new DirectorScenePlanApplyResult(true, payload.TargetDurationSeconds, payload.TotalEstimatedDurationSeconds, sceneIds, "The approved Director scene plan was applied to production scenes.");
        return new(true, null, result.SafeMessage, JsonSerializer.Serialize(result, DirectorJson.Options));
    }

    private static string RenderSummary(DirectorSceneProposal proposal)
    {
        var purpose = proposal.NarrativePurpose.Trim();
        var beat = proposal.StoryBeat.Trim();
        return string.IsNullOrWhiteSpace(beat) ? purpose : $"{purpose} Story beat: {beat}";
    }

    private static string RenderContinuity(DirectorSceneProposal proposal)
    {
        var values = proposal.ContinuityRequirements.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList();
        if (!string.IsNullOrWhiteSpace(proposal.TransitionRelationship)) values.Add($"Transition: {proposal.TransitionRelationship.Trim()}");
        if (!string.IsNullOrWhiteSpace(proposal.LocationEnvironment)) values.Add($"Environment: {proposal.LocationEnvironment.Trim()}");
        if (!string.IsNullOrWhiteSpace(proposal.TimeOfDay)) values.Add($"Time: {proposal.TimeOfDay.Trim()}");
        return string.Join(" ", values);
    }
}
