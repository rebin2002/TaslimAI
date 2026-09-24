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
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty);
        var memberships = await db.WorkspaceMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Include(member => member.Workspace)
            .OrderBy(member => member.Workspace.Type)
            .ThenBy(member => member.Workspace.Name)
            .ToListAsync(cancellationToken);
        return Ok(memberships.Select(member => new WorkspaceSummaryDto(
            member.Workspace.Id,
            member.Workspace.Name,
            member.Workspace.Slug,
            member.Workspace.Type.ToString(),
            member.Role.ToString())));
    }

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
