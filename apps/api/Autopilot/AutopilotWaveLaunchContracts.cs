namespace Taslim.Api.Autopilot;

/// <summary>
/// One planned task inside a follow-on development wave. The plan is built only
/// from pre-approved backlog items and carries bounded, non-sensitive metadata:
/// a stable task key, a short human title, the backlog item it came from, and
/// the immutable base ref the work is built on.
/// </summary>
public sealed record WaveLaunchTaskPlan(
    string TaskKey,
    string Title,
    string BacklogItemKey,
    string Branch,
    string BaseSha);

/// <summary>Provider-neutral plan for one follow-on wave.</summary>
public sealed record WaveLaunchPlan(
    string WaveKey,
    string SourceWaveKey,
    Guid SourceRunId,
    IReadOnlyList<WaveLaunchTaskPlan> Tasks,
    string PlanFingerprint);

/// <summary>
/// Provider-neutral launch request. <see cref="Instructions"/> is the bounded,
/// human-approved task description derived from the approved backlog item; it is
/// never persisted by the controller.
/// </summary>
public sealed record WaveLaunchProviderRequest(
    string WaveKey,
    string TaskKey,
    string Title,
    string Instructions,
    string BaseSha,
    string Branch,
    string IdempotencyKey);

public static class WaveLaunchProviderOutcomes
{
    /// <summary>The provider accepted the task and returned an external task identity.</summary>
    public const string Launched = "launched";

    /// <summary>Transient failure (network, timeout, 5xx, unconfigured). Safe to retry within bounds.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Deterministic refusal (4xx / contract mismatch). Never retried automatically.</summary>
    public const string Rejected = "rejected";
}

public sealed record WaveLaunchProviderResult(string Outcome, string? ExternalTaskId, string? ReasonCode, string? StatusDetail)
{
    public static WaveLaunchProviderResult Launched(string externalTaskId, string? detail = null) =>
        new(WaveLaunchProviderOutcomes.Launched, externalTaskId, null, detail);

    public static WaveLaunchProviderResult Unavailable(string reasonCode, string? detail = null) =>
        new(WaveLaunchProviderOutcomes.Unavailable, null, reasonCode, detail);

    public static WaveLaunchProviderResult Rejected(string reasonCode, string? detail = null) =>
        new(WaveLaunchProviderOutcomes.Rejected, null, reasonCode, detail);

    public bool IsLaunched =>
        string.Equals(Outcome, WaveLaunchProviderOutcomes.Launched, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ExternalTaskId);
}

/// <summary>Normalized, provider-neutral execution state of a launched task.</summary>
public static class WaveLaunchTaskStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Blocked = "blocked";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Unknown = "unknown";

    public static readonly IReadOnlySet<string> SucceededStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Succeeded };

    public static readonly IReadOnlySet<string> FailedStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Failed, Cancelled };

    public static readonly IReadOnlySet<string> TerminalStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Succeeded, Failed, Cancelled };
}

/// <summary>
/// Bounded, provider-neutral snapshot of a launched task, used by the polling
/// fallback when a signed completion event never arrives.
/// </summary>
public sealed record WaveLaunchTaskState(
    string Status,
    bool IsTerminal,
    bool IsSuccess,
    string? ExternalRef,
    string? StatusDetail);

/// <summary>
/// Provider-neutral wave-launch interface. The Autopilot Controller depends only
/// on this abstraction, so a different execution provider can be substituted
/// without touching the controller, the planner, or the persistence model.
/// Implementations must never persist, log, or return credentials.
/// </summary>
public interface IWaveLaunchProvider
{
    /// <summary>Opaque provider key used in audit entries. Never a model or prompt name.</summary>
    string ProviderKey { get; }

    /// <summary>True when the provider has the configuration and server-side credential it needs.</summary>
    bool IsConfigured { get; }

    /// <summary>Creates one external task for a planned wave task.</summary>
    Task<WaveLaunchProviderResult> LaunchTaskAsync(WaveLaunchProviderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the normalized state of a launched task. Returns null when the
    /// provider cannot resolve the task (treated as "unknown", never as success).
    /// </summary>
    Task<WaveLaunchTaskState?> GetTaskStateAsync(string externalTaskId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fallback provider used when no wave-launch provider is configured. It never
/// performs an external call and always reports "unavailable", so an
/// unconfigured deployment can never launch work by accident.
/// </summary>
public sealed class NullWaveLaunchProvider : IWaveLaunchProvider
{
    public const string Key = "unconfigured";

    public string ProviderKey => Key;

    public bool IsConfigured => false;

    public Task<WaveLaunchProviderResult> LaunchTaskAsync(WaveLaunchProviderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(WaveLaunchProviderResult.Unavailable("provider_not_configured", "No wave-launch provider is configured."));

    public Task<WaveLaunchTaskState?> GetTaskStateAsync(string externalTaskId, CancellationToken cancellationToken = default) =>
        Task.FromResult<WaveLaunchTaskState?>(null);
}
