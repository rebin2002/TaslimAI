using System.ComponentModel.DataAnnotations;

namespace Taslim.Api.Autopilot;

public sealed class LaunchAutopilotWaveRequest
{
    [Range(1, int.MaxValue)]
    public int WaveNumber { get; set; }
    [Required, StringLength(40, MinimumLength = 40)]
    public string ProductionBaseSha { get; set; } = string.Empty;
    [Required, StringLength(160)]
    public string IntegrationBranch { get; set; } = string.Empty;
    [Range(1, 20)]
    public int ConcurrencyCeiling { get; set; } = 20;
    [Range(1, 8)]
    public int MaxTaskAttempts { get; set; } = 3;
    [Range(1, 4)]
    public int MaxGateAttempts { get; set; } = 2;
    [StringLength(100)]
    public string? IdempotencyKey { get; set; }
    [Required, MinLength(1), MaxLength(20)]
    public List<LaunchAutopilotTaskRequest> Tasks { get; set; } = [];
}

public sealed class LaunchAutopilotTaskRequest
{
    [Required, StringLength(100)]
    public string TaskKey { get; set; } = string.Empty;
    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;
    [Required, StringLength(160)]
    public string BranchName { get; set; } = string.Empty;
    public bool Mandatory { get; set; } = true;
    public bool RequiresDecision { get; set; }
    [StringLength(40)]
    public string? DecisionReason { get; set; }
}

public sealed record AutopilotEventRequest
{
    [Required, StringLength(160)]
    public string EventId { get; set; } = string.Empty;
    [Required, StringLength(40)]
    public string EventType { get; set; } = string.Empty;
    [Required, StringLength(100)]
    public string TaskKey { get; set; } = string.Empty;
    [Required, StringLength(160)]
    public string BranchName { get; set; } = string.Empty;
    [Required, StringLength(40, MinimumLength = 40)]
    public string BaseSha { get; set; } = string.Empty;
    [StringLength(40, MinimumLength = 40)]
    public string? HeadSha { get; set; }
    public bool? Succeeded { get; set; }
    [StringLength(40)]
    public string? FailureKind { get; set; }
    [StringLength(100)]
    public string? FailureCode { get; set; }
    [StringLength(100_000)]
    public string? Payload { get; set; }
}

public sealed class AutopilotGateResultRequest
{
    [Required, StringLength(40)]
    public string GateType { get; set; } = string.Empty;
    public bool Passed { get; set; }
    [Required, StringLength(40, MinimumLength = 40)]
    public string CandidateSha { get; set; } = string.Empty;
    [StringLength(40)]
    public string? FailureCode { get; set; }
}

public sealed class AutopilotDeploymentRequest
{
    [Required, StringLength(40, MinimumLength = 40)]
    public string CandidateSha { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    [StringLength(100)]
    public string? FailureCode { get; set; }
}

public sealed class AutopilotSmokeRequest
{
    [Required, StringLength(40, MinimumLength = 40)]
    public string DeployedRevision { get; set; } = string.Empty;
    public bool Passed { get; set; }
    [StringLength(100)]
    public string? FailureCode { get; set; }
}

public sealed class AutopilotDecisionRequest
{
    [Required, StringLength(40)]
    public string DecisionReason { get; set; } = string.Empty;
    public bool Approved { get; set; }
    [Required, StringLength(500)]
    public string Note { get; set; } = string.Empty;
}

public sealed record AutopilotTransitionResult(
    Guid WaveId,
    string Status,
    string Outcome,
    string? Reason = null);

public sealed record AutopilotEventResult(
    Guid WaveId,
    Guid? TaskId,
    string Outcome,
    string TaskStatus,
    string WaveStatus,
    bool Duplicate,
    string? Reason = null);

public sealed record AutopilotTaskSummaryDto(
    Guid Id,
    string TaskKey,
    string Title,
    string BranchName,
    string BaseSha,
    string? HeadSha,
    string Status,
    int Attempt,
    int MaxAttempts,
    bool Mandatory,
    string? LastFailureKind,
    string? LastFailureCode,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record AutopilotGateSummaryDto(
    string GateType,
    string Status,
    int Attempt,
    int MaxAttempts,
    string? CandidateSha,
    string? FailureCode,
    DateTime? EvaluatedAt);

public sealed record AutopilotAuditDto(
    Guid Id,
    string Action,
    string Outcome,
    string Details,
    Guid? TaskId,
    Guid? RunId,
    string? EventId,
    DateTime CreatedAt);

public sealed record AutopilotWaveSummaryDto(
    Guid Id,
    int WaveNumber,
    string Status,
    string ProductionBaseSha,
    string IntegrationBranch,
    string? IntegrationCandidateSha,
    string? DeployedRevision,
    int ConcurrencyCeiling,
    int ActiveTaskCount,
    int MandatoryTaskCount,
    int SucceededTaskCount,
    int FailedTaskCount,
    int PendingTaskCount,
    int ProcessedEventCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    string? DecisionReason,
    string? DecisionNote,
    string? LastErrorCode,
    IReadOnlyList<AutopilotTaskSummaryDto> Tasks,
    IReadOnlyList<AutopilotGateSummaryDto> Gates,
    IReadOnlyList<AutopilotAuditDto> RecentAudit);

public sealed record AutopilotWatchdogOptions
{
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 1;
    public int RunLeaseMinutes { get; set; } = 5;
    public int TaskLeaseMinutes { get; set; } = 30;
}
