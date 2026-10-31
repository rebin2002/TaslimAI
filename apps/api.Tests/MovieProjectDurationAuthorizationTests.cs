using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Permission and antiforgery guardrails for metadata-only Movie Studio duration edits.
/// No video generation worker or real provider calls are enabled.
/// </summary>
public sealed class MovieProjectDurationAuthorizationTests : IClassFixture<MovieOperationalNoWorkerFactory>
{
    private readonly MovieOperationalNoWorkerFactory factory;

    public MovieProjectDurationAuthorizationTests(MovieOperationalNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Unrelated_user_cannot_edit_another_workspace_movie_duration()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(ownerClient, "Duration Privacy Owner");
        var created = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            ownerClient, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Private Feature Draft",
                description = "Another account must not change its planned runtime.",
                durationSeconds = 600,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        using var foreignClient = factory.CreateClient();
        await MovieOperationalFixtures.RegisterAsync(foreignClient, "Unrelated Duration Editor");

        using var denied = await MovieOperationalFixtures.SendWithCsrfAsync(
            foreignClient, HttpMethod.Patch,
            $"/api/movie-studio/projects/{created.Project.Id}/duration",
            new { durationSeconds = 7200 });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var fetched = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(
            ownerClient, $"/api/movie-studio/projects/{created.Project.Id}");
        Assert.Equal(600, fetched.DurationSeconds);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(600, (await db.MovieProjects.AsNoTracking()
            .SingleAsync(movie => movie.Id == created.Project.Id)).DurationSeconds);
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(job => job.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking()
            .AnyAsync(tx => tx.WorkspaceId == owner.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Duration_edit_without_antiforgery_token_is_rejected_without_mutation()
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Duration Csrf Owner");
        var created = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Csrf Protected Film",
                description = "Runtime mutation requires an antiforgery token.",
                durationSeconds = 600,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/movie-studio/projects/{created.Project.Id}/duration")
        {
            Content = JsonContent.Create(new { durationSeconds = 7200 }),
        };
        using var denied = await client.SendAsync(request);
        Assert.False(denied.IsSuccessStatusCode);

        var fetched = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(
            client, $"/api/movie-studio/projects/{created.Project.Id}");
        Assert.Equal(600, fetched.DurationSeconds);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(job => job.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking()
            .AnyAsync(tx => tx.WorkspaceId == owner.PersonalWorkspace.Id));
    }
}
