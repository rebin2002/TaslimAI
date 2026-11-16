using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieDurationBudgetStatuses
{
    public const string Valid = "Valid";
    public const string UnderBudget = "UnderBudget";
    public const string OverBudget = "OverBudget";
    public const string Incomplete = "Incomplete";
    public const string Invalid = "Invalid";
}

public static class MovieDurationBudgetDiagnosticSeverities
{
    public const string Warning = "Warning";
    public const string Error = "Error";
}

/// <summary>
/// Bounded, provider-neutral runtime policy. Durations are integer seconds and are
/// never rounded, scaled, or replaced by the validator.
/// </summary>
public static class MovieDurationBudgetPolicy
{
    public const int MaxMovieDurationSeconds = 24 * 60 * 60;
    public const int MaxSceneDurationSeconds = 24 * 60 * 60;
    public const int MaxShotDurationSeconds = 60 * 60;
    public const decimal ToleranceFraction = 0.02m;
    public const int MinimumToleranceSeconds = 1;
    public const int MaximumToleranceSeconds = 10;

    public static int ToleranceSecondsFor(long durationSeconds)
    {
        if (durationSeconds <= 0) return MinimumToleranceSeconds;
        var roundedUp = (long)decimal.Ceiling(durationSeconds * ToleranceFraction);
        return (int)Math.Clamp(roundedUp, MinimumToleranceSeconds, MaximumToleranceSeconds);
    }

    public static long GrossDeviationSecondsFor(long targetDurationSeconds) =>
        Math.Max(10L, targetDurationSeconds / 2L);

    public static string? ValidateSceneDuration(int? durationSeconds) =>
        Validate(durationSeconds, MaxSceneDurationSeconds, "Scene");

    public static string? ValidateShotDuration(int? durationSeconds) =>
        Validate(durationSeconds, MaxShotDurationSeconds, "Shot");

    private static string? Validate(int? durationSeconds, int maximum, string label)
    {
        if (!durationSeconds.HasValue) return null;
        if (durationSeconds <= 0) return $"{label} duration must be greater than zero.";
        return durationSeconds > maximum ? $"{label} duration cannot exceed {maximum} seconds." : null;
    }
}

public sealed record MovieDurationBudgetInput(
    int TargetDurationSeconds,
    IReadOnlyList<MovieDurationSceneInput> Scenes);

public sealed record MovieDurationSceneInput(
    Guid Id,
    string? Title,
    int? DurationSeconds,
    IReadOnlyList<MovieDurationShotInput> Shots,
    bool Archived = false);

public sealed record MovieDurationShotInput(
    Guid Id,
    int? DurationSeconds,
    bool Archived = false);

public sealed record MovieDurationBudgetDiagnostic(
    string Code,
    string Severity,
    string Path,
    string Message,
    long? ExpectedSeconds = null,
    long? ActualSeconds = null);

public sealed record MovieDurationBudgetSceneResult(
    Guid SceneId,
    string? SceneTitle,
    int? DeclaredDurationSeconds,
    long TotalShotDurationSeconds,
    long? DeltaSeconds,
    int ToleranceSeconds,
    string Status,
    IReadOnlyList<MovieDurationBudgetDiagnostic> Diagnostics);

public sealed record MovieDurationBudgetResult(
    string Status,
    bool IsValid,
    bool IsComplete,
    int TargetDurationSeconds,
    long TotalSceneDurationSeconds,
    long TotalShotDurationSeconds,
    long? SceneDeltaSeconds,
    int MovieToleranceSeconds,
    IReadOnlyList<MovieDurationBudgetSceneResult> Scenes,
    IReadOnlyList<MovieDurationBudgetDiagnostic> Diagnostics);

