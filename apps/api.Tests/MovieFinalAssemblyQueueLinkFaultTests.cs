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
/// Issue #696 fault-injection regression for the Movie final-assembly queue seam.
/// Scenario: the durable assembly row already exists, the simulated successful job
/// persistence commits a canonical GenerationJob, and only the assembly-to-job link write
/// afterwards is lost. A repeated identical (idempotent) request must not duplicate jobs,
/// must not lose or replace the canonical assembly, and must never leave an inconsistent
/// published pair, i.e. a terminal assembly whose committed job is still executable.
///
/// Deterministic and provider-free by construction:
/// - SQLite in-memory only. No PostgreSQL, no live storage, no deployment, no secrets.
/// - The job persistence seam is a local fake that mirrors GenerationJobService.CreateAsync
///   durable/idempotent-replay semantics, so no provider, model, usage reservation or
///   customer charging code is reached and no external call is made.
/// - The fault is injected exactly once through an EF SaveChanges interceptor that fires only
///   while a MovieAssembly row transitions from "no job link" to "job linked", which is the
///   assembly-job link write itself. The compensation write in the service catch block still
///   succeeds, so this models a transient link fault, not a dead database.
/// - This file owns no production source. MovieFinalAssembly.cs stays untouched (owned by the
///   PR #298 stack); no existing assembly test file is modified.
/// </summary>
public sealed class MovieFinalAssemblyQueueLinkFaultTests
{
    [Fact]
    public async Task Idempotent_retry_after_lost_assembly_link_never_leaves_a_terminal_assembly_with_an_executable_job()
    {
        await using var fixture = await FaultFixture.CreateAsync();

        // Attempt 1: durable assembly row, durable job, then the link write is lost.
        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, fixture.IdempotencyKey, CancellationToken.None));

        // The injected fault fired exactly once, on the link write only.
        Assert.Equal(1, fixture.Fault.FaultedLinkWrites);
        Assert.Equal(1, fixture.Jobs.CreateCallCount);

        var stranded = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == fixture.IdempotencyKey);
        var orphanJob = await fixture.Db.GenerationJobs.AsNoTracking().SingleAsync(item => item.IdempotencyKey == fixture.IdempotencyKey);

        // Attempt 2: the identical idempotent request.
        var retry = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, fixture.IdempotencyKey, CancellationToken.None);
        Assert.NotNull(retry);

        var convergedAssembly = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == fixture.IdempotencyKey);
        var convergedJob = await fixture.Db.GenerationJobs.AsNoTracking().SingleAsync(item => item.IdempotencyKey == fixture.IdempotencyKey);

        // Issue #696 required behavior 1 and 3: a transient pre-publication link fault must be
        // reconciled instead of published as an inconsistent pair. A supported fix may either
        // repair the durable link and keep the assembly in flight, or reconcile the committed
        // job to a terminal state before reporting a terminal assembly. Both are accepted here;
        // only the contradictory pairing is rejected.
        Assert.False(
            IsTerminalAssemblyStatus(convergedAssembly.Status) && IsExecutableJobStatus(convergedJob.Status),
            $"Inconsistent published state survived an idempotent retry: assembly '{convergedAssembly.Status}' " +
            $"(link {convergedAssembly.GenerationJobId}, error {convergedAssembly.LastErrorCode ?? "none"}, " +
            $"previous error {stranded.LastErrorCode ?? "none"}) while its committed job {convergedJob.Id} is still " +
            $"'{convergedJob.Status}'. The transient queue/link fault must converge on one canonical assembly with " +
            "a reconciled job, rather than stranding the export or leaving a job that can still publish a master " +
            "for a terminal assembly.");
    }

    [Fact]
    public async Task Idempotent_retries_after_lost_assembly_link_keep_one_canonical_assembly_and_one_committed_job()
    {
        await using var fixture = await FaultFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, fixture.IdempotencyKey, CancellationToken.None));
        var canonical = await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == fixture.IdempotencyKey);

        // Repeated identical submissions must never fan out into extra durable writes.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var repeated = await fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, fixture.IdempotencyKey, CancellationToken.None);
            Assert.NotNull(repeated);
            Assert.Equal(canonical.Id, repeated!.Id);
        }

        Assert.Equal(1, await fixture.Db.MovieAssemblies.CountAsync(item => item.MovieProjectId == fixture.MovieProjectId));
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync(item => item.IdempotencyKey == fixture.IdempotencyKey));
        Assert.Equal(canonical.Id, (await fixture.Db.MovieAssemblies.AsNoTracking().SingleAsync(item => item.IdempotencyKey == fixture.IdempotencyKey)).Id);
        Assert.Equal(1, fixture.Fault.FaultedLinkWrites);
    }

    [Fact]
    public async Task Lost_assembly_link_still_rejects_a_reused_key_for_a_different_request_fingerprint()
    {
        await using var fixture = await FaultFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, fixture.Request, fixture.IdempotencyKey, CancellationToken.None));

        // Issue #696 required behavior 2: any safe-recovery path must keep rejecting a reused key
        // whose fingerprint differs, and must not queue work for it.
        var mismatched = FixtureRequests.Request(MovieFinalAssemblyProfiles.Uhd4K, fixture.TakeId);
        var rejection = await Assert.ThrowsAsync<MovieFinalAssemblyValidationException>(
            () => fixture.Service.RequestAsync(fixture.UserId, fixture.MovieProjectId, mismatched, fixture.IdempotencyKey, CancellationToken.None));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", rejection.Code);

        Assert.Equal(1, fixture.Jobs.CreateCallCount);
        Assert.Equal(1, await fixture.Db.GenerationJobs.CountAsync(item => item.IdempotencyKey == fixture.IdempotencyKey));
    }

    private static bool IsTerminalAssemblyStatus(string status) =>
        status is MovieAssemblyStatuses.Ready or MovieAssemblyStatuses.Failed or MovieAssemblyStatuses.Cancelled;

    private static bool IsExecutableJobStatus(GenerationJobStatus status) =>
        status is GenerationJobStatus.Pending or GenerationJobStatus.Queued or GenerationJobStatus.Running;

    internal static class FixtureRequests
    {
        public static MovieFinalAssemblyRequest Request(string resolutionProfile, Guid takeId) => new()
        {
            ResolutionProfile = resolutionProfile,
            Timeline = [new MovieAssemblyTimelineItemRequest { TakeId = takeId, InPointSeconds = 0m }],
        };
    }

    /// <summary>
    /// Fails the first write that links a durable MovieAssembly row to a newly created job.
    /// Every other SaveChanges call, including the compensation write inside the service catch
    /// block, proceeds normally, so the injected fault is a transient link failure.
    /// </summary>
    private sealed class AssemblyLinkWriteFault : SaveChangesInterceptor
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
            if (context is null || FaultedLinkWrites > 0) return;
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
            return OriginalLink(entry) is null;
        }

        private static Guid? OriginalLink(EntityEntry<MovieAssembly> entry) =>
            entry.Property<Guid?>(nameof(MovieAssembly.GenerationJobId)).OriginalValue;
    }

    /// <summary>
    /// Durable, provider-free stand-in for the shared job-persistence seam owned by PR #263. It
    /// commits a canonical GenerationJob row and replays that same row for a reused idempotency
    /// key, matching GenerationJobService.CreateAsync, so the assertions measure real duplicate
    /// durable-work outcomes without constructing a provider, reservation, charge or queue.
    /// </summary>
    private sealed class DurableAssemblyJobService(TaslimDbContext db) : IGenerationJobService
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
                InputJson = request.InputJson,
                IdempotencyKey = idempotencyKey,
                RequestFingerprint = $"fault-injection:{request.JobType}",
                EstimatedProviderCostUsd = request.EstimatedProviderCostUsd,
                EstimatedProviderCostKnown = request.EstimatedProviderCostUsd.HasValue,
                ProgressPercent = 0,
                CreatedAt = DateTime.UtcNow,
            };
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            return job;
        }

        public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null) =>
            throw new NotSupportedException("Issue #696 link-fault coverage drives the request seam only.");

        public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
            db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId && item.CreatedByUserId == userId, cancellationToken);

        public Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Issue #696 link-fault coverage drives the request seam only.");

        public Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Issue #696 link-fault coverage drives the request seam only.");
    }

    private sealed class EmptyTransitionEdits : IMovieTimelineTransitionService
    {
        public Task<MovieTimelineTransitionEditDto?> GetLatestAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken) =>
            Task.FromResult<MovieTimelineTransitionEditDto?>(null);

        public Task<MovieTimelineTransitionEditDto?> ApplyAsync(Guid userId, Guid movieProjectId, Taslim.Api.Movies.MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<MovieTimelineTransitionEditDto?>(null);
    }

    /// <summary>Self-contained SQLite fixture with the link fault wired in.</summary>
    private sealed class FaultFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private FaultFixture(SqliteConnection connection, TaslimDbContext db, AssemblyLinkWriteFault fault, DurableAssemblyJobService jobs, MovieFinalAssemblyService service, Guid userId, Guid movieProjectId, Guid takeId, string idempotencyKey)
        {
            this.connection = connection;
            Db = db;
            Fault = fault;
            Jobs = jobs;
            Service = service;
            UserId = userId;
            MovieProjectId = movieProjectId;
            TakeId = takeId;
            IdempotencyKey = idempotencyKey;
            Request = FixtureRequests.Request(MovieFinalAssemblyProfiles.Hd1080p, takeId);
        }

        public TaslimDbContext Db { get; }
        public AssemblyLinkWriteFault Fault { get; }
        public DurableAssemblyJobService Jobs { get; }
        public MovieFinalAssemblyService Service { get; }
        public Guid UserId { get; }
        public Guid MovieProjectId { get; }
        public Guid TakeId { get; }
        public string IdempotencyKey { get; }
        public MovieFinalAssemblyRequest Request { get; }

        public static async Task<FaultFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fault = new AssemblyLinkWriteFault();
            var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).AddInterceptors(fault).Options);
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

            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"assembly-link-fault-{Guid.NewGuid():N}", NormalizedUserName = "ASSEMBLY-LINK-FAULT", DisplayName = "Assembly Link Fault Owner", CreatedAt = now, UpdatedAt = now });
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Assembly Link Fault Workspace", Slug = $"assembly-link-fault-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
            db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
            db.MovieProjects.Add(new MovieProject { Id = movieProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Link Fault Feature", Description = "Seeded for issue #696 fault injection.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
            db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
            db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieProjectId, Sequence = 1, Title = "Opening", Summary = "Only scene.", CreatedAt = now, UpdatedAt = now });
            db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Wide establishing shot.", CreatedAt = now, UpdatedAt = now });
            db.StoredFiles.Add(new StoredFile { Id = storedFileId, WorkspaceId = workspaceId, UserId = userId, OriginalFileName = "take-01.mp4", StoredFileName = "take-01.mp4", ContentType = "video/mp4", Extension = ".mp4", SizeBytes = 4_096, StorageProvider = FileStorageProviders.Local, StorageKey = $"tests/{storedFileId:N}", Status = StoredFileStatus.Ready, CreatedAt = now, ProcessedAt = now });
            db.Assets.Add(new Asset { Id = assetId, WorkspaceId = workspaceId, CreatedByUserId = userId, StoredFileId = storedFileId, Name = "Selected take master", AssetType = AssetTypes.Video, MimeType = "video/mp4", Status = AssetStatus.Active, MetadataJson = "{\"width\":1920,\"height\":1080}", CreatedAt = now, UpdatedAt = now });
            db.MovieTakes.Add(new MovieTake { Id = takeId, MovieShotId = shotId, VersionNumber = 1, Label = "take-01", Status = MovieTakeStatuses.Selected, AssetId = assetId, SelectedAt = now, SelectedByUserId = userId, FinalizedAt = now, StatusChangedAt = now, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();

            var jobs = new DurableAssemblyJobService(db);
            var service = new MovieFinalAssemblyService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)), jobs, new EmptyTransitionEdits());
            return new FaultFixture(connection, db, fault, jobs, service, userId, movieProjectId, takeId, "final-assembly-link-fault");
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
