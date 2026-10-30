namespace Taslim.Api.Usage;

/// <summary>
/// A server-classified capability. Callers must derive this from the API route/job type;
/// it must never be accepted from a query string or request body.
/// </summary>
public enum PublicFreeTierCapability
{
    Unspecified = 0,
    TextChat = 1,
    LowCostResearchLookup = 2,
    ImageGeneration = 20,
    VideoGeneration = 21,
    VoiceGeneration = 22,
    MusicGeneration = 23,
    ShortFormClipGeneration = 24,
    BackgroundProviderJob = 25,
    OtherProviderRoute = 100,
}

public enum PublicFreeTierPlan
{
    Unknown = 0,
    Free = 1,
    Paid = 2,
}

public enum PublicFreeTierProviderOutcome
{
    Succeeded = 1,
    Failed = 2,
    CancelledAfterDispatch = 3,
}

/// <summary>
/// Conservative defaults for the public free tier. Enabled is intentionally false: a
/// deployment must explicitly enable this only after the durable gate is integrated and
/// independently reviewed. Turning the option off makes Evaluate deny, never allow.
/// </summary>
public sealed class PublicFreeTierPolicyOptions
{
    public bool Enabled { get; set; }
    public bool RequireConfirmedEmail { get; set; } = true;
    public int MaxChatMessagesPerUserPerUtcDay { get; set; } = 3;
    public int MaxChatMessagesPerUserPerUtcWeek { get; set; } = 10;
    public int MaxInputTokens { get; set; } = 2_000;
    public int MaxOutputTokens { get; set; } = 512;
    public int MaxExposureSnapshotAgeSeconds { get; set; } = 15;

    // All amounts are estimated provider exposure in USD, including live reservations.
    public decimal MaxProviderSpendPerUserPerUtcDayUsd { get; set; } = 0.10m;
    public decimal MaxProviderSpendPerUserPerUtcMonthUsd { get; set; } = 0.50m;
    public decimal MaxProviderSpendPerWorkspacePerUtcDayUsd { get; set; } = 0.25m;
    public decimal MaxProviderSpendPerWorkspacePerUtcMonthUsd { get; set; } = 1.00m;
    public decimal MaxGlobalProviderSpendPerUtcDayUsd { get; set; } = 1.00m;
    public decimal MaxGlobalProviderSpendPerUtcMonthUsd { get; set; } = 5.00m;

    /// <summary>
    /// Only text chat is approved by default. Add another capability only after its pricing,
    /// token/resource bounds, routes, and tests have been reviewed. Media/jobs remain denied.
    /// </summary>
    public HashSet<PublicFreeTierCapability> AllowedCapabilities { get; set; } =
        new() { PublicFreeTierCapability.TextChat };
}

/// <summary>
/// Internal admission facts. The integration layer must create these from the authenticated
/// principal, canonical plan/billing state, and server-side workspace authorization. Never
/// bind this record directly from an HTTP payload.
/// </summary>
public sealed record PublicFreeTierAdmissionRequest(
    bool IsAuthenticated,
    Guid UserId,
    Guid WorkspaceId,
    bool AccountIsActive,
    bool EmailIsConfirmed,
    bool WorkspaceIsActive,
    bool IsWorkspaceMember,
    PublicFreeTierPlan Plan,
    PublicFreeTierCapability Capability,
    int InputTokens,
    int MaxOutputTokens,
    decimal? EstimatedProviderCostUsd,
    string Currency,
    string? PricingVersion,
    string IdempotencyKey);

/// <summary>
/// Snapshot returned only by the internal durable-ledger adapter. Every spend value includes
/// settled provider exposure and outstanding reservations. User message/spend counters are
/// aggregated across all of that user's workspaces; global counters include every free user.
/// Window keys and capture time are UTC. This snapshot is for policy evaluation only: the
/// adapter must re-read and reserve atomically in the same durable transaction.
/// </summary>
public sealed record PublicFreeTierExposureSnapshot(
    Guid UserId,
    Guid WorkspaceId,
    DateTimeOffset CapturedAtUtc,
    DateOnly UtcDay,
    DateOnly UtcIsoWeekStart,
    DateOnly UtcMonthStart,
    int UserMessagesForUtcDay,
    int UserMessagesForUtcWeek,
    decimal UserProviderExposureForUtcDayUsd,
    decimal UserProviderExposureForUtcMonthUsd,
    decimal WorkspaceProviderExposureForUtcDayUsd,
    decimal WorkspaceProviderExposureForUtcMonthUsd,
    decimal GlobalProviderExposureForUtcDayUsd,
    decimal GlobalProviderExposureForUtcMonthUsd,
    int UnknownProviderCostEntries);

