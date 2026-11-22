using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class UpscalingControllerPrincipalTests
{
    [Fact]
    public async Task Malformed_authenticated_principal_is_rejected_before_upscaling_authorization()
    {
        var controller = new UpscalingController(null!)
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
        var jobId = Guid.NewGuid();
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.Create(new CreateUpscalingJobRequest { WorkspaceId = workspaceId, SourceAssetId = Guid.NewGuid(), TargetResolution = "1080p" }, default),
            () => controller.Get(jobId, default),
            () => controller.List(workspaceId, cancellationToken: default),
            () => controller.Retry(jobId, default),
            () => controller.Cancel(jobId, default),
            () => controller.ReviewQuality(jobId, new ReviewUpscalingQualityRequest { Approved = true }, default),
        };

        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }
}
