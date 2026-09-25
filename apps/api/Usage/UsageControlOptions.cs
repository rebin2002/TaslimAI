namespace Taslim.Api.Usage;

public sealed class UsageControlOptions
{
    public bool GuardrailsEnabled { get; set; }
    public bool RejectUnknownEstimates { get; set; } = true;
    public decimal? MaxEstimatedProviderCostPerGenerationUsd { get; set; }
    public decimal? DailyWorkspaceProviderCostCeilingUsd { get; set; }
    public decimal? MonthlyWorkspaceProviderCostCeilingUsd { get; set; }
    public decimal? SingleTransactionAnomalyThresholdUsd { get; set; }
    public decimal? DailyWorkspaceAnomalyThresholdUsd { get; set; }
    public int? RepeatedFailedOperationsThreshold { get; set; }
    public int RepeatedFailedOperationsWindowMinutes { get; set; } = 60;
}

public sealed record UsagePreflightResult(
    bool Allowed,
    decimal? EstimatedProviderCostUsd,
    string? RejectionCode = null,
    string? RejectionMessage = null);

public sealed class UsageGuardrailRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
