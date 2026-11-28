using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Controllers;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionQualityControlControllerPrincipalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_authenticated_principal_is_rejected_before_quality_control_authorization(string? identifier)
    {
        var controller = new MovieProductionQualityControlController(null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        identifier is null ? [] : [new Claim(ClaimTypes.NameIdentifier, identifier)],
                        authenticationType: "test")),
                },
            },
        };

        var result = await controller.Preview(Guid.NewGuid(), null, default);

        Assert.IsType<UnauthorizedResult>(result);
    }
}
