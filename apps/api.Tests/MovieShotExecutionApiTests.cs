using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotExecutionPostCommitFailureGate
{
    private int failNext;

    public void Arm() => Interlocked.Exchange(ref failNext, 1);
    public bool ConsumeFailure() => Interlocked.Exchange(ref failNext, 0) == 1;
}

public sealed class ThrowAfterCommitMovieShotGenerationJobService(
    GenerationJobService inner,
    MovieShotExecutionPostCommitFailureGate gate) : IGenerationJobService
{
    public async Task<GenerationJob> CreateAsync(
        Guid userId,
        CreateGenerationJobRequest request,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null,
        string? requestId = null,
        Guid? retryOfJobId = null)
    {
        var job = await inner.CreateAsync(userId, request, cancellationToken, idempotencyKey, requestId, retryOfJobId);
        if (request.JobType == GenerationJobTypes.MovieClipGenerate && gate.ConsumeFailure())
            throw new InvalidOperationException("Injected response loss after the job transaction committed.");
        return job;
    }

    public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null) =>
        inner.RetryAsync(userId, jobId, cancellationToken, idempotencyKey, requestId);

    public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(userId, jobId, cancellationToken);

    public Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) =>
        inner.ListAsync(userId, filter, cancellationToken);

    public Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.CancelAsync(userId, jobId, cancellationToken);
}

public sealed class MovieShotExecutionApiFactory : MovieOperationalApiFactory
{
    private readonly MovieClipDurationRange? durationRange;
    private readonly bool supportsCostIntent;
    private readonly bool supportsReferencePackageCostIntent;
    private readonly int? sceneClipPromptMaxCharacters;
    public MovieShotExecutionPostCommitFailureGate CommitFailureGate { get; } = new();

    public MovieShotExecutionApiFactory() : this(null, true, true, null) { }

    internal MovieShotExecutionApiFactory(
        MovieClipDurationRange? durationRange,
        bool supportsCostIntent = true,
        bool supportsReferencePackageCostIntent = true,
        int? sceneClipPromptMaxCharacters = null)
    {
        this.durationRange = durationRange;
        this.supportsCostIntent = supportsCostIntent;
        this.supportsReferencePackageCostIntent = supportsReferencePackageCostIntent;
        this.sceneClipPromptMaxCharacters = sceneClipPromptMaxCharacters;
    }

    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
        services.RemoveAll<IMovieVideoProvider>();
        services.RemoveAll<IGenerationJobService>();
        services.AddSingleton(CommitFailureGate);
        services.AddScoped<IGenerationJobService>(serviceProvider => new ThrowAfterCommitMovieShotGenerationJobService(
            ActivatorUtilities.CreateInstance<GenerationJobService>(serviceProvider),
            serviceProvider.GetRequiredService<MovieShotExecutionPostCommitFailureGate>()));
        services.AddSingleton<IMovieVideoProvider>(serviceProvider => new FakeMovieVideoProvider(
                serviceProvider.GetRequiredService<FakeProviderScenarioCatalog>(),
                serviceProvider.GetRequiredService<FakeProviderCallLog>())
            {
                SceneClipDurationRange = durationRange ?? new MovieClipDurationRange(1, 3600),
                SceneClipCostIntentSupported = supportsCostIntent,
                ReferencePackageCostIntentSupported = supportsReferencePackageCostIntent,
                SceneClipPromptMaxCharacters = sceneClipPromptMaxCharacters,
            });
        });
    }
}

public sealed class MovieShotExecutionApiTests : IClassFixture<MovieShotExecutionApiFactory>
{
    private readonly MovieShotExecutionApiFactory factory;

