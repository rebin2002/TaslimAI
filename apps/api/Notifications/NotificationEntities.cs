using System;

namespace Taslim.Api.Notifications;

public static class NotificationTypes
{
    public const string GenerationCompleted = "generation.completed";
    public const string GenerationFailed = "generation.failed";
    public const string GenerationAttention = "generation.attention";
    public const string BillingPaymentFailed = "billing.payment_failed";
}

public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? AssetId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string DeduplicationKey { get; set; } = string.Empty;
    public string? ResourceTitle { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public sealed record NotificationListDto(
    IReadOnlyList<NotificationDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    int UnreadCount);

public sealed record NotificationDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid? GenerationJobId,
    Guid? AssetId,
    string Type,
    string? ResourceTitle,
    DateTime CreatedAt,
    DateTime? ReadAt,
    bool IsRead,
    string Destination);

public sealed record NotificationFilter(Guid WorkspaceId, int Page = 1, int PageSize = 20, bool UnreadOnly = false);

public sealed record NotificationReadRequest(Guid WorkspaceId);
public sealed record NotificationReadAllRequest(Guid WorkspaceId);

public interface INotificationEventWriter
{
    Task CreateGenerationCompletedAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task CreateGenerationFailedAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task CreateGenerationAttentionAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task CreateBillingPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, CancellationToken cancellationToken = default);
}
