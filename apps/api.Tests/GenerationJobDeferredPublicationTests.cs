using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationJobDeferredPublicationFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<DeferredMovieDispatchProbe>();
            services.AddTransient<IGenerationJobHandler>(provider =>
            {
                var probe = provider.GetRequiredService<DeferredMovieDispatchProbe>();
                probe.RecordHandlerResolution();
                return probe;
            });
        });
    }
}

public sealed class DeferredMovieDispatchProbe : IGenerationJobHandler
{
    private int calls;
    private int handlerResolutions;

    public int Calls => Volatile.Read(ref calls);
    public int HandlerResolutions => Volatile.Read(ref handlerResolutions);
    public void RecordHandlerResolution() => Interlocked.Increment(ref handlerResolutions);
    public bool CanHandle(string jobType) =>
        string.Equals(jobType, GenerationJobTypes.MovieClipGenerate, StringComparison.OrdinalIgnoreCase);

    public Task<GenerationHandlerResult> ExecuteAsync(
        GenerationJob job,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        return Task.FromResult(new GenerationHandlerResult("{}", Array.Empty<GenerationHandlerOutput>()));
    }
}

public sealed class GenerationJobDeferredPublicationTests : IClassFixture<GenerationJobDeferredPublicationFactory>
{
    private const string PublicationMarker = "approved-movie-production/v1";
    private readonly GenerationJobDeferredPublicationFactory factory;

    public GenerationJobDeferredPublicationTests(GenerationJobDeferredPublicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Worker_rejects_marked_movie_job_without_durable_production_links_before_handler_dispatch()
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var jobId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var sourceVersionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId,
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                JobType = GenerationJobTypes.MovieClipGenerate,
                Status = GenerationJobStatus.Pending,
                Title = "Synthetic approved production clip",
                InputJson = JsonSerializer.Serialize(new
                {
                    _taslimPublicationContract = PublicationMarker,
                    Operation = MovieStudioOperations.SceneClip,
                    MovieProjectId = projectId,
                    MovieClipId = clipId,
                    MovieSceneId = sceneId,
                    MovieShotId = shotId,
                    SourceProductionVersionId = sourceVersionId,
                }),
                CreatedAt = now,
                QueuedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var probe = factory.Services.GetRequiredService<DeferredMovieDispatchProbe>();
        var callsBefore = probe.Calls;
        var handlerResolutionsBefore = probe.HandlerResolutions;
        await Task.Delay(150);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var pending = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
            Assert.Equal(GenerationJobStatus.Pending, pending.Status);
            Assert.Equal(callsBefore, probe.Calls);
            Assert.Equal(handlerResolutionsBefore, probe.HandlerResolutions);
            await db.GenerationJobs.Where(item => item.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, GenerationJobStatus.Queued)
                    .SetProperty(item => item.QueuedAt, DateTime.UtcNow));
        }

        GenerationJob? final = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            final = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
            if (final.Status is GenerationJobStatus.Succeeded or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled)
                break;
            await Task.Delay(25);
        }

        Assert.NotNull(final);
        Assert.Equal(callsBefore, probe.Calls);
        Assert.Equal(GenerationJobStatus.Failed, final!.Status);
        Assert.Equal("MOVIE_PRODUCTION_LINK_INCOMPLETE", final.ErrorCode);
        Assert.Equal(handlerResolutionsBefore, probe.HandlerResolutions);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await finalDb.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{jobId:N}");
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact]
    public async Task Unmarked_legacy_movie_clip_without_a_production_version_still_reaches_its_handler()
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var input = new MovieGenerationInput(
            MovieStudioOperations.SceneClip,
            projectId,
            clipId,
            sceneId,
            shotId,
            "Synthetic legacy clip",
            8,
            "16:9",
            "cinematic",
            "en",
            null,
            "{}",
            "{}",
            "{}");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var project = new MovieProject
            {
                Id = projectId,
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                Title = "Synthetic legacy project",
                Description = "Provider-free compatibility fixture.",
                DurationSeconds = 8,
                CreatedAt = now,
                UpdatedAt = now,
                Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            };
            var scene = new MovieScene { Id = sceneId, MovieProjectId = projectId, Sequence = 1, Title = "Scene", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
            var shot = new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Legacy shot.", CreatedAt = now, UpdatedAt = now };
            var clip = new MovieClip
            {
                Id = clipId,
                MovieProjectId = projectId,
                MovieSceneId = sceneId,
                MovieShotId = shotId,
                GenerationJobId = jobId,
                Status = MovieClipStatuses.Queued,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var job = new GenerationJob
            {
                Id = jobId,
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                JobType = GenerationJobTypes.MovieClipGenerate,
                Status = GenerationJobStatus.Queued,
                Title = "Unmarked legacy MovieClipGenerate",
                InputJson = JsonSerializer.Serialize(input),
                EstimatedProviderCostUsd = 0m,
                EstimatedProviderCostKnown = true,
                CreatedAt = now,
                QueuedAt = now,
            };
            db.AddRange(project, scene, shot, clip, job);
            await db.SaveChangesAsync();
        }

        var probe = factory.Services.GetRequiredService<DeferredMovieDispatchProbe>();
        var callsBefore = probe.Calls;
        var handlerResolutionsBefore = probe.HandlerResolutions;
        GenerationJob? final = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            final = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
            if (final.Status is GenerationJobStatus.Succeeded or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled)
                break;
            await Task.Delay(25);
        }

        Assert.NotNull(final);
        Assert.Equal(GenerationJobStatus.Failed, final!.Status);
        Assert.NotEqual("MOVIE_PRODUCTION_LINK_INCOMPLETE", final.ErrorCode);
        Assert.Equal(callsBefore + 1, probe.Calls);
        Assert.Equal(handlerResolutionsBefore + 1, probe.HandlerResolutions);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await verifyDb.MovieProductionVersions.AsNoTracking().Where(version => version.MovieShotId == shotId).ToListAsync());
    }

    private static async Task<(Guid UserId, Guid WorkspaceId)> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Deferred Publication Tester",
            email = $"deferred-publication-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return (
            root.GetProperty("user").GetProperty("id").GetGuid(),
            root.GetProperty("personalWorkspace").GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

