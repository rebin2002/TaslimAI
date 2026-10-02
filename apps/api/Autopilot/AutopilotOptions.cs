namespace Taslim.Api.Autopilot;

/// <summary>
/// Configuration for the event-driven Autopilot Controller foundation.
/// Activation is deliberately conservative: the controller is disabled and in
/// dry-run/simulation mode by default, and every capability that could spend
/// money, touch production data, or publish a release requires an explicit
/// operator decision before it can be turned on.
/// </summary>
public sealed class AutopilotOptions
{
    /// <summary>Master feature flag. Defaults to false; nothing is orchestrated until an operator enables it.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Simulation mode. When true the controller evaluates and records every decision but performs no external action.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Explicit alias for dry-run used by operator tooling. Kept in sync with <see cref="DryRun"/>.</summary>
    public bool SimulationMode { get; set; } = true;

    /// <summary>Maximum number of wave tasks the controller will consider in flight at once.</summary>
    public int MaxConcurrency { get; set; } = 20;

    /// <summary>Bounded automatic retry attempts for an ordinary (deterministic, non-blocking) task failure.</summary>
    public int MaxTaskAttempts { get; set; } = 3;

    /// <summary>Base backoff applied between ordinary retries.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 30;

    /// <summary>Upper bound for retry backoff. Backoff never grows without limit.</summary>
    public int RetryMaxDelaySeconds { get; set; } = 900;

    /// <summary>Lease length for the exclusive wave/run/release lock.</summary>
    public int LockLeaseMinutes { get; set; } = 15;

    /// <summary>Hourly watchdog cadence used to reconcile missed or lost completion events.</summary>
    public int WatchdogIntervalMinutes { get; set; } = 60;

    /// <summary>An event that is still unprocessed after this window is considered missed and is reconciled by the watchdog.</summary>
    public int EventStaleAfterMinutes { get; set; } = 30;

    /// <summary>Requires every intake event to carry a valid signature. Must not be disabled in production.</summary>
    public bool RequireSignedEvents { get; set; } = true;

    /// <summary>Maximum accepted clock skew for a signed event before it is treated as a replay.</summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    /// <summary>Bounded size of an accepted intake payload.</summary>
    public int MaxEventPayloadCharacters { get; set; } = 20000;

    /// <summary>Server-side only: name of the environment variable holding the webhook signing secret.</summary>
    public string SigningSecretEnvironmentVariable { get; set; } = "AUTOPILOT_WEBHOOK_SECRET";

    /// <summary>Merge gating. False by default: merging the wave candidate into main requires a human decision.</summary>
    public bool AllowAutomaticIntegrationMerge { get; set; } = false;

    /// <summary>Release gating. False by default: handing off to production requires a human decision.</summary>
    public bool AllowAutomaticRelease { get; set; } = false;

    /// <summary>Charging must remain OFF. Enabling it is always a human decision.</summary>
    public bool ChargingEnabled { get; set; } = false;

    /// <summary>Paid/external generation providers must remain OFF. Enabling them is always a human decision.</summary>
    public bool PaidProvidersEnabled { get; set; } = false;

    /// <summary>Requires browser E2E evidence before an integration gate can pass.</summary>
    public bool RequireBrowserE2E { get; set; } = true;

    /// <summary>Requires migration evidence before an integration gate can pass.</summary>
    public bool RequireMigrations { get; set; } = true;

    /// <summary>Terminal bound on the number of watchdog reconciliations for a single run.</summary>
    public int MaxWatchdogReconciliations { get; set; } = 24;

    // -----------------------------------------------------------------------
    // Follow-on ("next wave") development launch. Every live capability below is
    // off by default: the controller may plan a follow-on wave only from a
    // pre-approved backlog, and only when an operator has explicitly enabled it.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Enables launching a follow-on development wave once a wave is gate-green.
    /// Defaults to false: the controller records eligibility only.
    /// </summary>
    public bool AllowNextWaveLaunch { get; set; } = false;

    /// <summary>
    /// Allows the follow-on development wave to start while the human
    /// production-release decision for the source wave is still pending. When
    /// false, a wave parked at <c>handoff_pending_human</c> does not start the
    /// next development batch. Defaults to false.
    /// </summary>
    public bool AllowNextWaveLaunchWhenReleasePending { get; set; } = false;

    /// <summary>Hard bound on the number of tasks in one follow-on wave. Never more than 20.</summary>
    public int MaxTasksPerWave { get; set; } = 20;

    /// <summary>Hard bound on launch attempts for one planned task. Never more than 3.</summary>
    public int MaxLaunchAttempts { get; set; } = 3;

