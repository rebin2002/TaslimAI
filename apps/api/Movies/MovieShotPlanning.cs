using System.Text.Json;

namespace Taslim.Api.Movies;

public static class MovieShotPlanStates
{
    public const string Draft = "Draft";
    public const string ReadyForStoryboard = "ReadyForStoryboard";
    public const string Storyboard = "Storyboard";
    public const string Production = "Production";
    public const string Archived = "Archived";
}

public sealed record MovieShotReadinessCheckDto(string Key, string Label, bool Satisfied, string Detail);

public sealed record MovieShotReadinessDto(
    bool Ready,
    IReadOnlyList<MovieShotReadinessCheckDto> Checks,
    IReadOnlyList<string> Missing,
    string Summary);

public sealed record MovieSceneShotPlanDto(
    Guid SceneId,
    int SceneSequence,
    string SceneTitle,
    string SceneSummary,
    int? SceneDurationSeconds,
    string SceneStatus,
    int ShotCount,
    int ActiveShotCount,
    int ReadyShotCount,
    int TotalDurationSeconds,
    int CoveragePercent,
    IReadOnlyList<MovieShotDto> Shots);

public sealed class MovieStudioShotUpdateRequest
{
    public string Description { get; set; } = string.Empty;
    public string? Purpose { get; set; }
    public string? Subjects { get; set; }
    public string? LocationSet { get; set; }
    public int? DurationSeconds { get; set; }
    public string? ProductionRequirements { get; set; }
    public string? ContinuityReferences { get; set; }
    public string? CameraAndFraming { get; set; }
    public string? CameraMotion { get; set; }
    public string? Narration { get; set; }
    public string? Dialogue { get; set; }
    public string? VisualContinuityNotes { get; set; }
    public CinematographyIntentSelection? Cinematography { get; set; }
    public IReadOnlyList<Guid>? SubjectCharacterIds { get; set; }
    public string? Status { get; set; }
}

public sealed record MovieShotReorderRequest(int Sequence);

public static class MovieShotReadiness
{
    public static MovieShotReadinessDto Evaluate(MovieShot shot)
    {
        var checks = new[]
        {
            Check("purpose", "Shot purpose", shot.Purpose, "State what this shot must communicate."),
            Check("action", "Description / action", shot.Description, "Describe the visible action or beat."),
            Check("subjects", "Subjects / characters", shot.Subjects, "Name the subjects or characters in frame."),
            Check("location", "Location / set", shot.LocationSet, "Identify the location or set."),
            new MovieShotReadinessCheckDto(
                "duration",
                "Expected duration",
                shot.DurationSeconds is > 0,
                shot.DurationSeconds is > 0 ? $"{shot.DurationSeconds} seconds planned." : "Set a duration greater than zero."),
            Check("requirements", "Production requirements", shot.ProductionRequirements, "Capture props, performance, VFX, sound, or other requirements."),
            Check("continuity", "Continuity references", shot.ContinuityReferences ?? shot.VisualContinuityNotes, "Reference the guide, prior shot, or locked continuity facts."),
            new MovieShotReadinessCheckDto(
                "cinematography",
                "Cinematography summary",
                HasCinematography(shot),
                HasCinematography(shot) ? CinematographySummary(shot) : "Add framing, camera movement, or cinematography intent."),
        };
        var missing = checks.Where(item => !item.Satisfied).Select(item => item.Label).ToArray();
        var completed = checks.Length - missing.Length;
        var ready = missing.Length == 0 && !string.Equals(shot.Status, MovieShotStatuses.Archived, StringComparison.OrdinalIgnoreCase);
        var summary = ready
            ? "All required planning fields are present. This shot may proceed to Storyboard."
            : missing.Length == 0
                ? "This shot is archived and cannot proceed to Storyboard."
                : $"{completed} of {checks.Length} readiness checks complete; add {string.Join(", ", missing)}.";
        return new MovieShotReadinessDto(ready, checks, missing, summary);
    }

    public static string PlanState(MovieShot shot)
    {
        if (shot.ArchivedAt.HasValue || string.Equals(shot.Status, MovieShotStatuses.Archived, StringComparison.OrdinalIgnoreCase)) return MovieShotPlanStates.Archived;
        return shot.ProductionStage switch
        {
            MovieProductionStages.ShotPlan when Evaluate(shot).Ready => MovieShotPlanStates.ReadyForStoryboard,
            MovieProductionStages.ShotPlan => MovieShotPlanStates.Draft,
            MovieProductionStages.StoryboardCandidate or MovieProductionStages.ApprovedStoryboard => MovieShotPlanStates.Storyboard,
            _ => MovieShotPlanStates.Production,
        };
    }

    public static string CinematographySummary(MovieShot shot)
    {
        var parts = new[] { shot.CameraAndFraming, shot.CameraMotion, HasJsonIntent(shot.CinematographyJson) ? "structured intent" : null }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());
        return string.Join(" · ", parts);
    }

    public static IReadOnlyList<Guid> ParseSubjectCharacterIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeSubjectCharacterIds(IEnumerable<Guid>? ids) =>
        JsonSerializer.Serialize((ids ?? []).Distinct().ToArray());

    private static MovieShotReadinessCheckDto Check(string key, string label, string? value, string missingDetail) =>
        new(key, label, !string.IsNullOrWhiteSpace(value), string.IsNullOrWhiteSpace(value) ? missingDetail : "Present.");

    private static bool HasCinematography(MovieShot shot) =>
        !string.IsNullOrWhiteSpace(shot.CameraAndFraming) || !string.IsNullOrWhiteSpace(shot.CameraMotion) || HasJsonIntent(shot.CinematographyJson);

    private static bool HasJsonIntent(string? json) => !string.IsNullOrWhiteSpace(json) && json.Trim() is not "{}" and not "null";
}
