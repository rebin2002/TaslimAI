using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
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

    [Fact]
    public async Task Concurrent_checkpoint_initialization_replays_one_canonical_project_checkpoint()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-movie-checkpoint-{Guid.NewGuid():N}.db");
        var barrier = new CheckpointLookupBarrier();
        try
        {
            var seedOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            await using var seedDb = new TaslimDbContext(seedOptions);
            await seedDb.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(seedDb);

            async Task<MovieProductionCheckpointDto?> ReadCheckpointAsync()
            {
                var options = new DbContextOptionsBuilder<TaslimDbContext>()
                    .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                    .AddInterceptors(barrier)
                    .Options;
                await using var db = new TaslimDbContext(options);
                var access = new MovieCollaborationAccess(db, new WorkspaceAccessService(db));
                var service = new MovieProductionCheckpointService(db, access, null!);
                return await service.GetAsync(seed.UserId, seed.MovieProjectId, CancellationToken.None);
            }

            var results = await Task.WhenAll(ReadCheckpointAsync(), ReadCheckpointAsync());
            Assert.All(results, result => Assert.NotNull(result));
            Assert.All(results, result => Assert.Equal(seed.MovieProjectId, result!.MovieProjectId));

            await using var verifyDb = new TaslimDbContext(seedOptions);
            var checkpoints = await verifyDb.MovieProductionCheckpoints.AsNoTracking()
                .Where(item => item.MovieProjectId == seed.MovieProjectId)
                .ToListAsync();
            Assert.Single(checkpoints);
            Assert.Equal(1, checkpoints[0].Version);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void Checkpoint_retry_classifier_rejects_unrelated_unique_conflicts()
    {
        var sqliteConflict = new DbUpdateException(
            "checkpoint project conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieProductionCheckpoints.MovieProjectId'."));
        var postgresConflict = new DbUpdateException(
            "checkpoint PostgreSQL project conflict",
            new InvalidOperationException("23505: duplicate key value violates unique constraint \"IX_MovieProductionCheckpoints_MovieProjectId\""));
        var otherConflict = new DbUpdateException(
            "other conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieProductionCheckpoints.Id'."));

        Assert.True(MovieProductionCheckpointService.IsCheckpointProjectUniqueViolation(sqliteConflict));
        Assert.True(MovieProductionCheckpointService.IsCheckpointProjectUniqueViolation(postgresConflict));
        Assert.False(MovieProductionCheckpointService.IsCheckpointProjectUniqueViolation(otherConflict));
    }

    private static MovieProductionCheckpointItemDto Analyze(
        IReadOnlyList<MovieProductionCheckpointVersionSnapshot> versions,
        IReadOnlyList<MovieProductionCheckpointTakeSnapshot> takes,
        IReadOnlyList<MovieProductionCheckpointJobSnapshot> jobs) =>
        MovieProductionCheckpointAnalyzer.AnalyzeShot(ShotId, "Opening", 1, 1, "Scene 1 · Shot 1", MovieShotStatuses.Planned, UpdatedAt, versions, takes, jobs);

    private static async Task<(Guid UserId, Guid MovieProjectId)> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieProjectId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "checkpoint-owner",
            NormalizedUserName = "CHECKPOINT-OWNER",
            DisplayName = "Checkpoint Owner",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Checkpoint Workspace",
            Slug = $"checkpoint-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieProjectId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Title = "Checkpoint Movie",
            Description = "A checkpoint race fixture.",
            DurationSeconds = 30,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
        });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movieProjectId,
            UserId = userId,
            Role = MovieTeamRoles.Producer,
            IsProjectOwner = true,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return (userId, movieProjectId);
    }

    private sealed class CheckpointLookupBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsCheckpointLookup(command))
            {
                if (Interlocked.Increment(ref arrivals) >= 2) released.TrySetResult(true);
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }

        private static bool IsCheckpointLookup(DbCommand command) =>
            command.CommandText.Contains("MovieProductionCheckpoints", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("MovieProjectId", StringComparison.OrdinalIgnoreCase);
    }
}