public sealed class GenerationJobPendingPublicationFactory : GenerationJobsApiFactory
{
    public string DatabasePathForTests => databasePath;

    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobQueue>();
            services.AddSingleton<DeferredPublicationQueueProbe>();
            services.AddScoped<IGenerationJobQueue, CountingDatabaseGenerationJobQueue>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public sealed class DeferredPublicationQueueProbe
{
    private int enqueueCount;
    public int EnqueueCount => Volatile.Read(ref enqueueCount);
    public void RecordEnqueue() => Interlocked.Increment(ref enqueueCount);
}

public sealed class CountingDatabaseGenerationJobQueue : IGenerationJobQueue
{
    private readonly DatabaseGenerationJobQueue inner;
    private readonly DeferredPublicationQueueProbe probe;

    public CountingDatabaseGenerationJobQueue(TaslimDbContext db, DeferredPublicationQueueProbe probe)
    {
        inner = new DatabaseGenerationJobQueue(db);
        this.probe = probe;
    }

    public Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        probe.RecordEnqueue();
        return inner.EnqueueAsync(jobId, cancellationToken);
    }
}

public sealed class GenerationJobPendingPublicationTests : IClassFixture<GenerationJobPendingPublicationFactory>
{
    private const string PublicationMarker = "approved-movie-production/v1";
    private const string LinkageIncompleteErrorCode = "MOVIE_PRODUCTION_LINK_INCOMPLETE";
    private readonly GenerationJobPendingPublicationFactory factory;

    public GenerationJobPendingPublicationTests(GenerationJobPendingPublicationFactory factory) => this.factory = factory;

    [Theory]
    [InlineData("missing", "COST_ESTIMATE_UNKNOWN")]
    [InlineData("unknown", "COST_ESTIMATE_UNKNOWN")]
    [InlineData("scalar-mismatch", "COST_ESTIMATE_UNKNOWN")]
    [InlineData("json-mismatch", "COST_ESTIMATE_UNKNOWN")]
    public async Task Approved_production_rejects_missing_unknown_or_mismatched_usd_estimates_before_any_writes(
        string failureKind,
        string expectedCode)
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var movie = await SeedApprovedMovie(workspaceId, userId);
        var request = CreateRequest(workspaceId, movie, movie.FirstClipId);
        switch (failureKind)
        {
            case "missing":
                request.InternalCostEstimate = null;
                request.InternalCostEstimateJson = null;
                break;
            case "unknown":
                var unknownEstimate = MovieGenerationCostEstimate.Unknown("synthetic_unknown");
                request.InternalCostEstimate = unknownEstimate.ToGenerationCostEstimate();
                request.InternalCostEstimateJson = unknownEstimate.ToJson();
                break;
            case "scalar-mismatch":
                request.EstimatedProviderCostUsd = 0.01m;
                break;
            case "json-mismatch":
                request.InternalCostEstimateJson = new MovieGenerationCostEstimate(
                    MovieGenerationCostEstimateStates.Estimated, 0m, 0.01m, UsageCurrencies.Usd,
                    null, null, null, []).ToJson();
                break;
        }

