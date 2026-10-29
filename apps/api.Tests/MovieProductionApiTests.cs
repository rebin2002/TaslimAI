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
    public async Task Production_rejects_another_members_private_unassigned_asset()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(owner, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Private production asset",
            description = "Production must not cross private asset boundaries.",
            durationSeconds = 20,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Opening", summary = "A protected opening." });
        var shot = await SendWithCsrf<MovieShotDto>(owner, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "A private-reference shot." });

        using var privateOwner = factory.CreateClient();
        var privateOwnerAuth = await Register(privateOwner);
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, privateOwnerAuth.User.Id);
        var privateFileId = Guid.NewGuid();
        var privateAssetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.StoredFiles.Add(new StoredFile
            {
                Id = privateFileId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = privateOwnerAuth.User.Id,
                OriginalFileName = "private-reference.png",
                StoredFileName = "private-reference.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = 128,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"test/{privateFileId:N}",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
            });
            db.Assets.Add(new Asset
            {
                Id = privateAssetId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = privateOwnerAuth.User.Id,
                StoredFileId = privateFileId,
                Name = "Private reference",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(owner, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new
        {
            stage = MovieProductionStages.StoryboardCandidate,
            label = "Should reject private reference",
            compositionJson = "{}",
            assetId = privateAssetId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PRODUCTION_ASSET_SCOPE_INVALID", error.GetProperty("error").GetProperty("code").GetString());
        using var verification = factory.Services.CreateScope();
        Assert.False(await verification.ServiceProvider.GetRequiredService<TaslimDbContext>().MovieProductionVersions.AnyAsync(item => item.MovieShotId == shot.Id));
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
        // An approved production render needs an explicitly capable, provider-free
        // adapter; the default unconfigured provider now correctly fails closed.
        using var fakeFactory = new MovieOperationalNoWorkerFactory();
        using (var init = fakeFactory.Services.CreateScope())
            init.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = fakeFactory.CreateClient();
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

        const string replayKey = "approved-production-creative-intent";
        var renderPath = $"/api/movie-studio/shots/{shot.Id}/production/render";
        var renderPayload = new { sourceVersionId = approvedMotion.Id, label = "Explicit render" };
        using var firstResponse = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, renderPayload, idempotencyKey: replayKey);
        Assert.True(firstResponse.IsSuccessStatusCode, await firstResponse.Content.ReadAsStringAsync());
        var render = await firstResponse.Content.ReadFromJsonAsync<MovieProductionRenderResponse>();
        Assert.NotNull(render);
        Assert.Equal(render!.Job.Id, render.Version.GenerationJobId);
        Assert.NotEqual(Guid.Empty, render.ClipId);

        var production = await client.GetFromJsonAsync<MovieShotProductionDto>($"/api/movie-studio/shots/{shot.Id}/production");
        Assert.NotNull(production);
        Assert.Contains(production!.Versions, item => item.Stage == MovieProductionStages.ProductionRender && item.GenerationJobId == render.Job.Id);
        Assert.Empty(production.Takes);
        using var scope = fakeFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));

        // An approved production render must durably bind its queued job to
        // the provider selected at admission. If runtime routing changes
        // before the worker starts, that job must not be sent elsewhere.
        var queuedJob = await db.GenerationJobs.AsNoTracking()
            .SingleAsync(item => item.Id == render.Job.Id);
        var queuedInput = JsonSerializer.Deserialize<MovieGenerationInput>(queuedJob.InputJson);
        Assert.NotNull(queuedInput);
        Assert.Equal(scope.ServiceProvider.GetRequiredService<IMovieVideoProvider>().Key,
            queuedInput.SelectedProviderKey);
        // The previously captured character bible must travel with the job,
        // not merely live on the clip record where the provider cannot see it.
        var queuedClip = await db.MovieClips.AsNoTracking()
            .SingleAsync(item => item.Id == render.ClipId);
        Assert.False(string.IsNullOrWhiteSpace(queuedInput.CharacterContinuityJson));
        Assert.Equal(queuedClip.ContinuitySnapshotJson, queuedInput.CharacterContinuityJson);

        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shot.Id);
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieShotId == shot.Id);
        var ledgerBefore = await db.UsageTransactions.CountAsync();
        using var replayResponse = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, renderPayload, idempotencyKey: replayKey);
        Assert.True(replayResponse.IsSuccessStatusCode, await replayResponse.Content.ReadAsStringAsync());
        var replay = await replayResponse.Content.ReadFromJsonAsync<MovieProductionRenderResponse>();
        Assert.NotNull(replay);
        Assert.Equal(render.Job.Id, replay!.Job.Id);
        Assert.Equal(render.ClipId, replay.ClipId);

        // A restored/reconfigured provider route cannot silently replay a
        // canonical production job that was priced for another adapter.
        // Exercise provider identity drift without outbound video requests.
        var providerBoundJob = await db.GenerationJobs.SingleAsync(item => item.Id == render.Job.Id);
        var originalInputJson = providerBoundJob.InputJson;
        providerBoundJob.InputJson = JsonSerializer.Serialize(queuedInput with
        {
            SelectedProviderKey = "other-video-adapter",
        });
        await db.SaveChangesAsync();
        using var changedProvider = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, renderPayload, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedProvider.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedProvider.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Older jobs without adapter identity continue to replay their
        // original canonical clip/job; this is a compatibility-only exception.
        providerBoundJob.InputJson = JsonSerializer.Serialize(queuedInput with
        {
            SelectedProviderKey = null,
        });
        await db.SaveChangesAsync();
        using var legacyReplay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, renderPayload, idempotencyKey: replayKey);
        Assert.True(legacyReplay.IsSuccessStatusCode,
            await legacyReplay.Content.ReadAsStringAsync());
        var legacyResult = await legacyReplay.Content.ReadFromJsonAsync<MovieProductionRenderResponse>();
        Assert.NotNull(legacyResult);
        Assert.Equal(render.Job.Id, legacyResult!.Job.Id);
        Assert.Equal(render.ClipId, legacyResult.ClipId);
        providerBoundJob.InputJson = originalInputJson;
        await db.SaveChangesAsync();

        // The same replay key cannot silently adopt a differently priced
        // output profile or a different creative title. All these negative
        // cases run against the already queued canonical job; no provider is invoked.
        using var changedResolution = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new { sourceVersionId = approvedMotion.Id, label = "Explicit render",
                targetResolution = "720p", qualityTier = DirectorQualityLevels.Standard },
            idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedResolution.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedResolution.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var changedQuality = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new { sourceVersionId = approvedMotion.Id, label = "Explicit render",
                targetResolution = "1080p", qualityTier = DirectorQualityLevels.Cinematic },
            idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedQuality.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedQuality.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var changedCreativeTitle = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new { sourceVersionId = approvedMotion.Id, label = "Explicit render",
                title = "Different creative request" },
            idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedCreativeTitle.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedCreativeTitle.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A changed label, shot direction or duration cannot reuse an already
        // priced canonical job, even if the approved source and key stay equal.
        using var changedLabel = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new { sourceVersionId = approvedMotion.Id, label = "Different render" },
            idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedLabel.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedLabel.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var shotRow = await db.MovieShots.SingleAsync(item => item.Id == shot.Id);
        var originalDescription = shotRow.Description;
        var originalDuration = shotRow.DurationSeconds;
        shotRow.Description = "Different shot direction after the canonical render was queued.";
        await db.SaveChangesAsync();
        using var changedDirection = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, renderPayload, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedDirection.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedDirection.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        shotRow.Description = originalDescription;
        shotRow.DurationSeconds = 25;
        await db.SaveChangesAsync();
        using var changedDuration = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, renderPayload, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedDuration.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedDuration.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Restore the original shot content: a subsequent continuity-card
        // edit must be the only changed dimension for this replay attempt.
        shotRow.DurationSeconds = originalDuration;
        await db.SaveChangesAsync();
        var continuity = scope.ServiceProvider.GetRequiredService<IMovieCharacterContinuityService>();
        var originalContinuity = await continuity.BuildSnapshotForTargetAsync(
            project.Project.Id, scene.Id, shot.Id, persist: false);
        Assert.Equal(queuedInput.CharacterContinuityJson, originalContinuity.SnapshotJson);

        // The name is present in the scene's original shot direction, so the
        // new character card becomes relevant to the immutable render prompt.
        db.MovieCharacters.Add(new MovieCharacter
        {
            Id = Guid.NewGuid(),
            MovieProjectId = project.Project.Id,
            Name = "Street",
            Description = "A new character's persistent wardrobe and features.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var revisedContinuity = await continuity.BuildSnapshotForTargetAsync(
            project.Project.Id, scene.Id, shot.Id, persist: false);
        Assert.NotEqual(queuedInput.CharacterContinuityJson, revisedContinuity.SnapshotJson);

        using var changedCharacter = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, renderPayload, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedCharacter.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedCharacter.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == replayKey));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }
    [Fact]
    public async Task Approved_production_render_rejects_unsupported_clip_duration_without_creating_billable_work()
    {
        // Run the real HTTP queue path against a bounded in-memory provider.
        // This must be rejected before continuity snapshots, clips, jobs, or usage reservations.
        using var boundedFactory = new MovieClipDurationApiFactory();
        using (var setup = boundedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = boundedFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Approved production duration admission", sceneDurationSeconds: 11);
        var shotId = fixture.Shot.Id;

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = true });
        var motion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/motion-preview",
            new { sourceVersionId = approvedKeyframe.Id });
        var approvedMotion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{motion.Id}/review", new { approve = true });

        using var scope = boundedFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId);
        var transactionsBefore = await db.UsageTransactions.CountAsync();
        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/render",
            new { sourceVersionId = approvedMotion.Id, label = "Unsupported production clip",
                confirmationAccepted = true }, idempotencyKey: "production-duration-11");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "production-duration-11"));
        Assert.Equal(transactionsBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Approved_production_render_rejects_unexecutable_priced_profile_without_billable_work()
    {
        // Use the real approved-render HTTP flow with an adapter that explicitly
        // refuses the requested priced execution profile. No provider is invoked.
        using var denyingFactory = new MovieClipIntentDeniedApiFactory();
        using (var setup = denyingFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = denyingFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Approved render priced profile admission", sceneDurationSeconds: 5);
        var shotId = fixture.Shot.Id;

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = true });
        var motion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/motion-preview",
            new { sourceVersionId = approvedKeyframe.Id });
        var approvedMotion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{motion.Id}/review", new { approve = true });

        using var scope = denyingFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId);
        var transactionsBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/render",
            new { sourceVersionId = approvedMotion.Id, targetResolution = "1080p",
                qualityTier = DirectorQualityLevels.Cinematic, confirmationAccepted = true },
            idempotencyKey: "production-priced-intent-rejected");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "production-priced-intent-rejected"));
        Assert.Equal(transactionsBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Selective_regeneration_requires_confirmation_preserves_history_and_invalidates_downstream()
    {
        // The positive execution path needs an explicitly capable synthetic adapter.
        // Real Runway/Manus are not asserted to honor 1080p regeneration intent.
        using var fakeFactory = new MovieOperationalNoWorkerFactory();
        using (var init = fakeFactory.Services.CreateScope())
            init.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = fakeFactory.CreateClient();
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

        using (var beforeConfirmation = fakeFactory.Services.CreateScope())
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

        using var scope = fakeFactory.Services.CreateScope();
        var dbAfter = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await dbAfter.GenerationJobs.CountAsync(item => item.Id == confirmed.Job.Id));
        Assert.Equal(3, await dbAfter.MovieProductionVersions.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(MovieProductionVersionStatuses.ReviewRequired, await dbAfter.MovieProductionVersions.Where(item => item.Id == downstream.Id).Select(item => item.Status).SingleAsync());
        Assert.Contains(await dbAfter.MovieProductionStageTransitions.Where(item => item.MovieShotId == shot.Id).ToListAsync(), item => item.EventType == "invalidated" && item.MovieProductionVersionId == downstream.Id);
        var job = await dbAfter.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == confirmed.Job.Id);
        using var input = JsonDocument.Parse(job.InputJson);
        Assert.Equal(preview.Request.Id.ToString(), input.RootElement.GetProperty("SelectiveRegenerationId").GetString());
        Assert.Contains("characterContinuity", input.RootElement.GetProperty("ChangedInputsJson").GetString());
        Assert.Equal("1080p", input.RootElement.GetProperty("SourceResolution").GetString());
        Assert.Equal("1080p", input.RootElement.GetProperty("TargetResolution").GetString());
        Assert.Equal("native", input.RootElement.GetProperty("ProcessingPath").GetString());
        Assert.False(string.IsNullOrWhiteSpace(input.RootElement.GetProperty("QualityTier").GetString()));
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
