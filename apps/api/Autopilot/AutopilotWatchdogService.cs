using Microsoft.Extensions.Options;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Hourly fallback watchdog. The event-driven path is primary; this service
/// exists only to reconcile missed, lost, or out-of-order completion events and
/// to re-drive a wave whose completion evidence never arrived. It never
/// performs a release or integration on its own and it respects the kill
/// switch, the pause flag, and the feature flag.
/// </summary>
public sealed class AutopilotWatchdogService(
    IServiceScopeFactory scopeFactory,
    IOptions<AutopilotOptions> options,
    ILogger<AutopilotWatchdogService> logger) : BackgroundService
{
    private readonly AutopilotOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("Autopilot watchdog is idle because Autopilot.Enabled is false.");
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(settings.WatchdogIntervalMinutes, 5, 1440));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
                    await RunReconciliationAsync(stoppingToken);
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

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    /// <summary>Executes one watchdog reconciliation pass. Exposed for operational use and tests.</summary>
    public async Task RunReconciliationAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAutopilotOrchestrator>();
        var result = await orchestrator.ReconcileAsync("hourly_watchdog", cancellationToken);
        logger.LogInformation(
            "Autopilot watchdog reconciliation completed. Outcome={Outcome}; Examined={Examined}; Processed={Processed}; Skipped={Skipped}",
            result.Outcome, result.EventsExamined, result.EventsProcessed, result.EventsSkipped);
    }

    private async Task<AutopilotControlSnapshot?> ReadControlAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAutopilotOrchestrator>();
        return await orchestrator.GetControlAsync(cancellationToken);
    }
}