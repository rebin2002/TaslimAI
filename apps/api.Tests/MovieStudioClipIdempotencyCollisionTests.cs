using System.Collections.Concurrent;
using System.Net;
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
/// Provider-free rendezvous at the real job-creation seam. Both requests must first
/// persist their own speculative MovieClip, because the legacy clip queue writes the clip
/// row before it claims the idempotency key. Parking the first arrival inside job creation
/// until the second arrival happens, and then releasing the second only after the first job
/// committed, turns the lookup-to-create race into a fixed interleaving instead of a
/// timing-sensitive one.
/// </summary>
public sealed class MovieClipIdempotencyCollisionGate
{
    private readonly ConcurrentDictionary<string, CollisionRendezvous> rendezvousByKey = new(StringComparer.Ordinal);

    public void Register(string targetKey) =>
        rendezvousByKey[targetKey] = new CollisionRendezvous();

    public Task<GenerationJob> RunAsync(
        string? key, Func<Task<GenerationJob>> create, CancellationToken cancellationToken) =>
        key is not null && rendezvousByKey.TryGetValue(key, out var rendezvous)
            ? rendezvous.RunAsync(create, cancellationToken)
            : create();

    private sealed class CollisionRendezvous
    {
        private readonly TaskCompletionSource<bool> firstArrived = NewSignal();
        private readonly TaskCompletionSource<bool> secondArrived = NewSignal();
        private readonly TaskCompletionSource<bool> firstCommitted = NewSignal();
        private int arrivals;

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
                await firstArrivalConfirmedAsync(cancellationToken);
                await firstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await create();
        }

