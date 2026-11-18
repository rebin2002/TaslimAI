using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;
using Xunit.Abstractions;

namespace Taslim.Api.Tests;

// TEST-ONLY, deterministic, provider-free reproduction for issue #696
// ("fix(movie): make failed pre-publication final-assembly queue requests safely retryable").
//
// Scope discipline:
//   * No production source is touched. apps/api/Movies/MovieFinalAssembly.cs stays owned by the
//     #298 lineage (and #305) and is deliberately NOT edited here.
//   * This file claims no path held by #298/#305/#769/#807: it is neither MovieFinalAssemblyTests.cs,
//     MovieFinalAssemblyQueueLinkFaultTests.cs nor MovieFinalAssemblyQueueLinkRecoveryTests.cs, and it
//     does not modify the shared MovieOperationalHarness.cs or any #263 path.
//   * Everything is synthetic: fake job-queue adapter only, in-memory SQLite, no provider call,
//     no customer charge, no live storage, no real data, no migration, no deployment.
//   * Fault injection happens at the queue seam only, which is the pre-publication window the issue
//     describes (assembly persisted as Planned -> jobs.CreateAsync -> assembly/job link -> Queued).
public sealed class MovieFinalAssemblyPrePublicationQueueFaultRecoveryTests(ITestOutputHelper output)
{
    // -------------------------------------------------------------------------------
    // Scenario (a): KNOWN failure. CreateAsync throws before anything durable is
    // committed, so no job exists. The same idempotency key MUST then be safely
    // re-drivable: one canonical assembly, at most one committed job, supported recovery.
    // -------------------------------------------------------------------------------
    [Fact]
    public async Task Known_queue_create_failure_then_same_key_retry_recovers_the_assembly()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var world = await SeedAsync(db);