/// <summary>HTTP-facing result for a future API adapter; no customer credit balance is involved.</summary>
public sealed record PublicFreeTierAdmissionDecision(
    bool Allowed,
    int StatusCode,
    string Code,
    string Message,
    DateTimeOffset? RetryAfterUtc = null)
{
    public static PublicFreeTierAdmissionDecision Allow() =>
        new(true, 200, "PUBLIC_FREE_TIER_ADMITTED", "The request is within the public free-tier policy.");

    public static PublicFreeTierAdmissionDecision Deny(
        int statusCode,
        string code,
        string message,
        DateTimeOffset? retryAfterUtc = null) =>
        new(false, statusCode, code, message, retryAfterUtc);
}

/// <summary>
/// Pure admission rules for public free access. This class is deliberately not wired into
/// Program.cs or provider routes in this isolated owner-safe change; do not treat it as an
/// active production firewall until every integration item in the handoff document is done.
/// </summary>
public static class PublicFreeTierPolicy
{
    public static PublicFreeTierAdmissionDecision Evaluate(
        PublicFreeTierAdmissionRequest request,
        PublicFreeTierExposureSnapshot snapshot,
        PublicFreeTierPolicyOptions options,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);

        nowUtc = nowUtc.ToUniversalTime();
        if (!options.Enabled)
            return Deny(503, "PUBLIC_FREE_TIER_FIREWALL_DISABLED", "Public free AI is temporarily unavailable.");
        if (!IsValid(options))
            return Deny(503, "PUBLIC_FREE_TIER_POLICY_MISCONFIGURED", "Public free AI is temporarily unavailable.");

        if (!request.IsAuthenticated)
            return Deny(401, "PUBLIC_FREE_TIER_AUTHENTICATION_REQUIRED", "Sign in to use free AI.");
        if (request.UserId == Guid.Empty || request.WorkspaceId == Guid.Empty || !request.AccountIsActive)
            return Deny(403, "PUBLIC_FREE_TIER_ACCOUNT_NOT_ELIGIBLE", "This account is not eligible for free AI.");
        if (options.RequireConfirmedEmail && !request.EmailIsConfirmed)
            return Deny(403, "PUBLIC_FREE_TIER_EMAIL_CONFIRMATION_REQUIRED", "Confirm your email before using free AI.");
        if (!request.WorkspaceIsActive || !request.IsWorkspaceMember)
            return Deny(403, "PUBLIC_FREE_TIER_WORKSPACE_ACCESS_DENIED", "This workspace is not available to your account.");
        if (request.Plan != PublicFreeTierPlan.Free)
            return Deny(403, "PUBLIC_FREE_TIER_PLAN_REQUIRED", "This access policy is for authenticated free-plan users only.");
        if (!Enum.IsDefined(request.Capability)
            || request.Capability == PublicFreeTierCapability.Unspecified
            || request.Capability is not (PublicFreeTierCapability.TextChat or PublicFreeTierCapability.LowCostResearchLookup)
            || !options.AllowedCapabilities.Contains(request.Capability))
            return Deny(403, "PUBLIC_FREE_TIER_CAPABILITY_NOT_ENABLED", "This AI capability is not available on the free plan.");

