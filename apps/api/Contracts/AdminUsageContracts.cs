using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

public sealed record AdminUsageSummaryDto(
    DateTime FromUtc,
    DateTime ToUtc,
    int TransactionCount,
    int SuccessfulCount,
    int FailedCount,
    int CancelledCount,
    int RefundedCount,
    decimal TotalProviderCostUsd,
    decimal TotalCustomerChargesUsd,
    decimal PendingEstimatedProviderCostUsd,
    int AnomalousCount,
    string Currency);

public sealed record AdminUsageFeatureBreakdownDto(
    string Feature,
    int TransactionCount,
    int SuccessfulCount,
    int FailedCount,
    int CancelledCount,
    decimal ProviderCostUsd,
    decimal CustomerChargesUsd);

public sealed record AdminUsageDailyBreakdownDto(
    DateTime DayUtc,
    int TransactionCount,
    decimal ProviderCostUsd,
    decimal CustomerChargesUsd);

public sealed record AdminUsageWorkspaceBreakdownDto(
    Guid WorkspaceId,
    string WorkspaceName,
    int TransactionCount,
    decimal ProviderCostUsd,
    decimal CustomerChargesUsd);

public sealed record AdminUsageUserBreakdownDto(
    Guid UserId,
    string? Email,
    string DisplayName,
    int TransactionCount,
    decimal ProviderCostUsd,
    decimal CustomerChargesUsd);

public sealed record AdminUsageStatusBreakdownDto(
    string Status,
    int TransactionCount,
    int SuccessfulCount,
    int FailedCount,
    int CancelledCount,
    decimal ProviderCostUsd,
    decimal CustomerChargesUsd);

public sealed record AdminUsageBreakdownsDto(
    IReadOnlyList<AdminUsageFeatureBreakdownDto> ByFeature,
    IReadOnlyList<AdminUsageDailyBreakdownDto> ByDay,
    IReadOnlyList<AdminUsageWorkspaceBreakdownDto> ByWorkspace,
    IReadOnlyList<AdminUsageUserBreakdownDto> ByUser,
    IReadOnlyList<AdminUsageStatusBreakdownDto> ByStatus);

public sealed record AdminUsageTransactionDto(
    Guid Id,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    Guid WorkspaceId,
    string WorkspaceName,
    Guid UserId,
    string? UserEmail,
    string UserDisplayName,
    Guid? ProjectId,
    Guid? ConversationId,
    Guid? GenerationJobId,
    string Feature,
    string Status,
    string Provider,
    string Model,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    int? ImageInputTokens,
    int? ImageOutputTokens,
    int? LatencyMs,
    decimal? EstimatedProviderCostUsd,
    decimal ProviderCostUsd,
    decimal ChargedAmount,
    string Currency,
    string? CostBasis,
    string? PricingVersion,
    string? PricingSnapshotJson,
    string? SafeMetadataJson,
    string? FailureCode,
    bool IsAnomalous,
    string? AnomalyCode,
    DateTime? RefundedAt);

public sealed record AdminUsageTransactionListDto(
    IReadOnlyList<AdminUsageTransactionDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record AdminUsageReportDto(
    AdminUsageSummaryDto Summary,
    AdminUsageBreakdownsDto Breakdowns,
    AdminUsageTransactionListDto Transactions);

public sealed record AdminUsageFilter(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    UsageFeature? Feature = null,
    UsageTransactionStatus? Status = null,
    Guid? WorkspaceId = null,
    Guid? UserId = null,
    Guid? GenerationJobId = null,
    int Page = 1,
    int PageSize = 25);
