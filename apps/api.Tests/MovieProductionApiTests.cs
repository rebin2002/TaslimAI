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

        var approvedStoryboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{candidate.Id}/review", new { approve = true, reason = "Composition approved." });
        Assert.Equal(MovieProductionStages.ApprovedStoryboard, approvedStoryboard.Stage);
        Assert.Equal(MovieProductionVersionStatuses.Approved, approvedStoryboard.Status);

        var keyframe = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new
        {
            stage = MovieProductionStages.ProductionKeyframe,
            sourceVersionId = candidate.Id,
            compositionJson = "{\"frame\":\"approved-keyframe\"}",
        });
        Assert.Equal(MovieProductionStages.ProductionKeyframe, keyframe.Stage);
        Assert.Equal(candidate.Id, keyframe.SourceVersionId);

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
    public async Task Selective_regeneration_requires_confirmation_preserves_history_and_invalidates_downstream()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id, mode = MovieProjectModes.Quick, title = "Selective wave", description = "Regenerate one shot only.", durationSeconds = 24, aspectRatio = "16:9", style = "cinematic", language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Dawn", summary = "A quiet opening beat." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Wide shot of the empty street." });
        var storyboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"layout\":\"left\"}" });
        await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true, reason = "Approved composition." });
        var downstream = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.ProductionKeyframe, sourceVersionId = storyboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
        downstream = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{downstream.Id}/review", new { approve = true, reason = "Approved keyframe." });

        using (var beforeConfirmation = factory.Services.CreateScope())
        {
            var db = beforeConfirmation.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
        }
        var preview = await SendWithCsrf<MovieSelectiveRegenerationResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/regeneration-requests", new
        {
            actionType = MovieRegenerationActionTypes.ProductionKeyframeCandidate,
            requestedStage = MovieProductionStages.ProductionKeyframe,
            reason = "The character silhouette needs the approved continuity update.",
            sourceVersionId = storyboard.Id,
            changedInputsJson = "{\"characterContinuity\":\"updated\"}",
            compositionJson = "{\"frame\":\"updated\"}",
            estimatedProviderCostUsd = 0.25m,
        });
        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation, preview.Request.Status);
        Assert.True(preview.Request.CostPreview.ConfirmationRequired);
        Assert.Null(preview.Job);

        var notConfirmed = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm", new { confirm = false });
        Assert.Equal(HttpStatusCode.BadRequest, notConfirmed.StatusCode);

        var confirmed = await SendWithCsrf<MovieSelectiveRegenerationResponse>(client, HttpMethod.Post, $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm", new { confirm = true });
        Assert.Equal(MovieRegenerationStatuses.Confirmed, confirmed.Request.Status);
        Assert.NotNull(confirmed.Job);
        Assert.NotNull(confirmed.ProductionVersion);
        Assert.NotNull(confirmed.Take);
        Assert.Equal(GenerationJobStatus.Queued.ToString(), confirmed.Job!.Status);
        Assert.Equal(MovieTakeStatuses.Generating, confirmed.Take!.Status);

        using var scope = factory.Services.CreateScope();
        var dbAfter = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await dbAfter.GenerationJobs.CountAsync(item => item.Id == confirmed.Job.Id));
        Assert.Equal(3, await dbAfter.MovieProductionVersions.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(MovieProductionVersionStatuses.ReviewRequired, await dbAfter.MovieProductionVersions.Where(item => item.Id == downstream.Id).Select(item => item.Status).SingleAsync());
        Assert.Contains(await dbAfter.MovieProductionStageTransitions.Where(item => item.MovieShotId == shot.Id).ToListAsync(), item => item.EventType == "invalidated" && item.MovieProductionVersionId == downstream.Id);
        var job = await dbAfter.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == confirmed.Job.Id);
        using var input = JsonDocument.Parse(job.InputJson);
        Assert.Equal(preview.Request.Id.ToString(), input.RootElement.GetProperty("SelectiveRegenerationId").GetString());
        Assert.Contains("characterContinuity", input.RootElement.GetProperty("ChangedInputsJson").GetString());
        Assert.Equal(0, await dbAfter.MovieVideoProviderExecutions.CountAsync(item => item.GenerationJobId == confirmed.Job.Id));
    }

    [Fact]
    public async Task Selective_regeneration_is_target_and_workspace_isolated()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(owner, HttpMethod.Post, "/api/movie-studio/projects", new { workspaceId = auth.PersonalWorkspace.Id, mode = MovieProjectModes.Quick, title = "Private selective", description = "Target isolation.", durationSeconds = 12, aspectRatio = "16:9", style = "cinematic", language = "en" });
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "One", summary = "One shot." });
        var shot = await SendWithCsrf<MovieShotDto>(owner, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "The target shot." });

        using var outsider = factory.CreateClient();
        await Register(outsider);
        var response = await SendWithCsrf(outsider, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/regeneration-requests", new
        {
            actionType = MovieRegenerationActionTypes.Cinematography, requestedStage = MovieProductionStages.StoryboardCandidate,
            reason = "Unauthorized direction change.", changedInputsJson = "{\"lens\":\"50mm\"}", compositionJson = "{}",
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().MovieRegenerationRequests.AnyAsync(item => item.MovieShotId == shot.Id));
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