        if (request.InputTokens < 0 || request.MaxOutputTokens < 1)
            return Deny(400, "PUBLIC_FREE_TIER_INVALID_TOKEN_BUDGET", "The request token budget is invalid.");
        if (request.InputTokens > options.MaxInputTokens || request.MaxOutputTokens > options.MaxOutputTokens)
            return Deny(413, "PUBLIC_FREE_TIER_TOKEN_LIMIT_EXCEEDED", "This request exceeds the free-plan token limit.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            return Deny(400, "PUBLIC_FREE_TIER_IDEMPOTENCY_KEY_REQUIRED", "The request could not be admitted safely.");

        if (!string.Equals(request.Currency?.Trim(), "USD", StringComparison.OrdinalIgnoreCase)
            || !request.EstimatedProviderCostUsd.HasValue
            || request.EstimatedProviderCostUsd.Value <= 0m
            || string.IsNullOrWhiteSpace(request.PricingVersion))
            return Deny(503, "PUBLIC_FREE_TIER_PRICE_UNAVAILABLE", "This AI request is unavailable because its provider cost cannot be verified.");

        if (snapshot.UserId != request.UserId || snapshot.WorkspaceId != request.WorkspaceId)
            return Deny(503, "PUBLIC_FREE_TIER_ACCOUNTING_SCOPE_MISMATCH", "Free AI usage could not be verified safely.");

        var snapshotAge = nowUtc - snapshot.CapturedAtUtc.ToUniversalTime();
        if (snapshotAge < TimeSpan.Zero || snapshotAge > TimeSpan.FromSeconds(options.MaxExposureSnapshotAgeSeconds))
            return Deny(503, "PUBLIC_FREE_TIER_ACCOUNTING_SNAPSHOT_STALE", "Free AI usage could not be verified safely.");

        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        var weekStart = UtcIsoWeekStart(today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        if (snapshot.UtcDay != today || snapshot.UtcIsoWeekStart != weekStart || snapshot.UtcMonthStart != monthStart)
            return Deny(503, "PUBLIC_FREE_TIER_ACCOUNTING_WINDOW_MISMATCH", "Free AI usage could not be verified safely.");

        if (snapshot.UnknownProviderCostEntries > 0)
            return Deny(503, "PUBLIC_FREE_TIER_UNKNOWN_PROVIDER_EXPOSURE", "Free AI is paused while provider usage is reconciled.");
        if (snapshot.UnknownProviderCostEntries < 0
            || snapshot.UserMessagesForUtcDay < 0
            || snapshot.UserMessagesForUtcWeek < 0
            || HasNegativeExposure(snapshot))
            return Deny(503, "PUBLIC_FREE_TIER_ACCOUNTING_DATA_INVALID", "Free AI usage could not be verified safely.");

        if (snapshot.UserMessagesForUtcDay >= options.MaxChatMessagesPerUserPerUtcDay)
            return Deny(429, "PUBLIC_FREE_TIER_DAILY_MESSAGE_LIMIT", "You have reached today's free AI message limit.", NextUtcDay(nowUtc));
        if (snapshot.UserMessagesForUtcWeek >= options.MaxChatMessagesPerUserPerUtcWeek)
            return Deny(429, "PUBLIC_FREE_TIER_WEEKLY_MESSAGE_LIMIT", "You have reached this week's free AI message limit.", NextUtcIsoWeek(nowUtc));

        var estimatedCost = request.EstimatedProviderCostUsd.Value;
        var userDaily = CheckCap(snapshot.UserProviderExposureForUtcDayUsd, estimatedCost, options.MaxProviderSpendPerUserPerUtcDayUsd,
            "PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT", "Today's free AI budget has been reached.", NextUtcDay(nowUtc));
        if (userDaily is not null) return userDaily;
        var userMonthly = CheckCap(snapshot.UserProviderExposureForUtcMonthUsd, estimatedCost, options.MaxProviderSpendPerUserPerUtcMonthUsd,
            "PUBLIC_FREE_TIER_USER_MONTHLY_SPEND_LIMIT", "This month's free AI budget has been reached.", NextUtcMonth(nowUtc));
        if (userMonthly is not null) return userMonthly;
        var workspaceDaily = CheckCap(snapshot.WorkspaceProviderExposureForUtcDayUsd, estimatedCost, options.MaxProviderSpendPerWorkspacePerUtcDayUsd,
            "PUBLIC_FREE_TIER_WORKSPACE_DAILY_SPEND_LIMIT", "Today's free AI workspace budget has been reached.", NextUtcDay(nowUtc));
        if (workspaceDaily is not null) return workspaceDaily;
        var workspaceMonthly = CheckCap(snapshot.WorkspaceProviderExposureForUtcMonthUsd, estimatedCost, options.MaxProviderSpendPerWorkspacePerUtcMonthUsd,
            "PUBLIC_FREE_TIER_WORKSPACE_MONTHLY_SPEND_LIMIT", "This month's free AI workspace budget has been reached.", NextUtcMonth(nowUtc));
        if (workspaceMonthly is not null) return workspaceMonthly;
        var globalDaily = CheckCap(snapshot.GlobalProviderExposureForUtcDayUsd, estimatedCost, options.MaxGlobalProviderSpendPerUtcDayUsd,
            "PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT", "Free AI is temporarily paused after today's provider budget was reached.", NextUtcDay(nowUtc));
        if (globalDaily is not null) return globalDaily;
        var globalMonthly = CheckCap(snapshot.GlobalProviderExposureForUtcMonthUsd, estimatedCost, options.MaxGlobalProviderSpendPerUtcMonthUsd,
            "PUBLIC_FREE_TIER_GLOBAL_MONTHLY_SPEND_LIMIT", "Free AI is temporarily paused after this month's provider budget was reached.", NextUtcMonth(nowUtc));
        if (globalMonthly is not null) return globalMonthly;

        return PublicFreeTierAdmissionDecision.Allow();
    }

    private static bool IsValid(PublicFreeTierPolicyOptions options) =>
        options.MaxChatMessagesPerUserPerUtcDay > 0
        && options.MaxChatMessagesPerUserPerUtcWeek >= options.MaxChatMessagesPerUserPerUtcDay
        && options.MaxInputTokens > 0
        && options.MaxOutputTokens > 0
        && options.MaxExposureSnapshotAgeSeconds > 0
        && options.MaxProviderSpendPerUserPerUtcDayUsd > 0m
        && options.MaxProviderSpendPerUserPerUtcMonthUsd > 0m
        && options.MaxProviderSpendPerWorkspacePerUtcDayUsd > 0m
        && options.MaxProviderSpendPerWorkspacePerUtcMonthUsd > 0m
        && options.MaxGlobalProviderSpendPerUtcDayUsd > 0m
        && options.MaxGlobalProviderSpendPerUtcMonthUsd > 0m
        && options.AllowedCapabilities is not null
        && !options.AllowedCapabilities.Contains(PublicFreeTierCapability.Unspecified);

    private static bool HasNegativeExposure(PublicFreeTierExposureSnapshot snapshot) =>
        snapshot.UserProviderExposureForUtcDayUsd < 0m
        || snapshot.UserProviderExposureForUtcMonthUsd < 0m
        || snapshot.WorkspaceProviderExposureForUtcDayUsd < 0m
        || snapshot.WorkspaceProviderExposureForUtcMonthUsd < 0m
        || snapshot.GlobalProviderExposureForUtcDayUsd < 0m
        || snapshot.GlobalProviderExposureForUtcMonthUsd < 0m;

    private static PublicFreeTierAdmissionDecision? CheckCap(
        decimal usedUsd,
        decimal requestedUsd,
        decimal limitUsd,
        string code,
        string message,
        DateTimeOffset retryAfterUtc)
    {
        // Subtraction avoids decimal addition overflow when the accounting store is corrupt.
        if (usedUsd > limitUsd || requestedUsd > limitUsd - usedUsd)
            return Deny(429, code, message, retryAfterUtc);
        return null;
    }

    private static PublicFreeTierAdmissionDecision Deny(
        int statusCode,
        string code,
        string message,
        DateTimeOffset? retryAfterUtc = null) =>
        PublicFreeTierAdmissionDecision.Deny(statusCode, code, message, retryAfterUtc);

    private static DateOnly UtcIsoWeekStart(DateOnly day)
    {
        var daysSinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-daysSinceMonday);
    }

