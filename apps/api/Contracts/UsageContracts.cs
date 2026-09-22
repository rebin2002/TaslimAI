namespace Taslim.Api.Contracts;

public sealed record UsageSummaryDto(
    int TotalRequests,
    int CompletedRequests,
    int FailedRequests,
    int CancelledRequests,
    int RefundedRequests,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal CustomerChargedAmount,
    string ChargedUnit);

public sealed record UsageTransactionDto(
    Guid Id,
    string Feature,
    string Status,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    decimal ChargedAmount,
    string ChargedUnit,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? FailureCode);

public sealed record UsageHistoryDto(
    IReadOnlyList<UsageTransactionDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
