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

public sealed class MovieShotExecutionApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieShotExecutionApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Approved_keyframe_execution_queues_independent_takes_with_server_shot_contract()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var (_, shot) = await CreateShot(client, auth.PersonalWorkspace.Id, "9:16", 7);
        var keyframe = await CreateApprovedKeyframe(client, shot.Id);

        var response = await SendWithCsrf<MovieShotExecutionResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute", new
        {
            keyframeVersionId = keyframe.Id,
            sourceResolution = "720p",
            masterResolution = "1080p",
            processingPath = "source_to_master",
            takeCount = 3,
            qualityLevel = MovieQualityLevels.Standard,
        }, "shot-execution-batch-1");

        Assert.Equal(3, response.Candidates.Count);
        Assert.Equal(7, response.DurationSeconds);
        Assert.Equal("9:16", response.AspectRatio);
        Assert.Equal("720p", response.Resolution.SourceResolution);
        Assert.Equal("1080p", response.Resolution.MasterResolution);
        Assert.Equal("source_to_master", response.Resolution.ProcessingPath);
        Assert.False(response.Resolution.UpscalingRequested);
        Assert.Equal(new[] { 1, 2, 3 }, response.Candidates.Select(item => item.TakeNumber));
        Assert.All(response.Candidates, item => Assert.Equal(GenerationJobStatus.Queued.ToString(), item.Job.Status));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var takes = await db.MovieTakes.Where(item => item.MovieShotId == shot.Id).OrderBy(item => item.VersionNumber).ToListAsync();
        Assert.Equal(3, takes.Count);
        Assert.All(takes, item => Assert.Null(item.SelectedAt));
        Assert.All(takes, item => Assert.Null(item.FinalizedAt));
        var persistedShot = await db.MovieShots.SingleAsync(item => item.Id == shot.Id);
        Assert.Null(persistedShot.SelectedTakeId);
        Assert.Null(persistedShot.FinalTakeId);
        Assert.Equal(3, await db.GenerationJobs.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id && item.JobType == GenerationJobTypes.MovieClipGenerate && item.InputJson.Contains(shot.Id.ToString())));
        Assert.Equal(0, await db.MovieVideoProviderExecutions.CountAsync(item => item.MovieClip.MovieShotId == shot.Id));

        var firstJob = await db.GenerationJobs.AsNoTracking().SingleAsync((GenerationJob item) => item.Id == response.Candidates[0].GenerationJobId);
        using var input = JsonDocument.Parse(firstJob.InputJson);
        Assert.Equal(7, input.RootElement.GetProperty("DurationSeconds").GetInt32());
        Assert.Equal("9:16", input.RootElement.GetProperty("AspectRatio").GetString());
        Assert.Equal(keyframe.Id.ToString(), input.RootElement.GetProperty("SourceProductionVersionId").GetString());
        Assert.Equal("720p", input.RootElement.GetProperty("SourceResolution").GetString());
        Assert.Equal("1080p", input.RootElement.GetProperty("MasterResolution").GetString());
        Assert.Equal("source_to_master", input.RootElement.GetProperty("ProcessingPath").GetString());
        Assert.False(input.RootElement.GetProperty("UpscalingRequested").GetBoolean());
        Assert.Contains(keyframe.Id.ToString(), input.RootElement.GetProperty("ReferencePackageJson").GetString());
        using var shotSnapshot = JsonDocument.Parse(input.RootElement.GetProperty("ShotJson").GetString()!);
        Assert.Equal("hallway-a", shotSnapshot.RootElement.GetProperty("screenDirection").GetProperty("Axis").GetString());
        using var referencePackage = JsonDocument.Parse(input.RootElement.GetProperty("ReferencePackageJson").GetString()!);
        Assert.Equal(2, referencePackage.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task Execution_idempotency_replays_the_same_candidate_batch_without_new_takes()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var (_, shot) = await CreateShot(client, auth.PersonalWorkspace.Id, "16:9", 5);
        var keyframe = await CreateApprovedKeyframe(client, shot.Id);
        var payload = new
        {
            keyframeVersionId = keyframe.Id,
            sourceResolution = "1080p",
            masterResolution = "1080p",
            processingPath = "native",
            takeCount = 2,
        };

        var first = await SendWithCsrf<MovieShotExecutionResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, "shot-execution-idempotent");
        var replay = await SendWithCsrf<MovieShotExecutionResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, "shot-execution-idempotent");

        Assert.Equal(first.Candidates.Select(item => item.TakeId), replay.Candidates.Select(item => item.TakeId));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(2, await db.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(2, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey != null && item.CreatedByUserId == auth.User.Id));
    }

    [Fact]
    public async Task Execution_rejects_reusing_a_key_for_a_different_resolution_plan()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var (_, shot) = await CreateShot(client, auth.PersonalWorkspace.Id, "16:9", 5);
        var keyframe = await CreateApprovedKeyframe(client, shot.Id);
        const string idempotencyKey = "shot-execution-plan-reuse";

        var first = await SendWithCsrf<MovieShotExecutionResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute", new
        {
            keyframeVersionId = keyframe.Id,
            sourceResolution = "1080p",
            masterResolution = "1080p",
            processingPath = "native",
            takeCount = 2,
        }, idempotencyKey);
        Assert.Equal(2, first.Candidates.Count);

        using var reused = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute", new
        {
            keyframeVersionId = keyframe.Id,
            sourceResolution = "720p",
            masterResolution = "1080p",
            processingPath = "source_to_master",
            takeCount = 2,
        }, idempotencyKey);

        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        using var body = JsonDocument.Parse(await reused.Content.ReadAsStringAsync());
        Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_REUSED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Execution_rejects_unapproved_keyframes_and_invalid_native_resolution_plans()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var (_, shot) = await CreateShot(client, auth.PersonalWorkspace.Id, "16:9", 4);
        var storyboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new
        {
            stage = MovieProductionStages.StoryboardCandidate,
            compositionJson = "{\"frame\":\"candidate\"}",
        });
        await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true, reason = "Source approved." });
        var invalidKeyframe = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/versions", new
        {
            stage = MovieProductionStages.ProductionKeyframe,
            sourceVersionId = storyboard.Id,
            compositionJson = "{\"frame\":\"keyframe\"}",
        });

        using var notApproved = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute", new
        {
            keyframeVersionId = invalidKeyframe.Id,
            takeCount = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, notApproved.StatusCode);
        using (var body = JsonDocument.Parse(await notApproved.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_KEYFRAME_NOT_APPROVED", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{invalidKeyframe.Id}/review", new { approve = true });
        using var invalidPlan = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute", new
        {
            keyframeVersionId = invalidKeyframe.Id,
            sourceResolution = "720p",
            masterResolution = "1080p",
            processingPath = "native",
            takeCount = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPlan.StatusCode);
        using var invalidBody = JsonDocument.Parse(await invalidPlan.Content.ReadAsStringAsync());
        Assert.Equal("SHOT_EXECUTION_NATIVE_RESOLUTION_MISMATCH", invalidBody.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<(MovieStudioProjectResponse Project, MovieShotDto Shot)> CreateShot(HttpClient client, Guid workspaceId, string aspectRatio, int durationSeconds)
    {
        var project = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = MovieProjectModes.Quick,
            title = "Shot execution test",
            description = "A disabled-provider execution test.",
            durationSeconds = 20,
            aspectRatio,
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new { title = "Scene", summary = "Execution scene." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "The production shot.", durationSeconds,
            screenDirection = new { axis = "hallway-a", orientation = "side_a", entranceDirection = "screen_left", exitDirection = "screen_right" },
        });
        return (project, shot);
    }

    private async Task<MovieProductionVersionDto> CreateApprovedKeyframe(HttpClient client, Guid shotId)
    {
        var storyboard = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/versions", new
        {
            stage = MovieProductionStages.StoryboardCandidate,
            compositionJson = "{\"frame\":\"approved\"}",
        });
        await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{storyboard.Id}/review", new { approve = true, reason = "Composition approved." });
        var keyframe = await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/versions", new
        {
            stage = MovieProductionStages.ProductionKeyframe,
            sourceVersionId = storyboard.Id,
            compositionJson = "{\"frame\":\"approved-keyframe\"}",
        });
        return await SendWithCsrf<MovieProductionVersionDto>(client, HttpMethod.Post, $"/api/movie-studio/production/versions/{keyframe.Id}/review", new { approve = true, reason = "Keyframe approved." });
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Shot Execution Tester",
            email = $"shot-execution-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload, string? idempotencyKey = null)
    {
        using var response = await SendWithCsrf(client, method, path, payload, idempotencyKey);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
