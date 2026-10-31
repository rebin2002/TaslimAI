using System.Collections.Concurrent;
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

/// <summary>
/// Provider-free gate at the real job-creation seam. Both requests first save
/// their own speculative MovieClip before either creates the idempotent job.
/// The canonical request commits its job before the conflicting one proceeds.
/// </summary>
public sealed class MovieClipCreationCollisionGate
{
    private readonly ConcurrentDictionary<string, CollisionRendezvous> rendezvousByKey =
        new(StringComparer.Ordinal);

    public void Register(string targetKey)
    {
        if (!rendezvousByKey.TryAdd(targetKey, new CollisionRendezvous()))
            throw new InvalidOperationException("A collision rendezvous is already registered for this test key.");
    }

    public Task WaitForFirstArrivalAsync(string targetKey) =>
        rendezvousByKey.TryGetValue(targetKey, out var rendezvous)
            ? rendezvous.WaitForFirstArrivalAsync()
            : throw new InvalidOperationException("No collision rendezvous is registered for this test key.");

    public Task<GenerationJob> RunAsync(
        string? key, Func<Task<GenerationJob>> create, CancellationToken cancellationToken)
    {
        if (key is null || !rendezvousByKey.TryGetValue(key, out var rendezvous))
            return create();
        return rendezvous.RunAsync(create, cancellationToken);
    }

    private sealed class CollisionRendezvous
    {
        private readonly TaskCompletionSource<bool> firstArrived = NewSignal();
        private readonly TaskCompletionSource<bool> secondArrived = NewSignal();
        private readonly TaskCompletionSource<bool> firstCommitted = NewSignal();
        private int arrivals;

        public Task WaitForFirstArrivalAsync() => firstArrived.Task;

        public async Task<GenerationJob> RunAsync(
            Func<Task<GenerationJob>> create, CancellationToken cancellationToken)
        {
            var arrival = Interlocked.Increment(ref arrivals);
            if (arrival == 1)
            {
                firstArrived.TrySetResult(true);
                await secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                try
                {
                    return await create();
                }
                finally
                {
                    firstCommitted.TrySetResult(true);
                }
            }
            if (arrival == 2)
            {
                secondArrived.TrySetResult(true);
                await firstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return await create();
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class MovieClipCollisionGenerationJobService(
    GenerationJobService inner, MovieClipCreationCollisionGate gate) : IGenerationJobService
{
    public Task<GenerationJob> CreateAsync(
        Guid userId, CreateGenerationJobRequest request,
        CancellationToken cancellationToken = default, string? idempotencyKey = null,
        string? requestId = null, Guid? retryOfJobId = null) =>
        gate.RunAsync(idempotencyKey,
            () => inner.CreateAsync(userId, request, cancellationToken, idempotencyKey, requestId, retryOfJobId),
            cancellationToken);

    public Task<GenerationJob?> RetryAsync(
        Guid userId, Guid jobId, CancellationToken cancellationToken = default,
        string? idempotencyKey = null, string? requestId = null) =>
        inner.RetryAsync(userId, jobId, cancellationToken, idempotencyKey, requestId);

    public Task<GenerationJob?> GetAsync(
        Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(userId, jobId, cancellationToken);

    public Task<GenerationJobListDto?> ListAsync(
        Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) =>
        inner.ListAsync(userId, filter, cancellationToken);

    public Task<GenerationJobCancelResult> CancelAsync(
        Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.CancelAsync(userId, jobId, cancellationToken);
}

public sealed class MovieClipCollisionApiFactory : MovieOperationalApiFactory
{
    public MovieClipCreationCollisionGate Gate { get; } = new();
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            // Test-only decorator. Keep the real job service, real SQLite
            // transaction, admission/cost checks and usage reservation.
            services.RemoveAll<IGenerationJobService>();
            services.AddSingleton(Gate);
            services.AddScoped<IGenerationJobService>(sp =>
                new MovieClipCollisionGenerationJobService(
                    ActivatorUtilities.CreateInstance<GenerationJobService>(sp),
                    sp.GetRequiredService<MovieClipCreationCollisionGate>()));
        });
    }
}

public sealed class MovieStudioConcurrentIdempotencyTests : IClassFixture<MovieClipCollisionApiFactory>
{
    private readonly MovieClipCollisionApiFactory factory;

    public MovieStudioConcurrentIdempotencyTests(MovieClipCollisionApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Simultaneous_conflicting_same_key_creates_one_canonical_clip_job_and_usage_reservation()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie simultaneous idempotency collision");
        var key = $"movie-barrier-different-requests-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        factory.Gate.Register(key);
        var canonicalRequest = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Canonical race clip" });
        var conflictingRequest = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Different title on same key" });

        // The canonical request stops after persisting its speculative clip,
        // before the generation job exists. Only then does the conflicting
        // request enter, so both definitely cross the lookup-to-create gap.
        var canonicalTask = MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, canonicalRequest,
            idempotencyKey: key);
        await factory.Gate.WaitForFirstArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(40));

        var conflictingTask = MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, conflictingRequest, idempotencyKey: key);

