namespace Taslim.Api.Contracts;

public sealed record MusicProviderCostSettlementCandidateDto(
    Guid AttemptId,
    Guid GenerationJobId,
    string JobStatus,
    string AttemptStatus,
    string Provider,
    string? ProviderExecutionId,
    string ResultClassification,
    string? FailureCode,
    DateTime StartedAt,
    DateTime? CompletedAt,
    decimal? EstimatedProviderCostUsd,
    bool EstimatedProviderCostKnown,
    string? SafeMetadataJson,
    decimal LedgerProviderCostUsd,
    bool LedgerProviderCostKnown,
    decimal CustomerChargedAmount,
    decimal CustomerReversedAmount);

public sealed record SettleMusicProviderCostRequest(
    decimal ActualProviderCostUsd,
    string EvidenceReference);

public sealed record SettleMusicProviderCostResponse(
    Guid AttemptId,
    Guid GenerationJobId,
    decimal AttemptActualProviderCostUsd,
    bool AttemptActualProviderCostKnown,
    decimal LedgerProviderCostUsd,
    bool LedgerProviderCostKnown,
    string CostBasis,
    decimal CustomerChargedAmount,
    decimal CustomerReversedAmount,
    Guid SettledByUserId,
    DateTime SettledAtUtc,
    string EvidenceReference);
