using Microsoft.Extensions.Options;
using Taslim.Api.Generation;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationJobOptionsValidationTests
{
    private readonly GenerationJobOptionsValidator validator = new();

    [Fact]
    public void Default_worker_settings_are_valid()
    {
        var result = validator.Validate(Options.DefaultName, new GenerationJobOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(nameof(GenerationJobOptions.WorkerConcurrency), -1)]
    [InlineData(nameof(GenerationJobOptions.WorkerConcurrency), 65)]
    [InlineData(nameof(GenerationJobOptions.PollIntervalMilliseconds), 0)]
    [InlineData(nameof(GenerationJobOptions.PollIntervalMilliseconds), 60_001)]
    [InlineData(nameof(GenerationJobOptions.CancellationPollMilliseconds), 0)]
    [InlineData(nameof(GenerationJobOptions.ClaimRecoveryIntervalMilliseconds), 999)]
    [InlineData(nameof(GenerationJobOptions.ClaimLeaseMinutes), 1)]
    [InlineData(nameof(GenerationJobOptions.LeaseRenewalIntervalMilliseconds), 999)]
    [InlineData(nameof(GenerationJobOptions.MaxAutomaticRetries), -1)]
    [InlineData(nameof(GenerationJobOptions.MaxAutomaticRetries), 11)]
    [InlineData(nameof(GenerationJobOptions.HeartbeatStaleAfterSeconds), 29)]
    public void Unsafe_worker_setting_is_rejected(string property, int value)
    {
        var options = new GenerationJobOptions();
        typeof(GenerationJobOptions).GetProperty(property)!.SetValue(options, value);

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
        Assert.Contains($"GenerationJobs:{property}", result.FailureMessage);
    }
}
