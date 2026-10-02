namespace Taslim.Api.Autopilot;

public static class AutopilotWaveStatuses
{
    public const string Running = "RUNNING";
    public const string NeedsDecision = "NEEDS_DECISION";
    public const string Paused = "PAUSED";
    public const string IntegrationGate = "INTEGRATION_GATE";
    public const string FinalReleaseGate = "FINAL_RELEASE_GATE";
    public const string Deploying = "DEPLOYING";
    public const string Smoke = "SMOKE";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

public static class AutopilotTaskStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string RetryPending = "RETRY_PENDING";
    public const string Failed = "FAILED";
    public const string Blocked = "BLOCKED";
}

public static class AutopilotRunStatuses
{
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Expired = "EXPIRED";
}

public static class AutopilotRunKinds
{
    public const string Task = "TASK";
    public const string Integration = "INTEGRATION";
    public const string FinalRelease = "FINAL_RELEASE";
    public const string Deploy = "DEPLOY";
    public const string Smoke = "SMOKE";
}

public static class AutopilotGateTypes
{
    public const string Integration = "INTEGRATION";
    public const string FinalRelease = "FINAL_RELEASE";
}

public static class AutopilotGateStatuses
{
    public const string Pending = "PENDING";
    public const string RetryPending = "RETRY_PENDING";
    public const string Passed = "PASSED";
    public const string Failed = "FAILED";
}

public static class AutopilotEventTypes
{
    public const string TaskStarted = "TASK_STARTED";
    public const string TaskCompleted = "TASK_COMPLETED";
}

public static class AutopilotDecisionReasons
{
    public const string ProductDirection = "PRODUCT_DIRECTION";
    public const string Pricing = "PRICING";
    public const string DestructiveSchema = "DESTRUCTIVE_SCHEMA";
    public const string SecuritySensitive = "SECURITY_SENSITIVE";
    public const string AmbiguousBusiness = "AMBIGUOUS_BUSINESS";
    public const string ProviderOrCharging = "PROVIDER_OR_CHARGING";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ProductDirection,
        Pricing,
        DestructiveSchema,
        SecuritySensitive,
        AmbiguousBusiness,
        ProviderOrCharging,
    };
}

public static class AutopilotFailureKinds
{
    public const string Ordinary = "ORDINARY";
    public const string Ci = "CI";
    public const string Integration = "INTEGRATION";
    public const string Decision = "DECISION";
    public const string ProductDirection = "PRODUCT_DIRECTION";
    public const string Pricing = "PRICING";
    public const string DestructiveSchema = "DESTRUCTIVE_SCHEMA";
    public const string SecuritySensitive = "SECURITY_SENSITIVE";
    public const string AmbiguousBusiness = "AMBIGUOUS_BUSINESS";
    public const string ProviderOrCharging = "PROVIDER_OR_CHARGING";

    public static bool RequiresDecision(string? kind) => kind is not null && kind is
        Decision or ProductDirection or Pricing or DestructiveSchema or SecuritySensitive or AmbiguousBusiness or ProviderOrCharging;
}

public sealed class AutopilotWave
{
    public Guid Id { get; set; }
    public int WaveNumber { get; set; }
    public string Status { get; set; } = AutopilotWaveStatuses.Running;
    public string ProductionBaseSha { get; set; } = string.Empty;
    public string IntegrationBranch { get; set; } = string.Empty;
    public string? IntegrationCandidateSha { get; set; }
    public string? DeployedRevision { get; set; }
    public int ConcurrencyCeiling { get; set; } = 20;
    public int MaxTaskAttempts { get; set; } = 3;
    public int MaxGateAttempts { get; set; } = 2;
    public string? DecisionReason { get; set; }
    public string? DecisionNote { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LaunchIdempotencyKey { get; set; }
    public string? LaunchFingerprint { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ICollection<AutopilotTask> Tasks { get; set; } = [];
    public ICollection<AutopilotRun> Runs { get; set; } = [];
    public ICollection<AutopilotEvent> Events { get; set; } = [];
    public ICollection<AutopilotGate> Gates { get; set; } = [];
    public ICollection<AutopilotAuditEntry> AuditEntries { get; set; } = [];
}

public sealed class AutopilotTask
{
    public Guid Id { get; set; }
    public Guid WaveId { get; set; }
    public string TaskKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string BaseSha { get; set; } = string.Empty;
    public string? HeadSha { get; set; }
    public string Status { get; set; } = AutopilotTaskStatuses.Pending;
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public bool Mandatory { get; set; } = true;
    public bool RequiresDecision { get; set; }
    public string? DecisionReason { get; set; }
    public string? LastFailureKind { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public AutopilotWave Wave { get; set; } = null!;
    public ICollection<AutopilotRun> Runs { get; set; } = [];
    public ICollection<AutopilotEvent> Events { get; set; } = [];
}

public sealed class AutopilotRun
{
    public Guid Id { get; set; }
    public Guid WaveId { get; set; }
    public Guid? TaskId { get; set; }
    public string Kind { get; set; } = AutopilotRunKinds.Task;
    public string Status { get; set; } = AutopilotRunStatuses.Running;
    public int Attempt { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string BaseSha { get; set; } = string.Empty;
    public string? HeadSha { get; set; }
    public string? FailureCode { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? CompletedAt { get; set; }
    public AutopilotWave Wave { get; set; } = null!;
    public AutopilotTask? Task { get; set; }
}

public sealed class AutopilotEvent
{
    public Guid Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public Guid WaveId { get; set; }
    public Guid? TaskId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string BaseSha { get; set; } = string.Empty;
    public string? HeadSha { get; set; }
    public bool? Succeeded { get; set; }
    public string? FailureKind { get; set; }
    public string? FailureCode { get; set; }
    public string? PayloadHash { get; set; }
    public bool Processed { get; set; }
    public string? IgnoredReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ProcessedAt { get; set; }
    public AutopilotWave Wave { get; set; } = null!;
    public AutopilotTask? Task { get; set; }
}

public sealed class AutopilotGate
{
    public Guid Id { get; set; }
    public Guid WaveId { get; set; }
    public string GateType { get; set; } = string.Empty;
    public string Status { get; set; } = AutopilotGateStatuses.Pending;
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 2;
    public string? CandidateSha { get; set; }
    public string? Checksum { get; set; }
    public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EvaluatedAt { get; set; }
    public AutopilotWave Wave { get; set; } = null!;
}

public sealed class AutopilotLock
{
    public Guid Id { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public DateTime AcquiredAt { get; set; }
    public DateTime LeaseUntil { get; set; }
}

public sealed class AutopilotAuditEntry
{
    public Guid Id { get; set; }
    public Guid WaveId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? RunId { get; set; }
    public string? EventId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public AutopilotWave Wave { get; set; } = null!;
}
