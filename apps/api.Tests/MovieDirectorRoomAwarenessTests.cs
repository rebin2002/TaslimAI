using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorRoomAwarenessTests
{
    [Fact]
    public void Scene_room_rejects_foreign_scene_and_mismatched_shot_selection()
    {
        var localSceneId = Guid.NewGuid();
        var localShotId = Guid.NewGuid();
        var foreignSceneId = Guid.NewGuid();
        var context = DirectorRoomAwareness.Build(
            DirectorRoomTypes.Scene,
            guideLocked: true,
            selectedSceneId: foreignSceneId,
            selectedShotId: localShotId,
            target: null,
            scenes:
            [
                new DirectorSceneContext(
                    localSceneId,
                    1,
                    "Local scene",
                    "Local scene summary",
                    10,
                    null,
                    [new DirectorShotContext(localShotId, 1, "A usable local shot", null, null, 5, null, null, null)]),
            ],
            production: null);

        Assert.Null(context.SelectedSceneId);
        Assert.Null(context.SelectedShotId);
        Assert.DoesNotContain(DirectorActionTypes.ScenePlanning, context.ValidActions);
        Assert.DoesNotContain(DirectorActionTypes.ShotPlanning, context.ValidActions);
        Assert.False(context.AvailablePrerequisites.Single(item => item.Key == "scene_selected").Satisfied);
        Assert.False(context.AvailablePrerequisites.Single(item => item.Key == "shot_selected").Satisfied);
        Assert.False(context.AvailablePrerequisites.Single(item => item.Key == "shot_plan_ready").Satisfied);
    }
}
