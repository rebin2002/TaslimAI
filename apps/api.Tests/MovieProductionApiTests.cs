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

public sealed class MovieProductionApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieProductionApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Storyboard_first_flow_persists_provenance_and_does_not_create_a_video_job()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Quick,
            title = "Storyboard first",
            description = "Approve composition before motion.",
            durationSeconds = 24,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Dawn", summary = "A quiet opening beat." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Wide shot of the empty street.", cameraAndFraming = "24mm wide" });

        var candidate = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new
        {
            stage = MovieProductionStages.StoryboardCandidate,
            label = "Composition A",
            compositionJson = "{\"blocking\":\"subject left\",\"lens\":\"24mm\"}",
            regenerationMetadataJson = "{\"reason\":\"initial composition\",\"preserve\":[\"subject placement\"]}",
            stageProvenanceJson = "{\"source\":\"shot-plan\",\"createdBy\":\"planner\"}",
            firstFrameNotes = "Begin before the subject enters.",
            lastFrameNotes = "Hold on the empty street.",
        });
        Assert.Equal(MovieProductionStages.StoryboardCandidate, candidate.Stage);
        Assert.Equal(MovieProductionVersionStatuses.PendingApproval, candidate.Status);
        Assert.Null(candidate.GenerationJobId);
        Assert.Contains(shot.Id.ToString(), candidate.ContinuitySnapshotReferenceJson);
        Assert.Contains("24mm wide", candidate.CinematographyReferenceJson);

        var approvedStoryboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{candidate.Id}/review", new { approve = true, reason = "Composition approved." });
        Assert.Equal(MovieProductionStages.ApprovedStoryboard, approvedStoryboard.Stage);
        Assert.Equal(MovieProductionVersionStatuses.Approved, approvedStoryboard.Status);

        var keyframe = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new
        {
            stage = MovieProductionStages.ProductionKeyframe,
            sourceVersionId = candidate.Id,
            compositionJson = "{\"frame\":\"approved-keyframe\"}",
            firstFrameNotes = "Start on the empty street.",
            lastFrameNotes = "End on the subject entering frame.",
        });
        Assert.Equal(MovieProductionStages.ProductionKeyframe, keyframe.Stage);
        Assert.Equal(candidate.Id, keyframe.SourceVersionId);
        Assert.Equal("Start on the empty street.", keyframe.FirstFrameNotes);
        Assert.Equal("End on the subject entering frame.", keyframe.LastFrameNotes);
        Assert.Contains(shot.Id.ToString(), keyframe.ContinuitySnapshotReferenceJson);

        var rejected = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = false, reason = "Lighting needs another pass." });
        Assert.Equal(MovieProductionVersionStatuses.Rejected, rejected.Status);
        Assert.Equal("Lighting needs another pass.", rejected.RejectionReason);

        var production = await client.GetFromJsonAsync<MovieShotProductionDto>($"/api/movie-studio/shots/{shot.Id}/production");
        Assert.NotNull(production);
        Assert.Equal(MovieProductionStages.ApprovedStoryboard, production!.CurrentStage);
        Assert.Equal(2, production.Versions.Count);
        Assert.Contains(production.Transitions, item => item.EventType == "approved" && item.ToStage == MovieProductionStages.ApprovedStoryboard);
        Assert.Contains(production.Transitions, item => item.EventType == "rejected" && item.Reason == "Lighting needs another pass.");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Equal(2, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shot.Id));
    }

    [Fact]
    public async Task Production_mutations_require_movie_team_edit_and_approval_permissions()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(owner, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Production authorization",
            description = "Team permission gate.",
            durationSeconds = 24,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Gate", summary = "A permission boundary." });
        var shot = await SendWithCsrf<MovieShotDto>(owner, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "A protected shot." });

        using var writer = factory.CreateClient();
        var writerAuth = await Register(writer);
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, writerAuth.User.Id);
        await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/collaboration/team", new
        {
            userId = writerAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
            permissionOverrides = new[] { new { permission = MoviePermissions.Edit, granted = false } },
        });

        var forbiddenCreate = await SendWithCsrf(writer, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{}" });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenCreate.StatusCode);

        var candidate = await SendWithCsrf<MovieProductionVersionDto>(owner, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{}" });
        var forbiddenReview = await SendWithCsrf(writer, HttpMethod.Post, $"/api/movie-studio/production/versions/{candidate.Id}/review", new { approve = true });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenReview.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Production Tester",
            email = $"production-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddWorkspaceMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
