using Taslim.Api.AcademicWorkflow;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The durable-state contract: snapshots must round-trip through storage unchanged,
/// checkpoints must be monotonic and replay-safe, and stage fingerprints must be stable
/// for identical inputs and sensitive only to what actually changes a stage's work.
/// </summary>
public sealed class AcademicWorkflowStateContractTests
{
    [Fact]
    public void Snapshot_RoundTrips_Bit_For_Bit_Through_The_Canonical_Serializer()
    {
        var original = BuildPopulatedSnapshot();

        var json = AcademicWorkflowSerialization.Serialize(original);
        var restored = AcademicWorkflowSerialization.Deserialize(json);
        var reserialized = AcademicWorkflowSerialization.Serialize(restored);

        Assert.Equal(json, reserialized);
        Assert.Equal(original.ProjectId, restored.ProjectId);
        Assert.Equal(original.Revision, restored.Revision);
        Assert.Equal(original.Sources.Count, restored.Sources.Count);
        Assert.Equal(original.Budget.Count, restored.Budget.Count);
        Assert.Equal(original.PendingApproval!.ApprovalKey, restored.PendingApproval!.ApprovalKey);
        Assert.Equal(AcademicProjectState.AwaitingApproval, restored.State);
        Assert.Equal(AcademicStageState.Completed, restored.Stage(AcademicStage.Screen).State);
        Assert.Contains("\"FullText\"", json); // enums persist as readable strings, not magic numbers
        Assert.Contains("10.1001/kurdhj.2020.3617", json);
    }

    [Fact]
    public void Checkpoints_Are_Monotonic_Replay_Safe_And_Conflict_Safe()
    {
        var snapshot = BuildPopulatedSnapshot();
        snapshot.Revision = 0; // the revision-0 birth snapshot, as StartAsync would persist it
        var store = new InMemoryAcademicWorkflowStateStore();
        Assert.True(store.TryCreateAsync(snapshot, CancellationToken.None).Result);

        var first = snapshot.Clone();
        first.Revision = 1;
        Assert.Equal(AcademicCommitStatus.Committed,
            store.TryCommitAsync(first, 0, "key|r1", CancellationToken.None).Result.Status);

        // Identical checkpoint replay is a no-op duplicate, never a second application.
        Assert.Equal(AcademicCommitStatus.DuplicateCheckpoint,
            store.TryCommitAsync(first, 0, "key|r1", CancellationToken.None).Result.Status);

        // A writer still holding revision 0 can never move the head.
        var stale = snapshot.Clone();
        stale.Revision = 1;
        Assert.Equal(AcademicCommitStatus.RevisionConflict,
            store.TryCommitAsync(stale, 0, "key|r1-stale", CancellationToken.None).Result.Status);

        // And out-of-order revisions are rejected: the head may only advance by one.
        var jumpAhead = snapshot.Clone();
        jumpAhead.Revision = 9;
        Assert.Equal(AcademicCommitStatus.RevisionConflict,
            store.TryCommitAsync(jumpAhead, 5, "key|r9", CancellationToken.None).Result.Status);
    }

    [Fact]
    public void Fingerprint_Is_Stable_Across_Crash_Replays_And_Worker_Restarts()
    {
        var before = BuildPopulatedSnapshot();
        var afterCrash = AcademicWorkflowSerialization.Deserialize(AcademicWorkflowSerialization.Serialize(before));

        // The crashed attempt bumped counters and timestamps; none of that is work identity.
        afterCrash.Stage(AcademicStage.Extract).AttemptCount = 2;
        afterCrash.Stage(AcademicStage.Extract).State = AcademicStageState.Failed;
        afterCrash.Stage(AcademicStage.Extract).LastErrorCode = "PROVIDER_UNAVAILABLE";
        afterCrash.UpdatedAtUtc = afterCrash.UpdatedAtUtc.AddHours(3);

        Assert.Equal(
            AcademicWorkflowSerialization.ComputeStageInputFingerprint(before, AcademicStage.Extract),
            AcademicWorkflowSerialization.ComputeStageInputFingerprint(afterCrash, AcademicStage.Extract));
        Assert.Equal(
            AcademicWorkflowSerialization.BuildStageIdempotencyKey(before.ProjectId, AcademicStage.Extract, "fp"),
            AcademicWorkflowSerialization.BuildStageIdempotencyKey(afterCrash.ProjectId, AcademicStage.Extract, "fp"));
    }

