using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #685 — long-form budget-planning slice. Question answered: can a 120-minute
/// (7,200-second) project brief produce a budget at all, and does planning it ever ask the cost
/// estimator for one oversized generation?
///
/// Root cause on current <c>main</c>: <see cref="MovieBudgetDirectorRequest.DurationSeconds"/> is
/// documented and validated as the representative duration of ONE shot (per-clip ceiling
/// 3,600 seconds), yet <c>EstimateProjectAsync</c> derived it as the whole project runtime
/// whenever a project had no planned shots — a film runtime modelled as a single shot. Any brief
/// longer than the per-clip ceiling therefore failed <c>GET /api/movie-studio/v2/projects/{id}/
/// budget-director</c> with <c>request_invalid</c> before producing any estimate, which is exactly
/// the state a 7,200-second plan reaches the moment the project cap is lifted. It also described a
/// feature-length film as one 3,600-second clip for shorter briefs.
///
/// Non-duplication evidence (2026-11-16, against the changed-file lists of all 483 open PRs):
/// - #269 owns the create-request project cap (<c>MovieEntities.cs</c>, <c>MovieStudioService.cs</c>)
///   and adds <c>MovieProjectDurationBoundaryTests</c>. It never touches <c>apps/api/Usage/</c>.
/// - #298, #305 and #807 own <c>MovieFinalAssembly.cs</c>, including its outstanding 500-selected-
///   take cap. This slice edits neither that file nor the assembly/export path.
/// - #799 (merged) proves scene-reorder timeline metadata survives at 7,200 seconds. That is scene
///   ordering, not budget derivation.
/// Neither <c>apps/api/Usage/MovieBudgetDirector.cs</c> nor this file's path is claimed by any open PR.
///
/// Provider-free and spend-free: in-memory SQLite plus a recording fake cost estimator. No
/// <c>IMovieVideoProvider</c>, no generation job, no usage reservation, no network, no charging.
/// Every test asserts the per-shot and per-clip ceilings stay enforced; none raises them.
/// </summary>
public sealed class MovieBudgetDirectorLongFormPlanTests
{
    private const int TwoHourSeconds = 7_200;
    private const int PerClipCeilingSeconds = 3_600;

    // ---------------------------------------------------------------- pure derivation

    [Theory]
    [InlineData(1)]
    [InlineData(29)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(600)]
    [InlineData(3_600)]
    [InlineData(3_601)]
    [InlineData(7_199)]
    [InlineData(TwoHourSeconds)]
    public void Unplanned_project_grid_covers_the_runtime_without_exceeding_the_per_clip_ceiling(int projectDurationSeconds)
    {
        var (shotCount, durationSeconds) =
            MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(projectDurationSeconds);

        // 1. The representative unit is always a legal shot, never a whole-film runtime.
        Assert.InRange(durationSeconds, 1, PerClipCeilingSeconds);
        Assert.InRange(shotCount, 1, 10_000);
        // 2. No silent truncation: the grid always covers at least the approved runtime.
        Assert.True(shotCount * (long)durationSeconds >= projectDurationSeconds,
            $"A {projectDurationSeconds}-second brief was planned as only {shotCount * (long)durationSeconds} seconds of footage.");
        // 3. Anything longer than one clip is never planned as a single clip.
        if (projectDurationSeconds > PerClipCeilingSeconds) Assert.True(shotCount > 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7_201)]
    [InlineData(100_000)]
    public void Brief_outside_the_approved_planning_window_fails_closed(int projectDurationSeconds)
    {
        var exception = Assert.Throws<MovieBudgetDirectorValidationException>(
            () => MovieBudgetDirectorService.DeriveUnplannedProjectShotGrid(projectDurationSeconds));
        Assert.Equal(MovieBudgetDirectorReasons.ProjectDurationUnsupported, exception.Code);
    }

    // ---------------------------------------------------------------- unbuilt long-form brief

