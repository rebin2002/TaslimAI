using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>One-shot fault injection at the real queue/transaction boundary.</summary>
public sealed class MovieQueueFailureGate
{
    private int failNext;

    public void Arm() => Interlocked.Exchange(ref failNext, 1);

    public bool ConsumeFailure() => Interlocked.Exchange(ref failNext, 0) == 1;
}

/// <summary>
/// Fails after GenerationJobService has saved the job and created its usage
/// reservation, but before its transaction can enqueue/commit the job.
/// </summary>
public sealed class FailOnceAfterReservationGenerationJobQueue(
    TaslimDbContext db,
    MovieQueueFailureGate gate) : IGenerationJobQueue
{
    public Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (gate.ConsumeFailure())
            throw new InvalidOperationException("Injected queue failure after usage reservation.");

        return new DatabaseGenerationJobQueue(db).EnqueueAsync(jobId, cancellationToken);
    }
}

public sealed class MovieQueueFailureApiFactory : MovieOperationalApiFactory
{
    public MovieQueueFailureGate FailureGate { get; } = new();

    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobQueue>();
            services.AddSingleton(FailureGate);
            services.AddScoped<IGenerationJobQueue>(sp => new FailOnceAfterReservationGenerationJobQueue(
                sp.GetRequiredService<TaslimDbContext>(),
                sp.GetRequiredService<MovieQueueFailureGate>()));
        });
    }
}

public sealed class MovieClipQueueFailureCleanupTests : IClassFixture<MovieQueueFailureApiFactory>
{
    private readonly MovieQueueFailureApiFactory factory;

    public MovieClipQueueFailureCleanupTests(MovieQueueFailureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Failed_scene_clip_queue_rolls_back_reservation_and_same_key_retry_commits_once()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie queue transaction rollback");
        const string key = "movie-queue-rollback-short-clip";
        factory.FailureGate.Arm();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        var input = new { title = "Short clip with queue failure" };
        using (var failed = await MovieOperationalFixtures.SendWithConfirmedMovieQuoteAsync(
            factory,
            client, HttpMethod.Post, path, input, idempotencyKey: key))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == key));
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());

        using (var retried = await MovieOperationalFixtures.SendWithConfirmedMovieQuoteAsync(
            factory,
            client, HttpMethod.Post, path, input, idempotencyKey: key))
            Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);

        var job = await db.GenerationJobs.SingleAsync(item => item.IdempotencyKey == key);
        var clip = await db.MovieClips.SingleAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        Assert.Equal(job.Id, clip.GenerationJobId);
        Assert.Single(await db.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{job.Id:N}").ToListAsync());
    }

    [Fact]
    public async Task Failed_selective_regeneration_rolls_back_reservation_and_same_key_retry_commits_once()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie regeneration transaction rollback", sceneDurationSeconds: 5);
        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"layout\":\"left\"}" });
        await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client,
            $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true, reason = "Storyboard approved." });
        var preview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests",
            new
            {
                actionType = MovieRegenerationActionTypes.ProductionKeyframeCandidate,
                requestedStage = MovieProductionStages.ProductionKeyframe,
                reason = "Rebuild the keyframe while preserving verified continuity.",
                sourceVersionId = storyboard.Id,
                changedInputsJson = "{\"camera\":\"new framing\"}",
                compositionJson = "{\"frame\":\"reframed\"}",
            });
        const string key = "movie-regeneration-queue-rollback";
        factory.FailureGate.Arm();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var path = $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm";
        using (var failed = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { confirm = true }, idempotencyKey: key))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation,
            await db.MovieRegenerationRequests.Where(item => item.Id == preview.Request.Id)
                .Select(item => item.Status).SingleAsync());
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == key));
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());

        using (var retried = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { confirm = true }, idempotencyKey: key))
            Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);

        Assert.Equal(MovieRegenerationStatuses.Confirmed,
            await db.MovieRegenerationRequests.Where(item => item.Id == preview.Request.Id)
                .Select(item => item.Status).SingleAsync());
        var job = await db.GenerationJobs.SingleAsync(item => item.IdempotencyKey == key);
        var clip = await db.MovieClips.SingleAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        Assert.Equal(job.Id, clip.GenerationJobId);
        Assert.Single(await db.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{job.Id:N}").ToListAsync());
    }
}
