using Taslim.Api.Notifications;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class NotificationDeliveryRetryTests
{
    [Fact]
    public async Task Retries_transient_delivery_until_it_succeeds()
    {
        var attempts = 0;

        var delivered = await NotificationDeliveryRetry.TryExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts < 3) throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            },
            delay: static _ => TimeSpan.Zero);

        Assert.True(delivered);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Stops_after_the_bounded_attempt_limit()
    {
        var attempts = 0;

        var delivered = await NotificationDeliveryRetry.TryExecuteAsync(
            () =>
            {
                attempts++;
                throw new InvalidOperationException("persistent");
            },
            delay: static _ => TimeSpan.Zero);

        Assert.False(delivered);
        Assert.Equal(NotificationDeliveryRetry.DefaultMaxAttempts, attempts);
    }

    [Fact]
    public async Task Propagates_cancellation_instead_of_retrying_after_shutdown()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => NotificationDeliveryRetry.TryExecuteAsync(
            () => throw new OperationCanceledException(cancellation.Token),
            delay: static _ => TimeSpan.Zero,
            cancellationToken: cancellation.Token));
    }
}
