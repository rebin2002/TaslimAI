using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieTakeUpscaleEligibilityTests
{
    [Fact]
    public void Candidate_take_is_not_eligible_even_when_ready()
    {
        var take = new MovieTake { Id = Guid.NewGuid(), Status = MovieTakeStatuses.Ready };
        var shot = new MovieShot { Id = Guid.NewGuid() };

        var result = MovieTakeUpscaleEligibilityEvaluator.Evaluate(take, shot, MovieUpscaleResolutionCatalog.P4K);

        Assert.False(result.Eligible);
        Assert.Equal(MovieTakeUpscaleEligibilityCodes.TakeNotSelected, result.Code);
        Assert.False(result.IsSelected);
        Assert.False(result.IsFinal);
    }

    [Fact]
    public void Selected_or_final_take_is_eligible_but_draft_take_is_not()
    {
        var take = new MovieTake { Id = Guid.NewGuid(), Status = MovieTakeStatuses.Ready };
        var shot = new MovieShot { Id = Guid.NewGuid(), SelectedTakeId = take.Id };

        var selected = MovieTakeUpscaleEligibilityEvaluator.Evaluate(take, shot, MovieUpscaleResolutionCatalog.P4K);
        take.Status = MovieTakeStatuses.Draft;
        var draft = MovieTakeUpscaleEligibilityEvaluator.Evaluate(take, shot, MovieUpscaleResolutionCatalog.P4K);

        Assert.True(selected.Eligible);
        Assert.True(selected.IsSelected);
        Assert.False(selected.IsFinal);
        Assert.False(draft.Eligible);
        Assert.Equal(MovieTakeUpscaleEligibilityCodes.TakeNotReady, draft.Code);

        take.Status = MovieTakeStatuses.Approved;
        shot.SelectedTakeId = null;
        shot.FinalTakeId = take.Id;
        var final = MovieTakeUpscaleEligibilityEvaluator.Evaluate(take, shot, MovieUpscaleResolutionCatalog.P4K);
        Assert.True(final.Eligible);
        Assert.True(final.IsFinal);
    }

    [Fact]
    public async Task Request_records_blocked_candidate_and_pending_selected_request_without_generation_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, projectId, shotId) = await SeedAsync(db);
        var service = new MovieTakeUpscaleEligibilityService(db, new WorkspaceAccessService(db));

        var candidate = new MovieTake { Id = Guid.NewGuid(), MovieShotId = shotId, VersionNumber = 2, Label = "Candidate", Status = MovieTakeStatuses.Ready, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MovieTakes.Add(candidate);
        await db.SaveChangesAsync();
        var blocked = await service.RequestAsync(userId, candidate.Id, new MovieTakeUpscaleRequest(), CancellationToken.None);

        Assert.NotNull(blocked);
        Assert.False(blocked!.Eligible);
        Assert.Equal(MovieTakeUpscaleEligibilityCodes.TakeNotSelected, blocked.Code);
        Assert.Equal(MovieTakeUpscaleAuditStatuses.Blocked, blocked.AuditStatus);

        var selectedTake = await db.MovieTakes.SingleAsync(item => item.Id != candidate.Id);
        var shot = await db.MovieShots.SingleAsync(item => item.Id == shotId);
        selectedTake.Status = MovieTakeStatuses.Approved;
        shot.SelectedTakeId = selectedTake.Id;
        await db.SaveChangesAsync();

        var requested = await service.RequestAsync(userId, selectedTake.Id, new MovieTakeUpscaleRequest { TargetMasterResolution = MovieUpscaleResolutionCatalog.P4K, SourceResolution = "720p" }, CancellationToken.None);

        Assert.NotNull(requested);
        Assert.True(requested!.Eligible);
        Assert.Equal(MovieTakeUpscaleEligibilityCodes.Eligible, requested.Code);
        Assert.Equal(MovieTakeUpscaleAuditStatuses.PendingExecution, requested.AuditStatus);
        Assert.Equal("720p", requested.SourceResolution);
        Assert.Equal(0, await db.GenerationJobs.CountAsync());
        Assert.Equal(2, await db.MovieTakeUpscaleAudits.CountAsync(item => item.MovieProjectId == projectId));
    }

    [Fact]
    public async Task Repeated_request_is_idempotent_for_same_selected_take_and_target()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, _, shotId) = await SeedAsync(db);
        var take = await db.MovieTakes.SingleAsync(item => item.MovieShotId == shotId);
        var shot = await db.MovieShots.SingleAsync(item => item.Id == shotId);
        take.Status = MovieTakeStatuses.Approved;
        shot.SelectedTakeId = take.Id;
        await db.SaveChangesAsync();
        var service = new MovieTakeUpscaleEligibilityService(db, new WorkspaceAccessService(db));

        var first = await service.RequestAsync(userId, take.Id, new MovieTakeUpscaleRequest(), CancellationToken.None);
        var second = await service.RequestAsync(userId, take.Id, new MovieTakeUpscaleRequest(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.AuditId, second!.AuditId);
        Assert.Equal(MovieTakeUpscaleEligibilityCodes.AlreadyRequested, second.Code);
        Assert.Equal(1, await db.MovieTakeUpscaleAudits.CountAsync());
    }

    [Fact]
    public async Task Concurrent_selected_requests_replay_one_active_audit()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-movie-upscale-{Guid.NewGuid():N}.db");
        var barrier = new ExistingUpscaleAuditBarrier();
        try
        {
            var seedOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            await using (var seedDb = new TaslimDbContext(seedOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var fixture = await SeedAsync(seedDb);
                var take = await seedDb.MovieTakes.SingleAsync(item => item.MovieShotId == fixture.ShotId);
                var shot = await seedDb.MovieShots.SingleAsync(item => item.Id == fixture.ShotId);
                take.Status = MovieTakeStatuses.Approved;
                shot.SelectedTakeId = take.Id;
                await seedDb.SaveChangesAsync();

                async Task<MovieTakeUpscaleEligibilityDto?> RequestAsync()
                {
                    var options = new DbContextOptionsBuilder<TaslimDbContext>()
                        .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                        .AddInterceptors(barrier)
                        .Options;
                    await using var db = new TaslimDbContext(options);
                    var service = new MovieTakeUpscaleEligibilityService(db, new WorkspaceAccessService(db));
                    return await service.RequestAsync(fixture.UserId, take.Id, new MovieTakeUpscaleRequest
                    {
                        TargetMasterResolution = MovieUpscaleResolutionCatalog.P4K,
                        SourceResolution = "1080p",
                    }, CancellationToken.None);
                }

                var results = await Task.WhenAll(RequestAsync(), RequestAsync());
                Assert.All(results, result => Assert.NotNull(result));
                Assert.Equal(results[0]!.AuditId, results[1]!.AuditId);
                Assert.Contains(results, result => result!.Code == MovieTakeUpscaleEligibilityCodes.AlreadyRequested);
                Assert.Equal(1, await seedDb.MovieTakeUpscaleAudits.CountAsync(item => item.MovieTakeId == take.Id));
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void Active_request_classifier_does_not_swallow_unrelated_unique_errors()
    {
        var activeConflict = new DbUpdateException(
            "active upscale request conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieTakeUpscaleAudits.MovieTakeId, MovieTakeUpscaleAudits.TargetMasterResolution'."));
        var unrelatedConflict = new DbUpdateException(
            "unrelated conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieTakeUpscaleAudits.Id'."));

        Assert.True(MovieTakeUpscaleEligibilityService.IsActiveRequestUniqueViolation(activeConflict));
        Assert.False(MovieTakeUpscaleEligibilityService.IsActiveRequestUniqueViolation(unrelatedConflict));
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private sealed class ExistingUpscaleAuditBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MovieTakeUpscaleAudits", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("TargetMasterResolution", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("Status", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref arrivals) >= 2) released.TrySetResult(true);
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private static async Task<(Guid UserId, Guid ProjectId, Guid ShotId)> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "upscale-owner", NormalizedUserName = "UPSCALE-OWNER", DisplayName = "Upscale Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Upscale Workspace", Slug = $"upscale-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = projectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Upscale Test", Description = "A movie.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = projectId, Sequence = 1, Title = "Scene", Summary = "Scene.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Shot", CreatedAt = now, UpdatedAt = now });
        db.MovieTakes.Add(new MovieTake { Id = Guid.NewGuid(), MovieShotId = shotId, VersionNumber = 1, Label = "Take 1", Status = MovieTakeStatuses.Ready, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (userId, projectId, shotId);
    }
}
