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
