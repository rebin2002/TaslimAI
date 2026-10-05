namespace Taslim.Api.Autopilot;

/// <summary>
/// Provider-neutral validation for completion signals. Missing or unknown task
/// outcomes must never be interpreted as success.
/// </summary>
public static class AutopilotInputValidation
{
    public static bool TryNormalizeOutcome(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return AutopilotTaskOutcomes.Supported.Contains(normalized);
    }
}
