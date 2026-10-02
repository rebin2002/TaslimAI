namespace Taslim.Api.Autopilot;

/// <summary>
/// Durable record of one follow-on wave launch attempt batch. A batch is keyed
/// to the source run and the deterministic follow-on wave key, so a restarted
/// process, a duplicate completion event, or a replayed reconciliation can never
/// create a second batch for the same source wave.
/// </summary>
public sealed class AutopilotWaveLaunchBatch
{
    public Guid Id { get; set; }

    /// <summary>Run that produced the follow-on wave. Never repointed.</summary>
    public Guid SourceRunId { get; set; }

    /// <summary>Wave key of the completed source wave.</summary>
    public string SourceWaveKey { get; set; } = string.Empty;

    /// <summary>Deterministic follow-on wave key, derived from the source wave key.</summary>
    public string WaveKey { get; set; } = string.Empty;

    public string Status { get; set; } = AutopilotWaveLaunchStatuses.Planned;

    /// <summary>Bounded reason code for the current status. Provider-neutral.</summary>
    public string? ReasonCode { get; set; }
    public string? LastStatusDetail { get; set; }

    /// <summary>Human decision required before the batch may proceed, when blocked.</summary>
    public string? HumanDecisionRequired { get; set; }

    /// <summary>Immutable ref the follow-on wave builds on. Never rewritten.</summary>
    public string BaseSha { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    /// <summary>SHA-256 of the bounded plan metadata; a changed plan for the same wave is refused.</summary>
    public string PlanFingerprint { get; set; } = string.Empty;

    public int TaskCount { get; set; }
    public int LaunchedTaskCount { get; set; }

    public bool DryRun { get; set; }

    public int LaunchAttempt { get; set; }
    public int MaxLaunchAttempts { get; set; }

    public int ReconcileCount { get; set; }
    public int MaxReconciles { get; set; }
    public DateTime? NextReconcileAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LaunchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}

/// <summary>
/// Durable record of one launched task inside a follow-on wave. The external
/// task identity, the launch attempt counter, the immutable base ref, and the
/// bounded plan metadata are all persisted so a restart can recover and
/// reconcile the launch without re-creating work.
/// </summary>
public sealed class AutopilotWaveLaunchTask
{
    public Guid Id { get; set; }

    public Guid BatchId { get; set; }
    public Guid SourceRunId { get; set; }

    public string SourceWaveKey { get; set; } = string.Empty;
    public string WaveKey { get; set; } = string.Empty;

    /// <summary>Stable plan task key (also the completion-signal task id for the follow-on wave).</summary>
    public string TaskKey { get; set; } = string.Empty;

    /// <summary>Bounded, human-approved title from the backlog item. Never a prompt.</summary>
    public string Title { get; set; } = string.Empty;

    public string BacklogItemKey { get; set; } = string.Empty;

    /// <summary>Identifier assigned by the execution provider. Unique when present.</summary>
    public string? ExternalTaskId { get; set; }

    /// <summary>Bounded, immutable external reference reported by the provider.</summary>
    public string? ExternalRef { get; set; }

    public string Status { get; set; } = AutopilotWaveLaunchStatuses.Planned;

    public string? ReasonCode { get; set; }
    public string? LastStatusDetail { get; set; }

    public string? Branch { get; set; }
    public string? BaseSha { get; set; }

    public bool DryRun { get; set; }

    public int LaunchAttempt { get; set; }
    public int MaxLaunchAttempts { get; set; }

    public int ReconcileCount { get; set; }
    public int MaxReconciles { get; set; }
    public DateTime? NextReconcileAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LaunchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}

public static class AutopilotWaveLaunchStatuses
{
    /// <summary>Planned, not yet launched. Retryable while attempts remain.</summary>
    public const string Planned = "planned";

    /// <summary>An attempt is in flight. A claim older than the lock lease is recovered.</summary>
    public const string Launching = "launching";

    /// <summary>Simulated in dry-run. Upgradable to a real launch when dry-run is turned off.</summary>
    public const string Simulated = "simulated";

    /// <summary>Accepted by the provider and awaiting a signed completion event or bounded polling.</summary>
    public const string Launched = "launched";

    /// <summary>Stopped: a human decision (including a missing backlog) is required.</summary>
    public const string Blocked = "blocked";

    /// <summary>Launch attempts exhausted or a deterministic provider refusal.</summary>
    public const string Failed = "failed";

    /// <summary>Reconciled: the launched task reached a terminal state.</summary>
    public const string Completed = "completed";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Blocked, Failed, Completed,
    };

    /// <summary>States in which the batch may still be launched or polled.</summary>
    public static readonly IReadOnlySet<string> Active = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Planned, Launching, Simulated, Launched,
    };
}

/// <summary>
/// A pre-approved unit of work. Only items that a human has explicitly approved
/// and that are classified as ordinary development work may ever be selected for
/// an autonomous follow-on wave.
/// </summary>
public sealed class AutopilotBacklogItem
{
    public Guid Id { get; set; }

    /// <summary>Stable, human-supplied key. Unique.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Bounded human title. Safe to expose.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Bounded acceptance summary. Never contains credentials or raw payloads.</summary>
    public string? AcceptanceSummary { get; set; }

    /// <summary>Development or non_development. Only development items may be auto-selected.</summary>
    public string Kind { get; set; } = AutopilotBacklogItemKinds.Development;

    /// <summary>Approval is a human decision; an unapproved item is never selected.</summary>
    public bool Approved { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNote { get; set; }

    /// <summary>Lower values are planned first.</summary>
    public int Priority { get; set; }

    /// <summary>Follow-on wave that consumed the item. A consumed item is never re-selected.</summary>
    public string? ConsumedByWaveKey { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class AutopilotBacklogItemKinds
{
    /// <summary>Ordinary development work: the only kind that may be launched autonomously.</summary>
    public const string Development = "development";

    /// <summary>Pricing, product-direction, security, schema, or spending work: always human.</summary>
    public const string NonDevelopment = "non_development";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Development, NonDevelopment,
    };

    public static string Normalize(string? value) =>
        Supported.Contains(value ?? string.Empty) ? value!.Trim().ToLowerInvariant() : NonDevelopment;
}

// ---------------------------------------------------------------------------
// Product-safe read contracts. No provider, model, or prompt names.
// ---------------------------------------------------------------------------

public sealed record AutopilotWaveLaunchTaskDto(
    Guid Id,
    string TaskKey,
    string Title,
    string BacklogItemKey,
    string Status,
    string? ReasonCode,
    string? ExternalTaskId,
    int LaunchAttempt,
    int MaxLaunchAttempts,
    int ReconcileCount,
    DateTime? NextReconcileAt,
    DateTime UpdatedAt);

public sealed record AutopilotWaveLaunchBatchDto(
    Guid Id,
    string SourceWaveKey,
    string WaveKey,
    string Status,
    string? ReasonCode,
    string? HumanDecisionRequired,
    string BaseSha,
    string Branch,
    bool DryRun,
    int TaskCount,
    int LaunchedTaskCount,
    int LaunchAttempt,
    int MaxLaunchAttempts,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LaunchedAt,
    IReadOnlyList<AutopilotWaveLaunchTaskDto> Tasks);

public sealed record AutopilotBacklogItemDto(
    Guid Id,
    string ItemKey,
    string Title,
    string Kind,
    bool Approved,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    int Priority,
    string? ConsumedByWaveKey,
    DateTime? ConsumedAt,
    DateTime UpdatedAt);
