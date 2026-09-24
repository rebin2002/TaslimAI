using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
public sealed class ProjectsController(TaslimDbContext db, WorkspaceAccessService access) : ControllerBase
{
    [HttpGet("api/workspaces/{workspaceId:guid}/projects")]
    public async Task<IActionResult> List(Guid workspaceId, [FromQuery] string? status, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();
        var requestedStatus = string.Equals(status, ProjectStatuses.Archived, StringComparison.OrdinalIgnoreCase)
            ? ProjectStatuses.Archived : ProjectStatuses.Active;
        var projects = await db.Projects.AsNoTracking()
            .Where(project => project.WorkspaceId == workspaceId && project.Status == requestedStatus)
            .OrderByDescending(project => project.UpdatedAt)
            .Select(project => ToDto(project))
            .ToListAsync(cancellationToken);
        return Ok(projects);
    }

    [HttpPost("api/workspaces/{workspaceId:guid}/projects")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid workspaceId, CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();
        var type = ResolveType(request.Type);
        if (type is null) return ApiResults.Validation(this, "Choose a valid project type.");
        var now = DateTime.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = request.Name.Trim(),
            Description = CleanDescription(request.Description), Type = type,
            Instructions = CleanText(request.Instructions), ContextNotes = CleanText(request.ContextNotes),
            Status = ProjectStatuses.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { projectId = project.Id }, ToDto(project));
    }

    [HttpGet("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        if (!await access.IsMemberAsync(GetUserId(), project.WorkspaceId, cancellationToken)) return Forbid();
        return Ok(ToDto(project));
    }

    [HttpGet("api/projects/{projectId:guid}/overview")]
    public async Task<IActionResult> Overview(Guid projectId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");

        var membership = await access.GetMembershipAsync(userId, project.WorkspaceId, cancellationToken);
        if (membership is null) return Forbid();

        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(item => item.Id == project.WorkspaceId, cancellationToken);
        if (workspace is null) return ApiResults.Error(this, 404, "WORKSPACE_NOT_FOUND", "Workspace not found.");

        var files = db.StoredFiles.AsNoTracking()
            .Where(file => file.ProjectId == projectId && file.WorkspaceId == project.WorkspaceId && file.Status != StoredFileStatus.Deleted);
        var assets = db.Assets.AsNoTracking()
            .Where(asset => asset.ProjectId == projectId && asset.WorkspaceId == project.WorkspaceId && asset.Status == AssetStatus.Active);
        var conversations = db.Conversations.AsNoTracking()
            .Where(conversation => conversation.ProjectId == projectId && conversation.WorkspaceId == project.WorkspaceId && conversation.UserId == userId);
        var activity = db.GenerationJobs.AsNoTracking()
            .Where(job => job.ProjectId == projectId && job.WorkspaceId == project.WorkspaceId);

        var counts = new ProjectOverviewCountsDto(
            await files.CountAsync(cancellationToken),
            await assets.CountAsync(cancellationToken),
            await conversations.CountAsync(cancellationToken),
            await activity.CountAsync(cancellationToken));

        var recentConversations = await conversations
            .OrderByDescending(item => item.UpdatedAt)
            .Take(8)
            .Select(item => new ConversationDto(item.Id, item.WorkspaceId, item.ProjectId, item.Title, item.Status.ToString(), item.CreatedAt, item.UpdatedAt, item.LastMessageAt))
            .ToListAsync(cancellationToken);

        var recentActivity = await activity
            .OrderByDescending(item => item.CreatedAt)
            .Take(8)
            .Select(item => new
            {
                Job = item,
                IsRead = db.ActivityReadStates.Any(read => read.UserId == userId && read.GenerationJobId == item.Id),
                AssetId = db.Assets.Where(asset => asset.SourceGenerationJobId == item.Id).Select(asset => (Guid?)asset.Id).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var workspaceDto = new WorkspaceSummaryDto(workspace.Id, workspace.Name, workspace.Slug, workspace.Type.ToString(), membership.Role.ToString());
        return Ok(new ProjectOverviewDto(
            ToDto(project),
            workspaceDto,
            counts,
            recentConversations,
            recentActivity.Select(item => ActivityContractMapper.ToDto(item.Job, item.IsRead, item.AssetId)).ToArray()));
    }

    [HttpPatch("api/projects/{projectId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        if (!await access.IsMemberAsync(GetUserId(), project.WorkspaceId, cancellationToken)) return Forbid();
        var type = ResolveType(request.Type);
        if (type is null) return ApiResults.Validation(this, "Choose a valid project type.");
        project.Name = request.Name.Trim();
        project.Description = CleanDescription(request.Description);
        project.Instructions = CleanText(request.Instructions);
        project.ContextNotes = CleanText(request.ContextNotes);
        project.Type = type;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(project));
    }

    [HttpPost("api/projects/{projectId:guid}/archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await FindAuthorizedProject(projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        project.Status = ProjectStatuses.Archived;
        project.ArchivedAt = DateTime.UtcNow;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(project));
    }

    [HttpPost("api/projects/{projectId:guid}/restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await FindAuthorizedProject(projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        project.Status = ProjectStatuses.Active;
        project.ArchivedAt = null;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(project));
    }

    private async Task<Project?> FindAuthorizedProject(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        return project is not null && await access.IsMemberAsync(GetUserId(), project.WorkspaceId, cancellationToken) ? project : null;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
    private static string? ResolveType(string? type) => string.IsNullOrWhiteSpace(type) ? ProjectTypes.General : ProjectTypes.Initial.FirstOrDefault(item => string.Equals(item, type.Trim(), StringComparison.OrdinalIgnoreCase));
    private static string? CleanDescription(string? description) => CleanText(description);
    private static string? CleanText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static ProjectDto ToDto(Project project) => new(project.Id, project.WorkspaceId, project.Name, project.Description, project.Instructions, project.ContextNotes, project.Type, project.Status, project.CreatedAt, project.UpdatedAt, project.ArchivedAt);
}
