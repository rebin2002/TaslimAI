namespace Taslim.Api.Notifications;

public static class NotificationDeliveryRetry
{
    public const int DefaultMaxAttempts = 3;

    public static async Task<bool> TryExecuteAsync(
        Func<Task> action,
        int maxAttempts = DefaultMaxAttempts,
        Func<int, TimeSpan>? delay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        delay ??= attempt => TimeSpan.FromMilliseconds(100 * attempt);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                var wait = delay(attempt);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }
}
