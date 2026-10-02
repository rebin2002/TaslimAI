using System.Text.Json.Serialization;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Provider-neutral completion signal accepted by the Taslim completion bridge.
/// The contract carries only orchestration facts: wave identity, task identity,
/// outcome classification, check results, and immutable SHAs. Provider names,
/// model names, prompts, and credentials are explicitly out of contract.
/// </summary>
public sealed class AutopilotCompletionSignal
{
    [JsonPropertyName("waveKey")]
    public string? WaveKey { get; set; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("failureClass")]
    public string? FailureClass { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("baseSha")]
    public string? BaseSha { get; set; }

    [JsonPropertyName("candidateSha")]
    public string? CandidateSha { get; set; }

    [JsonPropertyName("attempt")]
    public int? Attempt { get; set; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; set; }

    [JsonPropertyName("checks")]
    public Dictionary<string, bool>? Checks { get; set; }

    [JsonPropertyName("requiresHumanDecision")]
    public bool? RequiresHumanDecision { get; set; }

    [JsonPropertyName("humanDecisionKind")]
    public string? HumanDecisionKind { get; set; }

    /// <summary>Declared number of tasks in the wave. Enables partial-wave detection.</summary>
    [JsonPropertyName("expectedTaskCount")]
    public int? ExpectedTaskCount { get; set; }

    /// <summary>Explicit terminal notification that the wave has no further tasks to report.</summary>
    [JsonPropertyName("waveComplete")]
    public bool? WaveComplete { get; set; }
}

public static class AutopilotTaskOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Succeeded, Failed, Blocked, Cancelled,
    };
}

/// <summary>
/// Failure classification drives the automatic/human boundary. Only
/// <see cref="Ordinary"/> failures may be repaired automatically; every other
/// class stops the wave and requires a human decision.
/// </summary>
public static class AutopilotFailureClasses
{
    public const string None = "none";

    /// <summary>Deterministic CI/test/integration repair. Safe to retry automatically.</summary>
    public const string Ordinary = "ordinary";

    public const string Infrastructure = "infrastructure";
    public const string Migration = "migration";
    public const string BrowserE2E = "browser_e2e";
    public const string Security = "security";
    public const string Accounting = "accounting";
    public const string Integration = "integration";
    public const string Pricing = "pricing";
    public const string SchemaIrreversible = "schema_irreversible";
    public const string ProductDirection = "product_direction";
    public const string Unknown = "unknown";

    /// <summary>Classes that must never be auto-repaired and always stop a wave.</summary>
    public static readonly IReadOnlySet<string> Blocking = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Migration, BrowserE2E, Security, Accounting, Integration, Pricing, SchemaIrreversible, ProductDirection, Unknown,
    };

    public static readonly IReadOnlySet<string> Automatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Ordinary, Infrastructure,
    };

    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? None : value.Trim().ToLowerInvariant();
}

/// <summary>Provider-neutral check names that feed gate evaluation.</summary>
public static class AutopilotChecks
{
    public const string Build = "build";
    public const string UnitTests = "unit_tests";
    public const string Typecheck = "typecheck";
    public const string Lint = "lint";
    public const string BrowserE2E = "browser_e2e";
    public const string Migrations = "migrations";
    public const string Security = "security";
    public const string Accounting = "accounting";
    public const string IntegrationComplete = "integration_complete";

    /// <summary>Required for an integration gate to pass.</summary>
    public static readonly IReadOnlyList<string> IntegrationRequired =
        [Build, UnitTests, Typecheck, IntegrationComplete];

    /// <summary>Required for a release gate to pass, in addition to a passed integration gate.</summary>
    public static readonly IReadOnlyList<string> ReleaseRequired =
        [Security, Accounting];
}

/// <summary>Decision categories that always require a human decision.</summary>
public static class AutopilotHumanDecisions
{
    public const string Pricing = "pricing";
    public const string DestructiveSchema = "destructive_schema";
    public const string SecurityAmbiguity = "security_ambiguity";
    public const string ProductDirection = "product_direction";
    public const string EnableCharging = "enable_charging";
    public const string EnablePaidProviders = "enable_paid_providers";
    public const string ProductionRelease = "production_release";
    public const string IntegrationMerge = "integration_merge";
    public const string RepairEscalation = "repair_escalation";

