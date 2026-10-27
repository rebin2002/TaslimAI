using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueControllerPrincipalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_authenticated_principal_returns_401_for_every_dialogue_action(string? identifier)
    {
        var claims = identifier is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, identifier) };
        var controller = new MovieDialogueController(new NeverCalledDialogueService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        claims,
                        authenticationType: "Synthetic")),
                },
            },
        };

        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.Get(Guid.NewGuid(), CancellationToken.None),
            () => controller.AddLine(Guid.NewGuid(), null!, CancellationToken.None),
            () => controller.QueueTake(Guid.NewGuid(), null!, CancellationToken.None),
            () => controller.Approve(Guid.NewGuid(), null!, CancellationToken.None),
            () => controller.Select(Guid.NewGuid(), CancellationToken.None),
        };

        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    private sealed class NeverCalledDialogueService : IMovieDialogueProductionService
    {
        public Task<MovieDialogueClipDto?> GetClipAsync(Guid userId, Guid clipId, CancellationToken cancellationToken) => throw new InvalidOperationException("Service must not be called.");
        public Task<MovieDialogueLineDto?> AddLineAsync(Guid userId, Guid clipId, MovieDialogueLineRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Service must not be called.");
        public Task<MovieDialogueTakeResponse?> QueueTakeAsync(Guid userId, Guid lineId, MovieDialogueTakeRequest request, string? idempotencyKey, CancellationToken cancellationToken) => throw new InvalidOperationException("Service must not be called.");
        public Task<MovieDialogueLineDto?> ApproveTakeAsync(Guid userId, Guid takeId, MovieDialogueTakeApprovalRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Service must not be called.");
        public Task<MovieDialogueLineDto?> SelectTakeAsync(Guid userId, Guid takeId, CancellationToken cancellationToken) => throw new InvalidOperationException("Service must not be called.");
    }
}
