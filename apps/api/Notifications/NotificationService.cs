using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Notifications;

public interface INotificationService : INotificationEventWriter
{
    Task<NotificationListDto?> ListAsync(Guid userId, NotificationFilter filter, CancellationToken cancellationToken = default);
    Task<int?> GetUnreadCountAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<bool> MarkReadAsync(Guid userId, Guid notificationId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<bool> MarkAllReadAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default);
}

public sealed class NotificationService(TaslimDbContext db, WorkspaceAccessService access) : INotificationService
{
    public async Task<NotificationListDto?> ListAsync(Guid userId, NotificationFilter filter, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, filter.WorkspaceId, cancellationToken)) return null;
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var query = db.Notifications.AsNoTracking().Where(item => item.UserId == userId && item.WorkspaceId == filter.WorkspaceId);
        if (filter.UnreadOnly) query = query.Where(item => item.ReadAt == null);
        var totalCount = await query.CountAsync(cancellationToken);
        var unreadCount = await db.Notifications.AsNoTracking()
            .Where(item => item.UserId == userId && item.WorkspaceId == filter.WorkspaceId && item.ReadAt == null)
            .CountAsync(cancellationToken);
        var notifications = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new NotificationListDto(
            notifications.Select(ToDto).ToArray(),
            page,
            pageSize,
            totalCount,
            (int)Math.Ceiling(totalCount / (double)pageSize),
            unreadCount);
    }

    public async Task<int?> GetUnreadCountAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return null;
        return await db.Notifications.AsNoTracking()
            .Where(item => item.UserId == userId && item.WorkspaceId == workspaceId && item.ReadAt == null)
            .CountAsync(cancellationToken);
    }

    public async Task<bool> MarkReadAsync(Guid userId, Guid notificationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return false;
        var notification = await db.Notifications.FirstOrDefaultAsync(item => item.Id == notificationId && item.UserId == userId && item.WorkspaceId == workspaceId, cancellationToken);
        if (notification is null) return false;
        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> MarkAllReadAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return false;
        await db.Notifications.Where(item => item.UserId == userId && item.WorkspaceId == workspaceId && item.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReadAt, DateTime.UtcNow), cancellationToken);
        return true;
    }

    public Task CreateGenerationCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        CreateForJobAsync(jobId, NotificationTypes.GenerationCompleted, cancellationToken);

    public Task CreateGenerationFailedAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        CreateForJobAsync(jobId, NotificationTypes.GenerationFailed, cancellationToken);

    public Task CreateGenerationAttentionAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        CreateForJobAsync(jobId, NotificationTypes.GenerationAttention, cancellationToken);

    public async Task CreateBillingPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, CancellationToken cancellationToken = default)
    {
        var owners = await db.WorkspaceMembers.AsNoTracking()
            .Where(member => member.WorkspaceId == workspaceId && (member.Role == WorkspaceRole.Owner || member.Role == WorkspaceRole.Admin))
            .Select(member => member.UserId).ToListAsync(cancellationToken);
        var key = $"{NotificationTypes.BillingPaymentFailed}:{paymentAttemptId:N}";
        foreach (var userId in owners)
        {
            await AddIfMissingAsync(new Notification
            {
                Id = Guid.NewGuid(), UserId = userId, WorkspaceId = workspaceId,
                Type = NotificationTypes.BillingPaymentFailed, DeduplicationKey = key,
                ResourceTitle = "Payment", CreatedAt = DateTime.UtcNow,
            }, cancellationToken);
        }
    }

    private async Task CreateForJobAsync(Guid jobId, string type, CancellationToken cancellationToken)
    {
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null) return;
        var assetId = type == NotificationTypes.GenerationCompleted
            ? await db.Assets.AsNoTracking().Where(item => item.SourceGenerationJobId == jobId).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken)
            : null;
        await AddIfMissingAsync(new Notification
        {
            Id = Guid.NewGuid(), UserId = job.CreatedByUserId, WorkspaceId = job.WorkspaceId,
            ProjectId = job.ProjectId, GenerationJobId = job.Id, AssetId = assetId,
            Type = type, DeduplicationKey = $"{type}:{job.Id:N}",
            ResourceTitle = string.IsNullOrWhiteSpace(job.Title) ? ActivityContractMapper.DefaultTitle(job.JobType) : job.Title.Trim(),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken);
    }

    private async Task AddIfMissingAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (await db.Notifications.AnyAsync(item => item.DeduplicationKey == notification.DeduplicationKey && item.UserId == notification.UserId, cancellationToken)) return;
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static NotificationDto ToDto(Notification item) => new(
        item.Id, item.WorkspaceId, item.ProjectId, item.GenerationJobId, item.AssetId,
        item.Type, item.ResourceTitle, item.CreatedAt, item.ReadAt, item.ReadAt is not null,
        Destination(item));

    private static string Destination(Notification item) => item.Type switch
    {
        NotificationTypes.GenerationCompleted when item.AssetId.HasValue => "/assets",
        NotificationTypes.GenerationCompleted when item.GenerationJobId.HasValue => $"/activity?jobId={item.GenerationJobId.Value:N}",
        NotificationTypes.GenerationAttention or NotificationTypes.GenerationFailed when item.GenerationJobId.HasValue => $"/activity?jobId={item.GenerationJobId.Value:N}",
        NotificationTypes.BillingPaymentFailed => "/account/billing",
        _ => "/notifications",
    };
}