    private static DateTimeOffset NextUtcDay(DateTimeOffset nowUtc) =>
        new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);

    private static DateTimeOffset NextUtcIsoWeek(DateTimeOffset nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        return new DateTimeOffset(UtcIsoWeekStart(today).AddDays(7).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private static DateTimeOffset NextUtcMonth(DateTimeOffset nowUtc) =>
        new DateTimeOffset(new DateOnly(nowUtc.Year, nowUtc.Month, 1).AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

/// <summary>
/// Provider-cost reservation boundary for the eventual server integration. Implementations
/// must be durable and cross-replica atomic: read all applicable user/workspace/global windows,
/// evaluate this policy, and persist the idempotent reservation in one transaction before a
/// provider call. An in-memory limiter or a caller-supplied snapshot is not sufficient.
/// </summary>
public interface IPublicFreeTierSpendLedger
{
    Task<PublicFreeTierReservationResult> TryReserveBeforeProviderCallAsync(
        PublicFreeTierAdmissionRequest request,
        CancellationToken cancellationToken = default);

    Task<PublicFreeTierSettlementResult> RecordProviderOutcomeAsync(
        Guid reservationId,
        string completionIdempotencyKey,
        PublicFreeTierProviderOutcome outcome,
        bool actualCostKnown,
        decimal? actualProviderCostUsd,
        CancellationToken cancellationToken = default);

    /// <summary>Release estimated exposure only when the provider request is proven never dispatched.</summary>
    Task<PublicFreeTierSettlementResult> ReleaseUndispatchedAsync(
        Guid reservationId,
        string completionIdempotencyKey,
        CancellationToken cancellationToken = default);
}

public sealed record PublicFreeTierReservationResult(
    bool Reserved,
    Guid? ReservationId,
    bool IsIdempotentReplay,
    PublicFreeTierAdmissionDecision Decision);

public sealed record PublicFreeTierSettlementResult(
    bool Applied,
    bool IsIdempotentReplay,
    bool ReconciliationRequired,
    string Code);
