using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Activity;
using Taslim.Api.Contracts;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
public sealed class ActivityController(IActivityCenterService activity) : ControllerBase
{
    [HttpGet("api/activity")]
    public async Task<IActionResult> List(
        [FromQuery] Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? jobType = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await activity.ListAsync(userId, new ActivityFilter(workspaceId, page, pageSize, status, jobType), cancellationToken);
        return result is null ? Forbid() : Ok(result);
    }

    [HttpGet("api/activity/unread-count")]
    public async Task<IActionResult> UnreadCount([FromQuery] Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await activity.GetUnreadCountAsync(userId, workspaceId, cancellationToken);
        return result is null ? Forbid() : Ok(new { unreadCount = result.Value });
    }

    [HttpPost("api/activity/{jobId:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid jobId, ActivityReadRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await activity.MarkReadAsync(userId, jobId, request.WorkspaceId, cancellationToken);
        return result ? Ok(new { read = true }) : Forbid();
    }

    [HttpPost("api/activity/read-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(ActivityReadAllRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await activity.MarkAllReadAsync(userId, request.WorkspaceId, cancellationToken);
        return result ? Ok(new { read = true }) : Forbid();
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out userId);
}