    [Fact]
    public void Fingerprint_Only_Changes_When_The_Stages_Inputs_Change()
    {
        var baseline = BuildPopulatedSnapshot();
        var discoverFp = AcademicWorkflowSerialization.ComputeStageInputFingerprint(baseline, AcademicStage.Discover);
        var extractFp = AcademicWorkflowSerialization.ComputeStageInputFingerprint(baseline, AcademicStage.Extract);

        var newEvidence = baseline.Clone();
        newEvidence.Evidence.Add(new AcademicEvidenceItem
        {
            EvidenceId = "ev-new",
            SourceId = AcademicWorkflowFixtures.S2,
            Topic = "New extraction",
            Content = "Additional paraphrased finding from the licensed abstract.",
            Verbatim = false,
        });

        // Discovery's inputs did not change; extraction's did.
        Assert.Equal(discoverFp, AcademicWorkflowSerialization.ComputeStageInputFingerprint(newEvidence, AcademicStage.Discover));

        // Evidence is the Extract stage's OUTPUT, not its input, so it stays stable.
        Assert.Equal(extractFp, AcademicWorkflowSerialization.ComputeStageInputFingerprint(newEvidence, AcademicStage.Extract));
        // But its input owners do move it: a changed plan or a dropped included source.
        var planChanged = baseline.Clone();
        planChanged.Plan = planChanged.Plan! with { Objectives = ["Entirely new objective set"] };
        Assert.NotEqual(extractFp, AcademicWorkflowSerialization.ComputeStageInputFingerprint(planChanged, AcademicStage.Extract));

        var sourceDropped = baseline.Clone();
        sourceDropped.Screening.RemoveAll(item => item.SourceId == AcademicWorkflowFixtures.S1);
        Assert.NotEqual(extractFp, AcademicWorkflowSerialization.ComputeStageInputFingerprint(sourceDropped, AcademicStage.Extract));

        // And synthesis, which consumes evidence, reacts to the new extraction.
        var synthesizeFp = AcademicWorkflowSerialization.ComputeStageInputFingerprint(baseline, AcademicStage.Synthesize);
        Assert.NotEqual(synthesizeFp, AcademicWorkflowSerialization.ComputeStageInputFingerprint(newEvidence, AcademicStage.Synthesize));

        var withFeedback = baseline.Clone();
        withFeedback.Stage(AcademicStage.Extract).ApprovalFeedback = "Add KRG-specific sources.";
        Assert.NotEqual(extractFp, AcademicWorkflowSerialization.ComputeStageInputFingerprint(withFeedback, AcademicStage.Extract));
    }

    [Fact]
    public void Project_Ids_Are_Deterministic_Per_Request_Key()
    {
        var workspace = Guid.NewGuid();

        Assert.Equal(
            AcademicWorkflowOrchestrator.DeriveProjectId(workspace, "stable-key"),
            AcademicWorkflowOrchestrator.DeriveProjectId(workspace, "stable-key"));
        Assert.NotEqual(
            AcademicWorkflowOrchestrator.DeriveProjectId(workspace, "stable-key"),
            AcademicWorkflowOrchestrator.DeriveProjectId(workspace, "other-key"));
        Assert.NotEqual(
            AcademicWorkflowOrchestrator.DeriveProjectId(workspace, "stable-key"),
            AcademicWorkflowOrchestrator.DeriveProjectId(Guid.NewGuid(), "stable-key"));
    }

