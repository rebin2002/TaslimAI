using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Activity;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Taslim.Api.Projects;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
public sealed class ProjectsController(TaslimDbContext db, WorkspaceAccessService access) : ControllerBase
{
    private const int MaxProjectListPage = 10_000;
    private const int MaxProjectListPageSize = 100;

    [HttpGet("api/workspaces/{workspaceId:guid}/projects")]
    public async Task<IActionResult> List(
        Guid workspaceId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();
        var requestedStatus = string.Equals(status, ProjectStatuses.Archived, StringComparison.OrdinalIgnoreCase)
            ? ProjectStatuses.Archived : ProjectStatuses.Active;
        page = Math.Clamp(page, 1, MaxProjectListPage);
        pageSize = Math.Clamp(pageSize, 1, MaxProjectListPageSize);
        var projects = await db.Projects.AsNoTracking()
            .Where(project => project.WorkspaceId == workspaceId && project.Status == requestedStatus)
            .OrderByDescending(project => project.UpdatedAt)
            .ThenByDescending(project => project.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
        Response.Headers.ETag = ProjectRevision.FormatETag(project.UpdatedAt);
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
            .Where(file => file.ProjectId == projectId && file.WorkspaceId == project.WorkspaceId && file.Status != StoredFileStatus.Deleted)
            .Where(file => file.ConversationId == null || file.UserId == userId);
        var assets = db.Assets.AsNoTracking()
            .Where(asset => asset.ProjectId == projectId && asset.WorkspaceId == project.WorkspaceId && asset.Status == AssetStatus.Active);
        var conversations = db.Conversations.AsNoTracking()
            .Where(conversation => conversation.ProjectId == projectId && conversation.WorkspaceId == project.WorkspaceId && conversation.UserId == userId);
        // GATE-PRIV-04: these rows are rendered through ActivityContractMapper, the same projection
        // the Activity Center uses, so they must pass through the same creator-private fence. A
        // Research record stays with its creator even when it is project-scoped, because the record
        // carries the report title derived from that creator's private uploads; leaving this query
        // unfiltered disclosed another member's research record in counts.activity and
        // recentActivity while every other member-facing surface hid the same record.
        var activity = CreatorPrivacyFence.VisibleToMember(
            db.GenerationJobs.AsNoTracking()
                .Where(job => job.ProjectId == projectId && job.WorkspaceId == project.WorkspaceId),
            userId);

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
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        if (!await access.IsMemberAsync(GetUserId(), project.WorkspaceId, cancellationToken)) return Forbid();
        var type = ResolveType(request.Type);
        if (type is null) return ApiResults.Validation(this, "Choose a valid project type.");

        // The persisted UpdatedAt is the project's opaque revision stamp. When a client
        // declares the revision its form was built against with If-Match, a stale base is
        // refused before anything is touched; otherwise the revision observed a few lines
        // above still fences the write against a commit that lands mid-request.
        var baseRevision = project.UpdatedAt;
        var ifMatch = Request.Headers.TryGetValue("If-Match", out var ifMatchValue) ? ifMatchValue.ToString() : null;
        var wildcard = string.Equals(ifMatch?.Trim(), ProjectRevision.WildcardETag, StringComparison.Ordinal);
        // A concrete token means the client owns the base its edit was built on. The wildcard and
        // the header-less legacy form both mean "apply over whatever is there": that is a promise
        // about the write, not a licence to publish a stamp taken from the read above.
        var declaresRevision = false;
        if (!string.IsNullOrWhiteSpace(ifMatch) && !wildcard)
        {
            if (!ProjectRevision.TryParseETag(ifMatch, out var declaredRevision))
            {
                return ApiResults.Error(this, StatusCodes.Status400BadRequest, "INVALID_IF_MATCH",
                    "If-Match must be a revision token from this project's ETag header, or *.");
            }
            if (declaredRevision != baseRevision)
            {
                Response.Headers.ETag = ProjectRevision.FormatETag(baseRevision);
                return ApiResults.Error(this, StatusCodes.Status409Conflict, "PROJECT_REVISION_CONFLICT",
                    "Another edit reached this project first. Re-read it and apply your change again.");
            }
            declaresRevision = true;
        }

        var name = request.Name.Trim();
        var description = CleanDescription(request.Description);
        var instructions = CleanText(request.Instructions);
        var contextNotes = CleanText(request.ContextNotes);

        // One conditional UPDATE per attempt does the whole write, fenced on the revision the stamp
        // is derived from, so a commit that lands between the read above and this statement can
        // neither be walked over nor hand back an ETag that was already expired. What a moved row
        // means is the caller's semantics: a declared base is refused (re-aiming it would merge an
        // edit into content the client never saw), while an unconditional write — wildcard per
        // RFC 9110, or a legacy client sending no If-Match at all — re-targets the live revision
        // instead of failing a request that promised last-write-wins.
        var write = await ProjectRevision.WriteStampedAsync(
            db,
            projectId,
            baseRevision,
            declaresRevision ? RevisionFence.Declared : RevisionFence.LiveRow,
            (target, nextRevision) => target.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Name, name)
                .SetProperty(item => item.Description, description)
                .SetProperty(item => item.Instructions, instructions)
                .SetProperty(item => item.ContextNotes, contextNotes)
                .SetProperty(item => item.Type, type)
                .SetProperty(item => item.UpdatedAt, nextRevision),
                cancellationToken),
            cancellationToken);
        if (write.Outcome == RevisionWriteOutcome.Missing)
        {
            return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        }
        if (write.Outcome == RevisionWriteOutcome.Conflict)
        {
            Response.Headers.ETag = ProjectRevision.FormatETag(write.Revision);
            return ApiResults.Error(this, StatusCodes.Status409Conflict, "PROJECT_REVISION_CONFLICT",
                "Another edit reached this project first. Re-read it and apply your change again.");
        }

