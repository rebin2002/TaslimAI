namespace Taslim.Api.Movies;

/// <summary>
/// Explicit scene durations from an already authorized movie project. Scene
/// identity is kept only for planning, not for queuing or provider submission.
/// </summary>
public sealed record MoviePlannedSceneDuration(
    Guid SceneId,
    int SceneSequence,
    int DurationSeconds);

/// <summary>
/// A read-only slot within one scene. TimelineStartSeconds is an absolute,
/// zero-based offset; SceneOffsetSeconds is relative to its source scene.
/// The slot is not a shot, an approved reference or a render reservation.
/// </summary>
public sealed record MoviePlannedSceneClipSegment(
    Guid SceneId,
    int SceneSequence,
    int ClipSequence,
    int TimelineStartSeconds,
    int SceneOffsetSeconds,
    int DurationSeconds);

/// <summary>
/// Produces a duration-only scene-boundary-preserving clip preview. It rejects
/// incomplete/contradictory scene plans rather than inventing missing runtime,
/// borrowing seconds from adjacent scenes or silently crossing a story boundary.
/// There are no persistence, provider, pricing or generation side effects.
/// </summary>
public static class MovieSceneClipSegmentationPlanner
{
    public static bool TryPlan(
        int filmDurationSeconds,
        IReadOnlyList<MoviePlannedSceneDuration>? scenes,
        MovieClipDurationRange? providerRange,
        out IReadOnlyList<MoviePlannedSceneClipSegment> segments)
    {
        segments = Array.Empty<MoviePlannedSceneClipSegment>();
        if (filmDurationSeconds is < 1 or > MovieClipSegmentationPlanner.MaximumFilmDurationSeconds
            || scenes is not { Count: > 0 }
            || scenes.Count > filmDurationSeconds
            || providerRange is null)
            return false;

        // Each scene must have an explicit duration, unique identity and an
        // unambiguous order. Never infer scene lengths from the project total.
        var sceneIds = new HashSet<Guid>();
        var sequences = new HashSet<int>();
        long assignedSeconds = 0;
        foreach (var scene in scenes)
        {
            if (scene.SceneId == Guid.Empty
                || scene.SceneSequence < 1
                || scene.DurationSeconds < 1
                || !sceneIds.Add(scene.SceneId)
                || !sequences.Add(scene.SceneSequence))
                return false;

            assignedSeconds += scene.DurationSeconds;
            if (assignedSeconds > filmDurationSeconds)
                return false;
        }
        if (assignedSeconds != filmDurationSeconds)
            return false;

        var planned = new List<MoviePlannedSceneClipSegment>();
        var timelineStart = 0;
        foreach (var scene in scenes.OrderBy(item => item.SceneSequence))
        {
            if (!MovieClipSegmentationPlanner.TryPlan(scene.DurationSeconds, providerRange, out var sceneClips))
                return false; // Return no partial plan when any scene is unrepresentable.

            foreach (var clip in sceneClips)
                planned.Add(new MoviePlannedSceneClipSegment(
                    scene.SceneId,
                    scene.SceneSequence,
                    planned.Count + 1,
                    timelineStart + clip.StartSeconds,
                    clip.StartSeconds,
                    clip.DurationSeconds));

            timelineStart += scene.DurationSeconds;
        }

        if (timelineStart != filmDurationSeconds)
            return false;

        segments = planned;
        return true;
    }
}