    [Fact]
    public async Task Revision_And_Budget_Lineage_Survive_A_Full_Drain()
    {
        var harness = new AcademicWorkflowHarness();

        var result = await harness.DrainWithApprovalsAsync();

        var snapshot = result.Snapshot!;
        // Two checkpoints per stage (reservation + completion), one per approval gate, plus completion.
        Assert.Equal(19, snapshot.Revision);
        Assert.Equal(harness.Store.CommitLog.Count, harness.Store.CommitLog.Distinct().Count()); // every checkpoint key unique
        Assert.Equal(19, harness.Store.CommitLog.Count);
        foreach (var entry in snapshot.Budget)
        {
            var lineage = snapshot.Stage(entry.Stage).IdempotencyKey!;
            Assert.StartsWith(lineage, entry.AttemptKey); // metering is always rooted in the stage key lineage
        }
    }

    [Fact]
    public void Approval_Keys_Bind_Kind_And_Checkpoint_Revision()
    {
        var projectId = Guid.NewGuid();

        var a = AcademicWorkflowSerialization.BuildApprovalKey(projectId, AcademicApprovalKind.Plan, 5);
        var b = AcademicWorkflowSerialization.BuildApprovalKey(projectId, AcademicApprovalKind.Plan, 5);
        var c = AcademicWorkflowSerialization.BuildApprovalKey(projectId, AcademicApprovalKind.Plan, 6);
        var d = AcademicWorkflowSerialization.BuildApprovalKey(projectId, AcademicApprovalKind.Synthesis, 5);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    private static AcademicWorkflowSnapshot BuildPopulatedSnapshot()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            WorkspaceId = AcademicWorkflowFixtures.WorkspaceId,
            RequestKey = "state-contract",
            Request = AcademicWorkflowFixtures.Request("state-contract"),
            Revision = 5,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        snapshot.Plan = AcademicWorkflowFixtures.Plan();
        snapshot.Sources = AcademicWorkflowFixtures.Sources();
        snapshot.Screening = AcademicWorkflowFixtures.Screening();
        snapshot.Stage(AcademicStage.Plan).State = AcademicStageState.Completed;
        snapshot.Stage(AcademicStage.Plan).IdempotencyKey = "plan-key";
        snapshot.Stage(AcademicStage.Plan).OutputCheckpointRevision = 2;
        snapshot.Stage(AcademicStage.Discover).State = AcademicStageState.Completed;
        snapshot.Stage(AcademicStage.Discover).IdempotencyKey = "discover-key";
        snapshot.Stage(AcademicStage.Discover).OutputCheckpointRevision = 3;
        snapshot.Stage(AcademicStage.Screen).State = AcademicStageState.Completed;
        snapshot.Stage(AcademicStage.Screen).IdempotencyKey = "screen-key";
        snapshot.Stage(AcademicStage.Screen).OutputCheckpointRevision = 5;
        snapshot.Approvals.Add(new AcademicApprovalRecord
        {
            ApprovalKey = AcademicWorkflowSerialization.BuildApprovalKey(snapshot.ProjectId, AcademicApprovalKind.Plan, 2),
            Kind = AcademicApprovalKind.Plan,
            CheckpointRevision = 2,
            Decision = AcademicApprovalDecision.Approved,
            Notes = "Looks good.",
            DecidedByUserId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            DecidedAtUtc = now,
        });
        snapshot.Budget.Add(new AcademicBudgetEntry
        {
            AttemptKey = "plan-key|a1",
            Stage = AcademicStage.Plan,
            Attempt = 1,
            ReservedUsd = 0.4m,
            ReservedProviderCalls = 2,
            Committed = new AcademicUsageRecord { CostUsd = 0.35m, ProviderCalls = 2 },
        });
        snapshot.PendingApproval = new AcademicPendingApproval
        {
            Kind = AcademicApprovalKind.SourceLibrary,
            Stage = AcademicStage.Screen,
            CheckpointRevision = 5,
            ApprovalKey = AcademicWorkflowSerialization.BuildApprovalKey(snapshot.ProjectId, AcademicApprovalKind.SourceLibrary, 5),
            RequestedAtUtc = now,
        };
        snapshot.State = AcademicProjectState.AwaitingApproval;
        return snapshot;
    }
}
