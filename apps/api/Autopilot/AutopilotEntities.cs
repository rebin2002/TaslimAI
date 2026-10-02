namespace Taslim.Api.Autopilot;

/// <summary>
/// Durable intake record for a wave/task completion event delivered by the
/// Taslim completion bridge. This is the durable completion queue: every event
/// is persisted before it is processed, so a restart, a duplicate delivery, or
/// an out-of-order delivery can never lose or double-apply work.
/// </summary>
public sealed class AutopilotEvent
{
    public Guid Id { get; set; }

    /// <summary>Provider-neutral event type, e.g. <see cref="AutopilotEventTypes.WaveTaskCompleted"/>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Opaque identifier of the delivering integration. Never surfaced to product users.</summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>Identifier assigned by the source system; the pair (SourceSystem, ExternalEventId) is the replay anchor.</summary>
    public string ExternalEventId { get; set; } = string.Empty;

    /// <summary>Stable idempotency key derived from the source identity. Unique across the queue.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical payload; a repeated key with a different payload is rejected.</summary>
    public string PayloadHash { get; set; } = string.Empty;

    public string Status { get; set; } = AutopilotEventStatuses.Received;

    public bool SignatureVerified { get; set; }
    public bool ReplayProtected { get; set; }

    /// <summary>Bounded, non-sensitive summary. Never contains provider payloads, prompts, or credentials.</summary>
    public string? Reason { get; set; }

    public string? WaveKey { get; set; }
    public string? TaskId { get; set; }
    public string? Branch { get; set; }
    public string? BaseSha { get; set; }
    public string? CandidateSha { get; set; }

    public int Attempt { get; set; }
    public int ReconciliationCount { get; set; }

    public Guid? RunId { get; set; }
    public Guid? WaveTaskRecordId { get; set; }

    public DateTime ReceivedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    /// <summary>Bounded JSON of the provider-neutral completion signal. No provider/model/prompt names.</summary>
    public string? SignalJson { get; set; }
}

public static class AutopilotEventTypes
{
    public const string WaveTaskCompleted = "wave.task.completed";
    public const string WaveCompleted = "wave.completed";
    public const string WatchdogReconciliation = "watchdog.reconciliation";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WaveTaskCompleted, WaveCompleted, WatchdogReconciliation,
    };
}

public static class AutopilotEventStatuses
{
    public const string Received = "received";
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string DuplicateIgnored = "duplicate_ignored";
    public const string RejectedUnsigned = "rejected_unsigned";
    public const string RejectedReplay = "rejected_replay";
    public const string RejectedInvalid = "rejected_invalid";
    public const string Failed = "failed";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Processed, DuplicateIgnored, RejectedUnsigned, RejectedReplay, RejectedInvalid, Failed,
    };
}

/// <summary>Explicit run state machine for one autonomous wave.</summary>
public static class AutopilotRunStates
{
    public const string Planned = "planned";
    public const string AwaitingEvents = "awaiting_events";
    public const string InProgress = "in_progress";
    public const string TasksComplete = "tasks_complete";
    public const string IntegrationGate = "integration_gate";
    public const string IntegrationFailed = "integration_failed";
    public const string ReleaseGate = "release_gate";
    public const string ReleaseEligible = "release_eligible";
    public const string ReleaseBlocked = "release_blocked";
    public const string HandoffPendingHuman = "handoff_pending_human";
    public const string HandoffReady = "handoff_ready";
    public const string SmokeRunning = "smoke_running";
    public const string SmokePassed = "smoke_passed";
    public const string SmokeFailed = "smoke_failed";
    public const string NextWaveEligible = "next_wave_eligible";
    public const string Paused = "paused";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        NextWaveEligible, Completed, Cancelled,
    };

    public static readonly IReadOnlySet<string> SafetyStopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        IntegrationFailed, ReleaseBlocked, SmokeFailed, Cancelled,
    };
}

