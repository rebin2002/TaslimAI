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

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #696 scenario (a): the bounded re-drive of a final assembly that a transient
/// pre-publication queue fault stranded before anything was durably committed.
///
/// #1064 pins the reproduction itself (a same-key retry must converge instead of replaying the
/// failure), and #807 pins the reconciliation of a lost link write. This file deliberately pins
/// only the boundaries that a re-drive adds on top of those, and which neither file covers:
///
/// 1. The re-drive is bounded (required behavior 4): a fault that keeps recurring spends a
///    durable budget and then stops queueing, without ever orphaning a claimed row.
/// 2. Convergence is stable: once a re-drive has put the assembly back in flight, further
///    identical requests replay it and never re-enter the seam.
/// 3. The re-driven job is the very same canonical master export - same assembly identity, same
///    fingerprint, same key, still zero estimated provider cost - and re-driving does not touch
///    AttemptCount, which belongs to the execution seam.
/// 4. A re-drive never queues beside a durable job, not even when reconciliation refuses to
///    adopt that job: the zero-durable-jobs gate, not the budget, is what keeps at most one
///    canonical job for an idempotency key.
///
/// Deterministic and provider-free by construction: SQLite in-memory per test, a local seam that
/// mirrors GenerationJobService.CreateAsync durable-commit and key-reuse semantics, and no
/// provider, model, credential, usage reservation, charging, live storage, migration,
/// PostgreSQL, secret, deployment or queue transport is constructed or reached.
/// </summary>
public sealed class MovieFinalAssemblyQueueRedrive696Tests
{
    private const int ExpectedRedriveBudget = 3;

    // ---------------------------------------------------------------- test 1: bounded ---
    [Fact]
    public async Task Recurring_pre_publication_failure_spends_the_redrive_budget_and_then_stops_queueing()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var world = await SeedAsync(db);
        // The fault never clears: every seam call rejects before anything is durable.
        var queue = new RedriveSeam(db, SeamFault.RejectsBeforeCommit);
        var service = CreateService(db, queue);

