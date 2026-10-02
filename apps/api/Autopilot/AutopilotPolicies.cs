namespace Taslim.Api.Autopilot;

/// <summary>
/// Explicit run/task state machine. Every transition is validated; an unknown
/// or out-of-order transition is rejected rather than guessed.
/// </summary>
public static class AutopilotStateMachine
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RunTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [AutopilotRunStates.Planned] = Set(AutopilotRunStates.AwaitingEvents, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.AwaitingEvents] = Set(AutopilotRunStates.InProgress, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused, AutopilotRunStates.AwaitingEvents),
            [AutopilotRunStates.InProgress] = Set(AutopilotRunStates.AwaitingEvents, AutopilotRunStates.TasksComplete, AutopilotRunStates.IntegrationFailed, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.TasksComplete] = Set(AutopilotRunStates.IntegrationGate, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.IntegrationGate] = Set(AutopilotRunStates.ReleaseGate, AutopilotRunStates.IntegrationFailed, AutopilotRunStates.Paused),
            [AutopilotRunStates.IntegrationFailed] = Set(AutopilotRunStates.AwaitingEvents, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.ReleaseGate] = Set(AutopilotRunStates.ReleaseEligible, AutopilotRunStates.ReleaseBlocked, AutopilotRunStates.Paused),
            [AutopilotRunStates.ReleaseEligible] = Set(AutopilotRunStates.HandoffPendingHuman, AutopilotRunStates.HandoffReady, AutopilotRunStates.Paused, AutopilotRunStates.Cancelled),
            [AutopilotRunStates.ReleaseBlocked] = Set(AutopilotRunStates.AwaitingEvents, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.HandoffPendingHuman] = Set(AutopilotRunStates.HandoffReady, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.HandoffReady] = Set(AutopilotRunStates.SmokeRunning, AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.SmokeRunning] = Set(AutopilotRunStates.SmokePassed, AutopilotRunStates.SmokeFailed, AutopilotRunStates.Paused),
            [AutopilotRunStates.SmokePassed] = Set(AutopilotRunStates.NextWaveEligible, AutopilotRunStates.Completed, AutopilotRunStates.Paused),
            [AutopilotRunStates.SmokeFailed] = Set(AutopilotRunStates.Cancelled, AutopilotRunStates.Paused),
            [AutopilotRunStates.NextWaveEligible] = Set(AutopilotRunStates.Completed, AutopilotRunStates.Paused),
            [AutopilotRunStates.Paused] = Set(AutopilotRunStates.AwaitingEvents, AutopilotRunStates.InProgress, AutopilotRunStates.TasksComplete,
                AutopilotRunStates.IntegrationGate, AutopilotRunStates.ReleaseGate, AutopilotRunStates.ReleaseEligible,
                AutopilotRunStates.ReleaseBlocked, AutopilotRunStates.IntegrationFailed, AutopilotRunStates.HandoffPendingHuman,
                AutopilotRunStates.HandoffReady, AutopilotRunStates.SmokePassed, AutopilotRunStates.Cancelled),
            [AutopilotRunStates.Cancelled] = Set(),
            [AutopilotRunStates.Completed] = Set(),
        };

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> TaskTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [AutopilotTaskStates.Pending] = Set(AutopilotTaskStates.Running, AutopilotTaskStates.AwaitingResult, AutopilotTaskStates.Succeeded, AutopilotTaskStates.BlockedHuman),
            [AutopilotTaskStates.Running] = Set(AutopilotTaskStates.AwaitingResult, AutopilotTaskStates.Succeeded, AutopilotTaskStates.RetryScheduled, AutopilotTaskStates.TerminalFailed, AutopilotTaskStates.BlockedHuman),
            [AutopilotTaskStates.AwaitingResult] = Set(AutopilotTaskStates.Succeeded, AutopilotTaskStates.RetryScheduled, AutopilotTaskStates.TerminalFailed, AutopilotTaskStates.BlockedHuman),
            [AutopilotTaskStates.Succeeded] = Set(),
            [AutopilotTaskStates.RetryScheduled] = Set(AutopilotTaskStates.Running, AutopilotTaskStates.AwaitingResult, AutopilotTaskStates.Succeeded, AutopilotTaskStates.TerminalFailed, AutopilotTaskStates.BlockedHuman),
            [AutopilotTaskStates.TerminalFailed] = Set(),
            [AutopilotTaskStates.BlockedHuman] = Set(AutopilotTaskStates.Pending, AutopilotTaskStates.Running),
        };

    public static bool CanTransitionRun(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;
        return RunTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public static bool CanTransitionTask(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;
        return TaskTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Bounded retry policy. Backoff grows exponentially and is capped; the attempt
/// ceiling is enforced so no task can retry forever.
/// </summary>
public static class AutopilotRetryPolicy
{
    public static bool CanRetry(string failureClass, int attempt, int maxAttempts)
    {
        var normalized = AutopilotFailureClasses.Normalize(failureClass);
        if (!AutopilotFailureClasses.Automatic.Contains(normalized)) return false;
        return attempt < Math.Max(0, maxAttempts);
    }

    public static TimeSpan ComputeBackoff(int attempt, AutopilotOptions options)
    {
        var boundedAttempt = Math.Clamp(attempt, 0, 16);
        var baseSeconds = Math.Max(1, options.RetryBaseDelaySeconds);
        var raw = baseSeconds * Math.Pow(2, boundedAttempt);
        var capped = Math.Min(raw, Math.Max(baseSeconds, options.RetryMaxDelaySeconds));
        var jitter = DeterministicJitter(attempt, baseSeconds);
        return TimeSpan.FromSeconds(Math.Min(capped + jitter, Math.Max(baseSeconds, options.RetryMaxDelaySeconds)));
    }

    private static double DeterministicJitter(int attempt, int baseSeconds)
    {
        // Deterministic, bounded jitter (0..base/4) keeps tests reproducible while
        // still spreading simultaneous retries apart.
        var span = Math.Max(1, baseSeconds / 4);
        return (Math.Abs(attempt * 2654435761L) % span);
    }
}

/// <summary>
/// Safety policy. Encodes the rules that can never be bypassed by configuration:
/// no release on failed evidence, no destructive database action, and no paid
/// capability activation without a human decision.
/// </summary>
public static class AutopilotSafetyPolicy
{
    public static bool PaidCapabilitiesEnabled(AutopilotOptions options) =>
        options.ChargingEnabled || options.PaidProvidersEnabled;

    public static bool IsForbiddenAction(string action) =>
        AutopilotForbiddenActions.All.Contains(action, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Integration gate rules: every task must have succeeded, the recorded
    /// candidate SHA must be unchanged, and all required deterministic checks
    /// (build, unit tests, typecheck, browser E2E where required, migrations
    /// where required, integration completeness) must have passed.
    /// </summary>
    public static GateEvaluation EvaluateIntegrationGate(AutopilotGateInput input, AutopilotOptions options)
    {
        var failed = new List<string>();
        if (!input.AllTasksSucceeded) failed.Add("tasks_incomplete");
        if (input.HasBlockingFailure) failed.Add("blocking_failure_class");
        if (!input.CandidateShaMatches) failed.Add("candidate_sha_mismatch");
        if (!input.Passed(AutopilotChecks.Build)) failed.Add(AutopilotChecks.Build);
        if (!input.Passed(AutopilotChecks.UnitTests)) failed.Add(AutopilotChecks.UnitTests);
        if (!input.Passed(AutopilotChecks.Typecheck)) failed.Add(AutopilotChecks.Typecheck);
        if (!input.Passed(AutopilotChecks.IntegrationComplete)) failed.Add(AutopilotChecks.IntegrationComplete);
        if (options.RequireBrowserE2E && !input.Passed(AutopilotChecks.BrowserE2E)) failed.Add(AutopilotChecks.BrowserE2E);
        if (options.RequireMigrations && !input.Passed(AutopilotChecks.Migrations)) failed.Add(AutopilotChecks.Migrations);

        return failed.Count == 0
            ? new GateEvaluation(true, "integration_passed", "All required integration evidence passed.", [])
            : new GateEvaluation(false, "integration_evidence_incomplete", "Integration gate blocked by missing or failed evidence.", failed);
    }

    /// <summary>
    /// Release gate rules: the integration gate must already have passed and the
    /// security and accounting checks must be green. No release is ever eligible
    /// while a blocking failure or a pending human decision exists.
    /// </summary>
    public static GateEvaluation EvaluateReleaseGate(AutopilotGateInput input, bool integrationGatePassed)
    {
        var failed = new List<string>();
        if (!integrationGatePassed) failed.Add("integration_gate");
        if (input.HasBlockingFailure) failed.Add("blocking_failure_class");
        if (!input.CandidateShaMatches) failed.Add("candidate_sha_mismatch");
        if (!input.Passed(AutopilotChecks.Security)) failed.Add(AutopilotChecks.Security);
        if (!input.Passed(AutopilotChecks.Accounting)) failed.Add(AutopilotChecks.Accounting);

        return failed.Count == 0
            ? new GateEvaluation(true, "release_passed", "Release evidence passed.", [])
            : new GateEvaluation(false, "release_evidence_incomplete", "Release gate blocked by missing or failed evidence.", failed);
    }

    /// <summary>Destructive database actions are refused unconditionally, regardless of configuration.</summary>
    public static bool CanPerformDestructiveDatabaseAction(string action) => false;
}

public sealed record GateEvaluation(bool Passed, string ReasonCode, string Reason, IReadOnlyList<string> FailedChecks);

public sealed record AutopilotGateInput(
    bool AllTasksSucceeded,
    bool HasBlockingFailure,
    bool CandidateShaMatches,
    IReadOnlyDictionary<string, bool> Checks)
{
    public bool Passed(string check) => Checks.TryGetValue(check, out var value) && value;
}

/// <summary>
/// Human decision policy: the explicit list of decisions the controller must
/// never make on its own.
/// </summary>
public static class AutopilotHumanDecisionPolicy
{
    public static bool RequiresHumanDecision(string decisionKind) =>
        AutopilotHumanDecisions.All.Contains(decisionKind, StringComparer.OrdinalIgnoreCase);

    /// <summary>Evaluates a run's evidence for decisions that must escalate to a human.</summary>
    public static string? Evaluate(
        AutopilotOptions options,
        string? failureClass,
        bool signalRequiresHuman,
        string? signalDecisionKind,
        bool integrationGatePassed)
    {
        if (signalRequiresHuman && !string.IsNullOrWhiteSpace(signalDecisionKind))
            return signalDecisionKind;

        var normalized = AutopilotFailureClasses.Normalize(failureClass);
        switch (normalized)
        {
            case AutopilotFailureClasses.Pricing:
                return AutopilotHumanDecisions.Pricing;
            case AutopilotFailureClasses.SchemaIrreversible:
                return AutopilotHumanDecisions.DestructiveSchema;
            case AutopilotFailureClasses.Security:
                return AutopilotHumanDecisions.SecurityAmbiguity;
            case AutopilotFailureClasses.ProductDirection:
                return AutopilotHumanDecisions.ProductDirection;
            case AutopilotFailureClasses.Migration:
            case AutopilotFailureClasses.BrowserE2E:
            case AutopilotFailureClasses.Accounting:
            case AutopilotFailureClasses.Integration:
            case AutopilotFailureClasses.Unknown:
                return AutopilotHumanDecisions.SecurityAmbiguity;
        }

        if (integrationGatePassed && !options.AllowAutomaticIntegrationMerge)
            return AutopilotHumanDecisions.IntegrationMerge;
        if (integrationGatePassed && !options.AllowAutomaticRelease)
            return AutopilotHumanDecisions.ProductionRelease;
        if (!options.ChargingEnabled && options.PaidProvidersEnabled)
            return AutopilotHumanDecisions.EnableCharging;

        return null;
    }
}