    /// <summary>Bound on launch batches examined per cycle.</summary>
    public int MaxLaunchBatchesPerCycle { get; set; } = 20;

    /// <summary>Bound on launched tasks polled per reconciliation cycle.</summary>
    public int MaxLaunchReconciliations { get; set; } = 20;

    /// <summary>Terminal bound on polling reconciliations for one launched task.</summary>
    public int MaxLaunchReconciles { get; set; } = 24;

    /// <summary>
    /// Minutes to wait before the polling fallback considers a signed completion
    /// event missing. Signed events always remain the preferred path.
    /// </summary>
    public int PollingFallbackAfterMinutes { get; set; } = 30;

    /// <summary>Base backoff between polling reconciliations of a launched task.</summary>
    public int LaunchReconcileBackoffSeconds { get; set; } = 300;

    /// <summary>Server-side bridge base URL (HTTPS). Empty means no launch provider is configured.</summary>
    public string BridgeBaseUrl { get; set; } = string.Empty;

    /// <summary>Server-side only: name of the environment variable holding the bridge bearer token.</summary>
    public string BridgeTokenEnvironmentVariable { get; set; } = "TASLIM_BRIDGE_TOKEN";

    /// <summary>Bridge task-creation path. Defaults to the existing bridge contract.</summary>
    public string BridgeCreateTaskPath { get; set; } = "/v1/tasks";

    /// <summary>Bridge task-read path template. <c>{taskId}</c> is substituted with the escaped task id.</summary>
    public string BridgeGetTaskPathTemplate { get; set; } = "/v1/tasks/{taskId}";

    /// <summary>Bounded timeout for a single bridge call.</summary>
    public int BridgeTimeoutSeconds { get; set; } = 30;

    public void Normalize()
    {
        MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 20);
        MaxTaskAttempts = Math.Clamp(MaxTaskAttempts, 0, 10);
        RetryBaseDelaySeconds = Math.Clamp(RetryBaseDelaySeconds, 1, 3600);
        RetryMaxDelaySeconds = Math.Clamp(RetryMaxDelaySeconds, RetryBaseDelaySeconds, 86400);
        LockLeaseMinutes = Math.Clamp(LockLeaseMinutes, 1, 240);
        WatchdogIntervalMinutes = Math.Clamp(WatchdogIntervalMinutes, 5, 1440);
        EventStaleAfterMinutes = Math.Clamp(EventStaleAfterMinutes, 5, 1440);
        SignatureToleranceSeconds = Math.Clamp(SignatureToleranceSeconds, 30, 3600);
        MaxEventPayloadCharacters = Math.Clamp(MaxEventPayloadCharacters, 256, 1_000_000);
        MaxWatchdogReconciliations = Math.Clamp(MaxWatchdogReconciliations, 1, 500);
        MaxTasksPerWave = Math.Clamp(MaxTasksPerWave, 1, 20);
        MaxLaunchAttempts = Math.Clamp(MaxLaunchAttempts, 0, 3);
        MaxLaunchBatchesPerCycle = Math.Clamp(MaxLaunchBatchesPerCycle, 1, 20);
        MaxLaunchReconciliations = Math.Clamp(MaxLaunchReconciliations, 1, 50);
        MaxLaunchReconciles = Math.Clamp(MaxLaunchReconciles, 1, 100);
        PollingFallbackAfterMinutes = Math.Clamp(PollingFallbackAfterMinutes, 5, 1440);
        LaunchReconcileBackoffSeconds = Math.Clamp(LaunchReconcileBackoffSeconds, 30, 86400);
        BridgeTimeoutSeconds = Math.Clamp(BridgeTimeoutSeconds, 5, 120);
        BridgeBaseUrl = (BridgeBaseUrl ?? string.Empty).Trim();
        BridgeTokenEnvironmentVariable = string.IsNullOrWhiteSpace(BridgeTokenEnvironmentVariable)
            ? "TASLIM_BRIDGE_TOKEN"
            : BridgeTokenEnvironmentVariable.Trim();
        BridgeCreateTaskPath = NormalizePath(BridgeCreateTaskPath, "/v1/tasks");
        BridgeGetTaskPathTemplate = NormalizePath(BridgeGetTaskPathTemplate, "/v1/tasks/{taskId}");
        SimulationMode = DryRun;
    }

    /// <summary>True when a live follow-on launch is permitted by configuration.</summary>
    public bool LiveNextWaveLaunchEnabled => AllowNextWaveLaunch && !DryRun;

    private static string NormalizePath(string? value, string fallback)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0 || !candidate.StartsWith('/')) return fallback;
        return candidate;
    }
}
