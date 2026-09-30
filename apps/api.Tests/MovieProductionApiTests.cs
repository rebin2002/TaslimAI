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

        using (var storyboardResponse = await client.GetAsync($"/api/movie-studio/projects/{project.Project.Id}/storyboard"))
        {
            storyboardResponse.EnsureSuccessStatusCode();
            using var storyboardJson = JsonDocument.Parse(await storyboardResponse.Content.ReadAsStringAsync());
            Assert.True(storyboardJson.RootElement.TryGetProperty("scenes", out var scenes));
            Assert.Contains(scenes.EnumerateArray(), item => item.GetProperty("title").GetString() == "Dawn");
            Assert.False(storyboardJson.RootElement.TryGetProperty("characters", out _));
            Assert.False(storyboardJson.RootElement.TryGetProperty("locations", out _));
        }

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
    public async Task Keyframe_command_uses_fake_safe_image_job_locks_approval_selects_and_preserves_regeneration_history()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id, mode = MovieProjectModes.Quick, title = "Keyframe workflow",
            description = "Storyboard to keyframe without external providers.", durationSeconds = 24,
            aspectRatio = "16:9", style = "cinematic", language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Dawn", summary = "A controlled opening." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "A wide street at first light." });
        var storyboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"blocking\":\"left\"}" });
        var approvedStoryboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true });
        Assert.True(approvedStoryboard.IsLocked);

        var first = await SendWithCsrf<MovieKeyframeGenerationResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/keyframe", new
        {
            sourceStoryboardVersionId = approvedStoryboard.Id, label = "Keyframe one",
        });
        Assert.Equal(MovieProductionStages.ProductionKeyframe, first.Version.Stage);
        Assert.Equal(approvedStoryboard.Id, first.Version.SourceVersionId);
        Assert.Equal(GenerationJobTypes.ImageGenerate, first.Job.JobType);
        Assert.Equal(GenerationJobStatus.Queued.ToString(), first.Job.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var imageJob = await db.GenerationJobs.SingleAsync(item => item.Id == first.Job.Id);
            Assert.Contains("ProductionVersionId", imageJob.InputJson);
            Assert.DoesNotContain("provider", imageJob.InputJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model", imageJob.InputJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", imageJob.InputJson, StringComparison.OrdinalIgnoreCase);
        }

        var approvedKeyframe = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{first.Version.Id}/review", new { approve = true, reason = "Lock this keyframe." });
        Assert.Equal(MovieProductionStages.ApprovedKeyframe, approvedKeyframe.Stage);
        Assert.True(approvedKeyframe.IsLocked);
        var selected = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{first.Version.Id}/select-keyframe", null);
        Assert.True(selected.IsSelected);

        var regenerated = await SendWithCsrf<MovieKeyframeGenerationResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/keyframe", new
        {
            sourceStoryboardVersionId = approvedStoryboard.Id, label = "Keyframe two", compositionJson = "{\"blocking\":\"right\"}",
            regenerationMetadataJson = "{\"reason\":\"continuity pass\"}",
        });
        Assert.NotEqual(first.Version.Id, regenerated.Version.Id);
        Assert.Equal(first.Version.Id, selected.Id);

        var production = await client.GetFromJsonAsync<MovieShotProductionDto>($"/api/movie-studio/shots/{shot.Id}/production");
        Assert.NotNull(production);
        Assert.Equal(first.Version.Id, production!.SelectedKeyframeVersionId);
        Assert.Equal(first.Version.Id, production.Versions.Single(item => item.IsSelected).Id);
        Assert.Contains(production.Versions, item => item.Id == regenerated.Version.Id && item.Status == MovieProductionVersionStatuses.PendingApproval);
        Assert.Contains(production.Transitions, item => item.EventType == "selected" && item.MovieProductionVersionId == first.Version.Id);
        using var historyScope = factory.Services.CreateScope();
        var historyDb = historyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(3, await historyDb.MovieProductionVersions.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(2, await historyDb.GenerationJobs.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Production_review_requires_approve_permission_and_keeps_candidate_history()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(owner, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Approval boundary",
            description = "A storyboard approval security test.",
            durationSeconds = 20,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Opening", summary = "A controlled opening." });
        var shot = await SendWithCsrf<MovieShotDto>(owner, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "A locked-off opening frame." });
        var candidate = await SendWithCsrf<MovieProductionVersionDto>(owner, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{}" });

        using var reviewer = factory.CreateClient();
        var reviewerAuth = await Register(reviewer);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = ownerAuth.PersonalWorkspace.Id, UserId = reviewerAuth.User.Id, Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow });
            db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = project.Project.Id, UserId = reviewerAuth.User.Id, Role = MovieTeamRoles.Writer, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using var forbidden = await SendWithCsrf(reviewer, HttpMethod.Post, $"/api/movie-studio/production/versions/{candidate.Id}/review", new { approve = true, reason = "Should be forbidden." });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var production = await owner.GetAsync($"/api/movie-studio/shots/{shot.Id}/production");
        var productionBody = await production.Content.ReadFromJsonAsync<MovieShotProductionDto>();
        Assert.NotNull(productionBody);
        Assert.Single(productionBody!.Versions);
        Assert.Equal(MovieProductionVersionStatuses.PendingApproval, productionBody.Versions[0].Status);
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
    [Fact]
    public async Task Production_render_is_explicit_and_does_not_collapse_into_a_movie_take()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Quick,
            title = "Explicit production",
            description = "A render should require a deliberate action.",
            durationSeconds = 24,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Dawn", summary = "A quiet opening beat." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "A locked-off street wide shot." });
        var candidate = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{candidate.Id}/review", new { approve = true });
        var keyframe = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new { stage = MovieProductionStages.ProductionKeyframe, sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"keyframe\"}" });
        var approvedKeyframe = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = true });
        var motion = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/motion-preview", new { sourceVersionId = approvedKeyframe.Id });
        var approvedMotion = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{motion.Id}/review", new { approve = true });

        var render = await SendWithCsrf<MovieProductionRenderResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/render", new { sourceVersionId = approvedMotion.Id, label = "Explicit render" });
        Assert.Equal(render.Job.Id, render.Version.GenerationJobId);
        Assert.NotEqual(Guid.Empty, render.ClipId);

        var production = await client.GetFromJsonAsync<MovieShotProductionDto>($"/api/movie-studio/shots/{shot.Id}/production");
        Assert.NotNull(production);
        Assert.Contains(production!.Versions, item => item.Stage == MovieProductionStages.ProductionRender && item.GenerationJobId == render.Job.Id);
        Assert.Empty(production.Takes);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
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

    private async Task AddWorkspaceMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
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
