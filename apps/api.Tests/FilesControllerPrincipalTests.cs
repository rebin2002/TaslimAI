using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Controllers;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class FilesControllerPrincipalTests
{
    [Fact]
    public async Task Malformed_authenticated_principal_is_rejected_before_file_access()
    {
        var controller = new FilesController(null!, null!, null!, null!)
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
        var file = new FormFile(new MemoryStream([0x01]), 0, 1, "file", "notes.txt");
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.Upload(workspaceId, file, null, null, default),
            () => controller.List(workspaceId, null, null, cancellationToken: default),
            () => controller.Get(Guid.NewGuid(), default),
            () => controller.Delete(Guid.NewGuid(), default),
        };

        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }
}