public static class MovieDurationBudgetValidator
{
    public static MovieDurationBudgetResult Validate(MovieDurationBudgetInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var diagnostics = new List<MovieDurationBudgetDiagnostic>();
        var sceneResults = new List<MovieDurationBudgetSceneResult>();
        var target = input.TargetDurationSeconds;
        var targetIsValid = ValidateTarget(target, diagnostics);
        var totalSceneDuration = 0L;
        var totalShotDuration = 0L;
        var hasIncompleteData = false;
        var hasInvalidData = !targetIsValid;
        var hasUnderBudgetScene = false;
        var hasOverBudgetScene = false;
        var seenSceneIds = new HashSet<Guid>();
        var activeScenes = 0;

        foreach (var (scene, sceneIndex) in (input.Scenes ?? []).Select((item, index) => (item, index)))
        {
            if (scene is null || scene.Archived) continue;
            activeScenes++;
            var sceneDiagnostics = new List<MovieDurationBudgetDiagnostic>();
            var scenePath = $"scenes[{sceneIndex}]";
            var sceneDuration = scene?.DurationSeconds;
            var sceneId = scene?.Id ?? Guid.Empty;
            var sceneTitle = scene?.Title;
            var sceneDurationIsValid = sceneDuration is > 0 and <= MovieDurationBudgetPolicy.MaxSceneDurationSeconds;
            var sceneIncomplete = false;
            var sceneInvalid = false;
            var sceneUnderBudget = false;
            var sceneOverBudget = false;
            var shotTotal = 0L;
            var seenShotIds = new HashSet<Guid>();

            if (scene is null)
            {
                sceneDiagnostics.Add(Error("scene_missing", scenePath, "A scene entry is required."));
                sceneInvalid = true;
                sceneDurationIsValid = false;
            }
            else
            {
                if (!seenSceneIds.Add(scene.Id))
                {
                    sceneDiagnostics.Add(Error("duplicate_scene", scenePath, "Scene identifiers must be unique."));
                    sceneInvalid = true;
                }

                if (!scene.DurationSeconds.HasValue)
                {
                    sceneDiagnostics.Add(Error("scene_duration_missing", $"{scenePath}.durationSeconds", "A scene duration is required to verify the movie budget."));
                    sceneIncomplete = true;
                }
                else if (scene.DurationSeconds <= 0)
                {
                    sceneDiagnostics.Add(Error("scene_duration_non_positive", $"{scenePath}.durationSeconds", "Scene duration must be greater than zero.", 1, scene.DurationSeconds));
                    sceneInvalid = true;
                }
                else if (scene.DurationSeconds > MovieDurationBudgetPolicy.MaxSceneDurationSeconds)
                {
                    sceneDiagnostics.Add(Error("scene_duration_absurd", $"{scenePath}.durationSeconds", $"Scene duration cannot exceed {MovieDurationBudgetPolicy.MaxSceneDurationSeconds} seconds.", MovieDurationBudgetPolicy.MaxSceneDurationSeconds, scene.DurationSeconds));
                    sceneInvalid = true;
                }
                else
                {
                    totalSceneDuration += scene.DurationSeconds.Value;
                }

                var shots = scene.Shots ?? [];
                if (shots.Count == 0)
                {
                    sceneDiagnostics.Add(Error("shots_missing", $"{scenePath}.shots", "At least one active shot is required to verify a scene runtime."));
                    sceneIncomplete = true;
                }

                foreach (var (shot, shotIndex) in shots.Select((item, index) => (item, index)))
                {
                    if (shot is null || shot.Archived) continue;
                    var shotPath = $"{scenePath}.shots[{shotIndex}]";
                    if (shot is null)
                    {
                        sceneDiagnostics.Add(Error("shot_missing", shotPath, "A shot entry is required."));
                        sceneInvalid = true;
                        continue;
                    }
                    if (!seenShotIds.Add(shot.Id))
                    {
                        sceneDiagnostics.Add(Error("duplicate_shot", shotPath, "Shot identifiers must be unique within a scene."));
                        sceneInvalid = true;
                    }
                    if (!shot.DurationSeconds.HasValue)
                    {
                        sceneDiagnostics.Add(Error("shot_duration_missing", $"{shotPath}.durationSeconds", "A shot duration is required to verify the parent scene runtime."));
                        sceneIncomplete = true;
                    }
                    else if (shot.DurationSeconds <= 0)
                    {
                        sceneDiagnostics.Add(Error("shot_duration_non_positive", $"{shotPath}.durationSeconds", "Shot duration must be greater than zero.", 1, shot.DurationSeconds));
                        sceneInvalid = true;
                    }
                    else if (shot.DurationSeconds > MovieDurationBudgetPolicy.MaxShotDurationSeconds)
                    {
                        sceneDiagnostics.Add(Error("shot_duration_absurd", $"{shotPath}.durationSeconds", $"Shot duration cannot exceed {MovieDurationBudgetPolicy.MaxShotDurationSeconds} seconds.", MovieDurationBudgetPolicy.MaxShotDurationSeconds, shot.DurationSeconds));
                        sceneInvalid = true;
                    }
                    else
                    {
                        shotTotal += shot.DurationSeconds.Value;
                    }
                }
            }

            totalShotDuration += shotTotal;
            long? sceneDelta = sceneDurationIsValid ? shotTotal - sceneDuration!.Value : null;
            var sceneTolerance = MovieDurationBudgetPolicy.ToleranceSecondsFor(sceneDuration ?? 0);
            if (sceneDelta.HasValue && !sceneIncomplete && !sceneInvalid)
            {
                if (sceneDelta.Value > sceneTolerance)
                {
                    sceneOverBudget = true;
                    sceneDiagnostics.Add(Warning("shots_over_scene_budget", $"{scenePath}.shots", $"Shot runtime exceeds the parent scene by {sceneDelta.Value} seconds.", sceneDuration, shotTotal));
                }
                else if (sceneDelta.Value < -sceneTolerance)
                {
                    sceneUnderBudget = true;
                    sceneDiagnostics.Add(Warning("shots_under_scene_budget", $"{scenePath}.shots", $"Shot runtime leaves {-sceneDelta.Value} seconds of the parent scene unallocated.", sceneDuration, shotTotal));
                }
            }

            hasIncompleteData |= sceneIncomplete;
            hasInvalidData |= sceneInvalid;
            hasUnderBudgetScene |= sceneUnderBudget;
            hasOverBudgetScene |= sceneOverBudget;
            var sceneStatus = sceneInvalid
                ? MovieDurationBudgetStatuses.Invalid
                : sceneIncomplete
                    ? MovieDurationBudgetStatuses.Incomplete
                    : sceneOverBudget
                        ? MovieDurationBudgetStatuses.OverBudget
                        : sceneUnderBudget
                            ? MovieDurationBudgetStatuses.UnderBudget
                            : MovieDurationBudgetStatuses.Valid;
            sceneResults.Add(new MovieDurationBudgetSceneResult(
                sceneId,
                sceneTitle,
                sceneDuration,
                shotTotal,
                sceneDelta,
                sceneTolerance,
                sceneStatus,
                sceneDiagnostics));
            diagnostics.AddRange(sceneDiagnostics);
        }

        if (activeScenes == 0)
        {
            diagnostics.Add(Error("scenes_missing", "scenes", "At least one active scene is required to verify the movie budget."));
            hasIncompleteData = true;
        }

        var movieTolerance = MovieDurationBudgetPolicy.ToleranceSecondsFor(target);
        var sceneDeltaFromTarget = targetIsValid ? (long?)(totalSceneDuration - target) : null;
        if (sceneDeltaFromTarget.HasValue && !hasIncompleteData && !hasInvalidData)
        {
            if (sceneDeltaFromTarget.Value > movieTolerance)
            {
                diagnostics.Add(Warning("movie_over_budget", "scenes", $"Scene runtime exceeds the movie target by {sceneDeltaFromTarget.Value} seconds.", target, totalSceneDuration));
                if (sceneDeltaFromTarget.Value > MovieDurationBudgetPolicy.GrossDeviationSecondsFor(target))
                {
                    diagnostics.Add(Error("movie_duration_grossly_over_budget", "scenes", "The scene plan is grossly longer than the movie target.", target, totalSceneDuration));
                }
            }
            else if (sceneDeltaFromTarget.Value < -movieTolerance)
            {
                diagnostics.Add(Warning("movie_under_budget", "scenes", $"Scene runtime leaves {-sceneDeltaFromTarget.Value} seconds of the movie target unallocated.", target, totalSceneDuration));
                if (-sceneDeltaFromTarget.Value > MovieDurationBudgetPolicy.GrossDeviationSecondsFor(target))
                {
                    diagnostics.Add(Error("movie_duration_grossly_under_budget", "scenes", "The scene plan is grossly shorter than the movie target.", target, totalSceneDuration));
                }
            }
        }

        var status = !targetIsValid || hasInvalidData
            ? MovieDurationBudgetStatuses.Invalid
            : hasIncompleteData
                ? MovieDurationBudgetStatuses.Incomplete
                : hasOverBudgetScene || sceneDeltaFromTarget > movieTolerance
                    ? MovieDurationBudgetStatuses.OverBudget
                    : hasUnderBudgetScene || sceneDeltaFromTarget < -movieTolerance
                        ? MovieDurationBudgetStatuses.UnderBudget
                        : MovieDurationBudgetStatuses.Valid;
        return new MovieDurationBudgetResult(
            status,
            status == MovieDurationBudgetStatuses.Valid,
            !hasIncompleteData && !hasInvalidData,
            target,
            totalSceneDuration,
            totalShotDuration,
            sceneDeltaFromTarget,
            movieTolerance,
            sceneResults,
            diagnostics);
    }

