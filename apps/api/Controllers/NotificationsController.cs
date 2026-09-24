using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Notifications;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
public sealed class NotificationsController(INotificationService notifications) : ControllerBase
{
    [HttpGet("api/notifications")]
    public async Task<IActionResult> List([FromQuery] Guid workspaceId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        var result = await notifications.ListAsync(GetUserId(), new NotificationFilter(workspaceId, page, pageSize, unreadOnly), cancellationToken);
        return result is null ? Forbid() : Ok(result);
    }

    [HttpGet("api/notifications/unread-count")]
    public async Task<IActionResult> UnreadCount([FromQuery] Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await notifications.GetUnreadCountAsync(GetUserId(), workspaceId, cancellationToken);
        return result is null ? Forbid() : Ok(new { unreadCount = result.Value });
    }

    [HttpPost("api/notifications/{notificationId:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid notificationId, NotificationReadRequest request, CancellationToken cancellationToken)
    {
        var result = await notifications.MarkReadAsync(GetUserId(), notificationId, request.WorkspaceId, cancellationToken);
        return result ? Ok(new { read = true }) : Forbid();
    }

    [HttpPost("api/notifications/read-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(NotificationReadAllRequest request, CancellationToken cancellationToken)
    {
        var result = await notifications.MarkAllReadAsync(GetUserId(), request.WorkspaceId, cancellationToken);
        return result ? Ok(new { read = true }) : Forbid();
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
