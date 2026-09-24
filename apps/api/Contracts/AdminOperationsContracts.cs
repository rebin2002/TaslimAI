namespace Taslim.Api.Contracts;

public sealed record AdminOperationsDashboardDto(
    AdminOperationsRangeDto Range,
    AdminGenerationOverviewDto Generation,
    AdminUsageOperationsDto Usage,
    AdminUsersAndWorkspacesDto UsersAndWorkspaces,
    AdminAssetsAndStorageDto AssetsAndStorage,
    AdminBillingOperationsDto Billing,
    AdminOperationalSignalsDto Signals);

public sealed record AdminOperationsRangeDto(DateTime FromUtc, DateTime ToUtc);

public sealed record AdminGenerationOverviewDto(
    int TotalJobsInRange,
    IReadOnlyList<AdminCountBreakdownDto> ByStatus,
    IReadOnlyList<AdminCountBreakdownDto> ByStudio,
    IReadOnlyList<AdminRecentFailureDto> RecentFailures,
    IReadOnlyList<AdminRunningJobDto> RunningJobs,
    int QueuedOrPendingCount);

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
    DateTime CreatedAt);

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
    string ConfiguredStorageProvider,
    bool PersistentStorageConfigured);

public sealed record AdminBillingOperationsDto(
    bool CustomerChargingEnabled,
    string ConfiguredProvider,
    bool PaymentProviderConfigured,
    IReadOnlyList<AdminSubscriptionBreakdownDto> Subscriptions,
    IReadOnlyList<AdminCountBreakdownDto> PaymentAttemptsByStatus,
    int PendingReconciliationCount);

public sealed record AdminSubscriptionBreakdownDto(string PlanCode, string Status, int Count);

public sealed record AdminOperationalSignalsDto(
    int RunningJobCount,
    int QueuedOrPendingJobCount,
    int RecentFailureCount,
    int AnomalousUsageCountInRange,
    DateTime? LastCompletedGenerationAt);

public sealed record AdminOperationsFilter(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null);