        var queue = new SyntheticQueue(db, SyntheticQueueOutcome.FailsBeforeAnyDurableJob);
        var service = CreateService(db, queue);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None));

        var stranded = await db.MovieAssemblies.SingleAsync();
        Assert.Equal(MovieAssemblyStatuses.Failed, stranded.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieAssemblyQueueFailed, stranded.LastErrorCode);
        Assert.Null(stranded.GenerationJobId);

        // Known failure: nothing was durably committed, so exactly zero jobs may exist.
        Assert.Equal(0, await db.GenerationJobs.CountAsync());
        Assert.Single(queue.Calls);

        // Same key, same fingerprint, transient fault now gone.
        queue.Outcome = SyntheticQueueOutcome.Succeeds;
        var retry = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);

        output.WriteLine($"first attempt threw {failure.GetType().Name}: {failure.Message}");
        output.WriteLine($"same-key retry returned status={retry!.Status} job={retry.Job?.Id.ToString() ?? "none"} attempts={queue.Calls.Count}");

        // One canonical identity only ...
        Assert.Equal(1, await db.MovieAssemblies.CountAsync());
        // ... and the supported recovery path must actually re-drive the queue instead of
        // returning the stranded terminal row (issue #696 required behavior 1 and 4).
        Assert.Multiple(
            () => Assert.Equal(MovieAssemblyStatuses.Queued, retry.Status),
            () => Assert.NotNull(retry.GenerationJobId),
            () => Assert.Equal(2, queue.Calls.Count));
        Assert.Equal(1, await db.MovieAssemblies.CountAsync());
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        AssertNoCustomerCharge(db);
    }

    // -------------------------------------------------------------------------------
    // Scenario (b): AMBIGUOUS outcome. The job row is durably committed, then the
    // link/response step is lost. A same-key retry must reconcile THAT job and must
    // never create a second one.
    // -------------------------------------------------------------------------------
    [Fact]
    public async Task Ambiguous_persisted_job_is_reconciled_by_same_key_retry_without_duplicate()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var world = await SeedAsync(db);

        var queue = new SyntheticQueue(db, SyntheticQueueOutcome.CommitsJobThenLosesResponse);
        var service = CreateService(db, queue);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None));

        var committed = await db.GenerationJobs.SingleAsync();
        var assembly = await db.MovieAssemblies.SingleAsync();

        // The discriminator this issue asks for: an ambiguous persisted job is observable as
        // a committed, unlinked job, which is NOT the same state as the known failure above.
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        Assert.Null(assembly.GenerationJobId);
        Assert.Equal(MovieAssemblyStatuses.Failed, assembly.Status);
        Assert.Equal(Key, committed.IdempotencyKey);

        queue.Outcome = SyntheticQueueOutcome.Succeeds;
        var retry = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);

        output.WriteLine($"ambiguous same-key retry: status={retry!.Status} linked={retry.GenerationJobId?.ToString() ?? "none"} committed_jobs={await db.GenerationJobs.CountAsync()} queue_calls={queue.Calls.Count}");

        // Required behavior 3: reconcile the ambiguous create/link outcome rather than blindly
        // creating another job, and give the operator the supported recovery path.
        Assert.Equal(1, await db.MovieAssemblies.CountAsync());
        Assert.Multiple(
            () => Assert.Equal(committed.Id, retry.GenerationJobId),
            () => Assert.Equal(MovieAssemblyStatuses.Queued, retry.Status),
            () => Assert.Equal(1, queue.Calls.Count));
        // Above all: no duplicate job may ever appear for this identity.
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        AssertNoCustomerCharge(db);
    }

    // -------------------------------------------------------------------------------
    // Invariant that must hold in BOTH fault classes: repeated same-key requests never
    // duplicate the canonical assembly or the committed job, and never charge a customer.
    // -------------------------------------------------------------------------------
    [Fact]
    public async Task Repeated_same_key_requests_never_duplicate_assembly_job_or_charge()
    {
        foreach (var outcome in new[]
                 { SyntheticQueueOutcome.FailsBeforeAnyDurableJob, SyntheticQueueOutcome.CommitsJobThenLosesResponse })
        {
            await using var connection = await OpenConnectionAsync();
            await using var db = CreateDb(connection);
            var world = await SeedAsync(db);
            var queue = new SyntheticQueue(db, outcome);
            var service = CreateService(db, queue);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var result = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
                    output.WriteLine($"{outcome} attempt {attempt}: returned status={result!.Status} job={result.GenerationJobId?.ToString() ?? "none"}");
                }
                catch (Exception exception)
                {
                    output.WriteLine($"{outcome} attempt {attempt}: threw {exception.GetType().Name}");
                }
            }

            Assert.Equal(1, await db.MovieAssemblies.CountAsync());
            Assert.True(await db.GenerationJobs.CountAsync() <= 1,
                $"{outcome} committed {await db.GenerationJobs.CountAsync()} jobs for one idempotency key");
            AssertNoCustomerCharge(db);
            Assert.All(queue.Calls, call => Assert.Equal(0m, call.EstimatedProviderCostUsd));
        }
    }

    // -------------------------------------------------------------------------------
    // Required behavior 2: a reused key with a different fingerprint is rejected, not
    // silently accepted.
    // -------------------------------------------------------------------------------
    [Fact]
    public async Task Reused_key_with_different_fingerprint_is_rejected()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var world = await SeedAsync(db);

        var queue = new SyntheticQueue(db, SyntheticQueueOutcome.Succeeds);
        var service = CreateService(db, queue);

        var first = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
        Assert.Equal(MovieAssemblyStatuses.Queued, first!.Status);

        var different = AssemblyRequest();
        different.ResolutionProfile = MovieFinalAssemblyProfiles.Hd1080p;

        var exception = await Assert.ThrowsAsync<MovieFinalAssemblyValidationException>(() =>
            service.RequestAsync(world.UserId, world.MovieId, different, Key, CancellationToken.None));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", exception.Code);
        Assert.Equal(1, await db.MovieAssemblies.CountAsync());
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        AssertNoCustomerCharge(db);
    }

    // ------------------------------------------------------------------- plumbing ---
    private const string Key = "issue-696-synthetic-queue-fault";

    private static MovieFinalAssemblyRequest AssemblyRequest() => new()
    {
        ResolutionProfile = MovieFinalAssemblyProfiles.Uhd4K,
        Timeline = [],
        AudioMixInputs = [],
        IncludeApprovedSoundtrackCues = false,
        Captions = new MovieAssemblyCaptionsRequest { Mode = MovieAssemblyCaptionModes.None },
    };

    private static MovieFinalAssemblyService CreateService(TaslimDbContext db, SyntheticQueue queue) =>
        new(db,
            new MovieCollaborationAccess(db, new WorkspaceAccessService(db)),
            queue,
            new NoTransitions());

    private static void AssertNoCustomerCharge(TaslimDbContext db)
    {
        Assert.Equal(0, db.UsageTransactions.Count());
        Assert.Equal(0, db.CreditLedgerEntries.Count());
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed record SeededWorld(Guid UserId, Guid MovieId, Guid ShotId, Guid WorkspaceId);

    private static async Task<SeededWorld> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"i696-{userId:N}", NormalizedUserName = $"I696-{userId:N}".ToUpperInvariant(), DisplayName = "Issue 696 Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Issue 696 Workspace", Slug = $"i696-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = workspaceId,
            UserId = userId,
            OriginalFileName = "synthetic-take.mp4",
            StoredFileName = "synthetic-take.mp4",
            ContentType = "video/mp4",
            Extension = ".mp4",
            SizeBytes = 4_096,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = "synthetic/issue-696.mp4",
            Status = StoredFileStatus.Ready,
            CreatedAt = now,
            ProcessedAt = now,
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            StoredFileId = storedFileId,
            Name = "Synthetic take",
            AssetType = AssetTypes.Video,
            MetadataJson = "{\"width\":3840,\"height\":2160,\"durationSeconds\":6}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Issue 696 Repro", Description = "Synthetic.", DurationSeconds = 6, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Scene", Summary = "Synthetic scene.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Synthetic shot.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var movies = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await movies.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { AssetId = assetId }, CancellationToken.None);
        await movies.ApproveTakeAsync(userId, take!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved }, CancellationToken.None);
        await movies.SelectTakeAsync(userId, take.Id, false, CancellationToken.None);
        return new SeededWorld(userId, movieId, shotId, workspaceId);
    }

    /// <summary>Synthetic fault modes for the pre-publication queue seam.</summary>
    private enum SyntheticQueueOutcome
    {
        Succeeds,

        /// <summary>Known failure: the fault happens before any durable job row exists.</summary>
        FailsBeforeAnyDurableJob,

        /// <summary>Ambiguous outcome: the job commits durably, then the link/response is lost.</summary>
        CommitsJobThenLosesResponse,
    }

    /// <summary>
    /// Fake-only job adapter. It persists into the same in-memory SQLite context so the durable
    /// commit boundary is observable, but it never touches a provider, storage or billing adapter.
    /// </summary>
    private sealed class SyntheticQueue : IGenerationJobService
    {
        private readonly TaslimDbContext db;

        public SyntheticQueue(TaslimDbContext db, SyntheticQueueOutcome outcome)
        {
            this.db = db;
            Outcome = outcome;
        }

        public SyntheticQueueOutcome Outcome { get; set; }

        public List<CreateGenerationJobRequest> Calls { get; } = [];

        public async Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null, Guid? retryOfJobId = null)
        {
            Calls.Add(request);

            if (Outcome == SyntheticQueueOutcome.FailsBeforeAnyDurableJob)
                throw new InvalidOperationException("synthetic #696 fault: queue rejected the request before any job was durable");

            var job = new GenerationJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                CreatedByUserId = userId,
                JobType = request.JobType,
                Status = GenerationJobStatus.Pending,
                Title = request.Title,
                InputJson = request.InputJson,
                IdempotencyKey = idempotencyKey,
                RequestId = requestId,
                RetryOfJobId = retryOfJobId,
                EstimatedProviderCostUsd = request.EstimatedProviderCostUsd,
                CostEstimateJson = null,
                CreatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
            };

            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(CancellationToken.None);

            if (Outcome == SyntheticQueueOutcome.CommitsJobThenLosesResponse)
                throw new TimeoutException("synthetic #696 fault: durable job committed but the create response/link was lost");

            return job;
        }

        private static NotSupportedException Unused(string member) =>
            new($"issue #696 test-only reproduction only reaches the pre-publication queue seam; {member} is out of scope");

        public Task<GenerationJob> CreatePendingMovieProductionAsync(Guid userId, CreateGenerationJobRequest request, string idempotencyKey, CancellationToken cancellationToken = default, string? requestId = null) => throw Unused(nameof(CreatePendingMovieProductionAsync));
        public Task<GenerationJob?> PublishPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw Unused(nameof(PublishPendingMovieProductionAsync));
        public Task<bool> AbortPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw Unused(nameof(AbortPendingMovieProductionAsync));
        public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null) => throw Unused(nameof(RetryAsync));
        public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw Unused(nameof(GetAsync));
        public Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) => throw Unused(nameof(ListAsync));
        public Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw Unused(nameof(CancelAsync));
    }

    private sealed class NoTransitions : IMovieTimelineTransitionService
    {
        public Task<Taslim.Api.Movies.MovieTimelineTransitionEditDto?> GetLatestAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken) => Task.FromResult<Taslim.Api.Movies.MovieTimelineTransitionEditDto?>(null);
        public Task<Taslim.Api.Movies.MovieTimelineTransitionEditDto?> ApplyAsync(Guid userId, Guid movieProjectId, Taslim.Api.Movies.MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("issue #696 reproduction never edits transitions");
    }
}
