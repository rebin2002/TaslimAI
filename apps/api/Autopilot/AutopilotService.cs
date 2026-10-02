using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

public interface IAutopilotControllerService
{
    Task<AutopilotWaveSummaryDto> LaunchAsync(LaunchAutopilotWaveRequest request, CancellationToken cancellationToken = default);
    Task<AutopilotEventResult> HandleEventAsync(Guid waveId, AutopilotEventRequest request, CancellationToken cancellationToken = default);
    Task<AutopilotTransitionResult> RecordGateAsync(Guid waveId, AutopilotGateResultRequest request, CancellationToken cancellationToken = default);
    Task<AutopilotTransitionResult> RecordDeploymentAsync(Guid waveId, AutopilotDeploymentRequest request, CancellationToken cancellationToken = default);
    Task<AutopilotTransitionResult> RecordSmokeAsync(Guid waveId, AutopilotSmokeRequest request, CancellationToken cancellationToken = default);
    Task<AutopilotTransitionResult> ResolveDecisionAsync(Guid waveId, AutopilotDecisionRequest request, CancellationToken cancellationToken = default);
    Task<AutopilotTransitionResult> PauseAsync(Guid waveId, string reason, CancellationToken cancellationToken = default);
    Task<AutopilotTransitionResult> ReconcileAsync(Guid waveId, bool watchdog = false, CancellationToken cancellationToken = default);
    Task<int> ReconcileDueWavesAsync(CancellationToken cancellationToken = default);
    Task<AutopilotWaveSummaryDto?> GetSummaryAsync(Guid waveId, CancellationToken cancellationToken = default);
}

