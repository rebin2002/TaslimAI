using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Project timeline duration is distinct from a single paid video-provider clip.
/// All tests use an in-memory fake provider and have the generation worker disabled.
/// </summary>
public sealed class MovieProjectDurationBoundaryTests : IClassFixture<MovieOperationalNoWorkerFactory>
{
    private readonly MovieOperationalNoWorkerFactory factory;

    public MovieProjectDurationBoundaryTests(MovieOperationalNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Full_film_supports_two_hour_project_plan_without_queueing_a_video_job()
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Two Hour Film Owner");
        var created = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Two Hour Film",
                description = "A long-form film assembled from provider-supported short clips.",
                durationSeconds = 7200,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        Assert.Equal(7200, created.Project.DurationSeconds);
        Assert.Null(created.Job);
        var fetched = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(
            client, $"/api/movie-studio/projects/{created.Project.Id}");
        Assert.Equal(7200, fetched.DurationSeconds);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var persisted = await db.MovieProjects.AsNoTracking()
            .SingleAsync(item => item.Id == created.Project.Id);
        Assert.Equal(7200, persisted.DurationSeconds);
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7201)]
    [InlineData(int.MaxValue)]
    public async Task Project_duration_outside_approved_two_hour_boundary_is_rejected_before_persistence(int durationSeconds)
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Duration Boundary Owner");
        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Rejected Overlong Film",
                description = "Invalid project duration must fail before creating a project.",
                durationSeconds,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("120 minutes", await response.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.MovieProjects.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
    }

    [Theory]
    [InlineData(3601)]
    [InlineData(7200)]
    public async Task Quick_movie_rejects_overlong_brief_before_persistence(int durationSeconds)
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Quick Duration Boundary Owner");
        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Quick,
                title = "Rejected Overlong Quick Movie",
                description = "Quick briefs must not inherit a full-film timeline ceiling.",
                durationSeconds,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("60 minutes", await response.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.MovieProjects.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Quick_movie_accepts_one_hour_brief_without_queuing_generation()
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Quick One Hour Boundary Owner");
        var created = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Quick,
                title = "One Hour Quick Movie Brief",
                description = "The supported upper brief limit saves only project metadata.",
                durationSeconds = 3600,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        Assert.Equal(3600, created.Project.DurationSeconds);
        Assert.Null(created.Job);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Two_hour_project_duration_budget_preserves_scene_and_shot_totals()
    {
        // A feature-length timeline is assembled from planned shots, never from
        // a single feature-length paid-provider submission.
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Feature Runtime Budget Owner");
        var created = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Two Hour Timeline Budget",
                description = "Preserve 7,200 seconds of planned runtime without paid generation.",
                durationSeconds = 7200,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        var scene = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(
            client, $"/api/movie-studio/projects/{created.Project.Id}/scenes", new
            {
                title = "Feature-Length Sequence",
                summary = "Two planned shots spanning a two-hour movie timeline.",
                durationSeconds = 7200,
            });

        for (var shotNumber = 1; shotNumber <= 2; shotNumber++)
        {
            var shot = await MovieOperationalFixtures.PostAsync<MovieShotDto>(
                client, $"/api/movie-studio/scenes/{scene.Id}/shots", new
                {
                    description = $"Planned shot {shotNumber} of the long-form timeline.",
                    durationSeconds = 3600,
                });
            Assert.Equal(3600, shot.DurationSeconds);
        }

        var budget = await MovieOperationalFixtures.GetAsync<MovieDurationBudgetResult>(
            client, $"/api/movie-studio/projects/{created.Project.Id}/duration-budget");
        Assert.True(budget.IsValid);
        Assert.True(budget.IsComplete);
        Assert.Equal(7200, budget.TargetDurationSeconds);
        Assert.Equal(7200L, budget.TotalSceneDurationSeconds);
        Assert.Equal(7200L, budget.TotalShotDurationSeconds);
        Assert.Equal(MovieDurationBudgetStatuses.Valid, budget.Status);
        Assert.Equal(7200, Assert.Single(budget.Scenes).DeclaredDurationSeconds);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(job => job.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.MovieClips.AsNoTracking()
            .AnyAsync(clip => clip.MovieProjectId == created.Project.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking()
            .AnyAsync(tx => tx.WorkspaceId == owner.PersonalWorkspace.Id));
    }

}