        await Task.WhenAll(canonicalTask, conflictingTask)
            .WaitAsync(TimeSpan.FromSeconds(90));
        using var canonical = await canonicalTask;
        using var conflicting = await conflictingTask;
        Assert.Equal(System.Net.HttpStatusCode.Accepted, canonical.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = Assert.Single(await db.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync());
        Assert.Equal("Canonical race clip", job.Title);
        var canonicalClip = Assert.Single(await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(job.Id, canonicalClip.GenerationJobId);

        // The losing request must not reserve credits or mutate the winner's
        // reservation. The real usage service uses generation:{job-id} as its
        // idempotent transaction request id; only the canonical job has one.
        var usage = await db.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{job.Id:N}").ToListAsync();
        Assert.Single(usage);
    }

    [Fact]
    public async Task Simultaneous_identical_same_key_replays_one_canonical_clip_job_and_usage_reservation()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie simultaneous identical request replay");
        var key = $"movie-barrier-identical-requests-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        factory.Gate.Register(key);
        var request = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Identical race clip" });
        var providerCallsBefore = factory.Calls.Calls(FakeProviderKind.Movie);
        Assert.Equal(0, providerCallsBefore);

        // Both requests cross the initial job lookup after saving distinct
        // speculative clips. The gate commits the first job before releasing
        // the second CreateAsync, exercising the canonical-job reconciliation
        // path for an identical user intent and different server clip IDs.
        var firstTask = MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, request, idempotencyKey: key);
        await factory.Gate.WaitForFirstArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(40));
        var replayTask = MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, request, idempotencyKey: key);

        await Task.WhenAll(firstTask, replayTask).WaitAsync(TimeSpan.FromSeconds(90));
        using var first = await firstTask;
        using var replay = await replayTask;
        var firstBody = await first.Content.ReadAsStringAsync();
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, replay.StatusCode);

        using var firstJson = JsonDocument.Parse(firstBody);
        using var replayJson = JsonDocument.Parse(replayBody);
        var firstJobId = firstJson.RootElement.GetProperty("job").GetProperty("id").GetGuid();
        var replayJobId = replayJson.RootElement.GetProperty("job").GetProperty("id").GetGuid();
        var firstClipId = firstJson.RootElement.GetProperty("clipId").GetGuid();
        var replayClipId = replayJson.RootElement.GetProperty("clipId").GetGuid();
        Assert.Equal(firstJobId, replayJobId);
        Assert.Equal(firstClipId, replayClipId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = Assert.Single(await db.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync());
        Assert.Equal(firstJobId, job.Id);
        var clip = Assert.Single(await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(firstClipId, clip.Id);
        Assert.Equal(job.Id, clip.GenerationJobId);
        using var input = JsonDocument.Parse(job.InputJson);
        Assert.Equal(clip.Id, input.RootElement.GetProperty("MovieClipId").GetGuid());
        var usage = await db.UsageTransactions.AsNoTracking()
            .Where(item => item.GenerationJobId == job.Id).ToListAsync();
        Assert.Single(usage);
        Assert.Equal($"generation:{job.Id:N}", usage[0].RequestId);
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }
}