        private async Task firstArrivalConfirmedAsync(CancellationToken cancellationToken) =>
            await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Test-only decorator. It keeps the real job service, the real SQLite transaction, the
/// real cost guardrail and the real usage reservation, and only adds the rendezvous above.
/// </summary>
public sealed class MovieClipIdempotencyCollisionJobService(
    GenerationJobService inner,
    MovieClipIdempotencyCollisionGate gate) : IGenerationJobService
{
    public Task<GenerationJob> CreateAsync(
        Guid userId,
        CreateGenerationJobRequest request,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null,
        string? requestId = null,
        Guid? retryOfJobId = null) =>
        gate.RunAsync(
            idempotencyKey,
            () => inner.CreateAsync(userId, request, cancellationToken, idempotencyKey, requestId, retryOfJobId),
            cancellationToken);

    public Task<GenerationJob> CreatePendingMovieProductionAsync(
        Guid userId, CreateGenerationJobRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default, string? requestId = null) =>
        inner.CreatePendingMovieProductionAsync(userId, request, idempotencyKey, cancellationToken, requestId);

    public Task<GenerationJob?> PublishPendingMovieProductionAsync(
        Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.PublishPendingMovieProductionAsync(userId, jobId, cancellationToken);

    public Task<bool> AbortPendingMovieProductionAsync(
        Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.AbortPendingMovieProductionAsync(userId, jobId, cancellationToken);

    public Task<GenerationJob?> RetryAsync(
        Guid userId, Guid jobId, CancellationToken cancellationToken = default,
        string? idempotencyKey = null, string? requestId = null) =>
        inner.RetryAsync(userId, jobId, cancellationToken, idempotencyKey, requestId);

    public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(userId, jobId, cancellationToken);

    public Task<GenerationJobListDto?> ListAsync(
        Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) =>
        inner.ListAsync(userId, filter, cancellationToken);

    public Task<GenerationJobCancelResult> CancelAsync(
        Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
        inner.CancelAsync(userId, jobId, cancellationToken);
}

public sealed class MovieClipIdempotencyCollisionApiFactory : MovieClipIdempotencyNoWorkerApiFactory
{
    public MovieClipIdempotencyCollisionGate Gate { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobService>();
            services.AddSingleton(Gate);
            services.AddScoped<IGenerationJobService>(sp => new MovieClipIdempotencyCollisionJobService(
                ActivatorUtilities.CreateInstance<GenerationJobService>(sp),
                sp.GetRequiredService<MovieClipIdempotencyCollisionGate>()));
        });
    }
}

/// <summary>
/// Deterministic coverage for the clip-save/job-save race on one idempotency key: the
/// window no sequential test can reach, because both requests have to pass the replay
/// lookup before either job row exists.
/// </summary>
public sealed class MovieStudioClipIdempotencyCollisionTests : IClassFixture<MovieClipIdempotencyCollisionApiFactory>
{
    private readonly MovieClipIdempotencyCollisionApiFactory factory;

    public MovieStudioClipIdempotencyCollisionTests(MovieClipIdempotencyCollisionApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Simultaneous_identical_requests_replay_one_canonical_clip_job_and_reservation()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie simultaneous identical replay");
        var key = $"movie-collision-identical-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        var payload = new { title = "Simultaneous canonical clip" };
        factory.Gate.Register(key);
        var clipsBefore = await ClipCountAsync(fixture.Project.Project.Id);

        var responses = await Task.WhenAll(
            MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, payload, null, key),
            MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, payload, null, key));

        var bodies = await Task.WhenAll(responses.Select(ReadGenerationAsync));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(bodies[0].ClipId, bodies[1].ClipId);
        Assert.Equal(bodies[0].Job.Id, bodies[1].Job.Id);

        // One paid generation: one job, one clip and one reservation. The losing request
        // must leave no speculative clip behind, and the canonical clip must point at the
        // job that was priced for it.
        Assert.Equal(clipsBefore + 1, await ClipCountAsync(fixture.Project.Project.Id));
        var canonical = await SingleClipAsync(fixture.Project.Project.Id);
        Assert.Equal(bodies[0].ClipId, canonical.Id);
        Assert.Equal(bodies[0].Job.Id, canonical.GenerationJobId);
        Assert.Equal(1, await JobCountAsync(key));
        Assert.Equal(1, await ReservationCountAsync(bodies[0].Job.Id));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Simultaneous_conflicting_requests_reject_the_loser_and_keep_only_the_canonical_clip()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie simultaneous conflict");
        var key = $"movie-collision-conflict-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        factory.Gate.Register(key);
        var clipsBefore = await ClipCountAsync(fixture.Project.Project.Id);

        var responses = await Task.WhenAll(
            MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "Canonical race clip" }, null, key),
            MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "Different title on the same key" }, null, key));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));

        var statuses = responses.Select(response => response.StatusCode).OrderBy(status => (int)status).ToArray();
        Assert.Equal(new[] { HttpStatusCode.Accepted, HttpStatusCode.BadRequest }, statuses);
        var acceptedIndex = Array.IndexOf(statuses, HttpStatusCode.Accepted);
        var rejectedIndex = 1 - acceptedIndex;
        var accepted = JsonSerializer.Deserialize<MovieStudioGenerationResponse>(bodies[acceptedIndex], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var rejection = JsonDocument.Parse(bodies[rejectedIndex]);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", rejection.RootElement.GetProperty("error").GetProperty("code").GetString());

        // The rejected request had already persisted its own speculative clip before it
        // learned the key was claimed. That row must be gone, and the canonical clip, its
        // job and its single cost reservation must be exactly as the winner wrote them.
        Assert.Equal(clipsBefore + 1, await ClipCountAsync(fixture.Project.Project.Id));
        var canonical = await SingleClipAsync(fixture.Project.Project.Id);
        Assert.Equal(accepted.ClipId, canonical.Id);
        Assert.Equal(accepted.Job.Id, canonical.GenerationJobId);
        Assert.Equal(1, await JobCountAsync(key));
        Assert.Equal(1, await ReservationCountAsync(accepted.Job.Id));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Distinct_keys_racing_on_one_shot_still_queue_their_own_clips()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie distinct keys racing");
        var firstKey = $"movie-distinct-a-{Guid.NewGuid():N}";
        var secondKey = $"movie-distinct-b-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        var clipsBefore = await ClipCountAsync(fixture.Project.Project.Id);

        var responses = await Task.WhenAll(
            MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "First separate take" }, null, firstKey),
            MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "Second separate take" }, null, secondKey));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(ReadGenerationAsync));

        // Reconciliation must not collapse two genuinely different requests.
        Assert.NotEqual(bodies[0].ClipId, bodies[1].ClipId);
        Assert.Equal(clipsBefore + 2, await ClipCountAsync(fixture.Project.Project.Id));
        Assert.Equal(1, await JobCountAsync(firstKey));
        Assert.Equal(1, await JobCountAsync(secondKey));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    private async Task<MovieStudioGenerationResponse> ReadGenerationAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<MovieStudioGenerationResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<int> ClipCountAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieClips.AsNoTracking().CountAsync(item => item.MovieProjectId == movieProjectId);
    }

    private async Task<MovieClip> SingleClipAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clips = await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync();
        return Assert.Single(clips);
    }

    private async Task<int> JobCountAsync(string idempotencyKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.GenerationJobs.AsNoTracking().CountAsync(item => item.IdempotencyKey == idempotencyKey);
    }

    private async Task<int> ReservationCountAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.UsageTransactions.AsNoTracking().CountAsync(item => item.RequestId == $"generation:{jobId:N}");
    }
}
