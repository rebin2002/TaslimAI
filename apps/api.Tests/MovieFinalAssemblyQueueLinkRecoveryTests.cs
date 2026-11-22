using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #696 bounded-recovery contract for the Movie final-assembly queue seam.
///
/// PR #769 pins the RED invariant for scenario (b): after a lost assembly-to-job link write,
/// an idempotent retry must not leave a terminal assembly paired with an executable job.
/// This file pins the boundaries that the recovery must respect, which #769 deliberately
/// leaves out of scope so the owned source file carries no duplicated test-only work:
///
/// 1. Prevention (#696 behavior 1 and 3): the attempt that loses the link write must itself
///    publish a consistent pair, never the contradictory one, and must not duplicate the job.
/// 2. Repair of an already stranded pairing: a terminal queue-failed assembly that still points
///    at a committed, unclaimed job is brought back in flight by the identical request,
///    reusing that canonical job instead of queueing another one.
/// 3. Concurrency ownership (#696 behavior 4): a paired job that already started executing is
///    never re-driven, because re-driving it belongs to the explicit job retry seam.
/// 4. Adoption is refused for a canonical job row whose payload does not carry this exact
///    request fingerprint, so recovery can never publish a foreign master export.
/// 5. A genuinely terminal execution failure (MOVIE_ASSEMBLY_EXECUTION_FAILED) is not
///    resurrected: only the pre-publication queue marker is eligible for recovery.
///
/// Deterministic and provider-free by construction: SQLite in-memory per test, a local job
/// seam that mirrors GenerationJobService.CreateAsync durable/idempotent-replay semantics, and
/// no provider, model, credential, usage reservation, charging, live storage, migration,
/// PostgreSQL, secret, deployment or queue transport is constructed or reached.
/// </summary>
public sealed class MovieFinalAssemblyQueueLinkRecoveryTests
{
    private const string IdempotencyKey = "final-assembly-link-recovery";

