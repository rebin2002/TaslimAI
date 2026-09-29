using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDurationBudgetTests
{
    [Fact]
    public void Thirty_second_movie_with_nested_scene_and_shot_totals_is_valid()
    {
        var result = Validate(
            30,
            Scene(15, Shot(7), Shot(8)),
            Scene(15, Shot(5), Shot(10)));

        Assert.Equal(MovieDurationBudgetStatuses.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.True(result.IsComplete);
        Assert.Equal(30, result.TotalSceneDurationSeconds);
        Assert.Equal(30, result.TotalShotDurationSeconds);
        Assert.Equal(0, result.SceneDeltaSeconds);
        Assert.Equal(2, result.Scenes.Count);
        Assert.All(result.Scenes, scene => Assert.Equal(MovieDurationBudgetStatuses.Valid, scene.Status));
    }

    [Fact]
    public void User_supplied_durations_are_not_scaled_or_replaced()
    {
        var result = Validate(30, Scene(13, Shot(6), Shot(6)));

        Assert.Equal(13, result.Scenes.Single().DeclaredDurationSeconds);
        Assert.Equal(12, result.Scenes.Single().TotalShotDurationSeconds);
        Assert.Equal(13, result.TotalSceneDurationSeconds);
        Assert.Equal(-17, result.SceneDeltaSeconds);
        Assert.Contains(result.Diagnostics, item => item.Code == "movie_duration_grossly_under_budget");
    }

    [Theory]
    [InlineData(0, "movie_duration_non_positive")]
    [InlineData(-1, "movie_duration_non_positive")]
    [InlineData(86_401, "movie_duration_absurd")]
    public void Target_duration_is_bounded(int target, string code)
    {
        var result = Validate(target, Scene(1, Shot(1)));

        Assert.Equal(MovieDurationBudgetStatuses.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == code);
    }

    [Theory]
    [InlineData(0, "scene_duration_non_positive")]
    [InlineData(-1, "scene_duration_non_positive")]
    [InlineData(86_401, "scene_duration_absurd")]
    public void Scene_duration_is_bounded(int duration, string code)
    {
        var result = Validate(30, Scene(duration, Shot(1)));

        Assert.Equal(MovieDurationBudgetStatuses.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == code);
    }

    [Theory]
    [InlineData(0, "shot_duration_non_positive")]
    [InlineData(-1, "shot_duration_non_positive")]
    [InlineData(3_601, "shot_duration_absurd")]
    public void Shot_duration_is_bounded(int duration, string code)
    {
        var result = Validate(30, Scene(30, Shot(duration)));

        Assert.Equal(MovieDurationBudgetStatuses.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == code);
    }

    [Fact]
    public void Boundary_tolerance_is_inclusive_and_rounds_up_with_a_cap()
    {
        Assert.Equal(1, MovieDurationBudgetPolicy.ToleranceSecondsFor(30));
        Assert.Equal(3, MovieDurationBudgetPolicy.ToleranceSecondsFor(101));
        Assert.Equal(10, MovieDurationBudgetPolicy.ToleranceSecondsFor(501));

        var within = Validate(50, Scene(50, Shot(49)));
        var outside = Validate(50, Scene(50, Shot(48)));

        Assert.Equal(MovieDurationBudgetStatuses.Valid, within.Status);
        Assert.Equal(MovieDurationBudgetStatuses.UnderBudget, outside.Status);
        Assert.Contains(outside.Diagnostics, item => item.Code == "shots_under_scene_budget");
    }

    [Fact]
    public void Movie_under_and_over_budget_are_distinguished_after_nested_validation()
    {
        var under = Validate(30, Scene(10, Shot(10)), Scene(10, Shot(10)));
        var over = Validate(30, Scene(16, Shot(16)), Scene(16, Shot(16)));

        Assert.Equal(MovieDurationBudgetStatuses.UnderBudget, under.Status);
        Assert.Contains(under.Diagnostics, item => item.Code == "movie_under_budget");
        Assert.Equal(MovieDurationBudgetStatuses.OverBudget, over.Status);
        Assert.Contains(over.Diagnostics, item => item.Code == "movie_over_budget");
    }

    [Fact]
    public void Shot_total_over_parent_scene_is_reported_as_nested_over_budget()
    {
        var result = Validate(30, Scene(10, Shot(6), Shot(6)));

        Assert.Equal(MovieDurationBudgetStatuses.OverBudget, result.Status);
        Assert.Equal(12, result.TotalShotDurationSeconds);
        Assert.Contains(result.Diagnostics, item => item.Code == "shots_over_scene_budget");
        Assert.Equal(MovieDurationBudgetStatuses.OverBudget, result.Scenes.Single().Status);
    }

    [Fact]
    public void Missing_durations_and_empty_hierarchy_are_incomplete_not_silently_under_budget()
    {
        var missing = Validate(30, new MovieDurationSceneInput(Guid.NewGuid(), "Missing", null, [new MovieDurationShotInput(Guid.NewGuid(), null)]));
        var empty = MovieDurationBudgetValidator.Validate(new MovieDurationBudgetInput(30, []));

        Assert.Equal(MovieDurationBudgetStatuses.Incomplete, missing.Status);
        Assert.Contains(missing.Diagnostics, item => item.Code == "scene_duration_missing");
        Assert.Contains(missing.Diagnostics, item => item.Code == "shot_duration_missing");
        Assert.Equal(MovieDurationBudgetStatuses.Incomplete, empty.Status);
        Assert.Contains(empty.Diagnostics, item => item.Code == "scenes_missing");
    }

    [Fact]
    public void Grossly_incompatible_short_form_plan_is_invalid_and_explainable()
    {
        var result = Validate(30, Scene(120, Shot(120)));

        Assert.Equal(MovieDurationBudgetStatuses.OverBudget, result.Status);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, item => item.Code == "movie_duration_grossly_over_budget" && item.Severity == MovieDurationBudgetDiagnosticSeverities.Error);
    }

    [Fact]
    public void Archived_scenes_and_shots_do_not_consume_runtime_budget()
    {
        var archivedShot = new MovieDurationShotInput(Guid.NewGuid(), 90, Archived: true);
        var archivedScene = new MovieDurationSceneInput(Guid.NewGuid(), "Archived", 90, [archivedShot], Archived: true);
        var result = MovieDurationBudgetValidator.Validate(new MovieDurationBudgetInput(30,
        [
            Scene(30, Shot(30)),
            archivedScene,
        ]));

        Assert.Equal(MovieDurationBudgetStatuses.Valid, result.Status);
        Assert.Equal(30, result.TotalSceneDurationSeconds);
        Assert.Equal(30, result.TotalShotDurationSeconds);
        Assert.Single(result.Scenes);
    }

    private static MovieDurationBudgetResult Validate(int target, params MovieDurationSceneInput[] scenes) =>
        MovieDurationBudgetValidator.Validate(new MovieDurationBudgetInput(target, scenes));

    private static MovieDurationSceneInput Scene(int duration, params MovieDurationShotInput[] shots) =>
        new(Guid.NewGuid(), "Scene", duration, shots);

    private static MovieDurationShotInput Shot(int duration) =>
        new(Guid.NewGuid(), duration);
}
