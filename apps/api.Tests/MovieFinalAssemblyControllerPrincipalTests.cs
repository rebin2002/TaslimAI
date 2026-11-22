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
    [InlineData("queue", null)]
    [InlineData("queue", "")]
    [InlineData("queue", "not-a-guid")]
    [InlineData("queue", "00000000-0000-0000-0000-000000000000")]
    [InlineData("get", null)]
    [InlineData("get", "")]
    [InlineData("get", "not-a-guid")]
    [InlineData("get", "00000000-0000-0000-0000-000000000000")]
    [InlineData("download", null)]
    [InlineData("download", "")]
    [InlineData("download", "not-a-guid")]
    [InlineData("download", "00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_authenticated_principal_fails_closed_before_final_assembly_services(string action, string? identifier)
    {
        var claims = identifier is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, identifier) };
        var controller = new MovieFinalAssemblyController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        claims,
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
