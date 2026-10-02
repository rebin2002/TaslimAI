using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Read-side projection for the administrator Autopilot console. Every returned
/// shape is provider-neutral and contains no provider, model, or prompt names,
/// so it is safe even if a future product surface reuses it.
/// </summary>
public sealed class AutopilotConsoleService(TaslimDbContext db)
{
    public async Task<AutopilotOverviewDto> GetOverviewAsync(AutopilotControlSnapshot control, string? waveKey, CancellationToken cancellationToken = default)
    {
        var run = waveKey is null
            ? await db.AutopilotRuns.AsNoTracking().OrderByDescending(item => item.UpdatedAt).FirstOrDefaultAsync(cancellationToken)
            : await db.AutopilotRuns.AsNoTracking().FirstOrDefaultAsync(item => item.WaveKey == waveKey, cancellationToken);

        var tasks = run is null
            ? []
            : await db.AutopilotWaveTasks.AsNoTracking()
                .Where(item => item.RunId == run.Id)
                .OrderBy(item => item.TaskId)
                .ToListAsync(cancellationToken);

        var gates = run is null
            ? []
            : await db.AutopilotGateEvaluations.AsNoTracking()
                .Where(item => item.RunId == run.Id)
                .OrderBy(item => item.CreatedAt)
                .ToListAsync(cancellationToken);

        var handoff = run is null
            ? null
            : await db.AutopilotReleaseHandoffs.AsNoTracking()
                .FirstOrDefaultAsync(item => item.RunId == run.Id, cancellationToken);

        var audit = await QueryAuditAsync(run?.WaveKey, 50, cancellationToken);
        var launches = await ListLaunchesAsync(run?.WaveKey, 5, cancellationToken);
        var backlog = await ListBacklogAsync(20, cancellationToken);

        return new AutopilotOverviewDto(
            MapControl(control),
            run is null ? null : MapRun(run),
            tasks.Select(MapTask).ToList(),
            gates.Select(MapGate).ToList(),
            handoff is null ? null : MapHandoff(handoff),
            audit,
            launches,
            backlog);
    }

    /// <summary>Follow-on wave launches. Provider-neutral and safe to expose.</summary>
    public async Task<IReadOnlyList<AutopilotWaveLaunchBatchDto>> ListLaunchesAsync(
        string? sourceWaveKey,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = db.AutopilotWaveLaunchBatches.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(sourceWaveKey))
            query = query.Where(item => item.SourceWaveKey == sourceWaveKey);

        var batches = await query
            .OrderByDescending(item => item.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken);

        if (batches.Count == 0) return [];

        var batchIds = batches.Select(item => item.Id).ToList();
        var launchTasks = await db.AutopilotWaveLaunchTasks.AsNoTracking()
            .Where(item => batchIds.Contains(item.BatchId))
            .OrderBy(item => item.TaskKey)
            .ToListAsync(cancellationToken);