public sealed class AutopilotTransitionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AutopilotControllerService(
    TaslimDbContext db,
    IOptions<AutopilotWatchdogOptions> watchdogOptions,
    TimeProvider clock,
    ILogger<AutopilotControllerService> logger) : IAutopilotControllerService
{
    private readonly AutopilotWatchdogOptions options = watchdogOptions.Value;

    public async Task<AutopilotWaveSummaryDto> LaunchAsync(LaunchAutopilotWaveRequest request, CancellationToken cancellationToken = default)
    {
        ValidateLaunch(request);
        var fingerprint = Fingerprint(request);
        var resource = $"launch:{request.WaveNumber}";
        return await WithLockAsync(resource, async () =>
        {
            var existingByKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? null
                : await db.AutopilotWaves.AsNoTracking().SingleOrDefaultAsync(
                    item => item.LaunchIdempotencyKey == request.IdempotencyKey.Trim(), cancellationToken);
            if (existingByKey is not null)
            {
                if (!string.Equals(existingByKey.LaunchFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AutopilotTransitionException("AUTOPILOT_IDEMPOTENCY_REUSED", "The launch idempotency key was already used for a different wave.");
                return await LoadSummaryAsync(existingByKey.Id, cancellationToken)
                    ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave is no longer available.");
            }

            if (await db.AutopilotWaves.AnyAsync(item => item.WaveNumber == request.WaveNumber, cancellationToken))
                throw new AutopilotTransitionException("AUTOPILOT_WAVE_EXISTS", "This wave number has already been launched.");

            var now = UtcNow;
            var decisionTask = request.Tasks.FirstOrDefault(item =>
                item.RequiresDecision || !string.IsNullOrWhiteSpace(item.DecisionReason));
            var status = decisionTask is null ? AutopilotWaveStatuses.Running : AutopilotWaveStatuses.NeedsDecision;
            var wave = new AutopilotWave
            {
                Id = Guid.NewGuid(),
                WaveNumber = request.WaveNumber,
                Status = status,
                ProductionBaseSha = NormalizeSha(request.ProductionBaseSha),
                IntegrationBranch = request.IntegrationBranch.Trim(),
                ConcurrencyCeiling = request.ConcurrencyCeiling,
                MaxTaskAttempts = request.MaxTaskAttempts,
                MaxGateAttempts = request.MaxGateAttempts,
                DecisionReason = decisionTask is null ? null : NormalizeDecisionReason(decisionTask.DecisionReason),
                DecisionNote = decisionTask is null ? null : "Explicit operator decision is required before task execution.",
                LaunchIdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim(),
                LaunchFingerprint = fingerprint,
                CreatedAt = now,
                UpdatedAt = now,
            };
            foreach (var item in request.Tasks)
            {
                var requiresDecision = item.RequiresDecision || !string.IsNullOrWhiteSpace(item.DecisionReason);
                wave.Tasks.Add(new AutopilotTask
                {
                    Id = Guid.NewGuid(),
                    WaveId = wave.Id,
                    TaskKey = item.TaskKey.Trim(),
                    Title = item.Title.Trim(),
                    BranchName = item.BranchName.Trim(),
                    BaseSha = wave.ProductionBaseSha,
                    Status = requiresDecision ? AutopilotTaskStatuses.Blocked : AutopilotTaskStatuses.Pending,
                    Attempt = 0,
                    MaxAttempts = request.MaxTaskAttempts,
                    Mandatory = item.Mandatory,
                    RequiresDecision = requiresDecision,
                    DecisionReason = requiresDecision ? NormalizeDecisionReason(item.DecisionReason) : null,
                    CreatedAt = now,
                });
            }
            db.AutopilotWaves.Add(wave);
            AddAudit(wave, "wave.launch", status == AutopilotWaveStatuses.Running ? "accepted" : "paused",
                status == AutopilotWaveStatuses.Running ? "Wave launched with bounded task concurrency." : "Wave paused pending an explicit decision.");
            await db.SaveChangesAsync(cancellationToken);
            return ToSummary(wave);
        }, cancellationToken);
    }

    public async Task<AutopilotEventResult> HandleEventAsync(Guid waveId, AutopilotEventRequest request, CancellationToken cancellationToken = default)
    {
        ValidateEvent(request);
        return await WithLockAsync($"wave:{waveId}", async () =>
        {
            var now = UtcNow;
            var existing = await db.AutopilotEvents.AsNoTracking().SingleOrDefaultAsync(item => item.EventId == request.EventId.Trim(), cancellationToken);
            if (existing is not null)
            {
                var existingTask = existing.TaskId.HasValue
                    ? await db.AutopilotTasks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == existing.TaskId.Value, cancellationToken)
                    : null;
                var existingWave = await db.AutopilotWaves.AsNoTracking().SingleOrDefaultAsync(item => item.Id == existing.WaveId, cancellationToken);
                return new AutopilotEventResult(existing.WaveId, existing.TaskId, "duplicate", existingTask?.Status ?? "UNKNOWN",
                    existingWave?.Status ?? "UNKNOWN", true, "The event id was already durably processed.");
            }

            var wave = await db.AutopilotWaves.Include(item => item.Tasks).Include(item => item.Runs).SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken)
                ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave does not exist.");
            var task = wave.Tasks.SingleOrDefault(item => string.Equals(item.TaskKey, request.TaskKey.Trim(), StringComparison.Ordinal));
            var eventRecord = new AutopilotEvent
            {
                Id = Guid.NewGuid(),
                EventId = request.EventId.Trim(),
                WaveId = wave.Id,
                TaskId = task?.Id,
                EventType = request.EventType.Trim().ToUpperInvariant(),
                BranchName = request.BranchName.Trim(),
                BaseSha = NormalizeSha(request.BaseSha),
                HeadSha = request.HeadSha is null ? null : NormalizeSha(request.HeadSha),
                Succeeded = request.Succeeded,
                FailureKind = NormalizeFailureKind(request.FailureKind),
                FailureCode = NormalizeCode(request.FailureCode),
                PayloadHash = HashPayload(request.Payload),
                Processed = true,
                CreatedAt = now,
                ProcessedAt = now,
            };
            db.AutopilotEvents.Add(eventRecord);

            if (task is null)
                return await IgnoreEventAsync(wave, eventRecord, "task_not_found", cancellationToken);
            if (!string.Equals(task.BaseSha, eventRecord.BaseSha, StringComparison.Ordinal)
                || !string.Equals(task.BranchName, eventRecord.BranchName, StringComparison.Ordinal))
                return await IgnoreEventAsync(wave, eventRecord, "stale_branch_or_sha", cancellationToken);
            if (wave.Status is AutopilotWaveStatuses.NeedsDecision or AutopilotWaveStatuses.Paused)
                return await IgnoreEventAsync(wave, eventRecord, "wave_paused_for_explicit_decision", cancellationToken);
            if (wave.Status is AutopilotWaveStatuses.IntegrationGate or AutopilotWaveStatuses.FinalReleaseGate
                or AutopilotWaveStatuses.Deploying or AutopilotWaveStatuses.Smoke or AutopilotWaveStatuses.Completed)
                return await IgnoreEventAsync(wave, eventRecord, "task_events_closed_after_task_phase", cancellationToken);

            if (eventRecord.EventType == AutopilotEventTypes.TaskStarted)
            {
                if (task.Status == AutopilotTaskStatuses.Succeeded)
                    return await IgnoreEventAsync(wave, eventRecord, "task_already_succeeded", cancellationToken);
                if (task.Status == AutopilotTaskStatuses.Running)
                    return await IgnoreEventAsync(wave, eventRecord, "task_already_running", cancellationToken);
                if (task.Status == AutopilotTaskStatuses.Failed)
                    return await IgnoreEventAsync(wave, eventRecord, "task_retry_exhausted", cancellationToken);
                var active = wave.Tasks.Count(item => item.Status == AutopilotTaskStatuses.Running);
                if (active >= wave.ConcurrencyCeiling)
                    return await IgnoreEventAsync(wave, eventRecord, "concurrency_ceiling_reached", cancellationToken);
                if (task.Attempt == 0) task.Attempt = 1;
                if (task.Attempt > task.MaxAttempts)
                    return await IgnoreEventAsync(wave, eventRecord, "task_retry_exhausted", cancellationToken);
                task.Status = AutopilotTaskStatuses.Running;
                task.StartedAt = now;
                task.NextAttemptAt = null;
                var run = new AutopilotRun
                {
                    Id = Guid.NewGuid(),
                    WaveId = wave.Id,
                    TaskId = task.Id,
                    Kind = AutopilotRunKinds.Task,
                    Status = AutopilotRunStatuses.Running,
                    Attempt = task.Attempt,
                    BranchName = task.BranchName,
                    BaseSha = task.BaseSha,
                    StartedAt = now,
                    LeaseUntil = now.AddMinutes(Math.Max(1, options.TaskLeaseMinutes)),
                };
                db.AutopilotRuns.Add(run);
                AddAudit(wave, "task.start", "accepted", "Task start event accepted.", task, run);
                await db.SaveChangesAsync(cancellationToken);
                return new AutopilotEventResult(wave.Id, task.Id, "accepted", task.Status, wave.Status, false);
            }

            if (eventRecord.EventType != AutopilotEventTypes.TaskCompleted || request.Succeeded is null || string.IsNullOrWhiteSpace(eventRecord.HeadSha))
                return await IgnoreEventAsync(wave, eventRecord, "invalid_task_event_order_or_payload", cancellationToken);
            if (task.Status != AutopilotTaskStatuses.Running)
                return await IgnoreEventAsync(wave, eventRecord, "task_not_running", cancellationToken);

            var activeRun = wave.Runs
                .Where(item => item.TaskId == task.Id && item.Status == AutopilotRunStatuses.Running)
                .OrderByDescending(item => item.StartedAt)
                .FirstOrDefault();
            if (activeRun is null)
                return await IgnoreEventAsync(wave, eventRecord, "task_run_not_found", cancellationToken);
            activeRun.CompletedAt = now;
            activeRun.HeadSha = eventRecord.HeadSha;
            activeRun.Status = request.Succeeded.Value ? AutopilotRunStatuses.Succeeded : AutopilotRunStatuses.Failed;
            activeRun.FailureCode = eventRecord.FailureCode;
            task.HeadSha = eventRecord.HeadSha;
            task.CompletedAt = now;
            task.LastFailureCode = eventRecord.FailureCode;
            task.LastFailureKind = eventRecord.FailureKind;
            if (request.Succeeded.Value)
            {
                task.Status = AutopilotTaskStatuses.Succeeded;
                AddAudit(wave, "task.complete", "succeeded", "Task completion event accepted.", task, activeRun, eventRecord.EventId);
            }
            else if (AutopilotFailureKinds.RequiresDecision(eventRecord.FailureKind) || task.RequiresDecision)
            {
                task.Status = AutopilotTaskStatuses.Failed;
                wave.Status = AutopilotWaveStatuses.NeedsDecision;
                wave.DecisionReason = NormalizeDecisionReason(eventRecord.FailureKind) ?? task.DecisionReason ?? AutopilotDecisionReasons.AmbiguousBusiness;
                wave.DecisionNote = "Task outcome requires an explicit operator decision; no automatic retry was performed.";
                wave.LastErrorCode = eventRecord.FailureCode;
                AddAudit(wave, "task.complete", "needs_decision", "Task failure requires an explicit decision.", task, activeRun, eventRecord.EventId);
            }
            else if (task.Attempt < task.MaxAttempts)
            {
                task.Status = AutopilotTaskStatuses.RetryPending;
                task.NextAttemptAt = now;
                AddAudit(wave, "task.complete", "retry_pending", "Ordinary task failure scheduled for a bounded retry.", task, activeRun, eventRecord.EventId);
            }
            else
            {
                task.Status = AutopilotTaskStatuses.Failed;
                wave.Status = AutopilotWaveStatuses.Failed;
                wave.LastErrorCode = eventRecord.FailureCode ?? "TASK_RETRY_EXHAUSTED";
                AddAudit(wave, "task.complete", "retry_exhausted", "Task retry budget was exhausted; wave stopped safely.", task, activeRun, eventRecord.EventId);
            }
            wave.UpdatedAt = now;
            await ReconcileCoreAsync(wave, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new AutopilotEventResult(wave.Id, task.Id, request.Succeeded.Value ? "accepted" : "recorded", task.Status, wave.Status, false);
        }, cancellationToken);
    }

    public async Task<AutopilotTransitionResult> RecordGateAsync(Guid waveId, AutopilotGateResultRequest request, CancellationToken cancellationToken = default)
    {
        ValidateGate(request);
        return await WithLockAsync($"wave:{waveId}", async () =>
        {
            var wave = await db.AutopilotWaves.Include(item => item.Tasks).Include(item => item.Gates).SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken)
                ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave does not exist.");
            var gateType = request.GateType.Trim().ToUpperInvariant();
            var gate = wave.Gates.SingleOrDefault(item => item.GateType == gateType)
                ?? throw new AutopilotTransitionException("AUTOPILOT_GATE_NOT_READY", "The requested gate has not been opened.");
            var expectedStatus = gateType == AutopilotGateTypes.Integration ? AutopilotWaveStatuses.IntegrationGate : AutopilotWaveStatuses.FinalReleaseGate;
            if (wave.Status != expectedStatus || gate.Status != AutopilotGateStatuses.Pending)
                throw new AutopilotTransitionException("AUTOPILOT_GATE_ORDER", "The requested gate is not the current mandatory gate.");
            var candidate = NormalizeSha(request.CandidateSha);
            if (gateType == AutopilotGateTypes.Integration)
            {
                if (wave.Tasks.Any(item => item.Mandatory && item.Status != AutopilotTaskStatuses.Succeeded))
                    throw new AutopilotTransitionException("AUTOPILOT_TASKS_INCOMPLETE", "The integration gate requires every mandatory task to succeed.");
            }
            else if (!string.Equals(wave.IntegrationCandidateSha, candidate, StringComparison.Ordinal))
            {
                throw new AutopilotTransitionException("AUTOPILOT_CANDIDATE_MISMATCH", "The final release gate must use the immutable integration candidate.");
            }
            var now = UtcNow;
            gate.Attempt++;
            gate.CandidateSha = candidate;
            gate.EvaluatedAt = now;
            gate.FailureCode = NormalizeCode(request.FailureCode);
            if (!request.Passed)
            {
                if (gate.Attempt < gate.MaxAttempts)
                {
                    gate.Status = AutopilotGateStatuses.RetryPending;
                    wave.LastErrorCode = gate.FailureCode ?? "GATE_FAILED_RETRYABLE";
                    AddAudit(wave, $"gate.{gateType.ToLowerInvariant()}", "retry_pending", "Gate failure is retryable within the bounded gate budget.");
                }
                else
                {
                    gate.Status = AutopilotGateStatuses.Failed;
                    wave.Status = AutopilotWaveStatuses.Failed;
                    wave.LastErrorCode = gate.FailureCode ?? "GATE_RETRY_EXHAUSTED";
                    AddAudit(wave, $"gate.{gateType.ToLowerInvariant()}", "failed", "Gate retry budget was exhausted; wave stopped safely.");
                }
                wave.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return new AutopilotTransitionResult(wave.Id, wave.Status, "failed", gate.FailureCode);
            }
            gate.Status = AutopilotGateStatuses.Passed;
            gate.Checksum = candidate;
            if (gateType == AutopilotGateTypes.Integration)
            {
                if (wave.IntegrationCandidateSha is not null && !string.Equals(wave.IntegrationCandidateSha, candidate, StringComparison.Ordinal))
                    throw new AutopilotTransitionException("AUTOPILOT_CANDIDATE_IMMUTABLE", "The integration candidate cannot be changed once handed off.");
                wave.IntegrationCandidateSha = candidate;
                wave.Status = AutopilotWaveStatuses.FinalReleaseGate;
                if (!wave.Gates.Any(item => item.GateType == AutopilotGateTypes.FinalRelease))
                    db.AutopilotGates.Add(new AutopilotGate { Id = Guid.NewGuid(), WaveId = wave.Id, GateType = AutopilotGateTypes.FinalRelease, MaxAttempts = wave.MaxGateAttempts, CreatedAt = now });
                AddAudit(wave, "gate.integration", "passed", "Integration gate passed and immutable candidate was handed off.");
            }
            else
            {
                wave.Status = AutopilotWaveStatuses.Deploying;
                AddAudit(wave, "gate.final_release", "passed", "Final release gate passed; deployment may be requested for the immutable candidate.");
            }
            wave.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new AutopilotTransitionResult(wave.Id, wave.Status, "passed", candidate);
        }, cancellationToken);
    }

    public async Task<AutopilotTransitionResult> RecordDeploymentAsync(Guid waveId, AutopilotDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSha(request.CandidateSha, "candidate SHA");
        return await WithLockAsync($"wave:{waveId}", async () =>
        {
            var wave = await db.AutopilotWaves.Include(item => item.Gates).Include(item => item.Runs).SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken)
                ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave does not exist.");
            var candidate = NormalizeSha(request.CandidateSha);
            if (wave.Status != AutopilotWaveStatuses.Deploying
                || wave.Gates.SingleOrDefault(item => item.GateType == AutopilotGateTypes.FinalRelease)?.Status != AutopilotGateStatuses.Passed)
                throw new AutopilotTransitionException("AUTOPILOT_DEPLOY_ORDER", "Deployment requires a passed final release gate.");
            if (!string.Equals(wave.IntegrationCandidateSha, candidate, StringComparison.Ordinal))
                throw new AutopilotTransitionException("AUTOPILOT_CANDIDATE_MISMATCH", "Deployment must use the immutable integration candidate.");
            var now = UtcNow;
            var previousAttempts = wave.Runs.Count(item => item.Kind == AutopilotRunKinds.Deploy);
            var run = new AutopilotRun { Id = Guid.NewGuid(), WaveId = wave.Id, Kind = AutopilotRunKinds.Deploy, Attempt = previousAttempts + 1, Status = request.Succeeded ? AutopilotRunStatuses.Succeeded : AutopilotRunStatuses.Failed, BaseSha = wave.ProductionBaseSha, HeadSha = candidate, StartedAt = now, CompletedAt = now, FailureCode = NormalizeCode(request.FailureCode) };
            db.AutopilotRuns.Add(run);
            if (request.Succeeded)
            {
                wave.DeployedRevision = candidate;
                wave.Status = AutopilotWaveStatuses.Smoke;
                AddAudit(wave, "deploy", "succeeded", "Deployment recorded for the exact immutable candidate.", run: run);
            }
            else if (previousAttempts + 1 < wave.MaxGateAttempts)
            {
                wave.LastErrorCode = run.FailureCode ?? "DEPLOYMENT_FAILED_RETRYABLE";
                AddAudit(wave, "deploy", "retry_pending", "Deployment failed and remains retryable within the bounded deployment budget.", run: run);
            }
            else
            {
                wave.Status = AutopilotWaveStatuses.Failed;
                wave.LastErrorCode = run.FailureCode ?? "DEPLOYMENT_RETRY_EXHAUSTED";
                AddAudit(wave, "deploy", "failed", "Deployment retry budget was exhausted; wave stopped safely.", run: run);
            }
            wave.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new AutopilotTransitionResult(wave.Id, wave.Status, request.Succeeded ? "succeeded" : "failed", run.FailureCode);
        }, cancellationToken);
    }

    public async Task<AutopilotTransitionResult> RecordSmokeAsync(Guid waveId, AutopilotSmokeRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSha(request.DeployedRevision, "deployed revision");
        return await WithLockAsync($"wave:{waveId}", async () =>
        {
            var wave = await db.AutopilotWaves.Include(item => item.Runs).SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken)
                ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave does not exist.");
            var revision = NormalizeSha(request.DeployedRevision);
            if (wave.Status != AutopilotWaveStatuses.Smoke)
                throw new AutopilotTransitionException("AUTOPILOT_SMOKE_ORDER", "Production smoke is only valid after a recorded deployment.");
            if (!string.Equals(wave.DeployedRevision, revision, StringComparison.Ordinal))
                throw new AutopilotTransitionException("AUTOPILOT_DEPLOYED_REVISION_MISMATCH", "Smoke verification must match the exact deployed revision.");
            var now = UtcNow;
            var previousAttempts = wave.Runs.Count(item => item.Kind == AutopilotRunKinds.Smoke);
            var run = new AutopilotRun { Id = Guid.NewGuid(), WaveId = wave.Id, Kind = AutopilotRunKinds.Smoke, Attempt = previousAttempts + 1, Status = request.Passed ? AutopilotRunStatuses.Succeeded : AutopilotRunStatuses.Failed, BaseSha = wave.ProductionBaseSha, HeadSha = revision, StartedAt = now, CompletedAt = now, FailureCode = NormalizeCode(request.FailureCode) };
            db.AutopilotRuns.Add(run);
            if (request.Passed)
            {
                wave.Status = AutopilotWaveStatuses.Completed;
                wave.CompletedAt = now;
                AddAudit(wave, "production.smoke", "passed", "Production smoke passed against the exact deployed revision.", run: run);
            }
            else if (previousAttempts + 1 >= wave.MaxGateAttempts)
            {
                wave.Status = AutopilotWaveStatuses.Failed;
                wave.LastErrorCode = run.FailureCode ?? "PRODUCTION_SMOKE_FAILED";
                AddAudit(wave, "production.smoke", "failed", "Production smoke failed after the bounded retry budget.", run: run);
            }
            else
            {
                wave.LastErrorCode = run.FailureCode ?? "PRODUCTION_SMOKE_RETRYABLE";
                AddAudit(wave, "production.smoke", "retry_pending", "Production smoke failed and remains retryable.", run: run);
            }
            wave.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new AutopilotTransitionResult(wave.Id, wave.Status, request.Passed ? "passed" : "failed", run.FailureCode);
        }, cancellationToken);
    }

    public async Task<AutopilotTransitionResult> ResolveDecisionAsync(Guid waveId, AutopilotDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var reason = NormalizeDecisionReason(request.DecisionReason)
            ?? throw new AutopilotTransitionException("AUTOPILOT_DECISION_REASON_INVALID", "The decision reason is not supported.");
        if (string.IsNullOrWhiteSpace(request.Note))
            throw new AutopilotTransitionException("AUTOPILOT_DECISION_NOTE_REQUIRED", "An explicit operator note is required.");
        return await WithLockAsync($"wave:{waveId}", async () =>
        {
            var wave = await db.AutopilotWaves.Include(item => item.Tasks).SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken)
                ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave does not exist.");
            if (wave.Status is not (AutopilotWaveStatuses.NeedsDecision or AutopilotWaveStatuses.Paused))
                throw new AutopilotTransitionException("AUTOPILOT_DECISION_NOT_REQUIRED", "The wave is not waiting for an explicit decision.");
            if (wave.DecisionReason is not null && !string.Equals(wave.DecisionReason, reason, StringComparison.OrdinalIgnoreCase))
                throw new AutopilotTransitionException("AUTOPILOT_DECISION_REASON_MISMATCH", "The decision reason does not match the paused wave.");
            var now = UtcNow;
            wave.DecisionNote = request.Note.Trim();
            if (request.Approved)
            {
                wave.DecisionReason = null;
                wave.Status = AutopilotWaveStatuses.Running;
                foreach (var task in wave.Tasks.Where(item => item.Status == AutopilotTaskStatuses.Blocked))
                    task.Status = AutopilotTaskStatuses.Pending;
                AddAudit(wave, "decision.resolve", "approved", "Explicit operator decision recorded; eligible tasks were released.");
            }
            else
            {
                wave.Status = AutopilotWaveStatuses.Paused;
                AddAudit(wave, "decision.resolve", "paused", "Decision was not approved; wave remains paused.");
            }
            wave.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new AutopilotTransitionResult(wave.Id, wave.Status, request.Approved ? "approved" : "paused", reason);
        }, cancellationToken);
    }

    public async Task<AutopilotTransitionResult> PauseAsync(Guid waveId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new AutopilotTransitionException("AUTOPILOT_PAUSE_REASON_REQUIRED", "A pause reason is required.");
        return await WithLockAsync($"wave:{waveId}", async () =>
        {
            var wave = await db.AutopilotWaves.SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken)
                ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave does not exist.");
            if (wave.Status is AutopilotWaveStatuses.Completed or AutopilotWaveStatuses.Failed)
                throw new AutopilotTransitionException("AUTOPILOT_WAVE_TERMINAL", "A terminal wave cannot be paused.");
            wave.Status = AutopilotWaveStatuses.Paused;
            wave.DecisionReason = AutopilotDecisionReasons.AmbiguousBusiness;
            wave.DecisionNote = reason.Trim()[..Math.Min(500, reason.Trim().Length)];
            wave.UpdatedAt = UtcNow;
            AddAudit(wave, "wave.pause", "paused", "Wave paused explicitly by an operator.");
            await db.SaveChangesAsync(cancellationToken);
            return new AutopilotTransitionResult(wave.Id, wave.Status, "paused", wave.DecisionReason);
        }, cancellationToken);
    }

    public async Task<AutopilotTransitionResult> ReconcileAsync(Guid waveId, bool watchdog = false, CancellationToken cancellationToken = default)
    {
        return await WithLockAsync($"wave:{waveId}", async () =>
        {
            var wave = await db.AutopilotWaves.Include(item => item.Tasks).Include(item => item.Runs).Include(item => item.Gates).SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken)
                ?? throw new AutopilotTransitionException("AUTOPILOT_WAVE_NOT_FOUND", "The wave does not exist.");
            await ReconcileCoreAsync(wave, cancellationToken);
            AddAudit(wave, watchdog ? "watchdog.reconcile" : "wave.reconcile", "completed", "Durable event queue and state were reconciled.");
            wave.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return new AutopilotTransitionResult(wave.Id, wave.Status, "reconciled");
        }, cancellationToken);
    }

    public async Task<int> ReconcileDueWavesAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.AutopilotWaves.AsNoTracking()
            .Where(item => item.Status != AutopilotWaveStatuses.Completed && item.Status != AutopilotWaveStatuses.Failed)
            .Select(item => item.Id).ToListAsync(cancellationToken);
        var count = 0;
        foreach (var id in ids)
        {
            try
            {
                await ReconcileAsync(id, true, cancellationToken);
                count++;
            }
            catch (AutopilotTransitionException exception)
            {
                logger.LogWarning("Autopilot watchdog skipped wave {WaveId}. Code={Code}", id, exception.Code);
            }
        }
        return count;
    }

    public async Task<AutopilotWaveSummaryDto?> GetSummaryAsync(Guid waveId, CancellationToken cancellationToken = default) =>
        await LoadSummaryAsync(waveId, cancellationToken);

    private async Task ReconcileCoreAsync(AutopilotWave wave, CancellationToken cancellationToken)
    {
        if (wave.Status is AutopilotWaveStatuses.NeedsDecision or AutopilotWaveStatuses.Paused or AutopilotWaveStatuses.Failed or AutopilotWaveStatuses.Completed)
            return;
        var now = UtcNow;
        foreach (var run in wave.Runs.Where(item => item.Status == AutopilotRunStatuses.Running && item.LeaseUntil.HasValue && item.LeaseUntil.Value <= now).ToList())
        {
            run.Status = AutopilotRunStatuses.Expired;
            run.CompletedAt = now;
            if (run.TaskId.HasValue)
            {
                var task = wave.Tasks.SingleOrDefault(item => item.Id == run.TaskId.Value);
                if (task is not null && task.Status == AutopilotTaskStatuses.Running)
                {
                    task.LastFailureKind = AutopilotFailureKinds.Ordinary;
                    task.LastFailureCode = "TASK_LEASE_EXPIRED";
                    task.Status = task.Attempt < task.MaxAttempts ? AutopilotTaskStatuses.RetryPending : AutopilotTaskStatuses.Failed;
                    task.NextAttemptAt = now;
                    if (task.Status == AutopilotTaskStatuses.Failed)
                    {
                        wave.Status = AutopilotWaveStatuses.Failed;
                        wave.LastErrorCode = task.LastFailureCode;
                    }
                    AddAudit(wave, "task.lease_expired", task.Status == AutopilotTaskStatuses.RetryPending ? "retry_pending" : "failed", "Watchdog recovered an expired task lease.", task, run);
                }
            }
        }
        if (wave.Status == AutopilotWaveStatuses.Failed) return;
        foreach (var task in wave.Tasks.Where(item => item.Status == AutopilotTaskStatuses.RetryPending && (!item.NextAttemptAt.HasValue || item.NextAttemptAt <= now)).ToList())
        {
            if (task.Attempt >= task.MaxAttempts)
            {
                task.Status = AutopilotTaskStatuses.Failed;
                wave.Status = AutopilotWaveStatuses.Failed;
                wave.LastErrorCode = task.LastFailureCode ?? "TASK_RETRY_EXHAUSTED";
                continue;
            }
            task.Attempt++;
            task.Status = AutopilotTaskStatuses.Pending;
            task.NextAttemptAt = null;
            task.StartedAt = null;
            task.CompletedAt = null;
            AddAudit(wave, "task.retry", "scheduled", "Bounded ordinary retry released to the completion queue.", task);
        }
        foreach (var gate in wave.Gates.Where(item => item.Status == AutopilotGateStatuses.RetryPending).ToList())
        {
            gate.Status = AutopilotGateStatuses.Pending;
            AddAudit(wave, $"gate.{gate.GateType.ToLowerInvariant()}.retry", "scheduled", "Bounded gate retry released.");
        }
        if (wave.Status == AutopilotWaveStatuses.Running
            && wave.Tasks.Where(item => item.Mandatory).All(item => item.Status == AutopilotTaskStatuses.Succeeded))
        {
            wave.Status = AutopilotWaveStatuses.IntegrationGate;
            if (!wave.Gates.Any(item => item.GateType == AutopilotGateTypes.Integration))
                db.AutopilotGates.Add(new AutopilotGate { Id = Guid.NewGuid(), WaveId = wave.Id, GateType = AutopilotGateTypes.Integration, MaxAttempts = wave.MaxGateAttempts, CreatedAt = now });
            AddAudit(wave, "wave.integration_gate", "opened", "All mandatory task completions reconciled; integration gate is mandatory.");
        }
        await Task.CompletedTask;
    }

    private async Task<AutopilotEventResult> IgnoreEventAsync(AutopilotWave wave, AutopilotEvent eventRecord, string reason, CancellationToken cancellationToken)
    {
        eventRecord.IgnoredReason = reason;
        AddAudit(wave, "event.reconcile", "ignored", $"Event was durably recorded but ignored: {reason}.", eventId: eventRecord.EventId);
        await db.SaveChangesAsync(cancellationToken);
        var task = eventRecord.TaskId.HasValue ? wave.Tasks.SingleOrDefault(item => item.Id == eventRecord.TaskId.Value) : null;
        return new AutopilotEventResult(wave.Id, eventRecord.TaskId, "ignored", task?.Status ?? "UNKNOWN", wave.Status, false, reason);
    }

    private async Task<AutopilotWaveSummaryDto?> LoadSummaryAsync(Guid waveId, CancellationToken cancellationToken)
    {
        var wave = await db.AutopilotWaves.AsNoTracking()
            .Include(item => item.Tasks)
            .Include(item => item.Gates)
            .Include(item => item.Events)
            .Include(item => item.AuditEntries)
            .SingleOrDefaultAsync(item => item.Id == waveId, cancellationToken);
        return wave is null ? null : ToSummary(wave);
    }

    private static AutopilotWaveSummaryDto ToSummary(AutopilotWave wave)
    {
        var tasks = wave.Tasks.OrderBy(item => item.TaskKey).Select(item => new AutopilotTaskSummaryDto(item.Id, item.TaskKey, item.Title, item.BranchName, item.BaseSha, item.HeadSha, item.Status, item.Attempt, item.MaxAttempts, item.Mandatory, item.LastFailureKind, item.LastFailureCode, item.StartedAt, item.CompletedAt)).ToArray();
        var gates = wave.Gates.OrderBy(item => item.GateType).Select(item => new AutopilotGateSummaryDto(item.GateType, item.Status, item.Attempt, item.MaxAttempts, item.CandidateSha, item.FailureCode, item.EvaluatedAt)).ToArray();
        var audit = wave.AuditEntries.OrderByDescending(item => item.CreatedAt).Take(50).Select(item => new AutopilotAuditDto(item.Id, item.Action, item.Outcome, item.Details, item.TaskId, item.RunId, item.EventId, item.CreatedAt)).ToArray();
        return new AutopilotWaveSummaryDto(wave.Id, wave.WaveNumber, wave.Status, wave.ProductionBaseSha, wave.IntegrationBranch, wave.IntegrationCandidateSha, wave.DeployedRevision, wave.ConcurrencyCeiling, tasks.Count(item => item.Status == AutopilotTaskStatuses.Running), tasks.Count(item => item.Mandatory), tasks.Count(item => item.Mandatory && item.Status == AutopilotTaskStatuses.Succeeded), tasks.Count(item => item.Status == AutopilotTaskStatuses.Failed), tasks.Count(item => item.Status is AutopilotTaskStatuses.Pending or AutopilotTaskStatuses.RetryPending or AutopilotTaskStatuses.Running), wave.Events.Count(item => item.Processed), wave.CreatedAt, wave.UpdatedAt, wave.CompletedAt, wave.DecisionReason, wave.DecisionNote, wave.LastErrorCode, tasks, gates, audit);
    }

    private async Task<T> WithLockAsync<T>(string resourceKey, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = UtcNow;
        var existing = await db.AutopilotLocks.SingleOrDefaultAsync(item => item.ResourceKey == resourceKey, cancellationToken);
        if (existing is not null && existing.LeaseUntil > now)
            throw new AutopilotTransitionException("AUTOPILOT_LOCKED", "Another controller operation currently owns this resource lock.");
        if (existing is null)
        {
            existing = new AutopilotLock { Id = Guid.NewGuid(), ResourceKey = resourceKey, OwnerId = Guid.NewGuid().ToString("N"), AcquiredAt = now, LeaseUntil = now.AddMinutes(Math.Max(1, options.RunLeaseMinutes)) };
            db.AutopilotLocks.Add(existing);
        }
        else
        {
            existing.OwnerId = Guid.NewGuid().ToString("N");
            existing.AcquiredAt = now;
            existing.LeaseUntil = now.AddMinutes(Math.Max(1, options.RunLeaseMinutes));
        }
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var result = await action();
            db.AutopilotLocks.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private void AddAudit(AutopilotWave wave, string action, string outcome, string details, AutopilotTask? task = null, AutopilotRun? run = null, string? eventId = null) =>
        db.AutopilotAuditEntries.Add(new AutopilotAuditEntry { Id = Guid.NewGuid(), WaveId = wave.Id, TaskId = task?.Id, RunId = run?.Id, EventId = eventId, Action = action, Outcome = outcome, Details = details, CreatedAt = UtcNow });

    private static void ValidateLaunch(LaunchAutopilotWaveRequest request)
    {
        if (request.WaveNumber < 1) throw new AutopilotTransitionException("AUTOPILOT_WAVE_NUMBER_INVALID", "The wave number must be positive.");
        ValidateSha(request.ProductionBaseSha, "production base SHA");
        if (string.IsNullOrWhiteSpace(request.IntegrationBranch)) throw new AutopilotTransitionException("AUTOPILOT_BRANCH_REQUIRED", "An integration branch is required.");
        if (request.Tasks.Count is < 1 or > 20) throw new AutopilotTransitionException("AUTOPILOT_TASK_COUNT_INVALID", "A wave must contain between one and twenty tasks.");
        if (request.Tasks.Select(item => item.TaskKey.Trim()).Distinct(StringComparer.Ordinal).Count() != request.Tasks.Count) throw new AutopilotTransitionException("AUTOPILOT_TASK_KEYS_DUPLICATE", "Task keys must be unique within a wave.");
        foreach (var task in request.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskKey) || string.IsNullOrWhiteSpace(task.BranchName)) throw new AutopilotTransitionException("AUTOPILOT_TASK_INVALID", "Every task needs a key and branch.");
            if (task.RequiresDecision && NormalizeDecisionReason(task.DecisionReason) is null) throw new AutopilotTransitionException("AUTOPILOT_DECISION_REASON_INVALID", "Decision-gated tasks must identify a supported decision reason.");
        }
    }

    private static void ValidateEvent(AutopilotEventRequest request)
    {
        if (!EnumLike(request.EventType, AutopilotEventTypes.TaskStarted, AutopilotEventTypes.TaskCompleted)) throw new AutopilotTransitionException("AUTOPILOT_EVENT_TYPE_INVALID", "Unsupported autopilot event type.");
        ValidateSha(request.BaseSha, "event base SHA");
        if (request.EventType.Equals(AutopilotEventTypes.TaskCompleted, StringComparison.OrdinalIgnoreCase) && (request.Succeeded is null || !IsSha(request.HeadSha))) throw new AutopilotTransitionException("AUTOPILOT_COMPLETION_INVALID", "Task completion requires success and a commit SHA.");
        if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.TaskKey) || string.IsNullOrWhiteSpace(request.BranchName)) throw new AutopilotTransitionException("AUTOPILOT_EVENT_INVALID", "Event id, task key, and branch are required.");
    }

    private static void ValidateGate(AutopilotGateResultRequest request) => ValidateSha(request.CandidateSha, "candidate SHA");
    private static void ValidateSha(string? value, string label)
    {
        if (!IsSha(value)) throw new AutopilotTransitionException("AUTOPILOT_SHA_INVALID", $"A valid 40-character {label} is required.");
    }
    private static bool IsSha(string? value) => value is { Length: 40 } && value.All(character => Uri.IsHexDigit(character));
    private static string NormalizeSha(string value) => value.Trim().ToLowerInvariant();
    private static string? NormalizeCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(100, value.Trim().Length)];
    private static string? NormalizeFailureKind(string? value) => string.IsNullOrWhiteSpace(value) ? AutopilotFailureKinds.Ordinary : value.Trim().ToUpperInvariant();
    private static string? NormalizeDecisionReason(string? value) => string.IsNullOrWhiteSpace(value) ? null : AutopilotDecisionReasons.All.Contains(value.Trim()) ? value.Trim().ToUpperInvariant() : null;
    private static bool EnumLike(string value, params string[] allowed) => allowed.Any(item => string.Equals(item, value.Trim(), StringComparison.OrdinalIgnoreCase));
    private static string? HashPayload(string? payload) => string.IsNullOrWhiteSpace(payload) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    private static string Fingerprint(LaunchAutopilotWaveRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", request.WaveNumber, request.ProductionBaseSha.Trim().ToLowerInvariant(), request.IntegrationBranch.Trim(), string.Join(",", request.Tasks.OrderBy(item => item.TaskKey).Select(item => string.Join(":", item.TaskKey.Trim(), item.BranchName.Trim(), item.Mandatory, item.RequiresDecision, item.DecisionReason))))))).ToLowerInvariant();
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
}
