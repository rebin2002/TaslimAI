using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotPlanningApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieShotPlanningApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Shot_plan_is_explainable_orderable_archivable_and_never_queues_generation()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Shot Planner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Dawn", summary = "A quiet opening beat.", durationSeconds = 20 });
        var incomplete = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "The street waits." });
        Assert.False(incomplete.Readiness.Ready);
        Assert.Contains("Shot purpose", incomplete.Readiness.Missing);
        Assert.Contains("Expected duration", incomplete.Readiness.Missing);
        var blockedGeneration = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/shots/{incomplete.Id}/generate", new { });
        Assert.Equal(HttpStatusCode.BadRequest, blockedGeneration.StatusCode);

        var ready = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "The lead crosses into the pool of morning light.",
            purpose = "Introduce the lead and establish the morning mood.",
            subjects = "Mara, carrying a canvas bag",
            locationSet = "Old city street set",
            durationSeconds = 8,
            productionRequirements = "Canvas bag; restrained crossing performance.",
            continuityReferences = "Guide: cool dawn palette; follows the empty street.",
            cameraAndFraming = "Medium-wide, eye level",
        });
        Assert.True(ready.Readiness.Ready);
        Assert.Equal("ReadyForStoryboard", ready.PlanState);

        var reordered = await SendWithCsrf<MovieSceneShotPlanDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{ready.Id}/reorder", new { sequence = 1 });
        Assert.Equal(ready.Id, reordered.Shots[0].Id);
        Assert.Equal(2, reordered.ShotCount);
        Assert.Equal(1, reordered.ReadyShotCount);

        var archived = await SendWithCsrf<MovieSceneShotPlanDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{incomplete.Id}/archive", null);
        Assert.Equal(1, archived.ActiveShotCount);
        Assert.Equal("Archived", archived.Shots.Single(item => item.Id == incomplete.Id).Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Shot_edit_and_generation_respect_collaboration_boundaries()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Planning Owner");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id);
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Interior", summary = "A tense exchange." });
        var shot = await SendWithCsrf<MovieShotDto>(owner, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Two figures face each other." });

        using var writer = factory.CreateClient();
        var writerAuth = await Register(writer, "Planning Viewer");
        await AddWorkspaceMember(movie.Project.WorkspaceId, writerAuth.User.Id);
        await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/team", new
        {
            userId = writerAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Edit, granted = false } },
        });

        var edit = await SendWithCsrf(writer, HttpMethod.Patch, $"/api/movie-studio/shots/{shot.Id}", new { description = "Attempted edit." });
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        var generation = await SendWithCsrf(writer, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/generate", new { });
        Assert.Equal(HttpStatusCode.NotFound, generation.StatusCode);
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-shot-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId)
    {
        return await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = MovieProjectModes.Full,
            title = "Shot planning test",
            description = "A durable shot planning test movie.",
            durationSeconds = 60,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
    }

    private async Task AddWorkspaceMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
