using Microsoft.Extensions.Options;

namespace Taslim.Api.Generation;

/// <summary>
/// Bounds worker timing and concurrency settings before the host starts. The
/// worker has defensive clamps for runtime overrides, but production config
/// errors must fail closed rather than silently creating an unsafe scheduler.
/// </summary>
public sealed class GenerationJobOptionsValidator : IValidateOptions<GenerationJobOptions>
{
    public ValidateOptionsResult Validate(string? name, GenerationJobOptions options)
    {
        var failures = new List<string>();
        AddRangeFailure(failures, nameof(options.WorkerConcurrency), options.WorkerConcurrency, 1, 64);
        AddRangeFailure(failures, nameof(options.PollIntervalMilliseconds), options.PollIntervalMilliseconds, 1, 60_000);
        AddRangeFailure(failures, nameof(options.CancellationPollMilliseconds), options.CancellationPollMilliseconds, 1, 60_000);
        AddRangeFailure(failures, nameof(options.ClaimRecoveryIntervalMilliseconds), options.ClaimRecoveryIntervalMilliseconds, 1_000, 600_000);
        AddRangeFailure(failures, nameof(options.ClaimLeaseMinutes), options.ClaimLeaseMinutes, 2, 1_440);
        AddRangeFailure(failures, nameof(options.LeaseRenewalIntervalMilliseconds), options.LeaseRenewalIntervalMilliseconds, 1_000, 600_000);
        AddRangeFailure(failures, nameof(options.MaxAutomaticRetries), options.MaxAutomaticRetries, 0, 10);
        AddRangeFailure(failures, nameof(options.HeartbeatStaleAfterSeconds), options.HeartbeatStaleAfterSeconds, 30, 3_600);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void AddRangeFailure(ICollection<string> failures, string property, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            failures.Add($"GenerationJobs:{property} must be between {minimum} and {maximum}.");
    }
}