        var probe = factory.Services.GetRequiredService<DeferredPublicationQueueProbe>();
        var enqueuesBefore = probe.EnqueueCount;
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var error = await Assert.ThrowsAsync<GenerationJobValidationException>(
                () => jobs.CreatePendingMovieProductionAsync(userId, request, $"invalid-price-{Guid.NewGuid():N}"));
            Assert.Equal(expectedCode, error.Code);
        }

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await db.GenerationJobs.AsNoTracking().Where(item => item.CreatedByUserId == userId).ToListAsync());
        Assert.Empty(await db.UsageTransactions.AsNoTracking().Where(item => item.UserId == userId).ToListAsync());
        Assert.Equal(enqueuesBefore, probe.EnqueueCount);
    }

    [Fact]
    public async Task Same_key_replay_reuses_canonical_pending_job_and_publish_requires_all_durable_links()
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var movie = await SeedApprovedMovie(workspaceId, userId);
        var queueProbe = factory.Services.GetRequiredService<DeferredPublicationQueueProbe>();
        var enqueuesBefore = queueProbe.EnqueueCount;
        var key = new string('k', 80);
        GenerationJob first;
        GenerationJob replay;
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            first = await jobs.CreatePendingMovieProductionAsync(userId, CreateRequest(workspaceId, movie, movie.FirstClipId), key);
            replay = await jobs.CreatePendingMovieProductionAsync(userId, CreateRequest(workspaceId, movie, movie.SecondClipId), key);
        }

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(GenerationJobStatus.Pending, replay.Status);
        using (var document = JsonDocument.Parse(replay.InputJson))
        {
            Assert.Equal(PublicationMarker, document.RootElement.GetProperty("_taslimPublicationContract").GetString());
            Assert.Equal(movie.FirstClipId, document.RootElement.GetProperty(nameof(MovieGenerationInput.MovieClipId)).GetGuid());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == key));
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{first.Id:N}");
            Assert.Equal(UsageTransactionStatus.Pending, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
            Assert.Equal(0m, usage.ProviderCostUsd);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var missingLinks = await Assert.ThrowsAsync<GenerationJobValidationException>(
                () => jobs.PublishPendingMovieProductionAsync(userId, first.Id));
            Assert.Equal(LinkageIncompleteErrorCode, missingLinks.Code);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var stillPending = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == first.Id);
            var stillReserved = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{first.Id:N}");
            Assert.Equal(GenerationJobStatus.Pending, stillPending.Status);
            Assert.Equal(UsageTransactionStatus.Pending, stillReserved.Status);
            Assert.Equal(0m, stillReserved.ChargedAmount);
            Assert.Equal(0m, stillReserved.ProviderCostUsd);
            await LinkDurableProductionRows(db, movie, first.Id, userId);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var canonicalReplay = await jobs.CreatePendingMovieProductionAsync(
                userId, CreateRequest(workspaceId, movie, movie.FirstClipId), key);
            Assert.Equal(first.Id, canonicalReplay.Id);
            Assert.Equal(GenerationJobStatus.Pending, canonicalReplay.Status);
        }
        Assert.Equal(enqueuesBefore, queueProbe.EnqueueCount);

        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var published = await jobs.PublishPendingMovieProductionAsync(userId, first.Id);
            Assert.NotNull(published);
            Assert.Equal(GenerationJobStatus.Queued, published!.Status);
        }
        Assert.Equal(enqueuesBefore + 1, queueProbe.EnqueueCount);
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var duplicatePublish = await jobs.PublishPendingMovieProductionAsync(userId, first.Id);
            Assert.NotNull(duplicatePublish);
            Assert.Equal(first.Id, duplicatePublish!.Id);
            Assert.Equal(GenerationJobStatus.Queued, duplicatePublish.Status);
            Assert.False(await jobs.AbortPendingMovieProductionAsync(userId, first.Id));
        }
        Assert.Equal(enqueuesBefore + 1, queueProbe.EnqueueCount);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await finalDb.GenerationJobs.CountAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == key));
        Assert.Equal(1, await finalDb.UsageTransactions.CountAsync(item => item.RequestId == $"generation:{first.Id:N}"));
        var finalUsage = await finalDb.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{first.Id:N}");
        Assert.Equal(UsageTransactionStatus.Pending, finalUsage.Status);
        Assert.Equal(0m, finalUsage.ChargedAmount);
    }

    [Theory]
    [InlineData("from-stage")]
    [InlineData("event-type")]
    public async Task Publish_rejects_stage_transition_with_wrong_source_or_event_without_queueing(string invalidField)
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var movie = await SeedApprovedMovie(workspaceId, userId);
        var queueProbe = factory.Services.GetRequiredService<DeferredPublicationQueueProbe>();
        var enqueuesBefore = queueProbe.EnqueueCount;
        GenerationJob job;
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            job = await jobs.CreatePendingMovieProductionAsync(
                userId, CreateRequest(workspaceId, movie, movie.FirstClipId), $"bad-transition-{Guid.NewGuid():N}");
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await LinkDurableProductionRows(db, movie, job.Id, userId);
            var transition = await db.MovieProductionStageTransitions.SingleAsync(item => item.GenerationJobId == job.Id);
            if (invalidField == "from-stage") transition.FromStage = "not-the-current-stage";
            else transition.EventType = "updated";
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var error = await Assert.ThrowsAsync<GenerationJobValidationException>(
                () => jobs.PublishPendingMovieProductionAsync(userId, job.Id));
            Assert.Equal(LinkageIncompleteErrorCode, error.Code);
        }
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(GenerationJobStatus.Pending,
            (await verifyDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id)).Status);
        Assert.Equal(UsageTransactionStatus.Pending,
            (await verifyDb.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{job.Id:N}")).Status);
        Assert.Equal(enqueuesBefore, queueProbe.EnqueueCount);
    }

    [Fact]
    public async Task Pending_abort_cancels_usage_once_and_same_key_replay_never_creates_a_second_reservation()
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var movie = await SeedApprovedMovie(workspaceId, userId);
        var key = $"abort-{Guid.NewGuid():N}";
        GenerationJob job;
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            job = await jobs.CreatePendingMovieProductionAsync(userId, CreateRequest(workspaceId, movie, movie.FirstClipId), key);
        }
        Assert.Equal(GenerationJobStatus.Pending, job.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            Assert.True(await jobs.AbortPendingMovieProductionAsync(userId, job.Id));
        }
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            Assert.False(await jobs.AbortPendingMovieProductionAsync(userId, job.Id));
            var replay = await jobs.CreatePendingMovieProductionAsync(userId, CreateRequest(workspaceId, movie, movie.SecondClipId), key);
            Assert.Equal(job.Id, replay.Id);
            Assert.Equal(GenerationJobStatus.Cancelled, replay.Status);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var publish = await jobs.PublishPendingMovieProductionAsync(userId, job.Id);
            Assert.Equal(GenerationJobStatus.Cancelled, publish!.Status);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await verifyDb.GenerationJobs.CountAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == key));
        var usage = await verifyDb.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.Equal(1, await verifyDb.UsageTransactions.CountAsync(item => item.RequestId == $"generation:{job.Id:N}"));
    }

    [Fact]
    public async Task Invalid_idempotency_keys_are_rejected_before_any_job_or_usage_row_is_created()
    {
        using var client = factory.CreateClient();
        var (userId, _) = await Register(client);
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var request = new CreateGenerationJobRequest();
            var missing = await Assert.ThrowsAsync<GenerationJobValidationException>(
                () => jobs.CreatePendingMovieProductionAsync(userId, request, null!));
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", missing.Code);
            var blank = await Assert.ThrowsAsync<GenerationJobValidationException>(
                () => jobs.CreatePendingMovieProductionAsync(userId, request, "  "));
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", blank.Code);
            var overlong = await Assert.ThrowsAsync<GenerationJobValidationException>(
                () => jobs.CreatePendingMovieProductionAsync(userId, request, new string('x', 81)));
            Assert.Equal("IDEMPOTENCY_KEY_INVALID", overlong.Code);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await verifyDb.GenerationJobs.AsNoTracking().Where(item => item.CreatedByUserId == userId).ToListAsync());
        Assert.Empty(await verifyDb.UsageTransactions.AsNoTracking().Where(item => item.UserId == userId).ToListAsync());
    }

    [Fact]
    public async Task Approved_production_submission_claim_is_atomic_and_same_job_replay_does_not_bump_timestamp()
    {
        var fixture = await SeedClaimableMovieJob();
        DateTime firstClaimTimestamp;

        await ClaimSubmission(fixture.JobId, fixture.ConcurrencyToken);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var version = await db.MovieProductionVersions.AsNoTracking().SingleAsync(item => item.GenerationJobId == fixture.JobId);
            firstClaimTimestamp = version.UpdatedAt;
            Assert.True(firstClaimTimestamp > fixture.InitialVersionTimestamp);
            Assert.Equal(1, await db.MovieProductionStageTransitions.CountAsync(item =>
                item.MovieProductionVersionId == version.Id && item.EventType == "provider_submission_claimed"));
        }

        await ClaimSubmission(fixture.JobId, fixture.ConcurrencyToken);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var finalVersion = await verifyDb.MovieProductionVersions.AsNoTracking().SingleAsync(item => item.GenerationJobId == fixture.JobId);
        Assert.Equal(firstClaimTimestamp, finalVersion.UpdatedAt);
        Assert.Equal(1, await verifyDb.MovieProductionStageTransitions.CountAsync(item =>
            item.MovieProductionVersionId == finalVersion.Id && item.EventType == "provider_submission_claimed"));
    }

    [Fact]
    public async Task Concurrent_same_job_claims_commit_one_event_and_recover_the_same_canonical_claim()
    {
        var fixture = await SeedClaimableMovieJob();
        using var start = new ManualResetEventSlim(false);
        var first = Task.Run(async () =>
        {
            start.Wait();
            await ClaimSubmission(fixture.JobId, fixture.ConcurrencyToken);
        });
        var second = Task.Run(async () =>
        {
            start.Wait();
            await ClaimSubmission(fixture.JobId, fixture.ConcurrencyToken);
        });

        start.Set();
        await Task.WhenAll(first, second);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var version = await db.MovieProductionVersions.AsNoTracking().SingleAsync(item => item.GenerationJobId == fixture.JobId);
        Assert.True(version.UpdatedAt > fixture.InitialVersionTimestamp);
        Assert.Equal(1, await db.MovieProductionStageTransitions.CountAsync(item =>
            item.MovieProductionVersionId == version.Id && item.EventType == "provider_submission_claimed"));
    }

    [Fact]
    public async Task Claim_recovers_when_commit_succeeds_but_the_commit_acknowledgement_is_lost()
    {
        var fixture = await SeedClaimableMovieJob();
        var interceptor = new ThrowAfterCommitOnceInterceptor();
        var options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite($"Data Source={factory.DatabasePathForTests};Cache=Shared;Default Timeout=30")
            .AddInterceptors(interceptor)
            .Options;

        await using (var db = new TaslimDbContext(options))
        {
            var store = new MovieVideoExecutionStore(db, Options.Create(new MovieVideoOptions()));
            await store.ClaimApprovedProductionSubmissionAsync(fixture.JobId, fixture.ConcurrencyToken, CancellationToken.None);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var version = await verifyDb.MovieProductionVersions.AsNoTracking().SingleAsync(item => item.GenerationJobId == fixture.JobId);
        Assert.True(version.UpdatedAt > fixture.InitialVersionTimestamp);
        Assert.Equal(1, await verifyDb.MovieProductionStageTransitions.CountAsync(item =>
            item.MovieProductionVersionId == version.Id && item.EventType == "provider_submission_claimed"));
    }

    [Theory]
    [InlineData("clip-link")]
    [InlineData("version-status")]
    [InlineData("created-provenance")]
    public async Task Approved_production_claim_fails_closed_when_the_canonical_graph_is_incomplete(string invalidPart)
    {
        var fixture = await SeedClaimableMovieJob();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            if (invalidPart == "clip-link")
                await db.MovieClips.Where(item => item.Id == fixture.ClipId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, (Guid?)null));
            else if (invalidPart == "version-status")
                await db.MovieProductionVersions.Where(item => item.GenerationJobId == fixture.JobId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieProductionVersionStatuses.Rejected));
            else
                await db.MovieProductionStageTransitions.Where(item => item.GenerationJobId == fixture.JobId && item.EventType == "created")
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.EventType, "updated"));
        }

        var error = await Assert.ThrowsAsync<GenerationJobValidationException>(
            () => ClaimSubmission(fixture.JobId, fixture.ConcurrencyToken));
        Assert.Equal("MOVIE_PRODUCTION_SUBMISSION_CLAIM_CONFLICT", error.Code);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await verifyDb.MovieProductionStageTransitions.AsNoTracking().Where(item =>
            item.GenerationJobId == fixture.JobId && item.EventType == "provider_submission_claimed").ToListAsync());
    }

    [Fact]
    public async Task Unmarked_legacy_movie_job_claim_is_a_no_op()
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var jobId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId,
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                JobType = GenerationJobTypes.MovieClipGenerate,
                Status = GenerationJobStatus.Running,
                ConcurrencyToken = token,
                InputJson = "{}",
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await ClaimSubmission(jobId, token);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await verifyDb.MovieProductionStageTransitions.AsNoTracking().Where(item => item.GenerationJobId == jobId).ToListAsync());
    }

    private async Task<ClaimableMovieFixture> SeedClaimableMovieJob()
    {
        using var client = factory.CreateClient();
        var (userId, workspaceId) = await Register(client);
        var movie = await SeedApprovedMovie(workspaceId, userId);
        GenerationJob job;
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            job = await jobs.CreatePendingMovieProductionAsync(
                userId, CreateRequest(workspaceId, movie, movie.FirstClipId), $"claim-{Guid.NewGuid():N}");
        }

        DateTime initialVersionTimestamp;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await LinkDurableProductionRows(db, movie, job.Id, userId);
            initialVersionTimestamp = await db.MovieProductionVersions.AsNoTracking()
                .Where(item => item.GenerationJobId == job.Id)
                .Select(item => item.UpdatedAt)
                .SingleAsync();
        }
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var published = await jobs.PublishPendingMovieProductionAsync(userId, job.Id);
            Assert.Equal(GenerationJobStatus.Queued, published!.Status);
        }

        var token = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var queued = await db.GenerationJobs.SingleAsync(item => item.Id == job.Id);
            queued.Status = GenerationJobStatus.Running;
            queued.ConcurrencyToken = token;
            db.MovieVideoProviderExecutions.Add(new MovieVideoProviderExecution
            {
                Id = Guid.NewGuid(),
                GenerationJobId = job.Id,
                MovieClipId = movie.FirstClipId,
                ProviderKey = "synthetic-provider",
                Status = MovieVideoExecutionStatuses.Submitted,
                AttemptCount = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new ClaimableMovieFixture(job.Id, token, movie.FirstClipId, initialVersionTimestamp);
    }

    [DisposablePostgresFact]
    public async Task Disposable_postgres_cas_claims_with_identical_preupdate_tokens_commit_once_and_replay_idempotently()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"claim_test_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        string? stepSummaryReceipt = null;
        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(testBuilder.ConnectionString).Options;
            Guid jobId;
            Guid token;
            await using (var db = new TaslimDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                (jobId, token, _) = await SeedPostgresClaimGraph(db);
            }

            DateTime persistedTokenBeforeClaims;
            await using (var snapshot = new TaslimDbContext(options))
            {
                persistedTokenBeforeClaims = await snapshot.MovieProductionVersions.AsNoTracking()
                    .Where(item => item.GenerationJobId == jobId)
                    .Select(item => item.UpdatedAt)
                    .SingleAsync();
            }

            var casBarrier = new PostgreSqlVersionCasBarrierInterceptor(persistedTokenBeforeClaims);
            var claimOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .AddInterceptors(casBarrier)
                .Options;
            async Task ClaimOnFreshContext()
            {
                await using var db = new TaslimDbContext(claimOptions);
                var store = new MovieVideoExecutionStore(db, Options.Create(new MovieVideoOptions()));
                await store.ClaimApprovedProductionSubmissionAsync(jobId, token, CancellationToken.None);
            }

            var claimTasks = new[] { Task.Run(ClaimOnFreshContext), Task.Run(ClaimOnFreshContext) };
            PostgreSqlVersionCasObservation[] observations;
            try
            {
                await casBarrier.WaitForBothCasCommandsAsync(TimeSpan.FromSeconds(30));
                observations = casBarrier.Observations;
                Assert.Equal(2, observations.Length);
                Assert.NotSame(observations[0].Transaction, observations[1].Transaction);
                Assert.NotSame(observations[0].Connection, observations[1].Connection);
                Assert.All(observations, attempt => Assert.True(attempt.TransactionAndConnectionWereOpen));
                Assert.All(observations, attempt => Assert.Equal(persistedTokenBeforeClaims, attempt.ComparedUpdatedAt));
            }
            catch
            {
                casBarrier.ReleaseCasCommands();
                try
                {
                    await Task.WhenAll(claimTasks);
                }
                catch
                {
                    // Preserve the original barrier/assertion failure after observing both tasks.
                }
                throw;
            }

            casBarrier.ReleaseCasCommands();
            await Task.WhenAll(claimTasks);
            var affectedRows = casBarrier.AffectedRows.OrderByDescending(count => count).ToArray();
            Assert.Equal(new[] { 1, 0 }, affectedRows);

            DateTime firstClaimTimestamp;
            Guid versionId;
            MovieProductionStageTransition firstClaimEvent;
            List<MovieProductionStageTransition> firstClaimEvents;
            await using (var verify = new TaslimDbContext(options))
            {
                var version = await verify.MovieProductionVersions.AsNoTracking().SingleAsync(item => item.GenerationJobId == jobId);
                versionId = version.Id;
                firstClaimTimestamp = version.UpdatedAt;
                Assert.True(firstClaimTimestamp - persistedTokenBeforeClaims >= TimeSpan.FromMilliseconds(1));
                Assert.True(firstClaimTimestamp > persistedTokenBeforeClaims);
                firstClaimEvents = await verify.MovieProductionStageTransitions.AsNoTracking()
                    .Where(item => item.MovieProductionVersionId == version.Id && item.EventType == "provider_submission_claimed")
                    .ToListAsync();
                firstClaimEvent = Assert.Single(firstClaimEvents);
                Assert.Equal(jobId, firstClaimEvent.GenerationJobId);
                Assert.Equal(version.MovieShotId, firstClaimEvent.MovieShotId);
                Assert.Equal(MovieProductionStages.ProductionRender, firstClaimEvent.FromStage);
                Assert.Equal(MovieProductionStages.ProductionRender, firstClaimEvent.ToStage);
            }

            await using (var replay = new TaslimDbContext(options))
            {
                var store = new MovieVideoExecutionStore(replay, Options.Create(new MovieVideoOptions()));
                await store.ClaimApprovedProductionSubmissionAsync(jobId, token, CancellationToken.None);
            }

            DateTime timestampAfterReplay;
            List<MovieProductionStageTransition> replayedClaimEvents;
            await using (var verifyReplay = new TaslimDbContext(options))
            {
                timestampAfterReplay = await verifyReplay.MovieProductionVersions.AsNoTracking()
                    .Where(item => item.GenerationJobId == jobId)
                    .Select(item => item.UpdatedAt)
                    .SingleAsync();
                replayedClaimEvents = await verifyReplay.MovieProductionStageTransitions.AsNoTracking()
                    .Where(item => item.MovieProductionVersionId == versionId && item.EventType == "provider_submission_claimed")
                    .ToListAsync();
            }

            var replayedClaimEvent = Assert.Single(replayedClaimEvents);
            var replayPreservedCanonicalEvent = replayedClaimEvent.Id == firstClaimEvent.Id
                && replayedClaimEvent.CreatedAt == firstClaimEvent.CreatedAt;
            Assert.Equal(firstClaimTimestamp, timestampAfterReplay);
            Assert.True(replayPreservedCanonicalEvent);

            var distinctOpenTransactions = observations.Length == 2
                && !ReferenceEquals(observations[0].Transaction, observations[1].Transaction)
                && observations.All(attempt => attempt.TransactionAndConnectionWereOpen);
            var distinctOpenConnections = observations.Length == 2
                && !ReferenceEquals(observations[0].Connection, observations[1].Connection)
                && observations.All(attempt => attempt.TransactionAndConnectionWereOpen);
            var samePersistedTokenBeforeCas = observations.Length == 2
                && observations.All(attempt => attempt.ComparedUpdatedAt == persistedTokenBeforeClaims);
            var oneCasWinnerAndOneLoser = affectedRows.SequenceEqual(new[] { 1, 0 });
            var oneDurableClaimEvent = firstClaimEvents.Count == 1 && replayedClaimEvents.Count == 1;
            var strictPersistedTimestampAdvance = firstClaimTimestamp > persistedTokenBeforeClaims;
            var replayKeptTimestampAndEvent = timestampAfterReplay == firstClaimTimestamp && replayPreservedCanonicalEvent;
            stepSummaryReceipt = $"""
            ### `Taslim.Api.Tests.GenerationJobPendingPublicationTests.Disposable_postgres_cas_claims_with_identical_preupdate_tokens_commit_once_and_replay_idempotently` — passed

- Database provider: disposable PostgreSQL test schema.
- CAS rendezvous before UPDATE: two distinct open transactions and connections observed the same persisted `UpdatedAt` token: **{distinctOpenTransactions && distinctOpenConnections && samePersistedTokenBeforeCas}**.
- Conditional UPDATE row counts, winner then loser: **{string.Join(", ", affectedRows)}**; exactly one winner and one loser: **{oneCasWinnerAndOneLoser}**.
- Durable claim event count before and after replay: **{firstClaimEvents.Count}** / **{replayedClaimEvents.Count}**; exactly one event: **{oneDurableClaimEvent}**.
- Persisted timestamp advanced strictly and replay preserved timestamp plus event ID/CreatedAt: **{strictPersistedTimestampAdvance}** / **{replayKeptTimestampAndEvent}**.

""";
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }

        var stepSummaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (stepSummaryReceipt is not null && !string.IsNullOrWhiteSpace(stepSummaryPath))
            await File.AppendAllTextAsync(stepSummaryPath, stepSummaryReceipt);
    }

    private async Task ClaimSubmission(Guid jobId, Guid concurrencyToken)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var store = new MovieVideoExecutionStore(db, Options.Create(new MovieVideoOptions()));
        await store.ClaimApprovedProductionSubmissionAsync(jobId, concurrencyToken, CancellationToken.None);
    }

    private sealed record ClaimableMovieFixture(Guid JobId, Guid ConcurrencyToken, Guid ClipId, DateTime InitialVersionTimestamp);

    private async Task<ApprovedMovieFixture> SeedApprovedMovie(Guid workspaceId, Guid userId)
    {
        var now = DateTime.UtcNow;
        var fixture = new ApprovedMovieFixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var project = new MovieProject
        {
            Id = fixture.MovieProjectId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Title = "Deferred publication test",
            Description = "Synthetic provider-free fixture.",
            DurationSeconds = 8,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
        };
        var scene = new MovieScene { Id = fixture.SceneId, MovieProjectId = fixture.MovieProjectId, Sequence = 1, Title = "Scene", Summary = "Synthetic scene.", CreatedAt = now, UpdatedAt = now };
        var shot = new MovieShot { Id = fixture.ShotId, MovieSceneId = fixture.SceneId, Sequence = 1, Description = "Synthetic shot.", CreatedAt = now, UpdatedAt = now };
        var source = new MovieProductionVersion
        {
            Id = fixture.SourceVersionId,
            MovieShotId = fixture.ShotId,
            VersionNumber = 1,
            Stage = MovieProductionStages.ApprovedKeyframe,
            Status = MovieProductionVersionStatuses.Approved,
            CompositionJson = "{}",
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var firstClip = new MovieClip { Id = fixture.FirstClipId, MovieProjectId = fixture.MovieProjectId, MovieSceneId = fixture.SceneId, MovieShotId = fixture.ShotId, Status = MovieClipStatuses.Queued, DurationSeconds = 8, CreatedAt = now, UpdatedAt = now };
        var secondClip = new MovieClip { Id = fixture.SecondClipId, MovieProjectId = fixture.MovieProjectId, MovieSceneId = fixture.SceneId, MovieShotId = fixture.ShotId, Status = MovieClipStatuses.Queued, DurationSeconds = 8, CreatedAt = now, UpdatedAt = now };
        db.AddRange(project, scene, shot, source, firstClip, secondClip);
        await db.SaveChangesAsync();
        return fixture;
    }

    private static CreateGenerationJobRequest CreateRequest(Guid workspaceId, ApprovedMovieFixture movie, Guid clipId)
    {
        var movieEstimate = new MovieGenerationCostEstimate(
            MovieGenerationCostEstimateStates.Estimated, 0m, 0m, UsageCurrencies.Usd,
            null, null, null, []);
        var estimate = movieEstimate.ToGenerationCostEstimate();
        var input = new MovieGenerationInput(
            MovieStudioOperations.SceneClip,
            movie.MovieProjectId,
            clipId,
            movie.SceneId,
            movie.ShotId,
            "Synthetic approved render",
            8,
            "16:9",
            "cinematic",
            "en",
            null,
            "{}",
            "{}",
            "{}",
            SourceProductionVersionId: movie.SourceVersionId);
        return new CreateGenerationJobRequest
        {
            WorkspaceId = workspaceId,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Title = "Synthetic approved render",
            InputJson = JsonSerializer.Serialize(input),
            EstimatedProviderCostUsd = 0m,
            InternalCostEstimate = estimate,
            InternalCostEstimateJson = movieEstimate.ToJson(),
            ConfirmationAccepted = true,
        };
    }

    private static async Task LinkDurableProductionRows(TaslimDbContext db, ApprovedMovieFixture movie, Guid jobId, Guid userId)
    {
        var now = DateTime.UtcNow;
        var clip = await db.MovieClips.SingleAsync(item => item.Id == movie.FirstClipId);
        clip.GenerationJobId = jobId;
        clip.Status = MovieClipStatuses.Queued;
        clip.UpdatedAt = now;
        var version = new MovieProductionVersion
        {
            Id = Guid.NewGuid(),
            MovieShotId = movie.ShotId,
            VersionNumber = 2,
            Stage = MovieProductionStages.ProductionRender,
            Status = MovieProductionVersionStatuses.PendingApproval,
            CompositionJson = "{}",
            SourceVersionId = movie.SourceVersionId,
            GenerationJobId = jobId,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieProductionVersions.Add(version);
        db.MovieProductionStageTransitions.Add(new MovieProductionStageTransition
        {
            Id = Guid.NewGuid(),
            MovieShotId = movie.ShotId,
            MovieProductionVersionId = version.Id,
            FromStage = MovieProductionStages.ShotPlan,
            ToStage = MovieProductionStages.ProductionRender,
            EventType = "created",
            SourceVersionId = movie.SourceVersionId,
            GenerationJobId = jobId,
            ActorUserId = userId,
            CreatedAt = now,
            MetadataJson = "{}",
        });
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid UserId, Guid WorkspaceId)> Register(HttpClient client)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Deferred Publication Service Tester",
            email = $"deferred-service-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return (root.GetProperty("user").GetProperty("id").GetGuid(), root.GetProperty("personalWorkspace").GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<(Guid JobId, Guid Token, DateTime InitialTimestamp)> SeedPostgresClaimGraph(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var sourceVersionId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var now = DateTime.UtcNow.AddMinutes(-1);
        const string marker = "approved-movie-production/v1";
        var inputJson = JsonSerializer.Serialize(new
        {
            _taslimPublicationContract = marker,
            Operation = MovieStudioOperations.SceneClip,
            MovieProjectId = projectId,
            MovieClipId = clipId,
            MovieSceneId = sceneId,
            MovieShotId = shotId,
            Description = "synthetic PostgreSQL claim",
            DurationSeconds = 8,
            AspectRatio = "16:9",
            Style = "cinematic",
            Language = "en",
            AdditionalInstructions = (string?)null,
            ContinuityGuideJson = "{}",
            SceneJson = "{}",
            ShotJson = "{}",
            SourceProductionVersionId = sourceVersionId,
        });
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = $"claim-{userId:N}@example.test",
            NormalizedUserName = $"CLAIM-{userId:N}@EXAMPLE.TEST",
            Email = $"claim-{userId:N}@example.test",
            NormalizedEmail = $"CLAIM-{userId:N}@EXAMPLE.TEST",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            DisplayName = "Synthetic claim test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "Synthetic claim workspace",
            Slug = $"claim-{workspaceId:N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var movieProject = new MovieProject
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Title = "Synthetic claim project",
            Description = "Provider-free test fixture.",
            DurationSeconds = 8,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), MovieProjectId = projectId, UpdatedAt = now },
        };
        var scene = new MovieScene { Id = sceneId, MovieProjectId = projectId, Sequence = 1, Title = "Scene", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        var shot = new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, ProductionStage = MovieProductionStages.MotionPreview, Description = "Synthetic shot.", CreatedAt = now, UpdatedAt = now };
        var source = new MovieProductionVersion
        {
            Id = sourceVersionId,
            MovieShotId = shotId,
            VersionNumber = 1,
            Stage = MovieProductionStages.MotionPreview,
            Status = MovieProductionVersionStatuses.Approved,
            CompositionJson = "{}",
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var clip = new MovieClip
        {
            Id = clipId,
            MovieProjectId = projectId,
            MovieSceneId = sceneId,
            MovieShotId = shotId,
            GenerationJobId = jobId,
            Status = MovieClipStatuses.Queued,
            DurationSeconds = 8,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var job = new GenerationJob
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Status = GenerationJobStatus.Running,
            ConcurrencyToken = token,
            Title = "Synthetic approved render",
            InputJson = inputJson,
            CreatedAt = now,
            QueuedAt = now,
        };
        var version = new MovieProductionVersion
        {
            Id = versionId,
            MovieShotId = shotId,
            VersionNumber = 2,
            Stage = MovieProductionStages.ProductionRender,
            Status = MovieProductionVersionStatuses.PendingApproval,
            CompositionJson = "{}",
            SourceVersionId = sourceVersionId,
            GenerationJobId = jobId,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var createdTransition = new MovieProductionStageTransition
        {
            Id = Guid.NewGuid(),
            MovieShotId = shotId,
            MovieProductionVersionId = versionId,
            FromStage = MovieProductionStages.MotionPreview,
            ToStage = MovieProductionStages.ProductionRender,
            EventType = "created",
            SourceVersionId = sourceVersionId,
            GenerationJobId = jobId,
            ActorUserId = userId,
            CreatedAt = now,
        };
        var execution = new MovieVideoProviderExecution
        {
            Id = Guid.NewGuid(),
            GenerationJobId = jobId,
            MovieClipId = clipId,
            ProviderKey = "synthetic-provider",
            Status = MovieVideoExecutionStatuses.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AddRange(user, workspace, movieProject, scene, shot, source, clip, job, version, createdTransition, execution);
        await db.SaveChangesAsync();
        return (jobId, token, now);
    }

    private sealed class PostgreSqlVersionCasBarrierInterceptor(DateTime expectedPersistedToken) : DbCommandInterceptor
    {
        private static readonly Regex UpdatedAtPredicate = new(
            "(?:\\\"?UpdatedAt\\\"?)\\s*=\\s*(?<parameter>@[A-Za-z0-9_]+|:[A-Za-z0-9_]+|\\$[0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly ConcurrentQueue<PostgreSqlVersionCasObservation> observations = new();
        private readonly ConcurrentQueue<int> affectedRows = new();
        private readonly TaskCompletionSource<bool> bothCasCommands = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseCasCommands = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int casCommandCount;

        public PostgreSqlVersionCasObservation[] Observations => observations.ToArray();
        public int[] AffectedRows => affectedRows.ToArray();

        public Task WaitForBothCasCommandsAsync(TimeSpan timeout) => bothCasCommands.Task.WaitAsync(timeout);

        public void ReleaseCasCommands() => releaseCasCommands.TrySetResult(true);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!TryReadComparedUpdatedAt(command, out var comparedUpdatedAt)) return result;
            if (comparedUpdatedAt != expectedPersistedToken)
                throw new InvalidOperationException("The CAS predicate did not use the pre-read persisted UpdatedAt token.");

            var transaction = command.Transaction ?? throw new InvalidOperationException("The version CAS must execute inside its claim transaction.");
            var connection = command.Connection ?? throw new InvalidOperationException("The version CAS command must have an open connection.");
            var transactionConnection = transaction.Connection;
            observations.Enqueue(new PostgreSqlVersionCasObservation(
                transaction,
                connection,
                comparedUpdatedAt,
                transactionConnection is not null
                    && transactionConnection.State == ConnectionState.Open
                    && connection.State == ConnectionState.Open
                    && ReferenceEquals(transactionConnection, connection)));

            var count = Interlocked.Increment(ref casCommandCount);
            if (count == 2) bothCasCommands.TrySetResult(true);
            if (count > 2) throw new InvalidOperationException("Expected exactly two version CAS commands in the PostgreSQL contention test.");

            await releaseCasCommands.Task.WaitAsync(cancellationToken);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (TryReadComparedUpdatedAt(command, out _)) affectedRows.Enqueue(result);
            return ValueTask.FromResult(result);
        }

        private bool TryReadComparedUpdatedAt(DbCommand command, out DateTime comparedUpdatedAt)
        {
            comparedUpdatedAt = default;
            var sql = command.CommandText;
            if (!sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("MovieProductionVersions", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("UpdatedAt", StringComparison.OrdinalIgnoreCase))
                return false;

            var whereIndex = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            if (whereIndex < 0) return false;
            var match = UpdatedAtPredicate.Match(sql[whereIndex..]);
            if (!match.Success) return false;

            var parameterToken = match.Groups["parameter"].Value;
            DbParameter parameter;
            if (parameterToken.StartsWith('$')
                && int.TryParse(parameterToken.AsSpan(1), out var ordinal)
                && ordinal > 0
                && ordinal <= command.Parameters.Count)
            {
                parameter = (DbParameter)command.Parameters[ordinal - 1];
            }
            else
            {
                var expectedName = parameterToken.TrimStart('@', ':');
                parameter = command.Parameters.Cast<DbParameter>().Single(item =>
                    string.Equals(item.ParameterName.TrimStart('@', ':', '$'), expectedName, StringComparison.OrdinalIgnoreCase));
            }

            comparedUpdatedAt = parameter.Value switch
            {
                DateTime value => value,
                DateTimeOffset value => value.UtcDateTime,
                _ => throw new InvalidOperationException("The UpdatedAt CAS predicate must bind a timestamp parameter."),
            };
            return true;
        }
    }

    private sealed record PostgreSqlVersionCasObservation(
        DbTransaction Transaction,
        DbConnection Connection,
        DateTime ComparedUpdatedAt,
        bool TransactionAndConnectionWereOpen);

    private sealed record ApprovedMovieFixture(Guid MovieProjectId, Guid SceneId, Guid ShotId, Guid SourceVersionId, Guid FirstClipId, Guid SecondClipId);
}

public sealed class DisposablePostgresFactAttribute : FactAttribute
{
    public DisposablePostgresFactAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(connectionString))
        {
            Skip = "Requires the disposable PostgreSQL service configured by the API CI job.";
            return;
        }

        try
        {
            if (!string.Equals(new NpgsqlConnectionStringBuilder(connectionString).Database, "taslim_tests", StringComparison.Ordinal))
                Skip = "Runs only against the disposable taslim_tests database configured by API CI.";
        }
        catch (ArgumentException)
        {
            Skip = "The API CI PostgreSQL test connection string is unavailable.";
        }
    }
}

public sealed class ThrowAfterCommitOnceInterceptor : DbTransactionInterceptor
{
    private int armed = 1;

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref armed, 0) == 1)
            throw new InvalidOperationException("Synthetic commit acknowledgement loss after durable commit.");
        return Task.CompletedTask;
    }
}
