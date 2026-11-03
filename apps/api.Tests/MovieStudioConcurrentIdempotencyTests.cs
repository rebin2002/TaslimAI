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

public sealed class MovieContinuitySnapshotFailureGate
{
    private int failAfterNextPersist;
    private int failuresTriggered;

    public bool FailureTriggered => Volatile.Read(ref failuresTriggered) > 0;

    public void FailAfterNextPersist() => Interlocked.Exchange(ref failAfterNextPersist, 1);

    public void ThrowIfArmed()
    {
        if (Interlocked.Exchange(ref failAfterNextPersist, 0) == 1)
        {
            Interlocked.Increment(ref failuresTriggered);
            throw new MovieContinuityPromptLimitException();
        }
    }
}

public sealed class MovieContinuitySnapshotFailureService(
    IMovieCharacterContinuityService inner,
    MovieContinuitySnapshotFailureGate failureGate) : IMovieCharacterContinuityService
{
    public Task<MovieCharacterContinuityProjectionDto?> ProjectAsync(
        Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId,
        CancellationToken cancellationToken = default) =>
        inner.ProjectAsync(userId, movieProjectId, sceneId, shotId, cancellationToken);

    public Task<MovieCharacterContinuitySnapshotDto?> BuildSnapshotAsync(
        Guid userId, Guid movieProjectId, Guid? sceneId, Guid? shotId,
        CancellationToken cancellationToken = default) =>
        inner.BuildSnapshotAsync(userId, movieProjectId, sceneId, shotId, cancellationToken);

    public Task<MovieCharacterContinuitySnapshotDto?> GetSnapshotAsync(
        Guid userId, Guid snapshotId, CancellationToken cancellationToken = default) =>
        inner.GetSnapshotAsync(userId, snapshotId, cancellationToken);

    public async Task<MovieCharacterContinuitySnapshotDto> BuildSnapshotForTargetAsync(
        Guid movieProjectId, Guid? sceneId, Guid? shotId, bool persist,
        CancellationToken cancellationToken = default, int? maximumPromptCharacters = null)
    {
        var snapshot = await inner.BuildSnapshotForTargetAsync(
            movieProjectId, sceneId, shotId, persist, cancellationToken, maximumPromptCharacters);
        if (persist) failureGate.ThrowIfArmed();
        return snapshot;
    }
}

public sealed class MovieClipCollisionApiFactory : MovieOperationalApiFactory
{
    public MovieClipCreationCollisionGate Gate { get; } = new();
    public MovieContinuitySnapshotFailureGate SnapshotFailure { get; } = new();
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
            services.RemoveAll<IMovieCharacterContinuityService>();
            services.AddSingleton(SnapshotFailure);
            services.AddScoped<IMovieCharacterContinuityService>(sp =>
                new MovieContinuitySnapshotFailureService(
                    ActivatorUtilities.CreateInstance<MovieCharacterContinuityService>(sp),
                    sp.GetRequiredService<MovieContinuitySnapshotFailureGate>()));
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

        List<Guid> snapshotsBefore;
        int usageBefore;
        int jobsBefore;
        int clipsBefore;
        using (var baselineScope = factory.Services.CreateScope())
        {
            var db = baselineScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            snapshotsBefore = await db.MovieCharacterContinuitySnapshots.AsNoTracking()
                .Where(item => item.MovieProjectId == fixture.Project.Project.Id)
                .Select(item => item.Id).ToListAsync();
            usageBefore = await db.UsageTransactions.CountAsync();
            jobsBefore = await db.GenerationJobs.CountAsync();
            clipsBefore = await db.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
        }
        var providerCallsBefore = factory.Calls.Calls(FakeProviderKind.Movie);

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
        var canonicalBody = await canonical.Content.ReadAsStringAsync();
        var conflictingBody = await conflicting.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.Accepted, canonical.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", conflictingBody, StringComparison.Ordinal);

        using var canonicalJson = JsonDocument.Parse(canonicalBody);
        var responseJobId = canonicalJson.RootElement.GetProperty("job").GetProperty("id").GetGuid();
        var responseClipId = canonicalJson.RootElement.GetProperty("clipId").GetGuid();