        return batches
            .Select(batch => MapLaunchBatch(batch, launchTasks.Where(task => task.BatchId == batch.Id).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<AutopilotBacklogItemDto>> ListBacklogAsync(int limit, CancellationToken cancellationToken = default) =>
        (await db.AutopilotBacklogItems.AsNoTracking()
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken))
        .Select(AutopilotBacklogService.Map)
        .ToList();

    public async Task<IReadOnlyList<AutopilotEventDto>> ListEventsAsync(int limit, CancellationToken cancellationToken = default) =>
        (await db.AutopilotEvents.AsNoTracking()
            .OrderByDescending(item => item.ReceivedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken))
        .Select(item => new AutopilotEventDto(
            item.Id, item.EventType, item.Status, item.WaveKey, item.TaskId, item.Branch,
            item.BaseSha, item.CandidateSha, item.Attempt, item.SignatureVerified, item.ReplayProtected,
            item.Reason, item.ReceivedAt, item.ProcessedAt))
        .ToList();

    public async Task<IReadOnlyList<AutopilotAuditDto>> ListAuditAsync(string? waveKey, int limit, CancellationToken cancellationToken = default) =>
        await QueryAuditAsync(waveKey, limit, cancellationToken);

    /// <summary>Catalogue of decisions the controller always defers to a human.</summary>
    public IReadOnlyList<string> HumanDecisionCatalogue() => AutopilotHumanDecisions.All;

    /// <summary>Operations the controller can never perform, at any configuration.</summary>
    public IReadOnlyList<string> ForbiddenOperations() => AutopilotForbiddenActions.All;

    public AutopilotControlDto MapControl(AutopilotControlSnapshot control) => new(
        control.FeatureEnabled,
        control.DryRun,
        control.Paused,
        control.KillSwitchEngaged,
        control.ChargingEnabled,
        control.PaidProvidersEnabled,
        control.MaxConcurrency,
        control.WatchdogIntervalMinutes,
        control.LastReason,
        control.UpdatedAt);

    private async Task<IReadOnlyList<AutopilotAuditDto>> QueryAuditAsync(string? waveKey, int limit, CancellationToken cancellationToken)
    {
        var query = db.AutopilotAuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(waveKey))
            query = query.Where(item => item.WaveKey == waveKey);

        return (await query
            .OrderByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken))
        .Select(item => new AutopilotAuditDto(
            item.Id, item.Action, item.Outcome, item.Reason, item.WaveKey, item.TaskId,
            item.TaskState, item.RunState, item.Attempt, item.Branch, item.BaseSha,
            item.CandidateSha, item.DryRun, item.CreatedAt))
        .ToList();
    }

    private static AutopilotRunDto MapRun(AutopilotRun run) => new(
        run.Id, run.WaveKey, run.State, run.Branch, run.BaseSha, run.CandidateSha,
        run.TaskCount, run.SucceededTaskCount, run.FailedTaskCount, run.RetryScheduledCount,
        run.IntegrationGatePassed, run.ReleaseGatePassed, run.HumanDecisionRequired,
        run.LastStatusDetail, run.CreatedAt, run.UpdatedAt);

    private static AutopilotTaskDto MapTask(AutopilotWaveTask task) => new(
        task.Id, task.TaskId, task.State, task.FailureClass, task.Attempt, task.MaxAttempts,
        task.RequiresHumanDecision, task.HumanDecisionKind, task.NextAttemptAt, task.LastReason, task.UpdatedAt);

    private static AutopilotGateDto MapGate(AutopilotGateEvaluation gate) => new(
        gate.GateKind, gate.Outcome, gate.CandidateSha, gate.ReasonCode, gate.Reason,
        ParseChecks(gate.FailedChecksJson), gate.CreatedAt);

    private static AutopilotHandoffDto MapHandoff(AutopilotReleaseHandoff handoff) => new(
        handoff.Status, handoff.Branch, handoff.CandidateSha, handoff.DryRun,
        handoff.SmokeStatus, handoff.LastStatusDetail, handoff.UpdatedAt);

    private static AutopilotWaveLaunchBatchDto MapLaunchBatch(AutopilotWaveLaunchBatch batch, IReadOnlyList<AutopilotWaveLaunchTask> tasks) => new(
        batch.Id, batch.SourceWaveKey, batch.WaveKey, batch.Status, batch.ReasonCode,
        batch.HumanDecisionRequired, batch.BaseSha, batch.Branch, batch.DryRun,
        batch.TaskCount, batch.LaunchedTaskCount, batch.LaunchAttempt, batch.MaxLaunchAttempts,
        batch.CreatedAt, batch.UpdatedAt, batch.LaunchedAt,
        tasks.Select(MapLaunchTask).ToList());

    private static AutopilotWaveLaunchTaskDto MapLaunchTask(AutopilotWaveLaunchTask task) => new(
        task.Id, task.TaskKey, task.Title, task.BacklogItemKey, task.Status, task.ReasonCode,
        task.ExternalTaskId, task.LaunchAttempt, task.MaxLaunchAttempts, task.ReconcileCount,
        task.NextReconcileAt, task.UpdatedAt);

    private static IReadOnlyList<string> ParseChecks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
