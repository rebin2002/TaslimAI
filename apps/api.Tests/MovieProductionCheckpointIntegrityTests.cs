using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionCheckpointIntegrityTests
{
    private static readonly DateTime UpdatedAt = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ShotId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void Rejected_take_with_stale_finalization_markers_is_not_complete()
    {
        var take = new MovieProductionCheckpointTakeSnapshot(
            Guid.NewGuid(), MovieTakeStatuses.Rejected, IsSelected: true, IsFinal: true, HasAsset: true, GenerationJobId: null);
        var shot = Analyze(
            ApprovedProductionVersions(),
            [take],
            []);

        Assert.NotEqual(MovieProductionCheckpointStates.Complete, shot.State);
        Assert.Equal(MovieProductionCheckpointActions.CreateRender, shot.NextAction);
    }

    [Fact]
    public void Selected_take_without_selection_marker_is_not_complete()
    {
        var take = new MovieProductionCheckpointTakeSnapshot(
            Guid.NewGuid(), MovieTakeStatuses.Selected, IsSelected: false, IsFinal: true, HasAsset: true, GenerationJobId: null);
        var shot = Analyze(
            ApprovedProductionVersions(),
            [take],
            []);

        Assert.NotEqual(MovieProductionCheckpointStates.Complete, shot.State);
        Assert.Equal(MovieProductionCheckpointActions.CreateRender, shot.NextAction);
    }

    private static MovieProductionCheckpointVersionSnapshot[] ApprovedProductionVersions() =>
    [
        new(Guid.NewGuid(), MovieProductionStages.ApprovedStoryboard, MovieProductionVersionStatuses.Approved, null, true),
        new(Guid.NewGuid(), MovieProductionStages.ApprovedKeyframe, MovieProductionVersionStatuses.Approved, null, true),
        new(Guid.NewGuid(), MovieProductionStages.MotionPreview, MovieProductionVersionStatuses.Approved, null, true),
    ];

    private static MovieProductionCheckpointItemDto Analyze(
        IReadOnlyList<MovieProductionCheckpointVersionSnapshot> versions,
        IReadOnlyList<MovieProductionCheckpointTakeSnapshot> takes,
        IReadOnlyList<MovieProductionCheckpointJobSnapshot> jobs) =>
        MovieProductionCheckpointAnalyzer.AnalyzeShot(
            ShotId, "Opening", 1, 1, "Scene 1 · Shot 1", MovieShotStatuses.Planned, UpdatedAt, versions, takes, jobs);
}
