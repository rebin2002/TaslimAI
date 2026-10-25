using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Authorization;
using Taslim.Api.Billing;
using Taslim.Api.Domain;

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
        var membership = await access.GetMembershipAsync(userId, workspaceId, cancellationToken);
        if (membership is null || membership.Role is not (WorkspaceRole.Owner or WorkspaceRole.Admin)) return Forbid();
        return Ok(await billing.GetAccountAsync(workspaceId, cancellationToken));
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
