using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionCheckpointTests
{
    private static readonly DateTime UpdatedAt = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ShotId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid JobId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Empty_plan_is_not_started_and_shot_dependencies_are_explicit()
    {
        var projection = MovieProductionCheckpointAnalyzer.Summarize([], []);
        Assert.Equal(MovieProductionCheckpointStates.NotStarted, projection.State);
        Assert.Equal(0, projection.ProgressPercent);

        var shot = Analyze([], [], []);
        Assert.Equal(MovieProductionCheckpointStates.Blocked, shot.State);
        Assert.Equal(MovieProductionCheckpointActions.CreateStoryboard, shot.NextAction);
        Assert.Equal(0, shot.ProgressPercent);
    }

    [Fact]
    public void Pending_approval_is_blocked_until_a_human_decision()
    {
        var shot = Analyze(
            [new(Guid.NewGuid(), MovieProductionStages.StoryboardCandidate, MovieProductionVersionStatuses.PendingApproval, null, false)],
            [],
            []);
        Assert.Equal(MovieProductionCheckpointActions.ReviewStoryboard, shot.NextAction);
        Assert.Equal("A production review is required before this shot can advance.", shot.BlockedReason);
    }

    [Fact]
    public void Failed_pass_without_output_is_recoverable_but_active_retry_wins()
    {
        var failed = Analyze(
            [new(Guid.NewGuid(), MovieProductionStages.ProductionRender, MovieProductionVersionStatuses.PendingApproval, JobId, false)],
            [],
            [new(JobId, GenerationJobStatus.Failed.ToString(), false)]);
        Assert.Equal(MovieProductionCheckpointStates.Recoverable, failed.State);
        Assert.Equal(MovieProductionCheckpointActions.Retry, failed.NextAction);
        Assert.Equal(JobId, failed.RecoveryJobId);

        var active = Analyze(
            [new(Guid.NewGuid(), MovieProductionStages.ProductionRender, MovieProductionVersionStatuses.PendingApproval, JobId, false)],
            [],
            [new(JobId, GenerationJobStatus.Running.ToString(), false), new(Guid.NewGuid(), GenerationJobStatus.Failed.ToString(), false)]);
        Assert.Equal(MovieProductionCheckpointStates.Running, active.State);
        Assert.Null(active.RecoveryJobId);
    }

    [Fact]
    public void Final_take_is_complete_and_progress_is_capped_at_one_hundred()
    {
        var takeId = Guid.NewGuid();
        var shot = Analyze(
            [],
            [new(takeId, MovieTakeStatuses.Selected, true, true, true, null)],
            []);
        var projection = MovieProductionCheckpointAnalyzer.Summarize([shot], []);
        Assert.Equal(MovieProductionCheckpointStates.Complete, projection.State);
        Assert.Equal(100, projection.ProgressPercent);
        Assert.Equal(1, projection.CompletedShots);
    }

    [Fact]
    public void Recovery_retry_identity_is_deterministic_bounded_and_source_scoped()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sourceJobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var key = MovieProductionCheckpointService.BuildRecoveryIdempotencyKey(projectId, sourceJobId);

        Assert.Equal($"movie-recovery:{projectId:N}:{sourceJobId:N}", key);
        Assert.InRange(key.Length, 1, 80);
        Assert.NotEqual(key, MovieProductionCheckpointService.BuildRecoveryIdempotencyKey(projectId, Guid.NewGuid()));
    }

    [Fact]
    public void Recovery_output_requires_an_active_asset_and_ready_private_file_in_movie_scope()
    {
        var movie = new MovieProject
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
        };
        var asset = new Asset
        {
            WorkspaceId = movie.WorkspaceId,
            ProjectId = movie.ProjectId,
            Status = AssetStatus.Active,
            StoredFile = new StoredFile
            {
                WorkspaceId = movie.WorkspaceId,
                ProjectId = movie.ProjectId,
                Status = StoredFileStatus.Ready,
            },
        };

        Assert.True(MovieProductionCheckpointReadiness.IsPublishableAsset(asset, movie));

        asset.StoredFile!.Status = StoredFileStatus.Failed;
        Assert.False(MovieProductionCheckpointReadiness.IsPublishableAsset(asset, movie));

        asset.StoredFile.Status = StoredFileStatus.Ready;
        asset.StoredFile.ProjectId = Guid.NewGuid();
        Assert.False(MovieProductionCheckpointReadiness.IsPublishableAsset(asset, movie));

        asset.StoredFile.ProjectId = movie.ProjectId;
        asset.StoredFile.ConversationId = Guid.NewGuid();
        Assert.False(MovieProductionCheckpointReadiness.IsPublishableAsset(asset, movie));
    }

    [Fact]
    public void Recovery_job_output_requires_a_ready_private_file()
    {
        var movie = new MovieProject { WorkspaceId = Guid.NewGuid(), ProjectId = Guid.NewGuid() };
        var file = new StoredFile
        {
            WorkspaceId = movie.WorkspaceId,
            ProjectId = movie.ProjectId,
            Status = StoredFileStatus.Ready,
        };
        var job = new GenerationJob
        {
            Outputs = { new GenerationJobOutput { StoredFile = file } },
        };

        Assert.True(MovieProductionCheckpointReadiness.HasPublishableJobOutput(job, movie));
        file.Status = StoredFileStatus.Deleted;
        Assert.False(MovieProductionCheckpointReadiness.HasPublishableJobOutput(job, movie));
    }

    private static MovieProductionCheckpointItemDto Analyze(
        IReadOnlyList<MovieProductionCheckpointVersionSnapshot> versions,
        IReadOnlyList<MovieProductionCheckpointTakeSnapshot> takes,
        IReadOnlyList<MovieProductionCheckpointJobSnapshot> jobs) =>
        MovieProductionCheckpointAnalyzer.AnalyzeShot(ShotId, "Opening", 1, 1, "Scene 1 · Shot 1", MovieShotStatuses.Planned, UpdatedAt, versions, takes, jobs);
}
