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
}
