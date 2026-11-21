using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieFinalAssemblyControllerPrincipalTests
{
    [Theory]
    [InlineData("queue")]
    [InlineData("get")]
    [InlineData("download")]
    public async Task Malformed_authenticated_principal_fails_closed_before_final_assembly_services(string action)
    {
        var controller = new MovieFinalAssemblyController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "not-a-guid")],
                        authenticationType: "Synthetic"))
                }
            }
        };

        IActionResult result = action switch
        {
            "queue" => await controller.Queue(Guid.NewGuid(), new MovieFinalAssemblyRequest(), CancellationToken.None),
            "get" => await controller.Get(Guid.NewGuid(), CancellationToken.None),
            _ => await controller.Download(Guid.NewGuid(), CancellationToken.None),
        };

        Assert.IsType<UnauthorizedResult>(result);
    }
}