        var firstAttempt = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None));
        Assert.NotNull(firstAttempt);
        var stranded = await db.MovieAssemblies.SingleAsync();
        Assert.Equal(MovieAssemblyStatuses.Failed, stranded.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieAssemblyQueueFailed, stranded.LastErrorCode);
        Assert.Equal(1, queue.Calls.Count);

        // Identical retries: each one is allowed to re-drive until the budget is gone, and the
        // client keeps receiving the honest terminal projection instead of a repeated throw.
        MovieFinalAssemblyDto? last = null;
        for (var retry = 1; retry <= ExpectedRedriveBudget + 2; retry++)
        {
            last = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
            Assert.NotNull(last);
        }

        Assert.Equal(MovieAssemblyStatuses.Failed, last!.Status);
        Assert.Null(last.GenerationJobId);
        // One first attempt plus exactly the budget: retries past the bound never re-enter the seam.
        Assert.Equal(1 + ExpectedRedriveBudget, queue.Calls.Count);
        // Every re-drive that failed gave the row back its marker: a claimed Planned row with no
        // durable job and no eligible recovery path is the stranding this issue is about.
        var settled = await db.MovieAssemblies.SingleAsync();
        Assert.Equal(MovieAssemblyStatuses.Failed, settled.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieAssemblyQueueFailed, settled.LastErrorCode);
        Assert.Null(settled.GenerationJobId);
        Assert.Equal(1, await db.MovieAssemblies.CountAsync());
        Assert.Equal(0, await db.GenerationJobs.CountAsync());
        AssertNoCustomerCharge(db);
    }

    // ----------------------------------------------------- test 2: stable convergence ---
    [Fact]
    public async Task Once_the_redrive_put_the_assembly_back_in_flight_identical_requests_stop_calling_the_seam()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var world = await SeedAsync(db);
        var queue = new RedriveSeam(db, SeamFault.RejectsBeforeCommit);
        var service = CreateService(db, queue);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None));
        // The transient fault clears.
        queue.Fault = SeamFault.CommitsDurableJob;

        var recovered = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(MovieAssemblyStatuses.Queued, recovered.Status);
        Assert.NotNull(recovered.GenerationJobId);
        Assert.Equal(2, queue.Calls.Count);

        // A healthy in-flight assembly must never be re-driven again, however many retries arrive.
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var replay = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
            Assert.NotNull(replay);
            Assert.Equal(MovieAssemblyStatuses.Queued, replay.Status);
            Assert.Equal(recovered.GenerationJobId, replay.GenerationJobId);
        }

        Assert.Equal(2, queue.Calls.Count);
        Assert.Equal(1, await db.MovieAssemblies.CountAsync());
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        Assert.Equal(1, await db.MovieAssemblies.CountAsync(item => item.Status == MovieAssemblyStatuses.Queued));
        AssertNoCustomerCharge(db);
    }

    // ------------------------------------------------------- test 3: payload identity ---
    [Fact]
    public async Task Redriven_job_carries_the_same_canonical_request_and_leaves_execution_attempts_untouched()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var world = await SeedAsync(db);
        var queue = new RedriveSeam(db, SeamFault.RejectsBeforeCommit);
        var service = CreateService(db, queue);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None));
        var stranded = await db.MovieAssemblies.SingleAsync();
        Assert.Equal(MovieAssemblyStatuses.Failed, stranded.Status);

        queue.Fault = SeamFault.CommitsDurableJob;
        var recovered = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
        Assert.NotNull(recovered);

        var published = await db.GenerationJobs.SingleAsync();
        // The payload is what the worker executes, so it must describe this exact stranded
        // assembly and nothing else: the re-drive rebuilds the original canonical request.
        var payload = System.Text.Json.JsonSerializer.Deserialize<MovieFinalAssemblyInput>(published.InputJson)!;
        Assert.Multiple(
            () => Assert.Equal(stranded.Id, payload.AssemblyId),
            () => Assert.Equal(world.MovieId, payload.MovieProjectId),
            () => Assert.Equal(stranded.RequestFingerprint, payload.RequestFingerprint),
            () => Assert.Equal(stranded.IdempotencyKey, payload.IdempotencyKey),
            () => Assert.Equal(Key, published.IdempotencyKey),
            () => Assert.Equal(GenerationJobTypes.MovieAssembly, published.JobType),
            () => Assert.Equal(0m, published.EstimatedProviderCostUsd),
            () => Assert.Equal(GenerationJobStatus.Pending, published.Status));
        // Queue re-driving is not an execution attempt: the worker's counter stays where it was.
        var settled = await db.MovieAssemblies.SingleAsync();
        Assert.Equal(0, settled.AttemptCount);
        Assert.Equal(recovered.GenerationJobId, published.Id);
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        AssertNoCustomerCharge(db);
    }

    // ---------------------------------------------------------- test 4: zero-jobs gate ---
    [Fact]
    public async Task Redrive_never_queues_beside_a_durable_job_even_when_recovery_refuses_to_adopt_it()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var world = await SeedAsync(db);
        // Ambiguous outcome first: the job committed durably, then the response was lost.
        var queue = new RedriveSeam(db, SeamFault.CommitsThenLosesResponse);
        var service = CreateService(db, queue);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None));
        var stranded = await db.MovieAssemblies.SingleAsync();
        var committed = await db.GenerationJobs.SingleAsync();
        Assert.Equal(MovieAssemblyStatuses.Failed, stranded.Status);
        Assert.Null(stranded.GenerationJobId);

        // Make that committed job foreign work a reconciliation must refuse, so nothing adopts it.
        committed.InputJson = "{\"operation\":\"final_assembly\",\"requestFingerprint\":\"foreign-unrelated-request\"}";
        await db.SaveChangesAsync();

        queue.Fault = SeamFault.CommitsDurableJob;
        var refused = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
        Assert.NotNull(refused);
        // The issue's hard boundary: at most one committed job per identity. The gate is the
        // durable-job probe, not the budget, so the seam is not re-entered at all here.
        Assert.Equal(1, queue.Calls.Count);
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        Assert.Equal(1, await db.MovieAssemblies.CountAsync());
        Assert.Equal(MovieAssemblyStatuses.Failed, refused.Status);

        // Remove the foreign row entirely and the same request is now provably scenario (a):
        // nothing is durable, so the re-drive is exactly what becomes eligible.
        db.GenerationJobs.Remove(committed);
        await db.SaveChangesAsync();
        var redriven = await service.RequestAsync(world.UserId, world.MovieId, AssemblyRequest(), Key, CancellationToken.None);
        Assert.NotNull(redriven);
        Assert.Equal(MovieAssemblyStatuses.Queued, redriven.Status);
        Assert.Equal(2, queue.Calls.Count);
        Assert.Equal(1, await db.GenerationJobs.CountAsync());
        AssertNoCustomerCharge(db);
    }

    // ------------------------------------------------------------------- plumbing ---
    private const string Key = "issue-696-queue-redrive";

    private static MovieFinalAssemblyRequest AssemblyRequest() => new()
    {
        ResolutionProfile = MovieFinalAssemblyProfiles.Uhd4K,
        Timeline = [],
        AudioMixInputs = [],
        IncludeApprovedSoundtrackCues = false,
        Captions = new MovieAssemblyCaptionsRequest { Mode = MovieAssemblyCaptionModes.None },
    };

    private static MovieFinalAssemblyService CreateService(TaslimDbContext db, RedriveSeam queue) =>
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

    private sealed record SeededWorld(Guid UserId, Guid MovieId, Guid WorkspaceId);

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
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"i696r-{userId:N}", NormalizedUserName = $"I696R-{userId:N}".ToUpperInvariant(), DisplayName = "Issue 696 Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Issue 696 Workspace", Slug = $"i696r-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
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
            StorageKey = "synthetic/issue-696-redrive.mp4",
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
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Issue 696 Redrive", Description = "Synthetic.", DurationSeconds = 6, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Scene", Summary = "Synthetic scene.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Synthetic shot.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var movies = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await movies.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { AssetId = assetId }, CancellationToken.None);
        await movies.ApproveTakeAsync(userId, take!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved }, CancellationToken.None);
        await movies.SelectTakeAsync(userId, take.Id, false, CancellationToken.None);
        return new SeededWorld(userId, movieId, workspaceId);
    }

    /// <summary>Synthetic fault modes for the pre-publication queue seam.</summary>
    private enum SeamFault
    {
        /// <summary>The job commits durably and the call succeeds: the healthy queueing path.</summary>
        CommitsDurableJob,
        /// <summary>Scenario (a): the request is rejected before any job row is durable.</summary>
        RejectsBeforeCommit,
        /// <summary>Scenario (b): the job commits, then the response/link is lost.</summary>
        CommitsThenLosesResponse,
    }

    /// <summary>
    /// Test-only seam. It writes into the same in-memory SQLite context so the durable commit
    /// boundary is observable, and mirrors the two properties the production re-drive relies on:
    /// a key that already committed a job replays that job instead of creating another one, and a
    /// different payload under a used key is refused. No provider, storage or billing adapter is
    /// constructed or reached.
    /// </summary>
    private sealed class RedriveSeam : IGenerationJobService
    {
        private readonly TaslimDbContext db;

        public RedriveSeam(TaslimDbContext db, SeamFault fault)
        {
            this.db = db;
            Fault = fault;
        }

        public SeamFault Fault { get; set; }

        public List<CreateGenerationJobRequest> Calls { get; } = [];

        public async Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null, Guid? retryOfJobId = null)
        {
            Calls.Add(request);
            if (Fault == SeamFault.RejectsBeforeCommit)
                throw new InvalidOperationException("synthetic #696 scenario (a) fault: queue rejected the request before any job was durable");
            var existing = await db.GenerationJobs
                .FirstOrDefaultAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.InputJson, request.InputJson, StringComparison.Ordinal))
                    throw new InvalidOperationException("IDEMPOTENCY_KEY_REUSED");
                return existing;
            }
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
            if (Fault == SeamFault.CommitsThenLosesResponse)
                throw new TimeoutException("synthetic #696 scenario (b) fault: durable job committed but the create response/link was lost");
            return job;
        }

        private static NotSupportedException OutOfScope(string member) =>
            new($"issue #696 scenario (a) re-drive tests only reach the pre-publication queue seam; {member} is out of scope");

        public Task<GenerationJob> CreatePendingMovieProductionAsync(Guid userId, CreateGenerationJobRequest request, string idempotencyKey, CancellationToken cancellationToken = default, string? requestId = null) => throw OutOfScope(nameof(CreatePendingMovieProductionAsync));

        public Task<GenerationJob?> PublishPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw OutOfScope(nameof(PublishPendingMovieProductionAsync));
        public Task<bool> AbortPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw OutOfScope(nameof(AbortPendingMovieProductionAsync));
        public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null) => throw OutOfScope(nameof(RetryAsync));
        public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw OutOfScope(nameof(GetAsync));
        public Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) => throw OutOfScope(nameof(ListAsync));
        public Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw OutOfScope(nameof(CancelAsync));
    }

    private sealed class NoTransitions : IMovieTimelineTransitionService
    {
        public Task<Taslim.Api.Movies.MovieTimelineTransitionEditDto?> GetLatestAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken) => Task.FromResult<Taslim.Api.Movies.MovieTimelineTransitionEditDto?>(null);
        public Task<Taslim.Api.Movies.MovieTimelineTransitionEditDto?> ApplyAsync(Guid userId, Guid movieProjectId, Taslim.Api.Movies.MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("issue #696 re-drive tests never edit transitions");
    }
}