    public static readonly IReadOnlyList<string> All =
    [
        Pricing, DestructiveSchema, SecurityAmbiguity, ProductDirection,
        EnableCharging, EnablePaidProviders, ProductionRelease, IntegrationMerge, RepairEscalation,
    ];
}

/// <summary>Destructive operations the controller can never perform, at any setting.</summary>
public static class AutopilotForbiddenActions
{
    public const string ForcePush = "force_push";
    public const string ResetProductionDatabase = "reset_production_database";
    public const string DropProductionData = "drop_production_data";
    public const string DisableChargingGuard = "disable_charging_guard";

    public static readonly IReadOnlyList<string> All =
    [
        ForcePush, ResetProductionDatabase, DropProductionData, DisableChargingGuard,
    ];
}

// ---------------------------------------------------------------------------
// Public (product-safe) API contracts. No provider, model, or prompt names.
// ---------------------------------------------------------------------------

public sealed record AutopilotControlDto(
    bool FeatureEnabled,
    bool DryRun,
    bool Paused,
    bool KillSwitchEngaged,
    bool ChargingEnabled,
    bool PaidProvidersEnabled,
    int MaxConcurrency,
    int WatchdogIntervalMinutes,
    string? LastReason,
    DateTime? UpdatedAt);

public sealed record AutopilotRunDto(
    Guid Id,
    string WaveKey,
    string State,
    string Branch,
    string BaseSha,
    string CandidateSha,
    int TaskCount,
    int SucceededTaskCount,
    int FailedTaskCount,
    int RetryScheduledCount,
    bool IntegrationGatePassed,
    bool ReleaseGatePassed,
    string? HumanDecisionRequired,
    string? LastStatusDetail,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AutopilotTaskDto(
    Guid Id,
    string TaskId,
    string State,
    string? FailureClass,
    int Attempt,
    int MaxAttempts,
    bool RequiresHumanDecision,
    string? HumanDecisionKind,
    DateTime? NextAttemptAt,
    string? LastReason,
    DateTime UpdatedAt);

public sealed record AutopilotEventDto(
    Guid Id,
    string EventType,
    string Status,
    string? WaveKey,
    string? TaskId,
    string? Branch,
    string? BaseSha,
    string? CandidateSha,
    int Attempt,
    bool SignatureVerified,
    bool ReplayProtected,
    string? Reason,
    DateTime ReceivedAt,
    DateTime? ProcessedAt);

public sealed record AutopilotAuditDto(
    Guid Id,
    string Action,
    string Outcome,
    string? Reason,
    string? WaveKey,
    string? TaskId,
    string? TaskState,
    string? RunState,
    int Attempt,
    string? Branch,
    string? BaseSha,
    string? CandidateSha,
    bool DryRun,
    DateTime CreatedAt);

public sealed record AutopilotGateDto(
    string GateKind,
    string Outcome,
    string CandidateSha,
    string? ReasonCode,
    string? Reason,
    IReadOnlyList<string> FailedChecks,
    DateTime CreatedAt);

public sealed record AutopilotHandoffDto(
    string Status,
    string Branch,
    string CandidateSha,
    bool DryRun,
    string? SmokeStatus,
    string? LastStatusDetail,
    DateTime UpdatedAt);

public sealed record AutopilotOverviewDto(
    AutopilotControlDto Control,
    AutopilotRunDto? Run,
    IReadOnlyList<AutopilotTaskDto> Tasks,
    IReadOnlyList<AutopilotGateDto> Gates,
    AutopilotHandoffDto? Handoff,
    IReadOnlyList<AutopilotAuditDto> RecentAudit);

/// <summary>Result of one controller cycle; safe to expose and to log.</summary>
public sealed record AutopilotCycleResult(
    bool Enabled,
    bool DryRun,
    string Outcome,
    string? Reason,
    int EventsExamined,
    int EventsProcessed,
    int EventsSkipped,
    IReadOnlyList<string> PlannedActions,
    IReadOnlyList<Guid> AuditedEventIds);
