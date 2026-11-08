using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Tests.ProviderTesting;
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
        var snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync(
            item => item.MovieProjectId == fixture.Project.Project.Id);
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        var input = new { title = "Short clip with queue failure" };
        using (var failed = await MovieOperationalFixtures.SendWithConfirmedMovieQuoteAsync(
            factory,
            client, HttpMethod.Post, path, input, idempotencyKey: key))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(snapshotsBefore, await db.MovieCharacterContinuitySnapshots.CountAsync(
            item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == key));
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());

        using (var retried = await MovieOperationalFixtures.SendWithConfirmedMovieQuoteAsync(
            factory,
            client, HttpMethod.Post, path, input, idempotencyKey: key))
            Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);

        var job = await db.GenerationJobs.SingleAsync(item => item.IdempotencyKey == key);
        var clip = await db.MovieClips.SingleAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        Assert.Equal(job.Id, clip.GenerationJobId);
        var retainedSnapshotId = clip.ContinuitySnapshotId
            ?? throw new InvalidOperationException("The committed clip has no continuity snapshot.");
        Assert.True(await db.MovieCharacterContinuitySnapshots.AsNoTracking().AnyAsync(
            item => item.Id == retainedSnapshotId
                && item.MovieProjectId == fixture.Project.Project.Id
                && item.MovieShotId == fixture.Shot.Id));
        Assert.Equal(snapshotsBefore + 1, await db.MovieCharacterContinuitySnapshots.CountAsync(
            item => item.MovieProjectId == fixture.Project.Project.Id));
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

