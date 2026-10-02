using Microsoft.Extensions.Options;

namespace Taslim.Api.Autopilot;

public sealed class AutopilotWatchdog(
    IServiceScopeFactory scopeFactory,
    IOptions<AutopilotWatchdogOptions> options,
    ILogger<AutopilotWatchdog> logger) : BackgroundService
{
    private readonly AutopilotWatchdogOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("Autopilot watchdog is disabled; webhook events remain the primary mechanism.");
            return;
        }
        var interval = TimeSpan.FromHours(Math.Max(1, settings.IntervalHours));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var controller = scope.ServiceProvider.GetRequiredService<IAutopilotControllerService>();
                var count = await controller.ReconcileDueWavesAsync(stoppingToken);
                logger.LogInformation("Autopilot hourly watchdog reconciled {WaveCount} active wave(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Autopilot hourly watchdog reconciliation failed.");
            }
        }
    }
}