    public MovieShotExecutionApiTests(MovieShotExecutionApiFactory factory)
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
        Assert.Equal("1080p", input.RootElement.GetProperty("TargetResolution").GetString());
        Assert.Equal(JsonValueKind.Null, input.RootElement.GetProperty("MasterResolution").ValueKind);
        Assert.Equal(MovieQualityLevels.Standard, input.RootElement.GetProperty("QualityTier").GetString());
        Assert.Equal("fake-movie", input.RootElement.GetProperty("SelectedProviderKey").GetString());
        Assert.Equal("fake-model", input.RootElement.GetProperty("SelectedProviderModelKey").GetString());
        Assert.Equal("source_to_master", input.RootElement.GetProperty("ProcessingPath").GetString());
        Assert.False(input.RootElement.GetProperty("UpscalingRequested").GetBoolean());
        Assert.Contains(keyframe.Id.ToString(), input.RootElement.GetProperty("ReferencePackageJson").GetString());
        using var shotSnapshot = JsonDocument.Parse(input.RootElement.GetProperty("ShotJson").GetString()!);
        Assert.Equal("hallway-a", shotSnapshot.RootElement.GetProperty("screenDirection").GetProperty("Axis").GetString());
        using var referencePackage = JsonDocument.Parse(input.RootElement.GetProperty("ReferencePackageJson").GetString()!);
        Assert.Equal(2, referencePackage.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task Ambiguous_post_commit_job_failure_preserves_candidate_and_reservation_for_reconciliation()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var (_, shot) = await CreateShot(client, auth.PersonalWorkspace.Id, "16:9", 5);
        var keyframe = await CreateApprovedKeyframe(client, shot.Id);
        const string idempotencyKey = "shot-execution-commit-response-loss";
        var payload = new
        {
            keyframeVersionId = keyframe.Id,
            sourceResolution = "1080p",
            masterResolution = "1080p",
            processingPath = "native",
            takeCount = 1,
        };
        factory.CommitFailureGate.Arm();
        var usageBefore = await CountUsageTransactions(auth.PersonalWorkspace.Id);

        using var ambiguous = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, idempotencyKey);
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        using (var body = JsonDocument.Parse(await ambiguous.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var replay = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, idempotencyKey);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using (var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.SingleAsync(item => item.IdempotencyKey == $"{idempotencyKey}:1");
        var clip = await db.MovieClips.SingleAsync(item => item.MovieShotId == shot.Id);
        var take = await db.MovieTakes.SingleAsync(item => item.MovieShotId == shot.Id);
        Assert.Equal(clip.Id, JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)!.MovieClipId);
        Assert.Null(clip.GenerationJobId);
        Assert.Null(take.GenerationJobId);
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.CreatedByUserId == auth.User.Id));
        Assert.Equal(1, await db.MovieClips.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(1, await db.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(usageBefore + 1, await db.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Stale_pre_job_placeholder_is_reclaimed_before_same_key_retry()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var (project, shot) = await CreateShot(client, auth.PersonalWorkspace.Id, "16:9", 5);
        var keyframe = await CreateApprovedKeyframe(client, shot.Id);
        var oldClipId = Guid.NewGuid();
        var oldTakeId = Guid.NewGuid();
        var staleAt = DateTime.UtcNow.AddHours(-2);
        const string idempotencyKey = "stale-pre-job-placeholder";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var shotEntity = await db.MovieShots.SingleAsync(item => item.Id == shot.Id);
            db.MovieClips.Add(new MovieClip
            {
                Id = oldClipId,
                MovieProjectId = project.Project.Id,
                MovieSceneId = shotEntity.MovieSceneId,
                MovieShotId = shot.Id,
                Status = MovieClipStatuses.Queued,
                DurationSeconds = 5,
                CreatedAt = staleAt,
                UpdatedAt = staleAt,
            });
            db.MovieTakes.Add(new MovieTake
            {
                Id = oldTakeId,
                MovieShotId = shot.Id,
                VersionNumber = 1,
                Label = "Take 1",
                Status = MovieTakeStatuses.Queued,
                QualityLevel = MovieQualityLevels.Standard,
                MovieClipId = oldClipId,
                GenerationJobId = null,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    execution = MovieShotExecutionOperations.ImageToVideo,
                    sourceProductionVersionId = keyframe.Id,
                    takeNumber = 1,
                    takeCount = 1,
                    idempotencyKey = $"{idempotencyKey}:1",
                    queuedInputFingerprint = new string('0', 64),
                }),
                StatusChangedAt = staleAt,
                StatusChangedByUserId = auth.User.Id,
                CreatedAt = staleAt,
                UpdatedAt = staleAt,
            });
            await db.SaveChangesAsync();
        }

        var usageBefore = await CountUsageTransactions(auth.PersonalWorkspace.Id);
        var queued = await SendWithCsrf<MovieShotExecutionResponse>(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", new
            {
                keyframeVersionId = keyframe.Id,
                sourceResolution = "1080p",
                masterResolution = "1080p",
                processingPath = "native",
                takeCount = 1,
            }, idempotencyKey);

        Assert.Single(queued.Candidates);
        Assert.NotEqual(oldTakeId, queued.Candidates[0].TakeId);
        Assert.NotEqual(oldClipId, queued.Candidates[0].ClipId);
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await finalDb.MovieTakes.AnyAsync(item => item.Id == oldTakeId));
        Assert.False(await finalDb.MovieClips.AnyAsync(item => item.Id == oldClipId));
        Assert.Equal(1, await finalDb.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(1, await finalDb.MovieClips.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(1, await finalDb.GenerationJobs.CountAsync(item => item.IdempotencyKey == $"{idempotencyKey}:1"));
        Assert.Equal(usageBefore + 1, await finalDb.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
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
        var usageBeforeMismatch = await CountUsageTransactions(auth.PersonalWorkspace.Id);
        using var changedBatchSize = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", new
            {
                keyframeVersionId = keyframe.Id,
                sourceResolution = "1080p",
                masterResolution = "1080p",
                processingPath = "native",
                takeCount = 3,
            }, "shot-execution-idempotent");
        Assert.Equal(HttpStatusCode.BadRequest, changedBatchSize.StatusCode);
        using (var changedBatchBody = JsonDocument.Parse(await changedBatchSize.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_REUSED", changedBatchBody.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var mismatchedReplay = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", new
            {
                keyframeVersionId = keyframe.Id,
                sourceResolution = "720p",
                masterResolution = "720p",
                processingPath = "native",
                takeCount = 2,
            }, "shot-execution-idempotent");
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedReplay.StatusCode);
        using (var mismatchBody = JsonDocument.Parse(await mismatchedReplay.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_REUSED", mismatchBody.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(2, await db.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(2, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey != null && item.CreatedByUserId == auth.User.Id));
        Assert.Equal(usageBeforeMismatch, await db.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Idempotency_rejects_candidate_with_tampered_upscale_intent()
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
            takeCount = 1,
        };
        var first = await SendWithCsrf<MovieShotExecutionResponse>(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, "shot-execution-tampered-input");
        using (var tamperScope = factory.Services.CreateScope())
        {
            var tamperDb = tamperScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await tamperDb.GenerationJobs.SingleAsync(item => item.Id == first.Candidates[0].GenerationJobId);
            var input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)!;
            job.InputJson = JsonSerializer.Serialize(input with { UpscalingRequested = true });
            await tamperDb.SaveChangesAsync();
        }

        var usageBeforeReplay = await CountUsageTransactions(auth.PersonalWorkspace.Id);
        using var replay = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, "shot-execution-tampered-input");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using (var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_REUSED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.InputJson.Contains(shot.Id.ToString())));
        Assert.Equal(usageBeforeReplay, await db.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Job_first_and_cross_shot_key_reuse_reconcile_unlinked_placeholder_without_reservation()
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
            takeCount = 1,
        };
        var first = await SendWithCsrf<MovieShotExecutionResponse>(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, "shot-execution-job-first");

        // Characterize the committed-job / not-yet-linked state between
        // GenerationJobService.Commit and the API's final link save.
        using (var stateScope = factory.Services.CreateScope())
        {
            var stateDb = stateScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var take = await stateDb.MovieTakes.SingleAsync(item => item.Id == first.Candidates[0].TakeId);
            var clip = await stateDb.MovieClips.SingleAsync(item => item.Id == first.Candidates[0].ClipId);
            take.GenerationJobId = null;
            clip.GenerationJobId = null;
            await stateDb.SaveChangesAsync();
        }

        var (_, otherShot) = await CreateShot(client, auth.PersonalWorkspace.Id, "16:9", 5);
        var otherKeyframe = await CreateApprovedKeyframe(client, otherShot.Id);
        var usageBefore = await CountUsageTransactions(auth.PersonalWorkspace.Id);

        using var crossShotReuse = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{otherShot.Id}/production/execute", new
            {
                keyframeVersionId = otherKeyframe.Id,
                sourceResolution = "1080p",
                masterResolution = "1080p",
                processingPath = "native",
                takeCount = 1,
            }, "shot-execution-job-first");
        Assert.Equal(HttpStatusCode.BadRequest, crossShotReuse.StatusCode);
        using (var body = JsonDocument.Parse(await crossShotReuse.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_REUSED", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var pendingReplay = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, "shot-execution-job-first");
        Assert.Equal(HttpStatusCode.BadRequest, pendingReplay.StatusCode);
        using (var body = JsonDocument.Parse(await pendingReplay.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        using (var stateScope = factory.Services.CreateScope())
        {
            var stateDb = stateScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await stateDb.GenerationJobs.SingleAsync(item => item.CreatedByUserId == auth.User.Id);
            job.Status = GenerationJobStatus.Failed;
            await stateDb.SaveChangesAsync();
        }
        using var terminalReplay = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", payload, "shot-execution-job-first");
        Assert.Equal(HttpStatusCode.BadRequest, terminalReplay.StatusCode);
        using (var body = JsonDocument.Parse(await terminalReplay.Content.ReadAsStringAsync()))
            Assert.Equal("SHOT_EXECUTION_IDEMPOTENCY_TERMINAL", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await finalDb.GenerationJobs.CountAsync(item => item.CreatedByUserId == auth.User.Id));
        Assert.Equal(1, await finalDb.MovieClips.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(0, await finalDb.MovieClips.CountAsync(item => item.MovieShotId == otherShot.Id));
        Assert.Equal(1, await finalDb.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(0, await finalDb.MovieTakes.CountAsync(item => item.MovieShotId == otherShot.Id));
        Assert.Equal(usageBefore, await finalDb.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Concurrent_same_key_requests_leave_one_candidate_and_one_usage_reservation()
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
            takeCount = 1,
        };
        var usageBefore = await CountUsageTransactions(auth.PersonalWorkspace.Id);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute")
        {
            Content = JsonContent.Create(payload),
        };
        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/production/execute")
        {
            Content = JsonContent.Create(payload),
        };
        foreach (var request in new[] { firstRequest, secondRequest })
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
            request.Headers.Add("Idempotency-Key", "shot-execution-concurrent");
        }

        var responses = await Task.WhenAll(client.SendAsync(firstRequest), client.SendAsync(secondRequest));
        try
        {
            Assert.Contains(responses, item => item.IsSuccessStatusCode);
            foreach (var response in responses.Where(item => !item.IsSuccessStatusCode))
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var errorCode = body.RootElement.GetProperty("error").GetProperty("code").GetString();
                Assert.NotNull(errorCode);
                Assert.Contains(errorCode!,
                    new[] { "SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS", "SHOT_EXECUTION_IDEMPOTENCY_REUSED" });
            }
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(1, await db.MovieClips.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.CreatedByUserId == auth.User.Id));
        Assert.Equal(usageBefore + 1, await db.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
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

    [Fact]
    public async Task Unsupported_provider_duration_rejects_before_clip_job_or_usage_reservation()
    {
        using var limitedFactory = new MovieShotExecutionApiFactory(new MovieClipDurationRange(2, 6));
        await AssertRejectedWithoutWritesAsync(limitedFactory, "MOVIE_CLIP_DURATION_UNSUPPORTED", 7, "720p", "720p", "native");
    }

    [Fact]
    public async Task Unsupported_priced_profile_rejects_before_clip_job_or_usage_reservation()
    {
        using var unsupportedFactory = new MovieShotExecutionApiFactory(null, supportsCostIntent: false);
        await AssertRejectedWithoutWritesAsync(unsupportedFactory, "MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED", 7, "720p", "1080p", "source_to_master");
    }

    [Fact]
    public async Task Unpriced_keyframe_reference_package_rejects_before_clip_job_or_usage_reservation()
    {
        using var unsupportedFactory = new MovieShotExecutionApiFactory(null, supportsReferencePackageCostIntent: false);
        await AssertRejectedWithoutWritesAsync(unsupportedFactory, "SHOT_EXECUTION_REFERENCE_PACKAGE_UNSUPPORTED", 7, "720p", "720p", "native");
    }

    [Fact]
    public async Task Prompt_overflow_rejects_before_snapshot_clip_take_job_or_usage_reservation()
    {
        using var limitedFactory = new MovieShotExecutionApiFactory(null, sceneClipPromptMaxCharacters: 1);
        await AssertRejectedWithoutWritesAsync(limitedFactory, "MOVIE_CLIP_PROMPT_UNSUPPORTED", 7, "720p", "720p", "native");
    }

    private async Task AssertRejectedWithoutWritesAsync(
        MovieShotExecutionApiFactory testFactory,
        string expectedCode,
        int durationSeconds,
        string sourceResolution,
        string masterResolution,
        string processingPath)
    {
        using var client = testFactory.CreateClient();
        var auth = await Register(client);
        var (project, shot) = await CreateShot(client, auth.PersonalWorkspace.Id, "16:9", durationSeconds);
        var keyframe = await CreateApprovedKeyframe(client, shot.Id);
        int jobsBefore;
        int clipsBefore;
        int takesBefore;
        int snapshotsBefore;
        int usageBefore;
        using (var beforeScope = testFactory.Services.CreateScope())
        {
            var beforeDb = beforeScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            jobsBefore = await beforeDb.GenerationJobs.CountAsync(item => item.InputJson.Contains(shot.Id.ToString()));
            clipsBefore = await beforeDb.MovieClips.CountAsync(item => item.MovieShotId == shot.Id);
            takesBefore = await beforeDb.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id);
            snapshotsBefore = await beforeDb.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == project.Project.Id);
            usageBefore = await beforeDb.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id);
        }

        using var response = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/shots/{shot.Id}/production/execute", new
            {
                keyframeVersionId = keyframe.Id,
                sourceResolution,
                masterResolution,
                processingPath,
                takeCount = 1,
                qualityLevel = MovieQualityLevels.Standard,
                confirmationAccepted = true,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.Equal(expectedCode, body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, testFactory.Calls.Calls(FakeProviderKind.Movie));
        using var afterScope = testFactory.Services.CreateScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(jobsBefore, await afterDb.GenerationJobs.CountAsync(item => item.InputJson.Contains(shot.Id.ToString())));
        Assert.Equal(clipsBefore, await afterDb.MovieClips.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(takesBefore, await afterDb.MovieTakes.CountAsync(item => item.MovieShotId == shot.Id));
        Assert.Equal(snapshotsBefore, await afterDb.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == project.Project.Id));
        Assert.Equal(usageBefore, await afterDb.UsageTransactions.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    private async Task<int> CountUsageTransactions(Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.UsageTransactions.CountAsync(item => item.WorkspaceId == workspaceId);
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
