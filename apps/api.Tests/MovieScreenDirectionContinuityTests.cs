using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieScreenDirectionContinuityTests
{
    [Fact]
    public void Plan_codec_normalizes_supported_values_and_round_trips_legacy_safe_json()
    {
        var plan = new MovieScreenDirectionPlan(
            Axis: "  corridor-a ",
            Orientation: "SIDE_A",
            EntranceDirection: "SCREEN_LEFT",
            ExitDirection: "SCREEN_RIGHT",
            EyelineDirection: "SCREEN_RIGHT",
            EyelineTarget: "Mara",
            SpatialAnchor: "door",
            Notes: "  Preserve the doorway geography. ");

        Assert.Null(MovieScreenDirectionPlanValidator.Validate(plan));
        var json = MovieScreenDirectionPlanCodec.ToJson(plan);
        var roundTrip = MovieScreenDirectionPlanCodec.FromJson(json);

        Assert.NotNull(roundTrip);
        Assert.Equal("corridor-a", roundTrip!.Axis);
        Assert.Equal(MovieScreenDirectionValues.SideA, roundTrip.Orientation);
        Assert.Equal(MovieScreenDirectionValues.ScreenLeft, roundTrip.EntranceDirection);
        Assert.Equal(MovieScreenDirectionValues.ScreenRight, roundTrip.ExitDirection);
        Assert.Equal("Mara", roundTrip.EyelineTarget);
        Assert.Equal("Preserve the doorway geography.", roundTrip.Notes);
    }

    [Fact]
    public void Adjacent_shots_report_axis_entry_eyeline_and_geography_conflicts_with_safe_edit_and_insert_recommendations()
    {
        var sceneId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstPlan = new MovieScreenDirectionPlan(
            Axis: "corridor-a", Orientation: MovieScreenDirectionValues.SideA,
            ExitDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineTarget: "Mara", SpatialAnchor: "door");
        var secondPlan = new MovieScreenDirectionPlan(
            Axis: "corridor-b", Orientation: MovieScreenDirectionValues.SideA,
            EntranceDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineTarget: "Mara", SpatialAnchor: "window");
        var scene = new MovieProductionContinuitySceneContext(
            sceneId, 1, "Corridor", "Mara crosses the corridor.", null, null, null,
            [
                Shot(firstId, 1, firstPlan),
                Shot(secondId, 2, secondPlan),
            ]);
        var context = new MovieProductionContinuityContext(
            Guid.NewGuid(), sceneId, secondId, new DirectorGuideContext("", "", "", "", "", null, false),
            [scene], [], null, [], [], [], DateTime.UtcNow);

        var result = MovieProductionContinuityAnalyzer.AnalyzeWithRecommendations(context);

        Assert.Contains(result.Findings, item => item.Category == DirectorStoryFindingCategories.ScreenDirectionAxis);
        Assert.Contains(result.Findings, item => item.Category == DirectorStoryFindingCategories.ScreenDirectionEntranceExit);
        Assert.Contains(result.Findings, item => item.Category == DirectorStoryFindingCategories.ScreenDirectionEyeline);
        Assert.Contains(result.Findings, item => item.Category == DirectorStoryFindingCategories.ScreenDirectionGeography);
        Assert.Contains(result.ScreenDirectionRecommendations, item => item.Kind == "edit" && item.TargetShotId == secondId);
        Assert.Contains(result.ScreenDirectionRecommendations, item => item.Kind == "insert" && item.ReferenceShotId == firstId && item.TargetShotId == secondId);
        Assert.All(result.ScreenDirectionRecommendations, item => Assert.False(item.AutomaticRewrite));
        Assert.All(result.Findings.Where(item => item.Category.StartsWith("screen_direction", StringComparison.Ordinal)), item => Assert.NotEmpty(item.Evidence));
    }

    [Fact]
    public void Explicit_axis_break_suppresses_adjacent_screen_direction_conflicts()
    {
        var first = Shot(Guid.NewGuid(), 1, new MovieScreenDirectionPlan(
            Axis: "a", Orientation: MovieScreenDirectionValues.SideA,
            ExitDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineTarget: "Mara", SpatialAnchor: "door"));
        var second = Shot(Guid.NewGuid(), 2, new MovieScreenDirectionPlan(
            Axis: "b", Orientation: MovieScreenDirectionValues.AxisBreak,
            EntranceDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineDirection: MovieScreenDirectionValues.ScreenRight,
            EyelineTarget: "Mara", SpatialAnchor: "window", AxisBreak: true));
        var scene = new MovieProductionContinuitySceneContext(Guid.NewGuid(), 1, "Cut", "", null, null, null, [first, second]);
        var context = new MovieProductionContinuityContext(Guid.NewGuid(), scene.Id, null, new DirectorGuideContext("", "", "", "", "", null, false), [scene], [], null, [], [], [], DateTime.UtcNow);

        var result = MovieScreenDirectionAnalyzer.Analyze(context);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public void Validator_rejects_unknown_direction_values()
    {
        var plan = new MovieScreenDirectionPlan(EntranceDirection: "diagonal");

        Assert.Contains("supported screen direction", MovieScreenDirectionPlanValidator.Validate(plan), StringComparison.OrdinalIgnoreCase);
    }

    private static MovieProductionContinuityShotContext Shot(Guid id, int sequence, MovieScreenDirectionPlan plan) =>
        new(id, sequence, $"Shot {sequence}", null, null, [], null, null, null, null, null, null, null, null, plan);
}