    [Fact]
    public async Task Unbuilt_two_hour_brief_plans_a_budget_instead_of_failing_the_per_clip_ceiling()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId) = await SeedProjectAsync(db, TwoHourSeconds, []);
        var estimator = new RecordingEstimator();
        var service = CreateService(db, estimator);

        var result = await service.EstimateProjectAsync(userId, movieId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MovieBudgetDirectorStates.Estimated, result.State);
        // The plan is a shot grid, not one impossible clip.
        Assert.True(result.NaivePath.GenerationUnits > 1);
        Assert.All(result.Plan.Where(item => item.DurationSecondsPerUnit > 0),
            item => Assert.InRange(item.DurationSecondsPerUnit, 1, PerClipCeilingSeconds));
        Assert.DoesNotContain(result.Plan, item => item.DurationSecondsPerUnit > PerClipCeilingSeconds);
    }

    [Fact]
    public async Task Two_hour_budget_never_asks_the_cost_estimator_for_a_single_oversized_generation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId) = await SeedProjectAsync(db, TwoHourSeconds, []);
        var estimator = new RecordingEstimator();
        var service = CreateService(db, estimator);

        await service.EstimateProjectAsync(userId, movieId, CancellationToken.None);

        Assert.NotEmpty(estimator.RequestedDurationSeconds);
        // The whole 7,200-second runtime must never reach the cost boundary as one unit, so no
        // downstream job or usage reservation can ever be quoted for a two-hour single clip.
        Assert.True(estimator.RequestedDurationSeconds.Max() <= PerClipCeilingSeconds,
            $"Budget planning asked the estimator for a single {estimator.RequestedDurationSeconds.Max()}-second generation.");
        Assert.DoesNotContain(estimator.RequestedDurationSeconds, seconds => seconds == TwoHourSeconds);
    }

    [Fact]
    public async Task Unbuilt_brief_is_never_modelled_as_one_full_length_clip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        // A ten-minute brief used to be quoted as a single 600-second clip.
        var (userId, movieId) = await SeedProjectAsync(db, 600, []);
        var estimator = new RecordingEstimator();
        var service = CreateService(db, estimator);

        var result = await service.EstimateProjectAsync(userId, movieId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(estimator.RequestedDurationSeconds, seconds => seconds == 600);
        Assert.All(estimator.RequestedDurationSeconds, seconds => Assert.InRange(seconds, 1, PerClipCeilingSeconds));
        Assert.True(result.NaivePath.GenerationUnits > 1);
    }

    [Fact]
    public async Task Two_hour_brief_above_the_approved_cap_fails_before_any_cost_estimate_is_made()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        // A stored row beyond the approved 120-minute cap must fail closed, not be truncated.
        var (userId, movieId) = await SeedProjectAsync(db, TwoHourSeconds + 1, []);
        var estimator = new RecordingEstimator();
        var service = CreateService(db, estimator);

        var exception = await Assert.ThrowsAsync<MovieBudgetDirectorValidationException>(
            () => service.EstimateProjectAsync(userId, movieId, CancellationToken.None));

        Assert.Equal(MovieBudgetDirectorReasons.ProjectDurationUnsupported, exception.Code);
        Assert.Empty(estimator.RequestedDurationSeconds);
    }

    // ---------------------------------------------------------------- preserved behaviour

    [Fact]
    public async Task Planned_shots_still_drive_the_representative_shot_duration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        // Four persisted 12-second shots inside a 7,200-second brief: the average shot length,
        // not a derived grid and not the project runtime, must be used.
        var (userId, movieId) = await SeedProjectAsync(db, TwoHourSeconds, [12, 12, 12, 12]);
        var estimator = new RecordingEstimator();
        var service = CreateService(db, estimator);

        var result = await service.EstimateProjectAsync(userId, movieId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MovieBudgetDirectorStates.Estimated, result.State);
        Assert.All(estimator.RequestedDurationSeconds, seconds => Assert.InRange(seconds, 1, 12));
    }

    [Fact]
    public async Task Explicit_preview_overrides_derivation_and_still_rejects_an_oversized_single_shot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId) = await SeedProjectAsync(db, TwoHourSeconds, []);
        var estimator = new RecordingEstimator();
        var service = CreateService(db, estimator);

        // The per-clip provider limit stays independently enforced for caller-supplied previews:
        // this slice does not widen it to make long-form planning pass.
        var exception = await Assert.ThrowsAsync<MovieBudgetDirectorValidationException>(
            () => service.EstimateProjectAsync(userId, movieId, new MovieBudgetDirectorRequest
            {
                DurationSeconds = TwoHourSeconds,
                ShotCount = 1,
                CandidatePassesPerShot = 1,
            }, CancellationToken.None));

        Assert.Equal(MovieBudgetDirectorReasons.RequestInvalid, exception.Code);
        Assert.Empty(estimator.RequestedDurationSeconds);
    }

    // ---------------------------------------------------------------- fixtures

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static MovieBudgetDirectorService CreateService(TaslimDbContext db, IMovieGenerationCostEstimator estimator) =>
        new(db, estimator, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private static async Task<(Guid UserId, Guid MovieId)> SeedProjectAsync(
        TaslimDbContext db, int durationSeconds, int[] shotDurations)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"longform-budget-{Guid.NewGuid():N}", NormalizedUserName = "LONGFORM-BUDGET", DisplayName = "Longform Budget Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Longform Budget Workspace", Slug = $"longform-budget-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Two Hour Feature",
            Description = "120-minute project budget planning.", DurationSeconds = durationSeconds,
            Mode = MovieProjectModes.Full, CreatedAt = now, UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
        });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        if (shotDurations.Length > 0)
        {
            db.MovieScenes.Add(new MovieScene
            {
                Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Scene One",
                Summary = "Planned scene.", DurationSeconds = shotDurations.Sum(),
                Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now,
            });
            var sequence = 1;
            foreach (var shotDuration in shotDurations)
            {
                db.MovieShots.Add(new MovieShot
                {
                    Id = Guid.NewGuid(), MovieSceneId = sceneId, Sequence = sequence++,
                    Description = $"Planned {shotDuration}-second shot.", DurationSeconds = shotDuration,
                    Status = MovieShotStatuses.Planned, CreatedAt = now, UpdatedAt = now,
                });
            }
        }
        await db.SaveChangesAsync();
        return (userId, movieId);
    }

    /// <summary>
    /// Records the duration of every cost request the planner makes. It is the witness for
    /// "no oversized single generation was ever quoted"; it performs no I/O and calls no provider.
    /// </summary>
    private sealed class RecordingEstimator : IMovieGenerationCostEstimator
    {
        public List<int> RequestedDurationSeconds { get; } = [];

        public Task<MovieGenerationCostEstimate> EstimateAsync(
            MovieGenerationCostRequest request,
            string? selectedProviderKey = null,
            string? selectedModelKey = null,
            CancellationToken cancellationToken = default)
        {
            RequestedDurationSeconds.Add(request.DurationSeconds);
            var amount = request.DurationSeconds;
            return Task.FromResult(new MovieGenerationCostEstimate(
                MovieGenerationCostEstimateStates.Estimated,
                amount,
                amount,
                "USD",
                "server-only-pricing",
                DateTime.UtcNow,
                "longform-planning-fixture",
                [new MovieGenerationCostComponent("generation", request.DurationSeconds, "seconds_per_attempt", amount, amount)]));
        }
    }
}
