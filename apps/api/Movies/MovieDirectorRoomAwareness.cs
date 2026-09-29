namespace Taslim.Api.Movies;

public static class DirectorRoomAwareness
{
    public static string NormalizeRoom(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "overview" => DirectorRoomTypes.Overview,
        "story" => DirectorRoomTypes.Story,
        "cast" => DirectorRoomTypes.Cast,
        "world" => DirectorRoomTypes.World,
        "scene" or "scenes" => DirectorRoomTypes.Scene,
        "shot" => DirectorRoomTypes.Shot,
        "storyboard" => DirectorRoomTypes.Storyboard,
        "production" => DirectorRoomTypes.Production,
        _ => DirectorRoomTypes.Story,
    };

    public static DirectorRoomContext Build(
        string? requestedRoom,
        bool guideLocked,
        Guid? selectedSceneId,
        Guid? selectedShotId,
        DirectorContextTargetDto? target,
        IReadOnlyList<DirectorSceneContext> scenes,
        DirectorProductionContext? production)
    {
        var room = NormalizeRoom(requestedRoom);
        var hasScene = selectedSceneId.HasValue;
        var hasShot = selectedShotId.HasValue;
        var shot = hasShot ? scenes.SelectMany(item => item.Shots).FirstOrDefault(item => item.Id == selectedShotId) : null;
        var shotPlanReady = shot is not null && !string.IsNullOrWhiteSpace(shot.Description);
        var storyboardApproved = string.Equals(production?.Stage, MovieProductionStages.ApprovedStoryboard, StringComparison.Ordinal)
            && string.Equals(production?.Status, MovieProductionVersionStatuses.Approved, StringComparison.Ordinal);

        var prerequisites = new List<DirectorPrerequisiteContext>
        {
            new("guide_locked", "Movie Guide locked", guideLocked, guideLocked ? "Authoritative guide rules are available." : "Lock the current Movie Guide first."),
            new("scene_selected", "Scene selected", hasScene, hasScene ? "The Director is scoped to the selected scene." : "Select a scene from the production map."),
            new("shot_selected", "Shot selected", hasShot, hasShot ? "The Director can target this shot." : "Select a shot when the room action is shot-specific."),
            new("shot_plan_ready", "Shot plan ready", shotPlanReady, shotPlanReady ? "The selected shot has a usable plan." : "Add the missing shot-plan detail before asking for shot planning."),
            new("storyboard_approved", "Storyboard approved", storyboardApproved, storyboardApproved ? "An approved storyboard version is available." : "Approve a storyboard candidate before advancing production."),
        };

        IReadOnlyList<string> validActions = room switch
        {
            DirectorRoomTypes.Cast => guideLocked ? [DirectorActionTypes.StoryAssistance] : [],
            DirectorRoomTypes.Scene => guideLocked && hasScene
                ? (hasShot && shotPlanReady
                    ? new[] { DirectorActionTypes.ScenePlanning, DirectorActionTypes.ShotPlanning }
                    : new[] { DirectorActionTypes.ScenePlanning })
                : [],
            DirectorRoomTypes.Storyboard => guideLocked && (hasScene || hasShot) ? [DirectorActionTypes.StoryboardPreparation] : [],
            DirectorRoomTypes.Production => guideLocked && (hasScene || hasShot) ? [DirectorActionTypes.ProductionReadiness] : [],
            DirectorRoomTypes.Overview or DirectorRoomTypes.Story => guideLocked ? [DirectorActionTypes.ProjectReadiness] : [],
            DirectorRoomTypes.World or DirectorRoomTypes.Shot => guideLocked && hasShot && shotPlanReady ? [DirectorActionTypes.ShotPlanning] : [],
            _ => [],
        };

        return new DirectorRoomContext(room, selectedSceneId, selectedShotId, prerequisites, validActions, target);
    }
}

public static class DirectorRoomTypes
{
    public const string Overview = "Overview";
    public const string Story = "Story";
    public const string Cast = "Cast";
    public const string World = "World";
    public const string Scene = "Scene";
    public const string Scenes = Scene;
    public const string Shot = "Shot";
    public const string Storyboard = "Storyboard";
    public const string Production = "Production";
}

public sealed class MovieDirectorPlanningActionExecutor(string actionType) : IDirectorActionExecutor
{
    public string ActionType => actionType;

    public Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, CancellationToken cancellationToken = default)
    {
        DirectorRoomActionPayload? payload;
        try { payload = System.Text.Json.JsonSerializer.Deserialize<DirectorRoomActionPayload>(action.PayloadJson, DirectorJson.Options); }
        catch (System.Text.Json.JsonException) { payload = null; }

        if (payload is null || !DirectorActionTypes.RoomPlanning.Contains(payload.ActionType) || !string.Equals(payload.ActionType, actionType, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new DirectorActionExecution(false, "DIRECTOR_ROOM_ACTION_INVALID", "The room planning action payload is invalid.", null));

        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            payload.Room,
            payload.ActionType,
            payload.SceneId,
            payload.ShotId,
            providerCalled = false,
            recordsChanged = false,
        }, DirectorJson.Options);
        return Task.FromResult(new DirectorActionExecution(true, null, "The Director recorded a room-scoped planning result; no media provider was called.", result));
    }
}
