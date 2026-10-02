using System.Security.Cryptography;
using System.Text;

namespace Taslim.Api.Autopilot;

public sealed record NextWaveEligibility(bool Eligible, string? ReasonCode, string? HumanDecisionRequired)
{
    public static NextWaveEligibility Allowed() => new(true, null, null);

    public static NextWaveEligibility Blocked(string reasonCode, string? humanDecision = null) => new(false, reasonCode, humanDecision);
}

/// <summary>
/// Deterministic, provider-neutral policy for follow-on development waves. It
/// encodes the hard boundary: a follow-on wave may only be planned from
/// pre-approved ordinary development work, only after the source wave is
/// gate-green, and never while a product, security, pricing, schema, or spending
/// decision is outstanding.
/// </summary>
public static class AutopilotNextWavePolicy
{
    /// <summary>Maximum length of any persisted wave key.</summary>
    public const int MaxWaveKeyLength = 120;

    /// <summary>Run states from which a follow-on wave may be planned.</summary>
    public static readonly IReadOnlySet<string> LaunchableStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AutopilotRunStates.NextWaveEligible,
        AutopilotRunStates.SmokePassed,
        AutopilotRunStates.HandoffReady,
        AutopilotRunStates.HandoffPendingHuman,
    };

    /// <summary>
    /// Human decisions that do not block a follow-on *development* batch: they
    /// gate the production release of the finished wave, not the start of the
    /// next batch of development work.
    /// </summary>
    public static readonly IReadOnlySet<string> ReleaseOnlyDecisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AutopilotHumanDecisions.ProductionRelease,
        AutopilotHumanDecisions.IntegrationMerge,
    };

    /// <summary>
    /// Deterministic follow-on wave key derived from the source wave key, so a
    /// restart or a replay resolves to the same wave and can never create a
    /// second batch for the same source wave.
    /// </summary>
    public static string NextWaveKey(string? sourceWaveKey)
    {
        var normalized = (sourceWaveKey ?? string.Empty).Trim();
        if (normalized.Length == 0) normalized = "wave";

        const string suffix = "-next";
        if (normalized.Length + suffix.Length <= MaxWaveKeyLength) return normalized + suffix;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..8];
        var keep = MaxWaveKeyLength - suffix.Length - hash.Length - 1;
        return $"{normalized[..keep]}-{hash}{suffix}";
    }

    /// <summary>Stable fingerprint of the bounded plan metadata.</summary>
    public static string PlanFingerprint(string waveKey, IReadOnlyList<WaveLaunchTaskPlan> tasks)
    {
        var builder = new StringBuilder();
        builder.Append(waveKey).Append('\n');
        foreach (var task in tasks)
        {
            builder.Append(task.TaskKey).Append('|')
                .Append(task.Title).Append('|')
                .Append(task.BacklogItemKey).Append('|')
                .Append(task.BaseSha).Append('|')
                .Append(task.Branch).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>Deterministic idempotency key sent to the execution provider.</summary>
    public static string LaunchIdempotencyKey(string waveKey, string taskKey) =>
        Bounded($"autopilot:{waveKey}:{taskKey}", 200);

    /// <summary>
    /// Evaluates whether a gate-green source run may start a follow-on wave.
    /// Every blocking condition is explicit; the default answer is "blocked".
    /// </summary>
    public static NextWaveEligibility Evaluate(
        AutopilotRun run,
        AutopilotOptions options,
        bool killSwitchEngaged,
        bool paused,
        bool hasExistingBatch)
    {
        if (!options.Enabled) return NextWaveEligibility.Blocked("feature_flag_disabled");
        if (killSwitchEngaged) return NextWaveEligibility.Blocked("kill_switch_engaged");
        if (paused) return NextWaveEligibility.Blocked("controller_paused");
        if (!options.AllowNextWaveLaunch) return NextWaveEligibility.Blocked("next_wave_launch_disabled");
        if (!LaunchableStates.Contains(run.State)) return NextWaveEligibility.Blocked($"run_state_not_launchable:{run.State}");
        if (!run.IntegrationGatePassed || !run.ReleaseGatePassed)
            return NextWaveEligibility.Blocked("gates_incomplete");

        var decision = run.HumanDecisionRequired;
        if (!string.IsNullOrWhiteSpace(decision))
        {
            if (!ReleaseOnlyDecisions.Contains(decision))
                return NextWaveEligibility.Blocked($"human_decision_pending:{decision}", decision);

            var releasePending = string.Equals(run.State, AutopilotRunStates.HandoffPendingHuman, StringComparison.OrdinalIgnoreCase)
                || string.Equals(decision, AutopilotHumanDecisions.ProductionRelease, StringComparison.OrdinalIgnoreCase);
            if (releasePending && !options.AllowNextWaveLaunchWhenReleasePending)
                return NextWaveEligibility.Blocked("release_pending_human", decision);
        }

        if (hasExistingBatch && !options.AllowNextWaveLaunch) return NextWaveEligibility.Blocked("next_wave_launch_disabled");

        return NextWaveEligibility.Allowed();
    }

    /// <summary>Destructive or release actions are never available to the launch loop.</summary>
    public static bool CanPerform(string action) =>
        !AutopilotSafetyPolicy.IsForbiddenAction(action)
        && !string.Equals(action, "production_release", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(action, "integration_merge", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bounded exponential backoff for launch retries and polling.</summary>
    public static TimeSpan ComputeBackoff(int attempt, AutopilotOptions options)
    {
        var boundedAttempt = Math.Clamp(attempt, 0, 16);
        var baseSeconds = Math.Max(30, options.LaunchReconcileBackoffSeconds);
        var raw = baseSeconds * Math.Pow(2, boundedAttempt);
        var capped = Math.Min(raw, Math.Max(baseSeconds, options.RetryMaxDelaySeconds));
        var jitter = Math.Abs(attempt * 2654435761L) % Math.Max(1, baseSeconds / 4);
        return TimeSpan.FromSeconds(Math.Min(capped + jitter, Math.Max(baseSeconds, options.RetryMaxDelaySeconds)));
    }

    private static string Bounded(string value, int max) => value.Length <= max ? value : value[..max];
}