/// <summary>Explicit per-task state machine inside a wave.</summary>
public static class AutopilotTaskStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string AwaitingResult = "awaiting_result";
    public const string Succeeded = "succeeded";
    public const string RetryScheduled = "retry_scheduled";
    public const string TerminalFailed = "terminal_failed";
    public const string BlockedHuman = "blocked_human";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Succeeded, TerminalFailed, BlockedHuman,
    };
}

public sealed class AutopilotRun
{
    public Guid Id { get; set; }

    /// <summary>Stable wave identifier, e.g. the agreed wave key. Unique.</summary>
    public string WaveKey { get; set; } = string.Empty;

    public string RunKey { get; set; } = string.Empty;

    public string State { get; set; } = AutopilotRunStates.AwaitingEvents;

    /// <summary>Immutable expected base SHA for the wave. Never rewritten once recorded.</summary>
    public string BaseSha { get; set; } = string.Empty;

    /// <summary>Immutable candidate SHA under evaluation. A conflicting write is rejected.</summary>
    public string CandidateSha { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public int TaskCount { get; set; }
    public int SucceededTaskCount { get; set; }
    public int FailedTaskCount { get; set; }
    public int RetryScheduledCount { get; set; }
    public int ReconciliationCount { get; set; }

    /// <summary>Declared task count for the wave. 0 means the wave size is not yet known.</summary>
    public int ExpectedTaskCount { get; set; }

    /// <summary>True once the bridge explicitly reports that the wave has no further tasks.</summary>
    public bool WaveClosed { get; set; }

    public DateTime? WaveCompletedAt { get; set; }

    public bool IntegrationGatePassed { get; set; }
    public bool ReleaseGatePassed { get; set; }

    /// <summary>Set when a human decision is required before the run may advance.</summary>
    public string? HumanDecisionRequired { get; set; }

    public string? LastReason { get; set; }
    public string? LastStatusDetail { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastEventAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}

public sealed class AutopilotWaveTask
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    public string WaveKey { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;

    public string State { get; set; } = AutopilotTaskStates.Pending;

    /// <summary>Normalized failure class; ordinary failures may be retried automatically.</summary>
    public string? FailureClass { get; set; }

    public bool RequiresHumanDecision { get; set; }
    public string? HumanDecisionKind { get; set; }

    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }

    public string? Branch { get; set; }
    public string? BaseSha { get; set; }
    public string? CandidateSha { get; set; }

    public string? EvidenceSummary { get; set; }
    public string? ChecksJson { get; set; }
    public string? LastReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastEventAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Immutable, append-only evidence for a gate evaluation. Unique per (run, gate, candidate SHA).</summary>
public sealed class AutopilotGateEvaluation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string WaveKey { get; set; } = string.Empty;

    /// <summary>Integration or release.</summary>
    public string GateKind { get; set; } = AutopilotGateKinds.Integration;

    public string Outcome { get; set; } = AutopilotGateOutcomes.Pending;

    public string CandidateSha { get; set; } = string.Empty;
    public string BaseSha { get; set; } = string.Empty;

    /// <summary>Bounded reason code, provider-neutral and safe to expose.</summary>
    public string? ReasonCode { get; set; }
    public string? Reason { get; set; }

    /// <summary>Bounded JSON list of failed check names.</summary>
    public string? FailedChecksJson { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class AutopilotGateKinds
{
    public const string Integration = "integration";
    public const string Release = "release";
}

public static class AutopilotGateOutcomes
{
    public const string Pending = "pending";
    public const string Passed = "passed";
    public const string Failed = "failed";
}

/// <summary>
/// Controlled release handoff record. In the foundation the handoff is prepared
/// and recorded; the actual production action stays behind an explicit
/// human authorization decision unless an operator enables unattended release.
/// </summary>
public sealed class AutopilotReleaseHandoff
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string WaveKey { get; set; } = string.Empty;

    public string CandidateSha { get; set; } = string.Empty;
    public string BaseSha { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;

    /// <summary>Prepared, AwaitingHuman, Authorized, Completed, Blocked.</summary>
    public string Status { get; set; } = AutopilotHandoffStatuses.Prepared;

    public bool DryRun { get; set; }

    public string? SmokeStatus { get; set; }
    public string? SmokeReason { get; set; }

    public string? LastStatusDetail { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class AutopilotHandoffStatuses
{
    public const string Prepared = "prepared";
    public const string AwaitingHuman = "awaiting_human";
    public const string Authorized = "authorized";
    public const string Completed = "completed";
    public const string Blocked = "blocked";
}

public static class AutopilotSmokeStatuses
{
    public const string NotRun = "not_run";
    public const string Passed = "passed";
    public const string Failed = "failed";
}

/// <summary>
/// Exclusive, fenced lock. A unique resource key guarantees that duplicate
/// events can never launch duplicate integrations, releases, or waves.
/// </summary>
public sealed class AutopilotLock
{
    public Guid Id { get; set; }

    /// <summary>Unique logical resource, e.g. <c>autopilot:wave:{waveKey}</c>.</summary>
    public string ResourceKey { get; set; } = string.Empty;

    public string OwnerToken { get; set; } = string.Empty;

    /// <summary>Monotonically increasing fencing token; a stale holder is always rejected.</summary>
    public long FencingToken { get; set; }

    public DateTime AcquiredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? HeldBy { get; set; }
}

/// <summary>
/// Append-only audit log for every decision. Deliberately stores only bounded
/// summaries: reason, status, attempt, task ids, branch, and SHA. Credentials,
/// provider payloads, prompts, and model names must never be written here.
/// </summary>
public sealed class AutopilotAuditEvent
{
    public Guid Id { get; set; }

    /// <summary>Empty for controller-level (non-actor) decisions.</summary>
    public Guid? ActorUserId { get; set; }

    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }

    public string Outcome { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? StatusDetail { get; set; }

    public string? WaveKey { get; set; }
    public string? TaskId { get; set; }
    public string? RunState { get; set; }
    public string? TaskState { get; set; }
    public int Attempt { get; set; }
    public string? Branch { get; set; }
    public string? BaseSha { get; set; }
    public string? CandidateSha { get; set; }
    public string? RequestId { get; set; }

    /// <summary>True when the decision was evaluated but not executed (dry-run/simulation).</summary>
    public bool DryRun { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Single-row control record implementing kill switch / pause / resume semantics.</summary>
public sealed class AutopilotControlState
{
    public Guid Id { get; set; }

    /// <summary>Fixed key so the table always holds exactly one control row.</summary>
    public string ControlKey { get; set; } = AutopilotControlState.SingletonKey;

    public bool Paused { get; set; }

    /// <summary>Hard kill switch. While engaged, no orchestration work is performed at all.</summary>
    public bool KillSwitchEngaged { get; set; }

    public string? LastReason { get; set; }
    public string? LastActor { get; set; }

    public DateTime UpdatedAt { get; set; }

    public const string SingletonKey = "autopilot-control";
}

public static class AutopilotAuditActions
{
    public const string EventReceived = "event.received";
    public const string EventRejected = "event.rejected";
    public const string EventDuplicateIgnored = "event.duplicate_ignored";
    public const string EventProcessed = "event.processed";
    public const string RunStateChanged = "run.state_changed";
    public const string TaskStateChanged = "task.state_changed";
    public const string TaskRetryScheduled = "task.retry_scheduled";
    public const string TaskRetryExhausted = "task.retry_exhausted";
    public const string GateEvaluated = "gate.evaluated";
    public const string HumanDecisionRequired = "human.decision_required";
    public const string ReleaseHandoffPrepared = "release.handoff_prepared";
    public const string ProductionSmoke = "release.production_smoke";
    public const string NextWaveEligibility = "wave.next_eligibility";
    public const string LockContended = "lock.contended";
    public const string LockAcquired = "lock.acquired";
    public const string WatchdogReconciled = "watchdog.reconciled";
    public const string ControlChanged = "control.changed";
    public const string SafetyStop = "safety.stop";
    public const string ControllerDisabled = "controller.disabled";
}

public static class AutopilotAuditOutcomes
{
    public const string Recorded = "recorded";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Blocked = "blocked";
    public const string Contended = "contended";
    public const string Required = "required";
    public const string Simulated = "simulated";
}
