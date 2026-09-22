using Taslim.Api.Generation;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationJobPollingTests
{
    [Fact]
    public void Defaults_back_off_idle_queue_polling_to_one_second()
    {
        var schedule = new GenerationJobPollingSchedule(new GenerationJobOptions());

        Assert.Equal(TimeSpan.FromSeconds(1), schedule.IdleDelay);
    }

    [Fact]
    public void Recovery_runs_immediately_then_uses_a_separate_thirty_second_cadence()
    {
        var options = new GenerationJobOptions();
        var schedule = new GenerationJobPollingSchedule(options);
        var now = DateTime.UtcNow;

        Assert.True(schedule.RecoveryDue(now));
        schedule.ScheduleNextRecovery(now);

        Assert.False(schedule.RecoveryDue(now.AddSeconds(29)));
        Assert.True(schedule.RecoveryDue(now.AddSeconds(30)));
    }

    [Fact]
    public void Recovery_interval_is_configurable_without_changing_idle_delay()
    {
        var schedule = new GenerationJobPollingSchedule(new GenerationJobOptions
        {
            PollIntervalMilliseconds = 250,
            ClaimRecoveryIntervalMilliseconds = 45000,
        });
        var now = DateTime.UtcNow;

        schedule.ScheduleNextRecovery(now);

        Assert.Equal(TimeSpan.FromMilliseconds(250), schedule.IdleDelay);
        Assert.False(schedule.RecoveryDue(now.AddSeconds(44)));
        Assert.True(schedule.RecoveryDue(now.AddSeconds(45)));
    }
}
