namespace Taslim.Api.Operations;

/// <summary>
/// Durable, provider-neutral liveness record for a generation worker slot.
/// This is internal telemetry and is never exposed through customer contracts.
/// </summary>
public sealed class GenerationWorkerHeartbeat
{
    public Guid Id { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public int WorkerIndex { get; set; }
    public int WorkerConcurrency { get; set; }
    public string Status { get; set; } = GenerationWorkerStatuses.Running;
    public DateTime StartedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LastClaimedAt { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public Guid? ActiveJobId { get; set; }
    public int ConsecutiveIterationFailures { get; set; }
}

public static class GenerationWorkerStatuses
{
    public const string Running = "running";
    public const string Stopping = "stopping";
}

/// <summary>
/// Append-only audit evidence for protected administrator operational actions.
/// State fields are deliberately bounded summaries; request input, prompts,
/// provider payloads, credentials, and storage keys must never be written here.
/// </summary>
public sealed class AdminOperationAuditEvent
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? RequestId { get; set; }
    public string? BeforeState { get; set; }
    public string? AfterState { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class AdminOperationActions
{
    public const string RecoverExpiredGenerationJob = "recover_expired_generation_job";
}

public static class AdminOperationOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Conflict = "conflict";
}