    private static bool ValidateTarget(int target, ICollection<MovieDurationBudgetDiagnostic> diagnostics)
    {
        if (target <= 0)
        {
            diagnostics.Add(Error("movie_duration_non_positive", "targetDurationSeconds", "Movie target duration must be greater than zero.", 1, target));
            return false;
        }
        if (target > MovieDurationBudgetPolicy.MaxMovieDurationSeconds)
        {
            diagnostics.Add(Error("movie_duration_absurd", "targetDurationSeconds", $"Movie target duration cannot exceed {MovieDurationBudgetPolicy.MaxMovieDurationSeconds} seconds.", MovieDurationBudgetPolicy.MaxMovieDurationSeconds, target));
            return false;
        }
        return true;
    }

    private static MovieDurationBudgetDiagnostic Warning(string code, string path, string message, long? expected = null, long? actual = null) =>
        new(code, MovieDurationBudgetDiagnosticSeverities.Warning, path, message, expected, actual);

    private static MovieDurationBudgetDiagnostic Error(string code, string path, string message, long? expected = null, long? actual = null) =>
        new(code, MovieDurationBudgetDiagnosticSeverities.Error, path, message, expected, actual);
}

public interface IMovieDurationBudgetService
{
    Task<MovieDurationBudgetResult?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
}

public sealed class MovieDurationBudgetService(TaslimDbContext db, MovieCollaborationAccess access) : IMovieDurationBudgetService
{
    public async Task<MovieDurationBudgetResult?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Scenes).ThenInclude(item => item.Shots)
            .SingleOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await access.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var input = new MovieDurationBudgetInput(
            movie.DurationSeconds,
            movie.Scenes.OrderBy(item => item.Sequence).Select(scene => new MovieDurationSceneInput(
                scene.Id,
                scene.Title,
                scene.DurationSeconds,
                scene.Shots.OrderBy(item => item.Sequence).Select(shot => new MovieDurationShotInput(
                    shot.Id,
                    shot.DurationSeconds,
                    shot.ArchivedAt.HasValue || string.Equals(shot.Status, MovieShotStatuses.Archived, StringComparison.OrdinalIgnoreCase))).ToArray(),
                scene.ArchivedAt.HasValue || string.Equals(scene.Status, MovieHierarchyStatuses.Archived, StringComparison.OrdinalIgnoreCase))).ToArray());
        return MovieDurationBudgetValidator.Validate(input);
    }
}
