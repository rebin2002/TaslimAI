using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Authorization;
using Taslim.Api.Billing;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/billing")]
public sealed class BillingController(WorkspaceAccessService access, IBillingAccountService billing) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid workspaceId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();
        return Ok(await billing.GetAccountAsync(workspaceId, cancellationToken));
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
