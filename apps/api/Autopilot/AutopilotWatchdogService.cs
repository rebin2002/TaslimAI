using Microsoft.Extensions.Options;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Event-woken completion processor with an hourly fallback watchdog. The
/// database queue remains authoritative: startup drains recover a crash after
/// event commit but before the process-local wake hint, and the timer reconciles
/// missed, lost, or out-of-order events. This service never performs a release
/// or integration on its own and respects the kill switch, pause, and feature
/// flag.
/// </summary>
public sealed class AutopilotWatchdogService(
    IServiceScopeFactory scopeFactory,
    IOptions<AutopilotOptions> options,
    ILogger<AutopilotWatchdogService> logger) : BackgroundService
{
    private const int MaxDrainBatchesPerPass = 20;
    private readonly AutopilotOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
            logger.LogInformation("Autopilot watchdog is idle because Autopilot.Enabled is false.");

        var interval = TimeSpan.FromMinutes(Math.Clamp(settings.WatchdogIntervalMinutes, 5, 1440));

        // Process already-committed rows immediately on startup. This is the
        // recovery path for a crash between database commit and wake signaling.
        try
        {
            await RunReconciliationAsync("startup_recovery", stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Autopilot startup event recovery failed safely.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var eventWakeup = await AutopilotEventDispatchSignal.WaitForEventOrTimeoutAsync(interval, stoppingToken);
                var reason = eventWakeup ? "completion_event" : "hourly_watchdog";
                var control = await ReadControlAsync(stoppingToken);
                if (!settings.Enabled)
                {
                    logger.LogDebug("Autopilot watchdog skipped: feature flag disabled.");
                }
                else if (control is { KillSwitchEngaged: true })
                {
                    logger.LogWarning("Autopilot watchdog skipped: kill switch engaged.");
                }
                else if (control is { Paused: true })
                {
                    logger.LogInformation("Autopilot watchdog skipped: controller paused.");
                }
                else
                {
                    await RunReconciliationAsync(reason, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Autopilot watchdog reconciliation failed safely.");
            }
        }
    }

    /// <summary>Executes one periodic fallback reconciliation pass.</summary>
    public Task RunReconciliationAsync(CancellationToken cancellationToken) =>
        RunReconciliationAsync("hourly_watchdog", cancellationToken);

    /// <summary>
    /// Drains the due durable completion queue in bounded batches before the
    /// explicit reconciliation call. Explicit event-drain or run-advance budget
    /// deferrals are re-driven immediately within this separately bounded pass;
    /// if the pass itself reaches its limit, another coalesced wake is queued so
    /// remaining work is not stranded until the hourly fallback. Future-backoff
    /// and Processing rows remain fail-closed without a hot retry loop.
    /// </summary>
    public async Task RunReconciliationAsync(string reason, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAutopilotOrchestrator>();
        var batchSize = Math.Clamp(settings.MaxConcurrency, 1, 20);
        var drainedEvents = 0;

        for (var batch = 0; batch < MaxDrainBatchesPerPass; batch++)
        {
            var result = await orchestrator.ProcessPendingEventsAsync(cancellationToken);
            drainedEvents += result.EventsExamined;
            var budgetDeferred = result.Outcome == "drain_deferred"
                && (result.Reason is "completion_event_drain_budget_exhausted" or "completion_run_advancement_deferred");
            if (!budgetDeferred && result.EventsExamined < batchSize)
                break;

            if (batch == MaxDrainBatchesPerPass - 1)
            {
                logger.LogWarning(
                    "Autopilot completion queue reached its bounded drain limit. Drained={Drained}; BatchSize={BatchSize}",
                    drainedEvents, batchSize);
                AutopilotEventDispatchSignal.NotifyCommittedEvent();
            }
        }

        // Run the explicit reconciliation only after the due queue drain. The
        // orchestrator may also advance eligibility during processing of a
        // particular completion after persisting that event's state transition.
        var reconciliation = await orchestrator.ReconcileAsync(reason, cancellationToken);
        logger.LogInformation(
            "Autopilot event-driven reconciliation completed. Reason={Reason}; Drained={Drained}; Outcome={Outcome}; Examined={Examined}; Processed={Processed}; Skipped={Skipped}",
            reason, drainedEvents, reconciliation.Outcome, reconciliation.EventsExamined,
            reconciliation.EventsProcessed, reconciliation.EventsSkipped);
    }

    private async Task<AutopilotControlSnapshot?> ReadControlAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAutopilotOrchestrator>();
        return await orchestrator.GetControlAsync(cancellationToken);
    }
}
