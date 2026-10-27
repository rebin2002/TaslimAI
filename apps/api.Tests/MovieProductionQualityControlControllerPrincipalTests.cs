using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Controllers;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionQualityControlControllerPrincipalTests
{
    [Fact]
    public async Task Malformed_authenticated_principal_is_rejected_before_quality_control_authorization()
    {
        var controller = new MovieProductionQualityControlController(null!, null!)
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

        var result = await controller.Preview(Guid.NewGuid(), null, default);

        Assert.IsType<UnauthorizedResult>(result);
    }
}
