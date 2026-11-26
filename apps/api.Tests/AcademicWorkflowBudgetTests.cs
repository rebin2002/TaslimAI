using Taslim.Api.AcademicWorkflow;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Budget ceilings must refuse work before it is bought and meter it exactly once after
/// it is spent: the properties that make long academic runs safe against accidental
/// recharges or duplicate provider bills.
/// </summary>
public sealed class AcademicWorkflowBudgetTests
{
    [Fact]
    public async Task Preflight_Rejects_Estimate_Above_Stage_Ceiling_Before_Handler_Runs()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.Discover].EstimateValue = new AcademicStageEstimate { EstimatedCostUsd = 4m, EstimatedProviderCalls = 2 };

        await harness.StartAsync("stage-ceiling");
        await harness.ApprovePendingAsync();
        var paused = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.PausedBudget, paused.Status);
        Assert.Equal("BUDGET_STAGE_LIMIT", paused.Code);
        Assert.Equal(0, harness.Handlers[AcademicStage.Discover].Calls.Count);
        Assert.DoesNotContain(paused.Snapshot!.Budget, entry => entry.Stage == AcademicStage.Discover);
        Assert.Equal(AcademicProjectState.PausedBudget, paused.Snapshot.State);

        // Resume cannot sneak past the refusal: it stays paused with zero new spend.
        var again = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
        Assert.Equal(AcademicWorkflowRunStatus.PausedBudget, again.Status);
        Assert.Equal(0, harness.Handlers[AcademicStage.Discover].Calls.Count);
    }

    [Fact]
    public async Task Total_Ceiling_Pauses_The_Project_And_Keeps_Charged_History()
    {
        var request = AcademicWorkflowFixtures.Request("total-ceiling") with
        {
            Budget = new AcademicBudgetPolicy { MaxTotalUsd = 0.9m, MaxStageUsd = 0.625m },
        };
        var harness = new AcademicWorkflowHarness(request);

        await harness.StartAsync("total-ceiling");
        await harness.ApprovePendingAsync();
        var paused = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.PausedBudget, paused.Status);
        Assert.Equal("BUDGET_TOTAL_LIMIT", paused.Code);
        Assert.Equal(0.5m, AcademicBudgetLedger.ChargedUsd(paused.Snapshot!));
        Assert.Empty(paused.Snapshot!.Sources);
    }

    [Fact]
    public async Task Measured_Overrun_Beyond_Total_Ceiling_Pauses_Without_Merging_Output()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.Discover].UsagePerCall = new AcademicUsageRecord { CostUsd = 30m, ProviderCalls = 5 };

        await harness.StartAsync("overrun");
        await harness.ApprovePendingAsync();
        var paused = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.PausedBudget, paused.Status);
        Assert.Equal("BUDGET_TOTAL_LIMIT", paused.Code);
        Assert.Empty(paused.Snapshot!.Sources);
        var discoverEntry = Assert.Single(paused.Snapshot.Budget.Where(entry => entry.Stage == AcademicStage.Discover));
        Assert.Null(discoverEntry.Committed); // refusal is conservative: the reservation stands as the charge
        Assert.Equal(discoverEntry.ReservedUsd, discoverEntry.ChargedUsd);
    }

    [Fact]
    public async Task Provider_Call_Ceiling_Refuses_Without_Launching_The_Handler()
    {
        var request = AcademicWorkflowFixtures.Request("call-ceiling") with
        {
            Budget = new AcademicBudgetPolicy { MaxStageProviderCalls = 3 },
        };
        var harness = new AcademicWorkflowHarness(request);
        harness.Handlers[AcademicStage.Discover].EstimateValue = new AcademicStageEstimate { EstimatedCostUsd = 0.1m, EstimatedProviderCalls = 5 };

        await harness.StartAsync("call-ceiling");
        await harness.ApprovePendingAsync();
        var paused = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal("BUDGET_STAGE_CALL_LIMIT", paused.Code);
        Assert.Equal(0, harness.Handlers[AcademicStage.Discover].Calls.Count);
    }

    [Fact]
    public async Task Golden_Pipeline_Meters_Every_Stage_Exactly_Once_In_Order()
    {
        var harness = new AcademicWorkflowHarness();

        var result = await harness.DrainWithApprovalsAsync();

        var budget = result.Snapshot!.Budget;
        Assert.Equal(AcademicStageOrder.Stages.Count, budget.Count);
        Assert.Equal(AcademicStageOrder.Stages.Select(stage => stage), budget.Select(entry => entry.Stage));
        Assert.All(budget, entry =>
        {
            Assert.Equal(1, entry.Attempt);
            Assert.NotNull(entry.Committed);
        });
        Assert.Equal(3.5m, AcademicBudgetLedger.ChargedUsd(result.Snapshot));
        Assert.Equal(8.5m, AcademicBudgetLedger.RemainingUsd(result.Snapshot));
        Assert.Equal(14, AcademicBudgetLedger.ChargedProviderCalls(result.Snapshot));
    }

    [Fact]
    public void Ledger_Reservation_Is_Idempotent_Per_Attempt_Key()
    {
        var snapshot = PopulatedSnapshot();
        var estimate = new AcademicStageEstimate { EstimatedCostUsd = 0.25m, EstimatedProviderCalls = 1 };

        Assert.True(AcademicBudgetLedger.Reserve(snapshot, "attempt-1", AcademicStage.Extract, 1, estimate));
        Assert.False(AcademicBudgetLedger.Reserve(snapshot, "attempt-1", AcademicStage.Extract, 1, estimate));
        Assert.Single(snapshot.Budget.Where(entry => entry.Stage == AcademicStage.Extract));
    }

    [Fact]
    public void Ledger_Metering_The_Same_Attempt_Twice_Never_Double_Charges()
    {
        var snapshot = PopulatedSnapshot();
        AcademicBudgetLedger.Reserve(
            snapshot, "attempt-1", AcademicStage.Extract, 1,
            new AcademicStageEstimate { EstimatedCostUsd = 0.25m, EstimatedProviderCalls = 1 });

        Assert.True(AcademicBudgetLedger.Meter(snapshot, "attempt-1", new AcademicUsageRecord { CostUsd = 0.3m, ProviderCalls = 2 }, out _, out _));
        Assert.True(AcademicBudgetLedger.Meter(snapshot, "attempt-1", new AcademicUsageRecord { CostUsd = 9.9m, ProviderCalls = 99 }, out _, out _));

        Assert.Equal(0.8m, AcademicBudgetLedger.ChargedUsd(snapshot)); // 0.5 plan + 0.3 metered once
        Assert.Equal(4, AcademicBudgetLedger.ChargedProviderCalls(snapshot));
    }

    [Fact]
    public void Ledger_Rejects_Unreserved_Usage_And_Invalid_Reports()
    {
        var snapshot = PopulatedSnapshot();

        Assert.False(AcademicBudgetLedger.Meter(snapshot, "ghost-key", new AcademicUsageRecord { CostUsd = 0.1m, ProviderCalls = 1 }, out var code, out _));
        Assert.Equal("BUDGET_UNRESERVED_USAGE", code);
        Assert.Throws<InvalidOperationException>(() => new AcademicUsageRecord { CostUsd = -1m, ProviderCalls = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AcademicUsageRecord { CostUsd = 0m, ProviderCalls = -2 }.Validate());
    }

    private static AcademicWorkflowSnapshot PopulatedSnapshot()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = Guid.NewGuid(),
            WorkspaceId = AcademicWorkflowFixtures.WorkspaceId,
            RequestKey = "ledger-unit",
            Request = AcademicWorkflowFixtures.Request("ledger-unit"),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        // Spend something first so ceilings are exercised against a non-zero ledger.
        AcademicBudgetLedger.Reserve(snapshot, "plan-a1", AcademicStage.Plan, 1, new AcademicStageEstimate { EstimatedCostUsd = 0.5m, EstimatedProviderCalls = 2 });
        AcademicBudgetLedger.Meter(snapshot, "plan-a1", new AcademicUsageRecord { CostUsd = 0.5m, ProviderCalls = 2 }, out _, out _);
        return snapshot;
    }
}
