using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed class MovieDirectorStoryActionExecutor(TaslimDbContext db, IMovieStoryService stories) : IDirectorActionExecutor
{
    public string ActionType => DirectorActionTypes.StoryAssistance;

    public async Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, CancellationToken cancellationToken = default)
    {
        DirectorStoryActionPayload? payload;
        try { payload = JsonSerializer.Deserialize<DirectorStoryActionPayload>(action.PayloadJson, DirectorJson.Options); }
        catch (JsonException) { payload = null; }
        if (payload is null || !DirectorStoryActionTypes.All.Contains(payload.Action))
            return new(false, "DIRECTOR_STORY_ACTION_INVALID", "The Story proposal payload is invalid.", null);

        if (string.Equals(payload.Action, DirectorStoryActionTypes.IdentifyInconsistencies, StringComparison.OrdinalIgnoreCase))
            return new(true, null, "The Director findings were recorded for review; no Story text was changed.", JsonSerializer.Serialize(new DirectorStoryApplyResult(false, null, "Review-only Story findings.", payload.Findings)));

        var story = await db.MovieStories.AsNoTracking()
            .Include(item => item.Revisions).ThenInclude(item => item.Scenes).ThenInclude(item => item.Elements)
            .SingleOrDefaultAsync(item => item.MovieProjectId == action.MovieProjectId, cancellationToken);
        var current = story?.CurrentRevisionId is Guid currentId ? story.Revisions.SingleOrDefault(item => item.Id == currentId) : null;
        if (story?.CurrentRevisionId != payload.BaseRevisionId)
            return new(false, "DIRECTOR_STORY_BASE_CHANGED", "The Story changed after this proposal was created. Create a fresh proposal before applying it.", null);

        var scenes = current?.Scenes.OrderBy(item => item.Ordinal).Select(ToRequest).ToList() ?? [];
        if (payload.ProposedScene is not null) scenes.Add(payload.ProposedScene);
        if (payload.TargetElementId is Guid targetElementId && payload.ReplacementContent is { Length: > 0 })
        {
            var target = scenes.SelectMany(item => item.Elements).FirstOrDefault(item => item is not null && item.Content.Length > 0);
            var original = current?.Scenes.SelectMany(item => item.Elements).FirstOrDefault(item => item.Id == targetElementId);
            if (original is null) return new(false, "DIRECTOR_STORY_PASSAGE_NOT_FOUND", "The selected Story passage is no longer available.", null);
            var targetScene = scenes.FirstOrDefault(item => item.SceneIdentifier.Equals(current!.Scenes.First(scene => scene.Elements.Any(element => element.Id == targetElementId)).SceneIdentifier, StringComparison.OrdinalIgnoreCase));
            var targetRequest = targetScene?.Elements.FirstOrDefault(item => string.Equals(item.Content, original.Content, StringComparison.Ordinal));
            if (targetRequest is null) return new(false, "DIRECTOR_STORY_PASSAGE_NOT_FOUND", "The selected Story passage is no longer available.", null);
            targetRequest.Content = payload.ReplacementContent;
        }

        var request = new MovieStoryRevisionRequest
        {
            Premise = payload.Premise ?? current?.Premise ?? string.Empty,
            Logline = payload.Logline ?? current?.Logline ?? string.Empty,
            Synopsis = payload.Synopsis ?? current?.Synopsis ?? string.Empty,
            Treatment = payload.Treatment ?? current?.Treatment ?? string.Empty,
            Authorship = MovieStoryAuthorship.AiSuggested,
            ParentRevisionId = payload.BaseRevisionId,
            ChangeSummary = $"Director proposal: {payload.Action}",
            Scenes = scenes,
        };
        if (string.IsNullOrWhiteSpace(request.Premise) || string.IsNullOrWhiteSpace(request.Logline) || string.IsNullOrWhiteSpace(request.Synopsis) || string.IsNullOrWhiteSpace(request.Treatment))
            return new(false, DirectorStoryCreativeFailureCodes.Invalid, "The approved Story AI output is incomplete and cannot be applied.", null);
        try
        {
            var result = await stories.CreateRevisionAsync(action.Proposal.CreatedByUserId, action.MovieProjectId, request, cancellationToken);
            var revisionId = result?.CurrentRevision?.Id;
            return result is null
                ? new(false, "DIRECTOR_STORY_APPLY_FAILED", "The Story revision could not be created.", null)
                : new(true, null, "The approved Director proposal was applied as a new editable Story revision.", JsonSerializer.Serialize(new DirectorStoryApplyResult(true, revisionId, "Applied as an editable Story revision.", payload.Findings)));
        }
        catch (MovieStoryValidationException exception)
        {
            return new(false, "DIRECTOR_STORY_APPLY_INVALID", exception.Message, null);
        }
    }

    private static MovieStorySceneRequest ToRequest(MovieScreenplayScene scene) => new()
    {
        SceneIdentifier = scene.SceneIdentifier,
        ActNumber = scene.ActNumber,
        SequenceNumber = scene.SequenceNumber,
        MovieSceneId = scene.MovieSceneId,
        Slugline = scene.Slugline,
        Synopsis = scene.Synopsis,
        Elements = scene.Elements.OrderBy(item => item.Ordinal).Select(element => new MovieScreenplayElementRequest
        {
            ElementType = element.ElementType,
            Content = element.Content,
            CharacterName = element.CharacterName,
            Parenthetical = element.Parenthetical,
        }).ToList(),
    };
}
