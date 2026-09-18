using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces")]
public sealed class WorkspacesController(TaslimDbContext db, WorkspaceAccessService access) : ControllerBase
{
    [HttpGet("{workspaceId:guid}")]
    public async Task<IActionResult> Get(Guid workspaceId, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty);
        var member = await access.GetMembershipAsync(userId, workspaceId, cancellationToken);
        if (member is null) return Forbid();
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(item => item.Id == workspaceId, cancellationToken);
        if (workspace is null) return ApiResults.Error(this, 404, "WORKSPACE_NOT_FOUND", "Workspace not found.");
        return Ok(new WorkspaceSummaryDto(workspace.Id, workspace.Name, workspace.Slug, workspace.Type.ToString(), member.Role.ToString()));
    }
}
