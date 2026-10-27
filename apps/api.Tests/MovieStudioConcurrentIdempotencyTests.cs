using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free gate at the real job-creation seam. Both requests first save
/// their own speculative MovieClip before either creates the idempotent job.
/// The canonical request commits its job before the conflicting one proceeds.
/// </summary>
public sealed class MovieClipCreationCollisionGate
{
    public string? TargetKey { get; set; }
    public TaskCompletionSource<bool> FirstArrived { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> SecondArrived { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> FirstCommitted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivals;

    public async Task<GenerationJob> RunAsync(
        string? key, Func<Task<GenerationJob>> create, CancellationToken cancellationToken)
    {
        if (!string.Equals(TargetKey, key, StringComparison.Ordinal) || TargetKey is null)
            return await create();

        var arrival = Interlocked.Increment(ref arrivals);
        if (arrival == 1)
        {
            FirstArrived.TrySetResult(true);
            await SecondArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            try
            {
                return await create();
            }
            finally
            {
                FirstCommitted.TrySetResult(true);
            }
        }
        if (arrival == 2)
        {
            SecondArrived.TrySetResult(true);
            await FirstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        return await create();
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
        const string key = "movie-barrier-different-requests-1";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        factory.Gate.TargetKey = key;

        // The canonical request stops after persisting its speculative clip,
        // before the generation job exists. Only then does the conflicting
        // request enter, so both definitely cross the lookup-to-create gap.
        var canonicalTask = MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { title = "Canonical race clip" },
            idempotencyKey: key);
        await factory.Gate.FirstArrived.Task.WaitAsync(TimeSpan.FromSeconds(40));

        var conflictingTask = MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new
            {
                title = "Canonical race clip",
                targetResolution = "4k",
            }, idempotencyKey: key);

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
}