        Guid canonicalJobId;
        Guid canonicalClipId;
        Guid canonicalSnapshotId;
        int usageAfterCollision;
        int jobsAfterCollision;
        int clipsAfterCollision;
        List<Guid> snapshotsAfterCollision;
        using (var collisionScope = factory.Services.CreateScope())
        {
            var db = collisionScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = Assert.Single(await db.GenerationJobs.AsNoTracking()
                .Where(item => item.IdempotencyKey == key).ToListAsync());
            Assert.Equal("Canonical race clip", job.Title);
            canonicalJobId = job.Id;
            Assert.Equal(canonicalJobId, responseJobId);

            var clip = Assert.Single(await db.MovieClips.AsNoTracking()
                .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
            canonicalClipId = clip.Id;
            Assert.Equal(canonicalClipId, responseClipId);
            Assert.Equal(canonicalJobId, clip.GenerationJobId);

            var snapshotsForShot = await db.MovieCharacterContinuitySnapshots.AsNoTracking()
                .Where(item => item.MovieProjectId == fixture.Project.Project.Id
                    && item.MovieShotId == fixture.Shot.Id)
                .ToListAsync();
            var canonicalSnapshot = Assert.Single(snapshotsForShot);
            canonicalSnapshotId = canonicalSnapshot.Id;
            Assert.Equal((Guid?)canonicalSnapshotId, clip.ContinuitySnapshotId);

            var usage = await db.UsageTransactions.AsNoTracking()
                .Where(item => item.RequestId == $"generation:{canonicalJobId:N}").ToListAsync();
            Assert.Single(usage);
            usageAfterCollision = await db.UsageTransactions.CountAsync();
            jobsAfterCollision = await db.GenerationJobs.CountAsync();
            clipsAfterCollision = await db.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsAfterCollision = await db.MovieCharacterContinuitySnapshots.AsNoTracking()
                .Where(item => item.MovieProjectId == fixture.Project.Project.Id)
                .Select(item => item.Id).ToListAsync();
        }

        using (var replay = await MovieOperationalFixtures.SendWithCsrfAsync(
                   client, HttpMethod.Post, path, canonicalRequest, idempotencyKey: key))
        {
            Assert.Equal(System.Net.HttpStatusCode.Accepted, replay.StatusCode);
            using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(canonicalJobId,
                replayJson.RootElement.GetProperty("job").GetProperty("id").GetGuid());
            Assert.Equal(canonicalClipId,
                replayJson.RootElement.GetProperty("clipId").GetGuid());
        }

        int usageAfterReplay;
        int jobsAfterReplay;
        int clipsAfterReplay;
        List<Guid> snapshotsAfterReplay;
        int orphanedNewSnapshots;
        using (var replayScope = factory.Services.CreateScope())
        {
            var db = replayScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var finalJob = Assert.Single(await db.GenerationJobs.AsNoTracking()
                .Where(item => item.IdempotencyKey == key).ToListAsync());
            Assert.Equal(canonicalJobId, finalJob.Id);
            var finalClip = Assert.Single(await db.MovieClips.AsNoTracking()
                .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
            Assert.Equal(canonicalClipId, finalClip.Id);
            Assert.Equal(canonicalJobId, finalClip.GenerationJobId);
            Assert.Equal((Guid?)canonicalSnapshotId, finalClip.ContinuitySnapshotId);

            var usage = await db.UsageTransactions.AsNoTracking()
                .Where(item => item.RequestId == $"generation:{canonicalJobId:N}").ToListAsync();
            Assert.Single(usage);
            usageAfterReplay = await db.UsageTransactions.CountAsync();
            jobsAfterReplay = await db.GenerationJobs.CountAsync();
            clipsAfterReplay = await db.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsAfterReplay = await db.MovieCharacterContinuitySnapshots.AsNoTracking()
                .Where(item => item.MovieProjectId == fixture.Project.Project.Id)
                .Select(item => item.Id).ToListAsync();

            var retainedSnapshotIds = new HashSet<Guid>();
            foreach (var id in await db.MovieClips.AsNoTracking()
                         .Where(item => item.MovieProjectId == fixture.Project.Project.Id
                             && item.ContinuitySnapshotId != null)
                         .Select(item => item.ContinuitySnapshotId!.Value).ToListAsync())
                retainedSnapshotIds.Add(id);
            foreach (var id in await db.MovieProductionVersions.AsNoTracking()
                         .Where(item => item.MovieShotId == fixture.Shot.Id
                             && item.ContinuitySnapshotId != null)
                         .Select(item => item.ContinuitySnapshotId!.Value).ToListAsync())
                retainedSnapshotIds.Add(id);
            foreach (var id in await db.MovieProductionKitReferences.AsNoTracking()
                         .Where(item => item.ReferenceType == MovieProductionKitReferenceTypes.ContinuitySnapshot)
                         .Select(item => item.SourceId).ToListAsync())
                retainedSnapshotIds.Add(id);

            var newSnapshotIds = snapshotsAfterReplay.Except(snapshotsBefore).ToArray();
            orphanedNewSnapshots = newSnapshotIds.Count(id => !retainedSnapshotIds.Contains(id));
            Assert.Single(newSnapshotIds);
            Assert.Single(snapshotsAfterCollision.Except(snapshotsBefore));
            Assert.Equal(snapshotsAfterCollision.OrderBy(id => id), snapshotsAfterReplay.OrderBy(id => id));
            Assert.Equal(0, orphanedNewSnapshots);
        }

        // The losing request must neither persist a second snapshot nor create
        // another job/reservation; replay must preserve the winner unchanged.
        Assert.Equal(1, jobsAfterReplay - jobsBefore);
        Assert.Equal(1, clipsAfterReplay - clipsBefore);
        Assert.Equal(1, usageAfterReplay - usageBefore);
        Assert.Equal(usageAfterCollision, usageAfterReplay);
        Assert.Equal(jobsAfterCollision, jobsAfterReplay);
        Assert.Equal(clipsAfterCollision, clipsAfterReplay);
        Assert.Equal(0, orphanedNewSnapshots);
        Assert.Equal(0, providerCallsBefore);
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Conflicting_same_key_preserves_original_error_when_loser_cleanup_fails()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie cleanup failure idempotency collision");
        var key = $"movie-cleanup-failure-different-requests-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        factory.Gate.Register(key);
        var canonicalRequest = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Cleanup failure canonical winner" });
        var conflictingRequest = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Cleanup failure conflicting loser" });

        var jobsBefore = 0;
        var usageBefore = 0;
        var clipsBefore = 0;
        var snapshotsBefore = 0;
        using (var baselineScope = factory.Services.CreateScope())
        {
            var db = baselineScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            jobsBefore = await db.GenerationJobs.CountAsync();
            usageBefore = await db.UsageTransactions.CountAsync();
            clipsBefore = await db.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
        }
        var providerCallsBefore = factory.Calls.Calls(FakeProviderKind.Movie);

        // Force the losing, still-unlinked speculative clip's cleanup DELETE to
        // fail. This test factory owns a private SQLite database.
        using (var triggerScope = factory.Services.CreateScope())
        {
            var db = triggerScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER \"FailMovieClipCleanup\" BEFORE DELETE ON \"MovieClips\" "
                + "WHEN OLD.\"GenerationJobId\" IS NULL "
                + "BEGIN SELECT RAISE(ABORT, 'injected speculative clip cleanup failure'); END;");
        }

        HttpResponseMessage? canonicalResponse = null;
        HttpResponseMessage? conflictingResponse = null;
        Exception? canonicalTransportFailure = null;
        Exception? conflictingTransportFailure = null;
        var conflictingBody = string.Empty;
        Guid? responseJobId = null;
        Guid? responseClipId = null;
        int jobsAfter = 0;
        int usageAfter = 0;
        int clipsAfter = 0;
        int snapshotsAfter = 0;
        int jobsForKey = 0;
        int usagesForCanonicalJob = 0;
        int canonicalClipCount = 0;
        Guid? canonicalJobId = null;
        Guid? canonicalClipId = null;
        Guid? canonicalSnapshotId = null;
        Guid? canonicalClipJobId = null;
        string? canonicalJobTitle = null;
        bool canonicalSnapshotExists = false;

        try
        {
            var canonicalTask = MovieOperationalFixtures.SendWithCsrfAsync(
                client, HttpMethod.Post, path, canonicalRequest, idempotencyKey: key);
            await factory.Gate.WaitForFirstArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(40));
            var conflictingTask = MovieOperationalFixtures.SendWithCsrfAsync(
                client, HttpMethod.Post, path, conflictingRequest, idempotencyKey: key);

            var bothRequests = Task.WhenAll(canonicalTask, conflictingTask);
            try
            {
                await bothRequests.WaitAsync(TimeSpan.FromSeconds(90));
            }
            catch when (bothRequests.IsCompleted)
            {
                // Capture a TestServer transport exception below so every
                // canonical persistence invariant is still inspected.
            }

            if (canonicalTask.IsCompletedSuccessfully)
                canonicalResponse = await canonicalTask;
            else
                canonicalTransportFailure = canonicalTask.Exception?.GetBaseException();
            if (conflictingTask.IsCompletedSuccessfully)
                conflictingResponse = await conflictingTask;
            else
                conflictingTransportFailure = conflictingTask.Exception?.GetBaseException();
            if (conflictingResponse is not null)
                conflictingBody = await conflictingResponse.Content.ReadAsStringAsync();

            using var verificationScope = factory.Services.CreateScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var matchingJobs = await verificationDb.GenerationJobs.AsNoTracking()
                .Where(item => item.IdempotencyKey == key).ToListAsync();
            jobsForKey = matchingJobs.Count;
            var canonicalJob = matchingJobs.FirstOrDefault();
            if (canonicalJob is not null)
            {
                canonicalJobId = canonicalJob.Id;
                canonicalJobTitle = canonicalJob.Title;
                var canonicalClips = await verificationDb.MovieClips.AsNoTracking()
                    .Where(item => item.MovieProjectId == fixture.Project.Project.Id
                        && item.GenerationJobId == canonicalJob.Id).ToListAsync();
                canonicalClipCount = canonicalClips.Count;
                var canonicalClip = canonicalClips.FirstOrDefault();
                if (canonicalClip is not null)
                {
                    canonicalClipId = canonicalClip.Id;
                    canonicalClipJobId = canonicalClip.GenerationJobId;
                    canonicalSnapshotId = canonicalClip.ContinuitySnapshotId;
                    if (canonicalSnapshotId is Guid snapshotId)
                        canonicalSnapshotExists = await verificationDb.MovieCharacterContinuitySnapshots
                            .AsNoTracking().AnyAsync(item => item.Id == snapshotId
                                && item.MovieProjectId == fixture.Project.Project.Id
                                && item.MovieShotId == fixture.Shot.Id);
                }
                usagesForCanonicalJob = await verificationDb.UsageTransactions.AsNoTracking()
                    .CountAsync(item => item.GenerationJobId == canonicalJob.Id);
            }

            jobsAfter = await verificationDb.GenerationJobs.CountAsync();
            usageAfter = await verificationDb.UsageTransactions.CountAsync();
            clipsAfter = await verificationDb.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsAfter = await verificationDb.MovieCharacterContinuitySnapshots.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);

