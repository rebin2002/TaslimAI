using Taslim.Api.Domain;
using Taslim.Api.Operations;

namespace Taslim.Api.Contracts;

public sealed record AdminOperationsDashboardDto(
    AdminOperationsRangeDto Range,
    AdminGenerationOverviewDto Generation,
    AdminUsageOperationsDto Usage,
    AdminUsersAndWorkspacesDto UsersAndWorkspaces,
    AdminAssetsAndStorageDto AssetsAndStorage,
    AdminBillingOperationsDto Billing,
    AdminOperationalSignalsDto Signals,
    IReadOnlyList<AdminProviderHealthDto> Providers,
    AdminMovieOperationsDto Movie,
    AdminWorkerOperationsDto Workers,
    IReadOnlyList<AdminOperationAuditDto> RecentAdminActions);

public sealed record AdminOperationsRangeDto(DateTime FromUtc, DateTime ToUtc);

public sealed record AdminGenerationOverviewDto(
    int TotalJobsInRange,
    IReadOnlyList<AdminCountBreakdownDto> ByStatus,
    IReadOnlyList<AdminCountBreakdownDto> ByStudio,
    IReadOnlyList<AdminRecentFailureDto> RecentFailures,
    IReadOnlyList<AdminRunningJobDto> RunningJobs,
    int QueuedOrPendingCount,
    int LongRunningJobCount,
    int TotalRetryCount);

public sealed record AdminCountBreakdownDto(string Key, int Count);

public sealed record AdminRecentFailureDto(
    Guid JobId,
    string JobType,
    string? ErrorCode,
    DateTime FailedAt);

public sealed record AdminRunningJobDto(
    Guid JobId,
    string JobType,
    int ProgressPercent,
    DateTime? QueuedAt,
    DateTime? StartedAt,
    DateTime CreatedAt,
    int RetryCount,
    DateTime? ClaimExpiresAt,
    bool IsLongRunning,
    bool IsLeaseExpired);

public sealed record AdminUsageOperationsDto(
    int RequestCount,
    int CompletedRequestCount,
    int FailedRequestCount,
    int PendingRequestCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ImageInputTokens,
    long ImageOutputTokens,
    decimal ProviderCostUsd,
    decimal CustomerChargesUsd,
    decimal PendingEstimatedProviderCostUsd,
    IReadOnlyList<AdminUsageFeatureOperationsDto> ByFeature);

public sealed record AdminUsageFeatureOperationsDto(
    string Feature,
    int RequestCount,
    int CompletedRequestCount,
    int FailedRequestCount,
    int PendingRequestCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ImageInputTokens,
    long ImageOutputTokens,
    decimal ProviderCostUsd,
    decimal CustomerChargesUsd);

public sealed record AdminUsersAndWorkspacesDto(
    int TotalUsers,
    int ActiveUsers,
    int DisabledUsers,
    int TotalWorkspaces,
    int PersonalWorkspaces,
    int BusinessWorkspaces,
    int ArchivedWorkspaces);

public sealed record AdminAssetsAndStorageDto(
    int TotalAssets,
    IReadOnlyList<AdminCountBreakdownDto> AssetsByType,
    int TotalStoredFiles,
    long StoredBytes,
    IReadOnlyList<AdminCountBreakdownDto> FilesByStatus,
    IReadOnlyList<AdminCountBreakdownDto> FilesByStorageProvider,
    IReadOnlyList<AdminCountBreakdownDto> FilesByExtractionStatus,
    int FailedFileCountInRange,
    int FailedExtractionCountInRange,
    string ConfiguredStorageProvider,
    bool PersistentStorageConfigured);

public sealed record AdminBillingOperationsDto(
    bool CustomerChargingEnabled,
    string ConfiguredProvider,
    bool PaymentProviderConfigured,
    IReadOnlyList<AdminSubscriptionBreakdownDto> Subscriptions,
    IReadOnlyList<AdminCountBreakdownDto> PaymentAttemptsByStatus,
    IReadOnlyList<AdminCountBreakdownDto> PaymentEventsByStatus,
    int PendingReconciliationCount,
    int WebhookFailureCount);

