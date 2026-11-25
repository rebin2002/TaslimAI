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