        var updated = await db.Projects.AsNoTracking().FirstAsync(item => item.Id == projectId, cancellationToken);
        Response.Headers.ETag = ProjectRevision.FormatETag(updated.UpdatedAt);
        return Ok(ToDto(updated));
    }

    [HttpPost("api/projects/{projectId:guid}/archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await FindAuthorizedProject(projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        var stamped = await StampStatusAsync(
            projectId,
            project.UpdatedAt,
            ProjectStatuses.Archived,
            DateTime.UtcNow,
            cancellationToken);
        if (stamped.Outcome == RevisionWriteOutcome.Missing) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        if (stamped.Outcome == RevisionWriteOutcome.Conflict)
        {
            Response.Headers.ETag = ProjectRevision.FormatETag(stamped.Revision);
            return ApiResults.Error(this, StatusCodes.Status409Conflict, "PROJECT_REVISION_CONFLICT",
                "Another edit reached this project first. Re-read it and apply your change again.");
        }
        // The stamp published here is the one the row actually carries after the
        // write, which is what makes it usable as the If-Match base of an open
        // draft. Success used to answer with the payload only, so a client that
        // fenced its edits had to re-read the row (or lose its binding) after every
        // archive, and a status flip looked like a revision it could not name.
        var committed = await db.Projects.AsNoTracking().FirstAsync(item => item.Id == projectId, cancellationToken);
        Response.Headers.ETag = ProjectRevision.FormatETag(committed.UpdatedAt);
        return Ok(ToDto(committed));
    }

    [HttpPost("api/projects/{projectId:guid}/restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await FindAuthorizedProject(projectId, cancellationToken);
        if (project is null) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        var stamped = await StampStatusAsync(
            projectId,
            project.UpdatedAt,
            ProjectStatuses.Active,
            null,
            cancellationToken);
        if (stamped.Outcome == RevisionWriteOutcome.Missing) return ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found.");
        if (stamped.Outcome == RevisionWriteOutcome.Conflict)
        {
            Response.Headers.ETag = ProjectRevision.FormatETag(stamped.Revision);
            return ApiResults.Error(this, StatusCodes.Status409Conflict, "PROJECT_REVISION_CONFLICT",
                "Another edit reached this project first. Re-read it and apply your change again.");
        }
        // The stamp published here is the one the row actually carries after the
        // write, which is what makes it usable as the If-Match base of an open
        // draft. Success used to answer with the payload only, so a client that
        // fenced its edits had to re-read the row (or lose its binding) after every
        // archive, and a status flip looked like a revision it could not name.
        var committed = await db.Projects.AsNoTracking().FirstAsync(item => item.Id == projectId, cancellationToken);
        Response.Headers.ETag = ProjectRevision.FormatETag(committed.UpdatedAt);
        return Ok(ToDto(committed));
    }

    /// <summary>
    /// Flips a project's status through the same stamped write the edit paths use. Archive and
    /// restore publish an ETag too, so they are held to the same rule as a PATCH: the stamp they
    /// publish must be ahead of whatever the row holds when the write lands, not merely ahead of
    /// the value read a few statements earlier. Assigning <c>DateTime.UtcNow</c> to the tracked
    /// entity satisfied neither — a commit that landed after the read was overwritten with a stamp
    /// that could be older or identical, which revalidated an ETag the API had already expired.
    /// Status itself keeps its last-write-wins behaviour: the fence only re-targets the revision,
    /// it never turns an archive into a conflict.
    /// </summary>
    private Task<ProjectRevisionWriteResult> StampStatusAsync(
        Guid projectId,
        DateTime observedRevision,
        string status,
        DateTime? archivedAt,
        CancellationToken cancellationToken) =>
        ProjectRevision.WriteStampedAsync(
            db,
            projectId,
            observedRevision,
            RevisionFence.LiveRow,
            (target, nextRevision) => target.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, status)
                .SetProperty(item => item.ArchivedAt, archivedAt)
                .SetProperty(item => item.UpdatedAt, nextRevision),
                cancellationToken),
            cancellationToken);

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