            if (canonicalResponse is not null && canonicalResponse.IsSuccessStatusCode)
            {
                using var responseJson = JsonDocument.Parse(
                    await canonicalResponse.Content.ReadAsStringAsync());
                responseJobId = responseJson.RootElement.GetProperty("job").GetProperty("id").GetGuid();
                responseClipId = responseJson.RootElement.GetProperty("clipId").GetGuid();
            }
        }
        finally
        {
            using var cleanupScope = factory.Services.CreateScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await cleanupDb.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS \"FailMovieClipCleanup\";");
        }

        var originalErrorPreserved = conflictingResponse?.StatusCode
                == System.Net.HttpStatusCode.BadRequest
            && conflictingBody.Contains("IDEMPOTENCY_KEY_REUSED", StringComparison.Ordinal);
        var canonicalReplyPreserved = canonicalResponse?.StatusCode
                == System.Net.HttpStatusCode.Accepted
            && responseJobId == canonicalJobId
            && responseClipId == canonicalClipId;
        var canonicalRecordsPreserved = jobsForKey == 1
            && jobsAfter == jobsBefore + 1
            && canonicalJobTitle == "Cleanup failure canonical winner"
            && canonicalClipCount == 1
            && canonicalClipJobId == canonicalJobId
            && canonicalSnapshotId.HasValue
            && canonicalSnapshotExists;
        var oneReservationPreserved = usageAfter == usageBefore + 1
            && usagesForCanonicalJob == 1;
        var failedCleanupWasExercised = clipsAfter == clipsBefore + 2
            && snapshotsAfter == snapshotsBefore + 2;
        var providerCallsUnchanged = factory.Calls.Calls(FakeProviderKind.Movie)
            == providerCallsBefore;

        var details = $"canonicalStatus={(int?)canonicalResponse?.StatusCode}, "
            + $"conflictingStatus={(int?)conflictingResponse?.StatusCode}, "
            + $"conflictingBody={conflictingBody}, "
            + $"canonicalTransportFailure={canonicalTransportFailure?.GetType().Name}, "
            + $"conflictingTransportFailure={conflictingTransportFailure?.GetType().Name}, "
            + $"jobsForKey={jobsForKey}/{jobsBefore + 1}, canonicalClips={canonicalClipCount}, "
            + $"canonicalJob={canonicalJobId}, canonicalClip={canonicalClipId}, "
            + $"canonicalSnapshot={canonicalSnapshotId}, snapshotExists={canonicalSnapshotExists}, "
            + $"usageDelta={usageAfter - usageBefore}, "
            + $"clipDelta={clipsAfter - clipsBefore}, snapshotDelta={snapshotsAfter - snapshotsBefore}, "
            + $"providerCalls={factory.Calls.Calls(FakeProviderKind.Movie) - providerCallsBefore}";

        canonicalResponse?.Dispose();
        conflictingResponse?.Dispose();
        Assert.True(originalErrorPreserved && canonicalReplyPreserved
            && canonicalRecordsPreserved && oneReservationPreserved
            && failedCleanupWasExercised && providerCallsUnchanged, details);
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
        var snapshots = await db.MovieCharacterContinuitySnapshots.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id
                && item.MovieShotId == fixture.Shot.Id)
            .ToListAsync();
        var canonicalSnapshot = Assert.Single(snapshots);
        Assert.Equal((Guid?)canonicalSnapshot.Id, clip.ContinuitySnapshotId);
        using var input = JsonDocument.Parse(job.InputJson);
        Assert.Equal(clip.Id, input.RootElement.GetProperty("MovieClipId").GetGuid());
        var usage = await db.UsageTransactions.AsNoTracking()
            .Where(item => item.GenerationJobId == job.Id).ToListAsync();
        Assert.Single(usage);
        Assert.Equal($"generation:{job.Id:N}", usage[0].RequestId);
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Failure_after_snapshot_save_rolls_back_before_speculative_clip_persistence()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie snapshot rollback boundary");
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        var request = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Snapshot rollback clip" });
        var clipsBefore = 0;
        var snapshotsBefore = 0;
        var jobsBefore = 0;
        var usageBefore = 0;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            clipsBefore = await db.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id);
            jobsBefore = await db.GenerationJobs.CountAsync();
            usageBefore = await db.UsageTransactions.CountAsync();
        }

        factory.SnapshotFailure.FailAfterNextPersist();
        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, request);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MOVIE_CLIP_PROMPT_UNSUPPORTED",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(factory.SnapshotFailure.FailureTriggered);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(clipsBefore, await verificationDb.MovieClips.CountAsync(
            item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(snapshotsBefore, await verificationDb.MovieCharacterContinuitySnapshots.CountAsync(
            item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await verificationDb.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await verificationDb.UsageTransactions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }
}
