using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AutopilotControllerTests
{
    private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
    private const string HeadSha = "1111111111111111111111111111111111111111";
    private const string CandidateSha = "2222222222222222222222222222222222222222";
    private const string OtherSha = "3333333333333333333333333333333333333333";

    [Fact]
    public async Task Launch_rejects_more_than_twenty_parallel_tasks()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var tasks = Enumerable.Range(1, 21).Select(index => TaskRequest($"task-{index}")).ToArray();
        var exception = await Assert.ThrowsAsync<AutopilotTransitionException>(() => service.LaunchAsync(Launch(9, tasks)));
        Assert.Equal("AUTOPILOT_TASK_COUNT_INVALID", exception.Code);
    }

    [Fact]
    public async Task Duplicate_events_are_idempotent_and_do_not_create_duplicate_runs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(1, TaskRequest("task-1")));
        var started = Event("start-1", AutopilotEventTypes.TaskStarted, "task-1");
        var first = await service.HandleEventAsync(summary.Id, started);
        var duplicate = await service.HandleEventAsync(summary.Id, started);
        Assert.Equal("accepted", first.Outcome);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(1, await fixture.Db.AutopilotRuns.CountAsync());

        var completed = Event("complete-1", AutopilotEventTypes.TaskCompleted, "task-1") with { Succeeded = true, HeadSha = HeadSha };
        await service.HandleEventAsync(summary.Id, completed);
        var final = await service.GetSummaryAsync(summary.Id);
        Assert.Equal(AutopilotWaveStatuses.IntegrationGate, final!.Status);
        Assert.Equal(2, final.ProcessedEventCount);
    }

    [Fact]
    public async Task Stale_sha_and_branch_events_are_recorded_but_cannot_advance_a_task()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(2, TaskRequest("task-1")));
        var stale = Event("stale-1", AutopilotEventTypes.TaskStarted, "task-1") with { BaseSha = OtherSha };
        var result = await service.HandleEventAsync(summary.Id, stale);
        Assert.Equal("ignored", result.Outcome);
        Assert.Equal("stale_branch_or_sha", result.Reason);
        Assert.Equal(AutopilotTaskStatuses.Pending, (await service.GetSummaryAsync(summary.Id))!.Tasks.Single().Status);
        Assert.Equal("stale_branch_or_sha", (await fixture.Db.AutopilotEvents.SingleAsync()).IgnoredReason);
    }

    [Fact]
    public async Task Partial_wave_does_not_open_integration_gate_until_all_mandatory_tasks_succeed()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(3, TaskRequest("task-1"), TaskRequest("task-2")));
        await StartAndCompleteAsync(service, summary.Id, "task-1", "partial");
        var partial = await service.GetSummaryAsync(summary.Id);
        Assert.Equal(AutopilotWaveStatuses.Running, partial!.Status);
        Assert.Empty(partial.Gates);

        await StartAndCompleteAsync(service, summary.Id, "task-2", "complete");
        var complete = await service.GetSummaryAsync(summary.Id);
        Assert.Equal(AutopilotWaveStatuses.IntegrationGate, complete!.Status);
        Assert.Contains(complete.Gates, gate => gate.GateType == AutopilotGateTypes.Integration && gate.Status == AutopilotGateStatuses.Pending);
    }

    [Fact]
    public async Task Ordinary_failure_retries_with_a_bound_and_exhaustion_fails_the_wave()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(4, [TaskRequest("task-1")], maxAttempts: 2));
        await service.HandleEventAsync(summary.Id, Event("start-4a", AutopilotEventTypes.TaskStarted, "task-1"));
        var failed = Event("fail-4a", AutopilotEventTypes.TaskCompleted, "task-1") with { Succeeded = false, HeadSha = HeadSha, FailureKind = AutopilotFailureKinds.Ordinary, FailureCode = "CI_FAILED" };
        await service.HandleEventAsync(summary.Id, failed);
        var retry = await service.GetSummaryAsync(summary.Id);
        Assert.Equal(AutopilotTaskStatuses.Pending, retry!.Tasks.Single().Status);
        Assert.Equal(2, retry.Tasks.Single().Attempt);

        await service.HandleEventAsync(summary.Id, Event("start-4b", AutopilotEventTypes.TaskStarted, "task-1"));
        var exhausted = Event("fail-4b", AutopilotEventTypes.TaskCompleted, "task-1") with { Succeeded = false, HeadSha = HeadSha, FailureKind = AutopilotFailureKinds.Ci, FailureCode = "CI_FAILED_AGAIN" };
        await service.HandleEventAsync(summary.Id, exhausted);
        var final = await service.GetSummaryAsync(summary.Id);
        Assert.Equal(AutopilotWaveStatuses.Failed, final!.Status);
        Assert.Equal(AutopilotTaskStatuses.Failed, final.Tasks.Single().Status);
    }

    [Fact]
    public async Task Failed_gate_is_bounded_and_cannot_be_bypassed_by_deployment()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(5, [TaskRequest("task-1")], maxGateAttempts: 2));
        await StartAndCompleteAsync(service, summary.Id, "task-1", "gate");
        await service.RecordGateAsync(summary.Id, new AutopilotGateResultRequest { GateType = AutopilotGateTypes.Integration, CandidateSha = CandidateSha, Passed = false, FailureCode = "INTEGRATION_FAILED" });
        var retry = await service.GetSummaryAsync(summary.Id);
        Assert.Equal(AutopilotWaveStatuses.IntegrationGate, retry!.Status);
        Assert.Equal(AutopilotGateStatuses.RetryPending, retry.Gates.Single().Status);
        await service.ReconcileAsync(summary.Id);
        await Assert.ThrowsAsync<AutopilotTransitionException>(() => service.RecordDeploymentAsync(summary.Id, new AutopilotDeploymentRequest { CandidateSha = CandidateSha, Succeeded = true }));
        await service.RecordGateAsync(summary.Id, new AutopilotGateResultRequest { GateType = AutopilotGateTypes.Integration, CandidateSha = CandidateSha, Passed = false, FailureCode = "INTEGRATION_FAILED_AGAIN" });
        Assert.Equal(AutopilotWaveStatuses.Failed, (await service.GetSummaryAsync(summary.Id))!.Status);
    }

    [Fact]
    public async Task Restart_recovery_expires_a_task_run_and_releases_the_next_attempt()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(6, [TaskRequest("task-1")], maxAttempts: 2));
        await service.HandleEventAsync(summary.Id, Event("start-6a", AutopilotEventTypes.TaskStarted, "task-1"));
        var run = await fixture.Db.AutopilotRuns.SingleAsync();
        run.LeaseUntil = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        await using var restartedDb = fixture.CreateDbContext();
        var restarted = fixture.CreateService(restartedDb);
        await restarted.ReconcileAsync(summary.Id, true);
        var recovered = await restarted.GetSummaryAsync(summary.Id);
        Assert.Equal(AutopilotTaskStatuses.Pending, recovered!.Tasks.Single().Status);
        Assert.Equal(2, recovered.Tasks.Single().Attempt);
        Assert.Contains(recovered.RecentAudit, item => item.Action == "task.lease_expired");
    }

    [Fact]
    public async Task Decision_sensitive_task_starts_paused_and_requires_explicit_resolution()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(7, new LaunchAutopilotTaskRequest { TaskKey = "pricing", Title = "Pricing decision", BranchName = "parallel/pricing", RequiresDecision = true, DecisionReason = AutopilotDecisionReasons.Pricing }));
        Assert.Equal(AutopilotWaveStatuses.NeedsDecision, summary.Status);
        var ignored = await service.HandleEventAsync(summary.Id, Event("start-7", AutopilotEventTypes.TaskStarted, "pricing"));
        Assert.Equal("ignored", ignored.Outcome);
        await service.ResolveDecisionAsync(summary.Id, new AutopilotDecisionRequest { DecisionReason = AutopilotDecisionReasons.Pricing, Approved = true, Note = "Approved for this wave only; charging remains disabled." });
        Assert.Equal(AutopilotTaskStatuses.Pending, (await service.GetSummaryAsync(summary.Id))!.Tasks.Single().Status);
    }

    [Fact]
    public async Task Deployment_and_smoke_require_the_exact_immutable_candidate_and_revision()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();
        var summary = await service.LaunchAsync(Launch(8, TaskRequest("task-1")));
        await StartAndCompleteAsync(service, summary.Id, "task-1", "release");
        await service.RecordGateAsync(summary.Id, new AutopilotGateResultRequest { GateType = AutopilotGateTypes.Integration, CandidateSha = CandidateSha, Passed = true });
        await service.RecordGateAsync(summary.Id, new AutopilotGateResultRequest { GateType = AutopilotGateTypes.FinalRelease, CandidateSha = CandidateSha, Passed = true });
        await Assert.ThrowsAsync<AutopilotTransitionException>(() => service.RecordDeploymentAsync(summary.Id, new AutopilotDeploymentRequest { CandidateSha = OtherSha, Succeeded = true }));
        await service.RecordDeploymentAsync(summary.Id, new AutopilotDeploymentRequest { CandidateSha = CandidateSha, Succeeded = true });
        await Assert.ThrowsAsync<AutopilotTransitionException>(() => service.RecordSmokeAsync(summary.Id, new AutopilotSmokeRequest { DeployedRevision = OtherSha, Passed = true }));
        await service.RecordSmokeAsync(summary.Id, new AutopilotSmokeRequest { DeployedRevision = CandidateSha, Passed = true });
        Assert.Equal(AutopilotWaveStatuses.Completed, (await service.GetSummaryAsync(summary.Id))!.Status);
    }

    private static LaunchAutopilotWaveRequest Launch(int waveNumber, params LaunchAutopilotTaskRequest[] tasks) => Launch(waveNumber, tasks, 3, 2);
    private static LaunchAutopilotWaveRequest Launch(int waveNumber, LaunchAutopilotTaskRequest[] tasks, int maxAttempts = 3, int maxGateAttempts = 2) => new()
    {
        WaveNumber = waveNumber,
        ProductionBaseSha = BaseSha,
        IntegrationBranch = $"integration/wave-{waveNumber}",
        ConcurrencyCeiling = 20,
        MaxTaskAttempts = maxAttempts,
        MaxGateAttempts = maxGateAttempts,
        Tasks = tasks.ToList(),
    };
    private static LaunchAutopilotTaskRequest TaskRequest(string key) => new() { TaskKey = key, Title = key, BranchName = $"parallel/{key}" };
    private static AutopilotEventRequest Event(string id, string type, string key) => new() { EventId = id, EventType = type, TaskKey = key, BranchName = $"parallel/{key}", BaseSha = BaseSha };

    private static async Task StartAndCompleteAsync(IAutopilotControllerService service, Guid waveId, string taskKey, string suffix)
    {
        await service.HandleEventAsync(waveId, Event($"start-{suffix}-{taskKey}", AutopilotEventTypes.TaskStarted, taskKey));
        await service.HandleEventAsync(waveId, Event($"complete-{suffix}-{taskKey}", AutopilotEventTypes.TaskCompleted, taskKey) with { Succeeded = true, HeadSha = HeadSha });
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string path;
        public TaslimDbContext Db { get; private set; } = null!;

        private TestFixture(SqliteConnection connection, string path, TaslimDbContext db)
        {
            this.connection = connection;
            this.path = path;
            Db = db;
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"taslim-autopilot-{Guid.NewGuid():N}.db");
            var connection = new SqliteConnection($"Data Source={path};Cache=Shared;Default Timeout=30");
            await connection.OpenAsync();
            var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new TestFixture(connection, path, db);
        }

        public TaslimDbContext CreateDbContext() => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        public IAutopilotControllerService CreateService(TaslimDbContext? context = null) => new AutopilotControllerService(context ?? Db, Options.Create(new AutopilotWatchdogOptions { Enabled = false, RunLeaseMinutes = 5, TaskLeaseMinutes = 30 }), TimeProvider.System, LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Error)).CreateLogger<AutopilotControllerService>());

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            try { File.Delete(path); } catch { }
        }
    }
}
