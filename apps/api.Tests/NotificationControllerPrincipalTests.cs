using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Controllers;
using Taslim.Api.Notifications;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class NotificationControllerPrincipalTests
{
    [Fact]
    public async Task Malformed_authenticated_principal_is_rejected_before_notification_authorization()
    {
        var controller = new NotificationsController(null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "not-a-guid")],
                        authenticationType: "test")),
                },
            },
        };
        var workspaceId = Guid.NewGuid();
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.List(workspaceId),
            () => controller.UnreadCount(workspaceId, default),
            () => controller.MarkRead(Guid.NewGuid(), new NotificationReadRequest(workspaceId), default),
            () => controller.MarkAllRead(new NotificationReadAllRequest(workspaceId), default),
        };

        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }
}
