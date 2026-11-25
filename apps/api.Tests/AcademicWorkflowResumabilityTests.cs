using Taslim.Api.AcademicWorkflow;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Resume-after-crash, replay, concurrency, and approval-revision semantics. These are
/// the properties that let a long academic project survive worker restarts, duplicate
/// intake, and parallel writers without redoing or double-paying finished work.
/// </summary>
public sealed class AcademicWorkflowResumabilityTests
{
    [Fact]
    public async Task Fresh_Worker_Resuming_The_Same_Store_Skips_Completed_Stages()
    {
        var harness = new AcademicWorkflowHarness();
        await harness.StartAsync("restart"); // Plan stage completes and parks on its gate.
        Assert.Equal(1, harness.Handlers[AcademicStage.Plan].Calls.Count);

        // A new worker: same durable store, brand-new handler doubles.
        var successor = new Dictionary<AcademicStage, ScriptedStageHandler>();
        foreach (var stage in AcademicStageOrder.Stages)
            successor[stage] = new ScriptedStageHandler(stage);
        var secondWorker = new AcademicWorkflowOrchestrator(
            harness.Store, successor.Values.Cast<IAcademicStageHandler>(), new SteadyTimeProvider());

        var resumed = await secondWorker.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, resumed.Status);
        Assert.Equal(AcademicApprovalKind.Plan, resumed.Snapshot!.PendingApproval!.Kind);
        Assert.Equal(0, successor[AcademicStage.Plan].Calls.Count);
        Assert.Equal(0, successor[AcademicStage.Discover].Calls.Count);
    }

    [Fact]
    public async Task Retryable_Failure_Reruns_The_Same_Stage_Under_The_Same_Key_Once()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.Discover].QueueFailure(usage: new AcademicUsageRecord { CostUsd = 0.3m, ProviderCalls = 1 });

        await harness.StartAsync("retry");
        await harness.ApprovePendingAsync(); // releases the Plan gate
        var failed = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.StageFailure, failed.Status);
        var stageRecord = failed.Snapshot!.Stage(AcademicStage.Discover);
        Assert.Equal(AcademicStageState.Failed, stageRecord.State);
        Assert.Equal(1, stageRecord.AttemptCount);
        Assert.Equal(0, failed.Snapshot.Sources.Count);

        var recovered = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, recovered.Status); // Discover+Screen then gate

        var recoveredRecord = recovered.Snapshot!.Stage(AcademicStage.Discover);
        Assert.Equal(2, harness.Handlers[AcademicStage.Discover].Calls.Count);
        Assert.Equal(2, recoveredRecord.AttemptCount);
        Assert.Equal(stageRecord.IdempotencyKey, recoveredRecord.IdempotencyKey);
        Assert.Equal(stageRecord.InputFingerprint, recoveredRecord.InputFingerprint);

        // Each attempt metered exactly once, first attempt's failure usage retained.
        var discoverEntries = recovered.Snapshot.Budget.Where(entry => entry.Stage == AcademicStage.Discover).ToList();
        Assert.Equal(2, discoverEntries.Count);
        Assert.Equal(0.3m, discoverEntries[0].ChargedUsd);
        Assert.Equal(0.5m, discoverEntries[1].ChargedUsd);
    }

    [Fact]
    public async Task Throwing_Handler_Leaves_Reservation_Charged_And_Project_Resumable()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.Discover].QueueException(new HttpRequestException("provider socket died"));

        await harness.StartAsync("crash-attempt");
        await harness.ApprovePendingAsync();
        var crashed = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.StageFailure, crashed.Status);
        Assert.Equal("STAGE_EXCEPTION", crashed.Code);
        var entry = Assert.Single(crashed.Snapshot!.Budget.Where(item => item.Stage == AcademicStage.Discover));
        Assert.Null(entry.Committed);
        Assert.Equal(entry.ReservedUsd, entry.ChargedUsd); // crash billing stays honest: reserved counts

        var recovered = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, recovered.Status);
        Assert.Equal(2, recovered.Snapshot!.Budget.Count(entry => entry.Stage == AcademicStage.Discover));
    }

    [Fact]
    public async Task Concurrent_Writer_Produces_Revision_Conflict_Not_Corruption()
    {
        var harness = new AcademicWorkflowHarness();
        var rogueWriterFired = false;
        harness.Handlers[AcademicStage.Discover].OnCall = context =>
        {
            if (rogueWriterFired)
                return;
            rogueWriterFired = true;
            // Rogue writer commits an unrelated checkpoint at the engine's revision,
            // so the engine's stage-complete commit must find the head moved under it.
            var hijacked = context.Snapshot.Clone();
            hijacked.Revision = context.Snapshot.Revision + 1;
            hijacked.StateDetail = "rogue writer";
            _ = harness.Store.TryCommitAsync(
                hijacked, context.Snapshot.Revision, "rogue|checkpoint", CancellationToken.None).Result;
        };

        await harness.StartAsync("conflict");
        await harness.ApprovePendingAsync();
        var raced = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.RevisionConflict, raced.Status);

        // No lost writes: the engine reloads the committed head and finishes normally.
        var after = await harness.DrainFromCurrentAsync();
        Assert.Equal(AcademicWorkflowRunStatus.Completed, after.Status);
        Assert.Equal(4, (await harness.Store.LoadAsync(harness.ProjectId, CancellationToken.None))!.Sources.Count);
    }

    [Fact]
    public async Task Approval_Decision_Replay_Is_Applied_Exactly_Once()
    {
        var harness = new AcademicWorkflowHarness();
        await harness.StartAsync("decision-replay");
        var beforeRevision = (await harness.Store.LoadAsync(harness.ProjectId, CancellationToken.None))!.Revision;

        var first = await harness.Orchestrator.SubmitApprovalDecisionAsync(
            harness.ProjectId, AcademicApprovalKind.Plan, AcademicApprovalDecision.Approved, "ok", Guid.NewGuid(), CancellationToken.None);
        var replay = await harness.Orchestrator.SubmitApprovalDecisionAsync(
            harness.ProjectId, AcademicApprovalKind.Plan, AcademicApprovalDecision.Approved, "ok", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(AcademicApprovalSubmissionStatus.Applied, first.Status);
        Assert.Equal(AcademicApprovalSubmissionStatus.AlreadyApplied, replay.Status);
        Assert.Equal(beforeRevision + 1, first.Snapshot!.Revision);
        Assert.Equal(1, replay.Snapshot!.Approvals.Count);
    }

    [Fact]
    public async Task Late_Decision_For_A_Stale_Kind_Is_Rejected_Without_State_Movement()
    {
        var harness = new AcademicWorkflowHarness();
        await harness.StartAsync("wrong-gate");

        var wrong = await harness.Orchestrator.SubmitApprovalDecisionAsync(
            harness.ProjectId, AcademicApprovalKind.ChapterDraft, AcademicApprovalDecision.Approved, null, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(AcademicApprovalSubmissionStatus.NoPendingApproval, wrong.Status);
        Assert.Equal(AcademicProjectState.AwaitingApproval, wrong.Snapshot!.State);
        Assert.Empty(wrong.Snapshot.Approvals);
    }

    [Fact]
    public async Task RequestRevision_Reruns_The_Gated_Stage_Clearing_Downstream_Artifacts()
    {
        var harness = new AcademicWorkflowHarness();
        await harness.ResumeUntilGateAsync(AcademicApprovalKind.Synthesis);
        var themesBefore = (await harness.Store.LoadAsync(harness.ProjectId, CancellationToken.None))!.Themes.Count;
        Assert.Equal(2, themesBefore);

        var submission = await harness.Orchestrator.SubmitApprovalDecisionAsync(
            harness.ProjectId,
            AcademicApprovalKind.Synthesis,
            AcademicApprovalDecision.RequestRevision,
            "Fold in the KRG-specific pricing literature and state the gap more precisely.",
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.Equal(AcademicApprovalSubmissionStatus.Applied, submission.Status);

        var pausedAgain = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, pausedAgain.Status);
        Assert.Equal(AcademicApprovalKind.Synthesis, pausedAgain.Snapshot!.PendingApproval!.Kind);
        var synthesizeCalls = harness.Handlers[AcademicStage.Synthesize].Calls;
        Assert.Equal(2, synthesizeCalls.Count);
        Assert.Null(synthesizeCalls[0].ApprovalFeedback);
        Assert.Contains("KRG-specific pricing literature", synthesizeCalls[1].ApprovalFeedback);
        Assert.Equal(2, pausedAgain.Snapshot.Themes.Count);            // replaced, never duplicated
        Assert.Equal(2, pausedAgain.Snapshot.Evidence.Count);          // upstream artifacts survive
        Assert.NotEqual(synthesizeCalls[0].IdempotencyKey, synthesizeCalls[1].IdempotencyKey); // new lineage
    }

    [Fact]
    public async Task Rejected_Approval_Cancels_The_Project_Terminally()
    {
        var harness = new AcademicWorkflowHarness();
        await harness.StartAsync("reject");

        var submission = await harness.Orchestrator.SubmitApprovalDecisionAsync(
            harness.ProjectId, AcademicApprovalKind.Plan, AcademicApprovalDecision.Rejected, "Scope needs rework first.", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(AcademicApprovalSubmissionStatus.Applied, submission.Status);
        Assert.Equal(AcademicProjectState.Cancelled, submission.Snapshot!.State);
        Assert.Equal("APPROVAL_REJECTED", submission.Snapshot.StateCode);

        var resumed = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
        Assert.Equal(AcademicWorkflowRunStatus.Cancelled, resumed.Status);
        Assert.Equal(0, harness.Handlers[AcademicStage.Discover].Calls.Count);
    }

    [Fact]
    public async Task Resume_On_Unknown_Project_Reports_NotFound()
    {
        var harness = new AcademicWorkflowHarness();

        var missing = await harness.Orchestrator.ResumeAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.NotFound, missing.Status);
    }
}