public sealed record AdminSubscriptionBreakdownDto(string PlanCode, string Status, int Count);

public sealed record AdminOperationalSignalsDto(
    int RunningJobCount,
    int QueuedOrPendingJobCount,
    int RecentFailureCount,
    int AnomalousUsageCountInRange,
    DateTime? LastCompletedGenerationAt);

public sealed record AdminProviderHealthDto(
    string Key,
    string Category,
    string Status,
    bool Enabled,
    bool Configured,
    int RecentSuccessCount,
    int RecentFailureCount,
    int? AverageLatencyMs,
    int RateLimitEventCount,
    int TimeoutEventCount,
    int QualityControlFailureCount,
    int RetryCount,
    int FallbackCount,
    bool FallbackTelemetryRecorded,
    decimal EstimatedProviderCostUsd,
    decimal ActualProviderCostUsd,
    DateTime? LastSuccessAt,
    DateTime? LastFailureAt,
    string? LastFailureCode,
    IReadOnlyList<AdminSanitizedGenerationFailureDto> RecentFailures);

public sealed record AdminSanitizedGenerationFailureDto(
    string JobType,
    string ErrorCode,
    DateTime OccurredAt);

public sealed record AdminMovieOperationsDto(
    int MovieJobCountInRange,
    IReadOnlyList<AdminCountBreakdownDto> JobsByStatus,
    int QueuedOrPendingCount,
    DateTime? OldestQueuedAt,
    double? OldestQueueAgeSeconds,
    double? AverageQueueAgeSeconds,
    int TotalRetryCount,
    int RetriedJobCount,
    int MaxRetryCount,
    IReadOnlyList<AdminCountBreakdownDto> FailuresByCode,
    int ProviderDisabledFailureCount,
    int QualityControlFailureCount,
    IReadOnlyList<AdminCountBreakdownDto> QualityControlByStatus,
    int MovieAssetCount,
    int MovieAssetWithStoredFileCount,
    int MovieAssetIngestionGapCount,
    int CompletedJobsWithoutAssetCount,
    int AccountingTransactionCount,
    int PendingAccountingCount,
    int MissingAccountingEvidenceCount,
    decimal EstimatedProviderCostUsd,
    decimal ActualProviderCostUsd,
    string ProviderStatus,
    bool ProviderEnabled,
    bool ProviderConfigured,
    int StuckJobCount,
    IReadOnlyList<AdminStuckMovieJobDto> StuckJobs);

public sealed record AdminStuckMovieJobDto(
    Guid JobId,
    string JobType,
    GenerationJobStatus Status,
    DateTime? ClaimExpiresAt,
    DateTime? StartedAt,
    int RetryCount,
    string? ErrorCode);

public sealed record AdminWorkerOperationsDto(
    int ConfiguredConcurrency,
    int ObservedWorkerCount,
    int HealthyWorkerCount,
    int StaleWorkerCount,
    IReadOnlyList<AdminWorkerStatusDto> Workers);

public sealed record AdminWorkerStatusDto(
    string WorkerId,
    string Status,
    DateTime StartedAt,
    DateTime LastSeenAt,
    DateTime? LastClaimedAt,
    DateTime? LastCompletedAt,
    Guid? ActiveJobId,
    int ConsecutiveIterationFailures,
    int WorkerConcurrency,
    bool IsStale);

public sealed record AdminOperationAuditDto(
    Guid Id,
    Guid ActorUserId,
    string Action,
    string TargetType,
    Guid? TargetId,
    string Outcome,
    string? Reason,
    DateTime CreatedAt);

public sealed record AdminJobRecoveryRequest(string Reason);

public sealed record AdminJobRecoveryResult(
    Guid JobId,
    GenerationJobStatus Status,
    int RetryCount,
    DateTime QueuedAt,
    string AuditAction);

public sealed record AdminOperationsFilter(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null);