public sealed class MovieClipCleanupDeleteFailureVisibilityTests
    : IClassFixture<MovieClipCollisionApiFactory>
{
    private readonly MovieClipCollisionApiFactory factory;

    public MovieClipCleanupDeleteFailureVisibilityTests(MovieClipCollisionApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Persistent_conflict_cleanup_delete_failure_is_quarantined_then_reconciled_on_authorized_reads()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie persistent cleanup deletion failure");
        var key = $"movie-persistent-cleanup-delete-failure-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        factory.Gate.Register(key);
        var canonicalRequest = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Cleanup DELETE canonical winner" });
        var conflictingRequest = await MovieOperationalFixtures.BuildConfirmedMovieGenerationRequestAsync(
            factory, path, new { title = "Cleanup DELETE conflicting loser" });

        int clipsBefore;
        int snapshotsBefore;
        int jobsBefore;
        int usageBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            jobsBefore = await db.GenerationJobs.CountAsync();
            usageBefore = await db.UsageTransactions.CountAsync();
        }

        var projectBefore = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(
            client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}");
        var workspaceBefore = await MovieOperationalFixtures.GetAsync<MovieWorkspaceResponse>(
            client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}/workspace?module=scenes");
        var projectVisibleBefore = VisibleProjectClipIds(projectBefore).Count;
        var workspaceVisibleBefore = VisibleWorkspaceClipIds(workspaceBefore).Count;

        using (var triggerScope = factory.Services.CreateScope())
        {
            var db = triggerScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER \"FailMovieClipCleanup\" BEFORE DELETE ON \"MovieClips\" "
                + "WHEN OLD.\"GenerationJobId\" IS NULL "
                + "BEGIN SELECT RAISE(ABORT, 'injected persistent speculative clip cleanup failure'); END;");
        }

        HttpResponseMessage? canonicalResponse = null;
        HttpResponseMessage? conflictingResponse = null;
        var canonicalBody = string.Empty;
        var conflictingBody = string.Empty;
        var retryBody = string.Empty;
        int clipsAfter;
        int snapshotsAfter;
        int jobsAfter;
        int usageAfter;
        int jobsForKey;
        int unlinkedClipsAfter;
        int cleanupPendingClipsAfter;
        Guid canonicalJobId;
        Guid canonicalClipId;
        Guid retryJobId;
        Guid retryClipId;
        Guid? cleanupPendingClipId = null;
        Guid? cleanupPendingSnapshotId = null;
        Guid? canonicalSnapshotId = null;
        bool pendingSnapshotRetained = false;
        HttpStatusCode? retryStatus = null;
        List<Guid> projectVisibleAfter;
        List<Guid> workspaceVisibleAfter;
        try
        {
            var canonicalTask = MovieOperationalFixtures.SendWithCsrfAsync(
                client, HttpMethod.Post, path, canonicalRequest, idempotencyKey: key);
            await factory.Gate.WaitForFirstArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(40));
            var conflictingTask = MovieOperationalFixtures.SendWithCsrfAsync(
                client, HttpMethod.Post, path, conflictingRequest, idempotencyKey: key);
            await Task.WhenAll(canonicalTask, conflictingTask).WaitAsync(TimeSpan.FromSeconds(90));
            canonicalResponse = await canonicalTask;
            conflictingResponse = await conflictingTask;
            canonicalBody = await canonicalResponse.Content.ReadAsStringAsync();
            conflictingBody = await conflictingResponse.Content.ReadAsStringAsync();

            using var canonicalJson = JsonDocument.Parse(canonicalBody);
            canonicalJobId = canonicalJson.RootElement.GetProperty("job").GetProperty("id").GetGuid();
            canonicalClipId = canonicalJson.RootElement.GetProperty("clipId").GetGuid();

            using (var retry = await MovieOperationalFixtures.SendWithCsrfAsync(
                client, HttpMethod.Post, path, canonicalRequest, idempotencyKey: key))
            {
                retryStatus = retry.StatusCode;
                retryBody = await retry.Content.ReadAsStringAsync();
            }
            using var retryJson = JsonDocument.Parse(retryBody);
            retryJobId = retryJson.RootElement.GetProperty("job").GetProperty("id").GetGuid();
            retryClipId = retryJson.RootElement.GetProperty("clipId").GetGuid();

            var projectAfter = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(
                client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}");
            var workspaceAfter = await MovieOperationalFixtures.GetAsync<MovieWorkspaceResponse>(
                client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}/workspace?module=scenes");
            projectVisibleAfter = VisibleProjectClipIds(projectAfter);
            workspaceVisibleAfter = VisibleWorkspaceClipIds(workspaceAfter);

            using var verificationScope = factory.Services.CreateScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            clipsAfter = await verificationDb.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsAfter = await verificationDb.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            jobsAfter = await verificationDb.GenerationJobs.CountAsync();
            usageAfter = await verificationDb.UsageTransactions.CountAsync();
            jobsForKey = await verificationDb.GenerationJobs.CountAsync(item => item.IdempotencyKey == key);
            unlinkedClipsAfter = await verificationDb.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id && item.GenerationJobId == null);
            var cleanupPendingClip = await verificationDb.MovieClips.AsNoTracking()
                .Where(item => item.MovieProjectId == fixture.Project.Project.Id
                    && item.GenerationJobId == null && item.Status == "CleanupPending")
                .Select(item => new { item.Id, item.ContinuitySnapshotId })
                .SingleOrDefaultAsync();
            cleanupPendingClipsAfter = await verificationDb.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id
                    && item.GenerationJobId == null && item.Status == "CleanupPending");
            cleanupPendingClipId = cleanupPendingClip?.Id;
            cleanupPendingSnapshotId = cleanupPendingClip?.ContinuitySnapshotId;
            canonicalSnapshotId = await verificationDb.MovieClips.AsNoTracking()
                .Where(item => item.Id == canonicalClipId)
                .Select(item => item.ContinuitySnapshotId)
                .SingleAsync();
            pendingSnapshotRetained = cleanupPendingSnapshotId is Guid pendingSnapshotId
                && await verificationDb.MovieCharacterContinuitySnapshots.AsNoTracking()
                    .AnyAsync(item => item.Id == pendingSnapshotId);
        }
        finally
        {
            canonicalResponse?.Dispose();
            conflictingResponse?.Dispose();
            using var cleanupScope = factory.Services.CreateScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await cleanupDb.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS \"FailMovieClipCleanup\";");
        }

        var recoveredProject = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(
            client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}");
        var recoveredWorkspace = await MovieOperationalFixtures.GetAsync<MovieWorkspaceResponse>(
            client, $"/api/movie-studio/projects/{fixture.Project.Project.Id}/workspace?module=scenes");
        int clipsAfterRecovery;
        int snapshotsAfterRecovery;
        int unlinkedClipsAfterRecovery;
        int cleanupPendingClipsAfterRecovery;
        using (var recoveryScope = factory.Services.CreateScope())
        {
            var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            clipsAfterRecovery = await recoveryDb.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            snapshotsAfterRecovery = await recoveryDb.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            unlinkedClipsAfterRecovery = await recoveryDb.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id && item.GenerationJobId == null);
            cleanupPendingClipsAfterRecovery = await recoveryDb.MovieClips.CountAsync(
                item => item.MovieProjectId == fixture.Project.Project.Id && item.Status == "CleanupPending");
        }

        var retryRemainsCanonical = canonicalResponse?.StatusCode == HttpStatusCode.Accepted
            && retryStatus == HttpStatusCode.Accepted
            && retryJobId == canonicalJobId && retryClipId == canonicalClipId;
        var originalConflictPreserved = conflictingResponse?.StatusCode == HttpStatusCode.BadRequest
            && conflictingBody.Contains("IDEMPOTENCY_KEY_REUSED", StringComparison.Ordinal);
        var orphanClipIsHidden = cleanupPendingClipId.HasValue
            && projectVisibleAfter.Count == projectVisibleBefore + 1
            && workspaceVisibleAfter.Count == workspaceVisibleBefore + 1
            && projectVisibleAfter.Contains(canonicalClipId)
            && workspaceVisibleAfter.Contains(canonicalClipId)
            && !projectVisibleAfter.Contains(cleanupPendingClipId.Value)
            && !workspaceVisibleAfter.Contains(cleanupPendingClipId.Value);
        var pendingResidueIsQuarantined = clipsAfter == clipsBefore + 2
            && snapshotsAfter == snapshotsBefore + 2
            && unlinkedClipsAfter == 1
            && cleanupPendingClipsAfter == 1
            && cleanupPendingSnapshotId.HasValue
            && pendingSnapshotRetained
            && canonicalSnapshotId.HasValue
            && canonicalSnapshotId != cleanupPendingSnapshotId;
        var cleanupReconcilesAfterDeleteRecovers = clipsAfterRecovery == clipsBefore + 1
            && snapshotsAfterRecovery == snapshotsBefore + 1
            && unlinkedClipsAfterRecovery == 0
            && cleanupPendingClipsAfterRecovery == 0
            && VisibleProjectClipIds(recoveredProject).Count == projectVisibleBefore + 1
            && VisibleWorkspaceClipIds(recoveredWorkspace).Count == workspaceVisibleBefore + 1
            && VisibleProjectClipIds(recoveredProject).Contains(canonicalClipId)
            && VisibleWorkspaceClipIds(recoveredWorkspace).Contains(canonicalClipId);
        var canonicalAccountingUnchanged = jobsForKey == 1
            && jobsAfter == jobsBefore + 1
            && usageAfter == usageBefore + 1
            && factory.Calls.Calls(FakeProviderKind.Movie) == 0;

        var details = $"canonicalStatus={(int?)canonicalResponse?.StatusCode}, retryStatus={(int?)retryStatus}, "
            + $"conflictStatus={(int?)conflictingResponse?.StatusCode}, conflictingBody={conflictingBody}, "
            + $"retryJob={retryJobId}/{canonicalJobId}, retryClip={retryClipId}/{canonicalClipId}, "
            + $"publicProjectClipDelta={projectVisibleAfter.Count - projectVisibleBefore}, "
            + $"publicWorkspaceClipDelta={workspaceVisibleAfter.Count - workspaceVisibleBefore}, "
            + $"persistedClipDelta={clipsAfter - clipsBefore}, unlinkedClips={unlinkedClipsAfter}, cleanupPending={cleanupPendingClipsAfter}, "
            + $"snapshotDelta={snapshotsAfter - snapshotsBefore}, pendingSnapshotRetained={pendingSnapshotRetained}, "
            + $"recoveredClipDelta={clipsAfterRecovery - clipsBefore}, recoveredSnapshotDelta={snapshotsAfterRecovery - snapshotsBefore}, "
            + $"recoveredUnlinked={unlinkedClipsAfterRecovery}, recoveredPending={cleanupPendingClipsAfterRecovery}, jobsForKey={jobsForKey}, "
            + $"usageDelta={usageAfter - usageBefore}, providerCalls={factory.Calls.Calls(FakeProviderKind.Movie)}";

        Assert.True(retryRemainsCanonical && originalConflictPreserved && orphanClipIsHidden
            && pendingResidueIsQuarantined && cleanupReconcilesAfterDeleteRecovers
            && canonicalAccountingUnchanged, details);
    }

    private static List<Guid> VisibleProjectClipIds(MovieStudioProjectDto project) => project.Clips
        .Concat(project.Scenes.SelectMany(scene => scene.Clips.Concat(scene.Shots.SelectMany(shot => shot.Clips))))
        .Select(item => item.Id)
        .Distinct()
        .ToList();

    private static List<Guid> VisibleWorkspaceClipIds(MovieWorkspaceResponse workspace) => workspace.Project.Clips
        .Concat(workspace.Project.Scenes.SelectMany(scene => scene.Clips))
        .Select(item => item.Id)
        .Distinct()
        .ToList();
}
