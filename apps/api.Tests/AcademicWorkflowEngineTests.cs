using Taslim.Api.AcademicWorkflow;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Golden-path coverage of the bounded plan → discover → screen → extract → synthesize →
/// draft → verify orchestration. All providers are scripted doubles; no network, no model.
/// </summary>
public sealed class AcademicWorkflowEngineTests
{
    [Fact]
    public async Task FullPipeline_Completes_Every_Stage_In_Order()
    {
        var harness = new AcademicWorkflowHarness();

        var result = await harness.DrainWithApprovalsAsync();

        Assert.Equal(AcademicWorkflowRunStatus.Completed, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Equal(AcademicProjectState.Completed, snapshot.State);
        foreach (var stage in AcademicStageOrder.Stages)
        {
            Assert.Equal(AcademicStageState.Completed, snapshot.Stage(stage).State);
            Assert.Equal(1, harness.Handlers[stage].Calls.Count);
        }

        Assert.NotNull(snapshot.Plan);
        Assert.Equal(4, snapshot.Sources.Count);
        Assert.Equal(4, snapshot.Screening.Count);
        Assert.Equal(2, snapshot.Evidence.Count);
        Assert.Equal(2, snapshot.Themes.Count);
        Assert.Single(snapshot.Chapters);
        Assert.True(snapshot.Verification!.Passed);
        Assert.Empty(snapshot.Verification.Issues.Where(issue => issue.Severity == AcademicIssueSeverity.Blocking));
        Assert.Equal(4, snapshot.Approvals.Count);
        Assert.All(snapshot.Approvals, record => Assert.Equal(AcademicApprovalDecision.Approved, record.Decision));
        Assert.Null(snapshot.PendingApproval);
        Assert.Equal(7, snapshot.Budget.Count);
        Assert.Equal(7, snapshot.Budget.Count(entry => entry.Committed is not null));
        Assert.Equal(3.5m, AcademicBudgetLedger.ChargedUsd(snapshot));
    }

    [Fact]
    public async Task FullPipeline_Without_Required_Approvals_Still_Completes()
    {
        var request = AcademicWorkflowFixtures.Request("no-gates") with { RequiredApprovals = [] };
        var harness = new AcademicWorkflowHarness(request);

        var result = await harness.DrainWithApprovalsAsync("no-gates");

        Assert.Equal(AcademicWorkflowRunStatus.Completed, result.Status);
        Assert.Empty(result.Snapshot!.Approvals);
    }

    [Fact]
    public async Task Multi_Stage_Slice_Advances_Past_Non_Gated_Stages_In_One_Resume()
    {
        // Production default: one resume call crosses every stage until a blocking point.
        var store = new InMemoryAcademicWorkflowStateStore();
        var handlers = AcademicStageOrder.Stages.Select(stage => (IAcademicStageHandler)new ScriptedStageHandler(stage)).ToList();
        var orchestrator = new AcademicWorkflowOrchestrator(store, handlers, new SteadyTimeProvider());

        var started = await orchestrator.StartAsync(AcademicWorkflowFixtures.Request("slice") with { RequiredApprovals = [] }, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.Completed, started.Status);
        Assert.True(started.Snapshot!.Revision >= 7);
    }

    [Fact]
    public async Task Start_Is_Idempotent_By_RequestKey_And_Never_Duplicates_Work()
    {
        var harness = new AcademicWorkflowHarness();

        var first = await harness.StartAsync("intake-key");
        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, first.Status);
        var planCalls = harness.Handlers[AcademicStage.Plan].Calls.Count;

        var replay = await harness.StartAsync("intake-key");

        Assert.Equal(first.Snapshot!.ProjectId, replay.Snapshot!.ProjectId);
        Assert.Equal(planCalls, harness.Handlers[AcademicStage.Plan].Calls.Count);
        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, replay.Status);
    }

    [Fact]
    public async Task Approval_Gates_Stop_The_Engine_At_Exact_Stages()
    {
        var harness = new AcademicWorkflowHarness();

        var plan = await harness.StartAsync("gates");
        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, plan.Status);
        Assert.Equal(AcademicApprovalKind.Plan, plan.Snapshot!.PendingApproval!.Kind);
        Assert.Equal(AcademicStage.Plan, plan.Snapshot.PendingApproval.Stage);
        Assert.Equal(AcademicStageState.NotStarted, plan.Snapshot.Stage(AcademicStage.Discover).State);
        Assert.True(plan.Snapshot.Stage(AcademicStage.Plan).OutputCheckpointRevision > 0);

        await harness.Orchestrator.SubmitApprovalDecisionAsync(
            harness.ProjectId, AcademicApprovalKind.Plan, AcademicApprovalDecision.Approved, null, Guid.NewGuid(), CancellationToken.None);
        var sourceLibrary = await harness.ResumeUntilGateAsync(AcademicApprovalKind.SourceLibrary);

        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, sourceLibrary.Status);
        Assert.Equal(AcademicApprovalKind.SourceLibrary, sourceLibrary.Snapshot!.PendingApproval!.Kind);
        Assert.Equal(AcademicStageState.Completed, sourceLibrary.Snapshot.Stage(AcademicStage.Screen).State);
        Assert.Equal(AcademicStageState.NotStarted, sourceLibrary.Snapshot.Stage(AcademicStage.Extract).State);
    }

    [Fact]
    public async Task Verify_Failing_Report_Cannot_Complete_The_Project()
    {
        var harness = new AcademicWorkflowHarness();
        await harness.ResumeUntilGateAsync(AcademicApprovalKind.Synthesis);
        await harness.ApprovePendingAsync();

        foreach (var _ in Enumerable.Range(0, 3))
            harness.Handlers[AcademicStage.Verify].QueueOutput(
                new AcademicStageOutput { Verification = AcademicWorkflowFixtures.Verification(passed: false) });

        AcademicWorkflowRunResult run = harness.RunResultPlaceholder;
        for (var step = 0; step < 8; step++)
        {
            run = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
            if (run.Status is AcademicWorkflowRunStatus.ProjectFailed or AcademicWorkflowRunStatus.Completed)
                break;
        }

        Assert.Equal(AcademicWorkflowRunStatus.ProjectFailed, run.Status);
        Assert.Equal("ACADEMIC_VERIFICATION_FAILED", run.Snapshot!.StateCode);
        Assert.Equal(AcademicProjectState.Failed, run.Snapshot.State);
        Assert.Null(run.Snapshot.Verification);
        Assert.Equal(3, harness.Handlers[AcademicStage.Verify].Calls.Count);
    }

    [Fact]
    public async Task Stage_Returning_Another_Stages_Artifact_Is_Rejected_Without_Merge()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.Discover].QueueOutput(new AcademicStageOutput { Screening = AcademicWorkflowFixtures.Screening() });

        await harness.StartAsync("misuse");
        await harness.ApprovePendingAsync();
        var run = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.StageFailure, run.Status);
        Assert.Equal("STAGE_OUTPUT_MISUSE", run.Code);
        Assert.Empty(run.Snapshot!.Sources);
        Assert.Equal(AcademicStageState.Failed, run.Snapshot.Stage(AcademicStage.Discover).State);
        Assert.Empty(run.Snapshot.Screening);
    }

    [Fact]
    public async Task Missing_Handler_Fails_The_Project_Terminal()
    {
        var store = new InMemoryAcademicWorkflowStateStore();
        var planOnly = new IAcademicStageHandler[] { new ScriptedStageHandler(AcademicStage.Plan) };
        var orchestrator = new AcademicWorkflowOrchestrator(store, planOnly, new SteadyTimeProvider());

        var result = await orchestrator.StartAsync(AcademicWorkflowFixtures.Request("no-handler") with { RequiredApprovals = [] }, CancellationToken.None);
        // First slice ran Plan and stopped at the bound; the Discover attempt fails terminally.
        result = await orchestrator.ResumeAsync(result.Snapshot!.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.ProjectFailed, result.Status);
        Assert.Equal("NO_STAGE_HANDLER", result.Snapshot!.StateCode);
    }

    [Fact]
    public async Task Uncited_MetadataOnly_Source_May_Still_Be_Cited_Bibliographically()
    {
        // The KRG annual report is licensed metadata-only. It must remain citable as a
        // reference — which is exactly what the golden pipeline does: S3 is cited in
        // chapter prose without any quote, locator, or extracted content.
        var harness = new AcademicWorkflowHarness();

        var result = await harness.DrainWithApprovalsAsync();

        var chapter = Assert.Single(result.Snapshot!.Chapters);
        var block = chapter.Blocks.Single(item => item.Kind == AcademicBlockKind.Paragraph);
        Assert.Contains(AcademicWorkflowFixtures.S3, block.CitationSourceIds);
        Assert.All(block.Quotes, quote => Assert.NotEqual(AcademicWorkflowFixtures.S3, quote.SourceId));
        Assert.DoesNotContain(result.Snapshot.Evidence, item => item.SourceId == AcademicWorkflowFixtures.S3);
    }

    [Fact]
    public void Request_Validation_Fails_Closed_On_Unbounded_Or_Empty_Intake()
    {
        AssertValidationFails(AcademicWorkflowFixtures.Request(" "));
        AssertValidationFails(AcademicWorkflowFixtures.Request("k") with { MaxDiscoverySources = 0 });
        AssertValidationFails(AcademicWorkflowFixtures.Request("k") with { MaxEvidencePerSource = 21 });
        AssertValidationFails(AcademicWorkflowFixtures.Request("k") with { MaxStageAttempts = 99 });
        AssertValidationFails(AcademicWorkflowFixtures.Request("k") with { ResearchQuestion = "" });
        AssertValidationFails(AcademicWorkflowFixtures.Request("k") with { Budget = new AcademicBudgetPolicy { MaxTotalUsd = -1m } });
        AssertValidationFails(AcademicWorkflowFixtures.Request("k") with { Budget = new AcademicBudgetPolicy { MaxStageUsd = 99m, MaxTotalUsd = 10m } });
        AssertValidationFails(AcademicWorkflowFixtures.Request("k") with { RequiredApprovals = [AcademicApprovalKind.Plan, AcademicApprovalKind.Plan] });
    }

    private static void AssertValidationFails(AcademicWorkflowRequest request) =>
        Assert.Throws<AcademicWorkflowRequestException>(() => request.Validate());

    [Fact]
    public async Task Plan_Merge_Owns_The_Plan_Version_And_Safeguards_The_Question()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.Plan].QueueOutput(new AcademicStageOutput
        {
            // A handler cannot forge the plan version; the engine re-derives it.
            Plan = AcademicWorkflowFixtures.Plan(version: 99) with { ResearchQuestion = "   " },
        });

        await harness.StartAsync("plan-revision");

        var snapshot = await harness.Store.LoadAsync(harness.ProjectId, CancellationToken.None);
        Assert.Equal(1, snapshot!.Plan!.PlanVersion);
        Assert.Equal(AcademicWorkflowFixtures.Request("x").ResearchQuestion, snapshot.Plan.ResearchQuestion);
    }

    [Fact]
    public async Task Draft_Headings_Off_The_Approved_Outline_Are_Integrity_Failures()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.DraftChapter].QueueOutput(new AcademicStageOutput
        {
            Chapters =
            [
                new AcademicChapterDraft
                {
                    ChapterId = "ch-x",
                    Heading = "Totally Improvised Chapter",
                    Blocks = [new AcademicChapterBlock { BlockId = "bx", Kind = AcademicBlockKind.Paragraph, Text = "Loose prose.", CitationSourceIds = [AcademicWorkflowFixtures.S1] }],
                },
            ],
        });

        await harness.ResumeUntilGateAsync(AcademicApprovalKind.Synthesis);
        await harness.ApprovePendingAsync();
        AcademicWorkflowRunResult run = harness.RunResultPlaceholder;
        for (var step = 0; step < 6; step++)
        {
            run = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
            if (run.Status is AcademicWorkflowRunStatus.StageFailure or AcademicWorkflowRunStatus.AwaitingApproval)
                break;
        }

        Assert.Equal("ACADEMIC_INTEGRITY_VIOLATION", run.Code);
        Assert.Contains("CHA_OUTLINE_MISMATCH", run.Detail);
        Assert.Empty(run.Snapshot!.Chapters);
    }

    [Fact]
    public async Task Chapter_Citing_An_Excluded_Retracted_Source_Is_Blocked_At_Draft()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.DraftChapter].QueueOutput(new AcademicStageOutput
        {
            Chapters =
            [
                new AcademicChapterDraft
                {
                    ChapterId = "ch-r",
                    Heading = "Introduction",
                    Blocks =
                    [
                        new AcademicChapterBlock
                        {
                            BlockId = "br",
                            Kind = AcademicBlockKind.Paragraph,
                            Text = "A retracted elasticity estimate circulated briefly.",
                            CitationSourceIds = [AcademicWorkflowFixtures.S4],
                        },
                    ],
                },
            ],
        });

        await harness.ResumeUntilGateAsync(AcademicApprovalKind.Synthesis);
        await harness.ApprovePendingAsync();
        AcademicWorkflowRunResult run = harness.RunResultPlaceholder;
        for (var step = 0; step < 6; step++)
        {
            run = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
            if (run.Status is AcademicWorkflowRunStatus.StageFailure or AcademicWorkflowRunStatus.AwaitingApproval)
                break;
        }

        Assert.Equal("ACADEMIC_INTEGRITY_VIOLATION", run.Code);
        Assert.Contains("CHA_CITATION_NOT_INCLUDED", run.Detail);
    }
}