    [Fact]
    public async Task Lost_link_write_publishes_a_consistent_pair_and_never_duplicates_the_canonical_job()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new RecoveryOptions(LinkWriteFaultBudget: 1));

        // Attempt 1 loses the link write; the caller must still see the real fault.
        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None));
        Assert.Equal(1, fixture.Fault!.FaultedLinkWrites);

        var firstAttempt = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        var firstJob = await fixture.Db.GenerationJobs.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);

        // The faulting attempt is not allowed to publish the forbidden pairing either.
        Assert.False(
            IsTerminalAssemblyStatus(firstAttempt.Status) && IsExecutableJobStatus(firstJob.Status),
            $"The faulting attempt published assembly '{firstAttempt.Status}' ({firstAttempt.LastErrorCode ?? "none"}) " +
            $"against committed job {firstJob.Id} in state '{firstJob.Status}'.");

        var retry = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);
        Assert.NotNull(retry);

        var converged = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        var canonical = await fixture.Db.GenerationJobs.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        Assert.Equal(firstAttempt.Id, converged.Id);
        Assert.Equal(firstJob.Id, canonical.Id);
        Assert.False(IsTerminalAssemblyStatus(converged.Status), $"Retry did not converge: assembly stayed '{converged.Status}'.");
        Assert.True(converged.GenerationJobId is not null, "The canonical assembly must carry the committed job link once the request converges.");

        // One canonical assembly, one committed job; convergence never re-enters the seam.
        Assert.Equal(1, fixture.Jobs.CreateCallCount);
        Assert.Equal(1, await fixture.Db.MovieAssemblies.CountAsync(item => item.MovieProjectId == fixture.MovieProjectId));
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync());
    }

    [Fact]
    public async Task Stranded_queue_failure_is_repaired_onto_the_canonical_job_instead_of_queueing_another_one()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new RecoveryOptions());
        var stranded = await StrandAsQueueFailureAsync(fixture, GenerationJobStatus.Pending);

        var retry = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.Equal(stranded.Id, retry!.Id);
        Assert.Equal(MovieAssemblyStatuses.Queued, retry.Status);
        Assert.Equal(stranded.GenerationJobId, retry.GenerationJobId);

        var repaired = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        Assert.Equal(MovieAssemblyStatuses.Queued, repaired.Status);
        Assert.Null(repaired.LastErrorCode);
        Assert.Equal(stranded.GenerationJobId, repaired.GenerationJobId);

        // The pre-existing committed job row is the canonical one: nothing new is queued and
        // nothing is published twice.
        Assert.Equal(1, fixture.Jobs.CreateCallCount);
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync());
        Assert.Equal(1, await fixture.Db.MovieAssemblies.CountAsync(item => item.MovieProjectId == fixture.MovieProjectId));
    }

    [Fact]
    public async Task Recovery_never_re_drives_a_paired_job_that_already_started_executing()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new RecoveryOptions());
        var stranded = await StrandAsQueueFailureAsync(fixture, GenerationJobStatus.Running);

        var retry = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.Equal(stranded.Id, retry!.Id);
        Assert.Equal(MovieAssemblyStatuses.Failed, retry.Status);

        var untouchedAssembly = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        var untouchedJob = await fixture.Db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == stranded.GenerationJobId);
        Assert.Equal(MovieAssemblyStatuses.Failed, untouchedAssembly.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieAssemblyQueueFailed, untouchedAssembly.LastErrorCode);
        Assert.Equal(GenerationJobStatus.Running, untouchedJob.Status);
        Assert.Equal(1, fixture.Jobs.CreateCallCount);
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync());
    }

    [Fact]
    public async Task Ambiguous_job_create_is_reconciled_onto_the_committed_job_without_queueing_another_one()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new RecoveryOptions(AmbiguousCreate: true));

        // The seam commits the canonical job row and then the response is lost, so the service
        // cannot know that durable queueing work already happened.
        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None));

        var stranded = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        var canonical = await fixture.Db.GenerationJobs.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        Assert.Null(stranded.GenerationJobId);
        Assert.Equal(GenerationJobStatus.Pending, canonical.Status);

        var retry = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.Equal(stranded.Id, retry!.Id);
        Assert.False(IsTerminalAssemblyStatus(retry.Status), $"An ambiguous create stayed terminal: '{retry.Status}'.");
        Assert.Equal(canonical.Id, retry.GenerationJobId);

        var converged = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        Assert.Equal(canonical.Id, converged.GenerationJobId);
        Assert.False(IsTerminalAssemblyStatus(converged.Status));

        // The committed row is reused rather than recreated, so no second job is ever queued.
        Assert.Equal(1, fixture.Jobs.CreateCallCount);
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync());
        Assert.Equal(1, await fixture.Db.MovieAssemblies.CountAsync(item => item.MovieProjectId == fixture.MovieProjectId));
    }

    [Fact]
    public async Task Recovery_refuses_a_committed_job_that_does_not_carry_this_request_fingerprint()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new RecoveryOptions(AmbiguousCreate: true, ForeignJobPayload: true));

        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None));

        var retry = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);

        Assert.NotNull(retry);
        // Fail closed: nothing is adopted, nothing is re-driven, and no second job appears.
        Assert.Null(retry!.GenerationJobId);

        var persisted = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        Assert.Null(persisted.GenerationJobId);
        Assert.NotEqual(MovieAssemblyStatuses.Ready, persisted.Status);
        Assert.Equal(1, fixture.Jobs.CreateCallCount);
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync());
    }

    [Fact]
    public async Task Terminal_execution_failure_is_not_resurrected_by_an_idempotent_retry()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new RecoveryOptions());

        var first = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);
        Assert.NotNull(first);
        var jobId = first!.GenerationJobId!.Value;

        // Mirror a real execution failure: the job settled terminally, so the assembly
        // projection is terminal with the execution marker, not the pre-publication queue one.
        var job = await fixture.Db.GenerationJobs.FirstAsync(item => item.Id == jobId);
        job.Status = GenerationJobStatus.Failed;
        job.ErrorCode = GenerationJobErrorCodes.MovieAssemblyExecutionFailed;
        job.FailedAt = DateTime.UtcNow;
        var assembly = await fixture.Db.MovieAssemblies.FirstAsync(item => item.Id == first.Id);
        assembly.Status = MovieAssemblyStatuses.Failed;
        assembly.LastErrorCode = GenerationJobErrorCodes.MovieAssemblyExecutionFailed;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var retry = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.Equal(first.Id, retry!.Id);
        Assert.Equal(MovieAssemblyStatuses.Failed, retry.Status);

        var stillTerminal = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == IdempotencyKey);
        Assert.Equal(MovieAssemblyStatuses.Failed, stillTerminal.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieAssemblyExecutionFailed, stillTerminal.LastErrorCode);

        // Bounded: an execution failure is never replayed into fresh durable work.
        Assert.Equal(1, fixture.Jobs.CreateCallCount);
        Assert.Equal(1, await fixture.Db.MovieAssemblies.CountAsync(item => item.MovieProjectId == fixture.MovieProjectId));
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync());
    }

    /// <summary>
    /// Drives one clean request and then persists the exact stranded pairing that the
    /// pre-fix compensation left behind: a terminal MOVIE_ASSEMBLY_QUEUE_FAILED assembly
    /// whose committed job is still linked. This is the state issue #696 reports as
    /// unrecoverable by retry, so it is reproduced durably instead of being mocked.
    /// </summary>
    private static async Task<MovieAssembly> StrandAsQueueFailureAsync(RecoveryFixture fixture, GenerationJobStatus jobStatus)
    {
        var created = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, IdempotencyKey, CancellationToken.None);
        Assert.NotNull(created);

        var job = await fixture.Db.GenerationJobs.FirstAsync(item => item.Id == created!.GenerationJobId);
        job.Status = jobStatus;
        if (jobStatus == GenerationJobStatus.Running) job.StartedAt = DateTime.UtcNow;

        var assembly = await fixture.Db.MovieAssemblies.FirstAsync(item => item.Id == created.Id);
        assembly.Status = MovieAssemblyStatuses.Failed;
        assembly.LastErrorCode = GenerationJobErrorCodes.MovieAssemblyQueueFailed;
        assembly.UpdatedAt = DateTime.UtcNow;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        return assembly;
    }

    private static bool IsTerminalAssemblyStatus(string status) =>
        status is MovieAssemblyStatuses.Ready or MovieAssemblyStatuses.Failed or MovieAssemblyStatuses.Cancelled;

    private static bool IsExecutableJobStatus(GenerationJobStatus status) =>
        status is GenerationJobStatus.Pending or GenerationJobStatus.Queued or GenerationJobStatus.Running;

    internal sealed record RecoveryOptions(bool AmbiguousCreate = false, bool ForeignJobPayload = false, int LinkWriteFaultBudget = 0);

    /// <summary>
    /// Fails the first <see cref="budget"/> writes that link a durable MovieAssembly row to a
    /// committed job and lets every other write proceed, so the injected fault is a transient
    /// link failure rather than a dead database.
    /// </summary>
    private sealed class AssemblyLinkWriteFault(int budget) : SaveChangesInterceptor
    {
        public int FaultedLinkWrites { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            InjectFault(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            InjectFault(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void InjectFault(DbContext? context)
        {
            if (context is null || FaultedLinkWrites >= budget) return;
            if (!context.ChangeTracker.Entries<MovieAssembly>().Any(IsLinkWrite)) return;
            FaultedLinkWrites++;
            throw new DbUpdateException(
                "Simulated transient failure while linking the durable final-assembly row to its committed job.",
                new IOException("Simulated transient database write failure (issue #696 fault injection)."));
        }

        private static bool IsLinkWrite(EntityEntry<MovieAssembly> entry)
        {
            if (entry.State != EntityState.Modified) return false;
            if (entry.Entity.GenerationJobId is null) return false;
            return entry.Property<Guid?>(nameof(MovieAssembly.GenerationJobId)).OriginalValue is null;
        }
    }

    /// <summary>
    /// Durable, provider-free stand-in for the shared job-persistence seam. It commits a
    /// canonical GenerationJob row like GenerationJobService.CreateAsync and replays that same
    /// row for a reused idempotency key; when <see cref="ambiguous"/> is set it instead loses
    /// the response after the durable commit, which is the ambiguous outcome that #696
    /// behavior 3 requires to be reconciled rather than blindly re-queued.
    /// </summary>
    private sealed class DurableAssemblyJobService(TaslimDbContext db, bool ambiguous, bool foreignPayload) : IGenerationJobService
    {
        public int CreateCallCount { get; private set; }

        public async Task<GenerationJob> CreateAsync(
            Guid userId,
            CreateGenerationJobRequest request,
            CancellationToken cancellationToken = default,
            string? idempotencyKey = null,
            string? requestId = null,
            Guid? retryOfJobId = null)
        {
            CreateCallCount++;
            if (idempotencyKey is not null)
            {
                var replay = await db.GenerationJobs.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.CreatedByUserId == userId && item.IdempotencyKey == idempotencyKey, cancellationToken);
                if (replay is not null) return replay;
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
                InputJson = DescribePayload(request.InputJson),
                IdempotencyKey = idempotencyKey,
                RequestFingerprint = $"recovery-contract:{request.JobType}",
                EstimatedProviderCostUsd = request.EstimatedProviderCostUsd,
                EstimatedProviderCostKnown = request.EstimatedProviderCostUsd.HasValue,
                ProgressPercent = 0,
                CreatedAt = DateTime.UtcNow,
            };
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);

            if (ambiguous)
            {
                // The row is durable, but the caller never learns it, so the assembly is
                // compensated as queue-failed with no link at all.
                throw new IOException("Simulated lost response after the durable job commit (issue #696 ambiguous create).");
            }

            return job;
        }

        private string DescribePayload(string inputJson)
        {
            if (!foreignPayload) return inputJson;
            var payload = JsonSerializer.Deserialize<MovieFinalAssemblyInput>(inputJson)!;
            return JsonSerializer.Serialize(payload with { RequestFingerprint = $"foreign-{payload.RequestFingerprint}" });
        }

        public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null) =>
            throw new NotSupportedException("Issue #696 recovery coverage drives the request seam only.");

        public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
            db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId && item.CreatedByUserId == userId, cancellationToken);

        public Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Issue #696 recovery coverage drives the request seam only.");

        public Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Issue #696 recovery coverage drives the request seam only.");
    }

    private sealed class EmptyTransitionEdits : IMovieTimelineTransitionService
    {
        public Task<MovieTimelineTransitionEditDto?> GetLatestAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken) =>
            Task.FromResult<MovieTimelineTransitionEditDto?>(null);

        public Task<MovieTimelineTransitionEditDto?> ApplyAsync(Guid userId, Guid movieProjectId, Taslim.Api.Movies.MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<MovieTimelineTransitionEditDto?>(null);
    }

    /// <summary>Self-contained SQLite fixture with the requested seam behaviour wired in.</summary>
    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private RecoveryFixture(SqliteConnection connection, TaslimDbContext db, AssemblyLinkWriteFault? fault, DurableAssemblyJobService jobs, MovieFinalAssemblyService service, Guid userId, Guid movieProjectId, Guid takeId)
        {
            this.connection = connection;
            Db = db;
            Fault = fault;
            Jobs = jobs;
            Service = service;
            UserId = userId;
            MovieProjectId = movieProjectId;
            TakeId = takeId;
            Request = new MovieFinalAssemblyRequest
            {
                ResolutionProfile = MovieFinalAssemblyProfiles.Hd1080p,
                Timeline = [new MovieAssemblyTimelineItemRequest { TakeId = takeId, InPointSeconds = 0m }],
            };
        }

        public TaslimDbContext Db { get; }
        public AssemblyLinkWriteFault? Fault { get; }
        public DurableAssemblyJobService Jobs { get; }
        public MovieFinalAssemblyService Service { get; }
        public Guid UserId { get; }
        public Guid MovieProjectId { get; }
        public Guid TakeId { get; }
        public MovieFinalAssemblyRequest Request { get; }

        public static async Task<RecoveryFixture> CreateAsync(RecoveryOptions options)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            AssemblyLinkWriteFault? fault = options.LinkWriteFaultBudget > 0 ? new AssemblyLinkWriteFault(options.LinkWriteFaultBudget) : null;
            var optionsBuilder = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection);
            if (fault is not null) optionsBuilder.AddInterceptors(fault);
            var db = new TaslimDbContext(optionsBuilder.Options);
            await db.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            var userId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var movieProjectId = Guid.NewGuid();
            var sceneId = Guid.NewGuid();
            var shotId = Guid.NewGuid();
            var takeId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var storedFileId = Guid.NewGuid();

            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"assembly-link-recovery-{Guid.NewGuid():N}", NormalizedUserName = "ASSEMBLY-LINK-RECOVERY", DisplayName = "Assembly Link Recovery Owner", CreatedAt = now, UpdatedAt = now });
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Assembly Link Recovery Workspace", Slug = $"assembly-link-recovery-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
            db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
            db.MovieProjects.Add(new MovieProject { Id = movieProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Recovery Contract Feature", Description = "Seeded for the issue #696 recovery boundary.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
            db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
            db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieProjectId, Sequence = 1, Title = "Opening", Summary = "Only scene.", CreatedAt = now, UpdatedAt = now });
            db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Wide establishing shot.", CreatedAt = now, UpdatedAt = now });
            db.StoredFiles.Add(new StoredFile { Id = storedFileId, WorkspaceId = workspaceId, UserId = userId, OriginalFileName = "take-01.mp4", StoredFileName = "take-01.mp4", ContentType = "video/mp4", Extension = ".mp4", SizeBytes = 4_096, StorageProvider = FileStorageProviders.Local, StorageKey = $"tests/{storedFileId:N}", Status = StoredFileStatus.Ready, CreatedAt = now, ProcessedAt = now });
            db.Assets.Add(new Asset { Id = assetId, WorkspaceId = workspaceId, CreatedByUserId = userId, StoredFileId = storedFileId, Name = "Selected take master", AssetType = AssetTypes.Video, MimeType = "video/mp4", Status = AssetStatus.Active, MetadataJson = "{\"width\":1920,\"height\":1080}", CreatedAt = now, UpdatedAt = now });
            db.MovieTakes.Add(new MovieTake { Id = takeId, MovieShotId = shotId, VersionNumber = 1, Label = "take-01", Status = MovieTakeStatuses.Selected, AssetId = assetId, SelectedAt = now, SelectedByUserId = userId, FinalizedAt = now, StatusChangedAt = now, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();

            var jobs = new DurableAssemblyJobService(db, options.AmbiguousCreate, options.ForeignJobPayload);
            var service = new MovieFinalAssemblyService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)), jobs, new EmptyTransitionEdits());
            return new RecoveryFixture(connection, db, fault, jobs, service, userId, movieProjectId, takeId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
