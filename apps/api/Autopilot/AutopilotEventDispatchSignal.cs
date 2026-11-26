using System.Threading;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Coalesced, in-process wake hint for the durable Autopilot completion queue.
/// A signal is sent only after the intake transaction returns successfully; the
/// database remains authoritative, so startup recovery and sender retries cover
/// a process crash between commit and notification.
/// </summary>
public static class AutopilotEventDispatchSignal
{
    private static readonly SemaphoreSlim PendingWakeup = new(0, 1);

    /// <summary>Notify the watchdog that committed completion work may be ready.</summary>
    public static void NotifyCommittedEvent()
    {
        try
        {
            PendingWakeup.Release();
        }
        catch (SemaphoreFullException)
        {
            // One outstanding wake is enough: the worker drains the durable queue.
        }
    }

    /// <summary>
    /// Wait for a completion wake or the watchdog fallback interval. Returning
    /// false means the fallback interval elapsed; returning true means a wake
    /// hint was consumed.
    /// </summary>
    public static Task<bool> WaitForEventOrTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        PendingWakeup.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Shared inter-process lease between durable signed-event intake and a next-wave
/// provider create. Launch renews the owner/token lease during the provider call,
/// aborts on renewal loss, and revalidates before accepting the provider response.
/// Renewal cannot replace downstream token checks or service-boundary coordination.
/// </summary>
public static class AutopilotCompletionLaunchFence
{
    public const string ResourceKey = "autopilot:completion-launch-fence";

    // Reserve two minutes beyond the configured HTTP timeout for allocator and
    // outcome persistence, plus one minute for reservation/setup before create.
    public const int PersistenceSafetyMarginSeconds = 120;
    public const int ReservationSetupMarginSeconds = 60;

    public static TimeSpan RenewalInterval(int leaseMinutes)
    {
        var leaseSeconds = Math.Max(60L, (long)Math.Max(1, leaseMinutes) * 60);
        return TimeSpan.FromSeconds(Math.Clamp((int)(leaseSeconds / 3), 1, 30));
    }

    public static int RequiredLeaseMinutes(int bridgeTimeoutSeconds)
    {
        var boundedTimeout = Math.Clamp(bridgeTimeoutSeconds, 5, 120);
        var requiredSeconds = boundedTimeout + PersistenceSafetyMarginSeconds + ReservationSetupMarginSeconds;
        return Math.Max(1, (requiredSeconds + 59) / 60);
    }
}
