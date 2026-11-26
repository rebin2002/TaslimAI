using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.AcademicIntegration.Persistence;
using Taslim.Api.AcademicWorkflow;
using Taslim.Api.Persistence;
using Xunit;
using Taslim.Api.Tests;

namespace Taslim.Api.Tests.AcademicIntegration;

/// <summary>
/// Storage-contract tests for the integration state port. Everything here runs against
/// in-memory SQLite through the real <see cref="TaslimDbContext"/> model (the same
/// <c>ApplyAcademicIntegrationModel</c> the composition root uses), so what these tests
/// prove about request-key uniqueness, checkpoint replay and revision compare-and-swap
/// is what production gets. Deterministic: fixed ids, no clocks beyond row bookkeeping,
/// no network.
/// </summary>
public sealed class AcademicWorkflowStoreTests
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection;

        internal Fixture()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite(_connection)
                .Options;
            // The shared context so the test exercises the exact model Program.cs builds.
            var probe = new TaslimDbContext(options);
            probe.Database.EnsureCreated();
            probe.Dispose();
            Context = new TaslimDbContext(options);
            Store = new EfAcademicWorkflowStateStore(Context);
        }

        internal TaslimDbContext Context { get; }
        internal EfAcademicWorkflowStateStore Store { get; }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class ConcurrentStageStartStore(IAcademicWorkflowStateStore inner) : IAcademicWorkflowStateStore
    {
        private readonly TaskCompletionSource<bool> bothInitialLoads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> bothInitialClaims = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim databaseGate = new(1, 1);
        private int initialLoadCount;
        private int initialClaimCount;

        public async Task<AcademicWorkflowSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken)
        {
            AcademicWorkflowSnapshot? snapshot;
            await databaseGate.WaitAsync(cancellationToken);
            try
            {
                snapshot = await inner.LoadAsync(projectId, cancellationToken);
            }
            finally
            {
                databaseGate.Release();
            }

            var arrival = Interlocked.Increment(ref initialLoadCount);
            if (arrival <= 2)
            {
                if (arrival == 2) bothInitialLoads.TrySetResult(true);
                await bothInitialLoads.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }

        public Task<AcademicWorkflowSnapshot?> FindByRequestKeyAsync(Guid workspaceId, string requestKey, CancellationToken cancellationToken) =>
            inner.FindByRequestKeyAsync(workspaceId, requestKey, cancellationToken);

        public Task<bool> TryCreateAsync(AcademicWorkflowSnapshot snapshot, CancellationToken cancellationToken) =>
            inner.TryCreateAsync(snapshot, cancellationToken);

        public async Task<AcademicCommitOutcome> TryCommitAsync(
            AcademicWorkflowSnapshot snapshot,
            long expectedRevision,
            string checkpointKey,
            CancellationToken cancellationToken)
        {
            if (expectedRevision == 0)
            {
                var arrival = Interlocked.Increment(ref initialClaimCount);
                if (arrival <= 2)
                {
                    if (arrival == 2) bothInitialClaims.TrySetResult(true);
                    await bothInitialClaims.Task.WaitAsync(cancellationToken);
                }
            }

            await databaseGate.WaitAsync(cancellationToken);
            try
            {
                return await inner.TryCommitAsync(snapshot, expectedRevision, checkpointKey, cancellationToken);
            }
            finally
            {
                databaseGate.Release();
            }
        }

        public async Task<AcademicCommitOutcome> TryCommitAttemptAsync(
            AcademicWorkflowSnapshot snapshot,
            long expectedRevision,
            string checkpointKey,
            Guid expectedAttemptId,
            CancellationToken cancellationToken)
        {
            await databaseGate.WaitAsync(cancellationToken);
            try
            {
                return await inner.TryCommitAttemptAsync(snapshot, expectedRevision, checkpointKey, expectedAttemptId, cancellationToken);
            }
            finally
            {
                databaseGate.Release();
            }
        }

        public async Task<AcademicCommitOutcome> TryRecoverAttemptAsync(
            AcademicWorkflowSnapshot snapshot,
            long expectedRevision,
            string checkpointKey,
            Guid? expectedAttemptId,
            CancellationToken cancellationToken)
        {
            await databaseGate.WaitAsync(cancellationToken);
            try
            {
                return await inner.TryRecoverAttemptAsync(snapshot, expectedRevision, checkpointKey, expectedAttemptId, cancellationToken);
            }
            finally
            {
                databaseGate.Release();
            }
        }
    }

    private sealed class SerializedStore(IAcademicWorkflowStateStore inner) : IAcademicWorkflowStateStore
    {
        private readonly SemaphoreSlim gate = new(1, 1);

        public Task<AcademicWorkflowSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken) =>
            RunAsync(() => inner.LoadAsync(projectId, cancellationToken), cancellationToken);

        public Task<AcademicWorkflowSnapshot?> FindByRequestKeyAsync(Guid workspaceId, string requestKey, CancellationToken cancellationToken) =>
            RunAsync(() => inner.FindByRequestKeyAsync(workspaceId, requestKey, cancellationToken), cancellationToken);

        public Task<bool> TryCreateAsync(AcademicWorkflowSnapshot snapshot, CancellationToken cancellationToken) =>
            RunAsync(() => inner.TryCreateAsync(snapshot, cancellationToken), cancellationToken);

        public Task<AcademicCommitOutcome> TryCommitAsync(
            AcademicWorkflowSnapshot snapshot,
            long expectedRevision,
            string checkpointKey,
            CancellationToken cancellationToken) =>
            RunAsync(() => inner.TryCommitAsync(snapshot, expectedRevision, checkpointKey, cancellationToken), cancellationToken);

        public Task<AcademicCommitOutcome> TryCommitAttemptAsync(
            AcademicWorkflowSnapshot snapshot,
            long expectedRevision,
            string checkpointKey,
            Guid expectedAttemptId,
            CancellationToken cancellationToken) =>
            RunAsync(() => inner.TryCommitAttemptAsync(snapshot, expectedRevision, checkpointKey, expectedAttemptId, cancellationToken), cancellationToken);

        public Task<AcademicCommitOutcome> TryRecoverAttemptAsync(
            AcademicWorkflowSnapshot snapshot,
            long expectedRevision,
            string checkpointKey,
            Guid? expectedAttemptId,
            CancellationToken cancellationToken) =>
            RunAsync(() => inner.TryRecoverAttemptAsync(snapshot, expectedRevision, checkpointKey, expectedAttemptId, cancellationToken), cancellationToken);

        private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await operation();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private sealed class BlockingPlanHandler : IAcademicStageHandler
    {
        private readonly TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public AcademicStage Stage => AcademicStage.Plan;
        public int CallCount => Volatile.Read(ref calls);
        public Task Started => entered.Task;
        public Task SecondStarted => secondEntered.Task;
        public void Release() => release.TrySetResult(true);

        public AcademicStageEstimate Estimate(AcademicStageContext context) => new()
        {
            EstimatedCostUsd = 0.5m,
            EstimatedProviderCalls = 2,
        };

        public async Task<AcademicStageOutcome> ExecuteAsync(AcademicStageContext context, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            if (call == 2) secondEntered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return AcademicWorkflowFixtures.OutcomeFor(
                AcademicStage.Plan,
                context.Snapshot,
                new AcademicUsageRecord { CostUsd = 0.5m, ProviderCalls = 2 });
        }
    }

    private static AcademicWorkflowSnapshot Snapshot(Guid workspaceId, string requestKey) => new()
    {
        ProjectId = AcademicWorkflowOrchestrator.DeriveProjectId(workspaceId, requestKey),
        WorkspaceId = workspaceId,
        RequestKey = requestKey,
        Request = AcademicWorkflowFixtures.Request(requestKey) with { WorkspaceId = workspaceId },
        State = AcademicProjectState.Running,
        Revision = 0,
        CreatedAtUtc = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task TryCreate_is_idempotent_per_request_key_and_writes_the_intake_checkpoint()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(WorkspaceA, "intake-key");

        Assert.True(await fixture.Store.TryCreateAsync(snapshot, default));
        Assert.False(await fixture.Store.TryCreateAsync(snapshot, default));

        Assert.Equal(1, await fixture.Context.Set<AcademicWorkflowProjectEntity>().CountAsync());
        var checkpoints = await fixture.Store.ListCheckpointsAsync(WorkspaceA, snapshot.ProjectId, 10, default);
        Assert.Single(checkpoints);
        Assert.Equal(0, checkpoints[0].Revision);
    }

    [Fact]
    public async Task TryCommit_applies_once_and_enforces_the_revision_compare_and_swap()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(WorkspaceA, "commit-key");
        await fixture.Store.TryCreateAsync(snapshot, default);

        var working = Snapshot(WorkspaceA, "commit-key");
        working.Revision = 1;
        working.State = AcademicProjectState.AwaitingApproval;
        working.Plan = AcademicWorkflowFixtures.Plan();
        var checkpointKey = $"{working.ProjectId:N}|1|test-stage-complete";

        var committed = await fixture.Store.TryCommitAsync(working, 0, checkpointKey, default);
        Assert.Equal(AcademicCommitStatus.Committed, committed.Status);

        // Same key replayed: reported duplicate, and the stored row is untouched.
        var replay = await fixture.Store.TryCommitAsync(working, 0, checkpointKey, default);
        Assert.Equal(AcademicCommitStatus.DuplicateCheckpoint, replay.Status);

        // A stale writer with a fresh key must not clobber the committed revision.
        var stale = working.Clone();
        stale.Revision = 1;
        stale.State = AcademicProjectState.Failed;
        var conflict = await fixture.Store.TryCommitAsync(stale, 0, $"{working.ProjectId:N}|1|stale-writer", default);
        Assert.Equal(AcademicCommitStatus.RevisionConflict, conflict.Status);

        var loaded = await fixture.Store.LoadAsync(working.ProjectId, default);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Revision);
        Assert.Equal(AcademicProjectState.AwaitingApproval, loaded.State);
        Assert.NotNull(loaded.Plan);
    }

    [Fact]
    public async Task Attempt_completion_requires_the_exact_durable_owner_token()
    {
        using var fixture = new Fixture();
        var initial = Snapshot(WorkspaceA, "sqlite-attempt-token-fence");
        Assert.True(await fixture.Store.TryCreateAsync(initial, default));

        var attemptId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var claimed = initial.Clone();
        claimed.Revision = 1;
        claimed.Stage(AcademicStage.Plan).State = AcademicStageState.Running;
        claimed.Stage(AcademicStage.Plan).AttemptCount = 1;
        claimed.ActiveAttempt = new AcademicLiveAttempt
        {
            AttemptId = attemptId,
            OwnerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Stage = AcademicStage.Plan,
            Attempt = 1,
            StartedAtUtc = initial.CreatedAtUtc,
        };
        Assert.Equal(AcademicCommitStatus.Committed,
            (await fixture.Store.TryCommitAsync(claimed, 0, $"{initial.ProjectId:N}|1|attempt-start", default)).Status);

        var completion = claimed.Clone();
        completion.Revision = 2;
        completion.State = AcademicProjectState.AwaitingApproval;
        completion.Stage(AcademicStage.Plan).State = AcademicStageState.Completed;
        completion.Plan = AcademicWorkflowFixtures.Plan();
        completion.ActiveAttempt = null;

        var wrongOwner = await fixture.Store.TryCommitAttemptAsync(
            completion, 1, $"{initial.ProjectId:N}|2|wrong-owner", Guid.NewGuid(), default);
        Assert.Equal(AcademicCommitStatus.RevisionConflict, wrongOwner.Status);
        var stillOwned = await fixture.Store.LoadAsync(initial.ProjectId, default);
        Assert.Equal(1, stillOwned!.Revision);
        Assert.Equal(attemptId, stillOwned.ActiveAttempt!.AttemptId);

        var validOwner = await fixture.Store.TryCommitAttemptAsync(
            completion, 1, $"{initial.ProjectId:N}|2|right-owner", attemptId, default);
        Assert.Equal(AcademicCommitStatus.Committed, validOwner.Status);
        Assert.Null((await fixture.Store.LoadAsync(initial.ProjectId, default))!.ActiveAttempt);
    }

    [Fact]
    public async Task Concurrent_resumes_on_disposable_sqlite_allow_only_the_stage_start_claim_winner_to_execute()
    {
        using var fixture = new Fixture();
        var request = AcademicWorkflowFixtures.Request("sqlite-concurrent-stage-start") with { WorkspaceId = WorkspaceA };
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = AcademicWorkflowOrchestrator.DeriveProjectId(WorkspaceA, request.RequestKey),
            WorkspaceId = WorkspaceA,
            RequestKey = request.RequestKey,
            Request = request,
            State = AcademicProjectState.Running,
            Revision = 0,
            CreatedAtUtc = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc),
        };
        Assert.True(await fixture.Store.TryCreateAsync(snapshot, default));

        // Both engines read the same durable revision and race the same revision-0 claim.
        // The adapter serializes DbContext access because EF contexts are not thread-safe;
        // SQLite still persists the winner and reports the identical claim as a duplicate.
        var racingStore = new ConcurrentStageStartStore(fixture.Store);
        var handler = new BlockingPlanHandler();
        var firstWorker = new AcademicWorkflowOrchestrator(racingStore, [handler]);
        var secondWorker = new AcademicWorkflowOrchestrator(racingStore, [handler]);
        var first = firstWorker.ResumeAsync(snapshot.ProjectId, CancellationToken.None);
        var second = secondWorker.ResumeAsync(snapshot.ProjectId, CancellationToken.None);

        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Task<AcademicWorkflowRunResult>? firstFinished = null;
        try
        {
            firstFinished = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AcademicWorkflowRunStatus.RevisionConflict, (await firstFinished).Status);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            handler.Release();
        }

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, results.Count(result => result.Status == AcademicWorkflowRunStatus.AwaitingApproval));
        Assert.Equal(1, results.Count(result => result.Status == AcademicWorkflowRunStatus.RevisionConflict));
        Assert.Equal(1, handler.CallCount);

        var durable = await fixture.Store.LoadAsync(snapshot.ProjectId, default);
        Assert.Equal(AcademicProjectState.AwaitingApproval, durable!.State);
        Assert.Equal(AcademicApprovalKind.Plan, durable.PendingApproval!.Kind);
        Assert.Equal(2, durable.Revision);
    }

    [Fact]
    public async Task Resume_that_reads_committed_running_snapshot_does_not_start_a_second_live_attempt()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(WorkspaceA, "sqlite-live-attempt-owner");
        Assert.True(await fixture.Store.TryCreateAsync(snapshot, default));

        var handler = new BlockingPlanHandler();
        var serializedStore = new SerializedStore(fixture.Store);
        var firstWorker = new AcademicWorkflowOrchestrator(serializedStore, [handler]);
        var secondWorker = new AcademicWorkflowOrchestrator(serializedStore, [handler]);
        var first = firstWorker.ResumeAsync(snapshot.ProjectId, CancellationToken.None);

        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var committedRunning = await fixture.Store.LoadAsync(snapshot.ProjectId, default);
        Assert.Equal(AcademicProjectState.Running, committedRunning!.State);
        Assert.Equal(AcademicStageState.Running, committedRunning.Stage(AcademicStage.Plan).State);
        var owner = Assert.IsType<AcademicLiveAttempt>(committedRunning.ActiveAttempt);
        Assert.Equal(1, owner.Attempt);
        Assert.NotEqual(Guid.Empty, owner.OwnerId);
        Assert.Equal(1, handler.CallCount);

        var second = secondWorker.ResumeAsync(snapshot.ProjectId, CancellationToken.None);
        try
        {
            var completedFirst = await Task.WhenAny(second, handler.SecondStarted).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(second, completedFirst);
            var blockedResult = await second;
            Assert.Equal(AcademicWorkflowRunStatus.AttemptInProgress, blockedResult.Status);
            Assert.Equal("STAGE_ATTEMPT_IN_PROGRESS", blockedResult.Code);
            Assert.Equal(committedRunning.Revision, blockedResult.Snapshot!.Revision);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            handler.Release();
        }

        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, (await first).Status);
        var durable = await fixture.Store.LoadAsync(snapshot.ProjectId, default);
        Assert.Equal(2, durable!.Revision);
        Assert.Equal(AcademicProjectState.AwaitingApproval, durable.State);
        Assert.Null(durable.ActiveAttempt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Verified_recovery_preserves_charge_and_fences_a_synthetic_late_completion_after_retry()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(WorkspaceA, "sqlite-abandoned-attempt-recovery");
        Assert.True(await fixture.Store.TryCreateAsync(snapshot, default));

        // Deliberately keep only a synthetic handler live across the state transition to
        // exercise stale-write fencing. This does not authorize production recovery while
        // an external handler is live; the API requires a privileged verified-stop action.
        var serializedStore = new SerializedStore(fixture.Store);
        var blockedHandler = new BlockingPlanHandler();
        var abandonedWorker = new AcademicWorkflowOrchestrator(serializedStore, [blockedHandler]);
        var abandonedRun = abandonedWorker.ResumeAsync(snapshot.ProjectId, CancellationToken.None);
        await blockedHandler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var inFlight = await fixture.Store.LoadAsync(snapshot.ProjectId, default);
        var attemptId = Assert.IsType<AcademicLiveAttempt>(inFlight!.ActiveAttempt).AttemptId;
        var recovered = await abandonedWorker.RecoverAbandonedAttemptAsync(
            snapshot.ProjectId,
            inFlight.Revision,
            attemptId,
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "Synthetic test-only operator verification for the late-return fencing scenario.",
            CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.StageFailure, recovered.Status);
        Assert.Equal("STAGE_ATTEMPT_RECOVERED", recovered.Code);
        Assert.Equal(AcademicStageState.Failed, recovered.Snapshot!.Stage(AcademicStage.Plan).State);
        Assert.Null(recovered.Snapshot.ActiveAttempt);
        Assert.Equal(attemptId, recovered.Snapshot.LastAttemptRecovery!.AttemptId);
        Assert.Equal("Synthetic test-only operator verification for the late-return fencing scenario.",
            recovered.Snapshot.LastAttemptRecovery.Reason);
        var abandonedReservation = Assert.Single(recovered.Snapshot.Budget);
        Assert.Null(abandonedReservation.Committed);
        Assert.Equal(abandonedReservation.ReservedUsd, abandonedReservation.ChargedUsd);
        Assert.Equal(abandonedReservation.ReservedProviderCalls, abandonedReservation.ChargedProviderCalls);

        var retryHandler = new ScriptedStageHandler(AcademicStage.Plan);
        retryHandler.QueueOutput(new AcademicStageOutput
        {
            Plan = AcademicWorkflowFixtures.Plan() with { WorkingTitle = "retry attempt wins" },
        });
        var retryWorker = new AcademicWorkflowOrchestrator(serializedStore, [retryHandler]);
        var retryResult = await retryWorker.ResumeAsync(snapshot.ProjectId, CancellationToken.None);
        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, retryResult.Status);
        Assert.Equal(1, retryHandler.Calls.Count);

        blockedHandler.Release();
        var staleResult = await abandonedRun;
        Assert.Equal(AcademicWorkflowRunStatus.RevisionConflict, staleResult.Status);
        Assert.Equal("CHECKPOINT_CONFLICT", staleResult.Code);

        var durable = await fixture.Store.LoadAsync(snapshot.ProjectId, default);
        Assert.Equal("retry attempt wins", durable!.Plan!.WorkingTitle);
        Assert.Equal(2, durable.Stage(AcademicStage.Plan).AttemptCount);
        Assert.Equal(2, durable.Budget.Count);
        Assert.Null(durable.ActiveAttempt);
        Assert.Null(durable.Budget[0].Committed);
        Assert.NotNull(durable.Budget[1].Committed);
    }

    [Fact]
    public async Task Committed_snapshot_round_trips_byte_identically_through_the_canonical_serializer()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(WorkspaceA, "round-trip");
        await fixture.Store.TryCreateAsync(snapshot, default);

        var advanced = snapshot.Clone();
        advanced.Revision = 1;
        advanced.Sources = AcademicWorkflowFixtures.Sources();
        advanced.Screening = AcademicWorkflowFixtures.Screening();
        advanced.Evidence = AcademicWorkflowFixtures.Evidence();
        advanced.Chapters = AcademicWorkflowFixtures.Chapters();
        advanced.Themes = AcademicWorkflowFixtures.Themes();
        advanced.Approvals.Add(new AcademicApprovalRecord
        {
            ApprovalKey = $"{advanced.ProjectId:N}:Plan:1",
            Kind = AcademicApprovalKind.Plan,
            CheckpointRevision = 1,
            Decision = AcademicApprovalDecision.Approved,
            DecidedByUserId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            DecidedAtUtc = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc),
        });

        var commit = await fixture.Store.TryCommitAsync(advanced, 0, $"{advanced.ProjectId:N}|1|round-trip", default);
        Assert.Equal(AcademicCommitStatus.Committed, commit.Status);

        var loaded = await fixture.Store.LoadAsync(advanced.ProjectId, default);
        Assert.NotNull(loaded);
        Assert.Equal(AcademicWorkflowSerialization.Serialize(advanced), AcademicWorkflowSerialization.Serialize(loaded));
    }

    [Fact]
    public async Task Request_key_lookup_is_workspace_fenced_in_both_directions()
    {
        using var fixture = new Fixture();
        await fixture.Store.TryCreateAsync(Snapshot(WorkspaceA, "shared-key"), default);

        Assert.NotNull(await fixture.Store.FindByRequestKeyAsync(WorkspaceA, "shared-key", default));
        Assert.Null(await fixture.Store.FindByRequestKeyAsync(WorkspaceB, "shared-key", default));

        // The same request key in another workspace is a different, creatable project.
        Assert.True(await fixture.Store.TryCreateAsync(Snapshot(WorkspaceB, "shared-key"), default));
    }

    [Fact]
    public async Task Commit_refuses_a_snapshot_that_reaches_across_the_workspace_fence()
    {
        using var fixture = new Fixture();
        var row = Snapshot(WorkspaceA, "fence-key");
        await fixture.Store.TryCreateAsync(row, default);

        // The workspace id is init-only by design, so the forgery is written the way an
        // adversarial snapshot would arrive: canonical JSON with the fence swapped, then
        // handed to the store as if it had legitimately been loaded from workspace B.
        var forged = AcademicWorkflowSerialization.Deserialize(
            AcademicWorkflowSerialization.Serialize(row).Replace(WorkspaceA.ToString(), WorkspaceB.ToString()))!;
        forged.Revision = 1;
        var commit = await fixture.Store.TryCommitAsync(forged, 0, $"{forged.ProjectId:N}|1|forged", default);
        Assert.Equal(AcademicCommitStatus.RevisionConflict, commit.Status);

        var stored = await fixture.Store.LoadAsync(row.ProjectId, default);
        Assert.Equal(WorkspaceA, stored!.WorkspaceId);
        Assert.Equal(0, stored.Revision);
    }

    [Fact]
    public async Task Projection_watermark_moves_forward_only()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(WorkspaceA, "watermark-key");
        await fixture.Store.TryCreateAsync(snapshot, default);

        Assert.Equal(-1, await fixture.Store.GetProjectedRevisionAsync(snapshot.ProjectId, default));
        Assert.True(await fixture.Store.SetProjectedRevisionAsync(snapshot.ProjectId, 5, default));
        Assert.False(await fixture.Store.SetProjectedRevisionAsync(snapshot.ProjectId, 3, default));
        Assert.Equal(5, await fixture.Store.GetProjectedRevisionAsync(snapshot.ProjectId, default));
    }

    [Fact]
    public async Task Checkpoint_ledger_is_ordered_by_revision_and_scoped_by_workspace()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(WorkspaceA, "ledger-key");
        await fixture.Store.TryCreateAsync(snapshot, default);

        var current = snapshot;
        for (long revision = 1; revision <= 3; revision++)
        {
            var next = current.Clone();
            next.Revision = revision;
            var result = await fixture.Store.TryCommitAsync(
                next, revision - 1, $"{next.ProjectId:N}|{revision}|ledger-{revision}", default);
            Assert.Equal(AcademicCommitStatus.Committed, result.Status);
            current = next;
        }

        var ledger = await fixture.Store.ListCheckpointsAsync(WorkspaceA, snapshot.ProjectId, 50, default);
        Assert.Equal([0, 1, 2, 3], ledger.Select(entry => entry.Revision).ToArray());
        Assert.Empty(await fixture.Store.ListCheckpointsAsync(WorkspaceB, snapshot.ProjectId, 50, default));
    }

    [Fact]
    public async Task Unknown_project_loads_null_and_reports_no_workspace()
    {
        using var fixture = new Fixture();
        var missing = Guid.NewGuid();
        Assert.Null(await fixture.Store.LoadAsync(missing, default));
        Assert.Null(await fixture.Store.GetWorkspaceIdAsync(missing, default));
    }
}
