using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/search")]
public sealed class GlobalSearchController(TaslimDbContext db) : ControllerBase
{
    private const int DefaultLimitPerType = 8;
    private const int MaxLimitPerType = 12;
    private const int MaxQueryLength = 100;

    [HttpGet]
    public async Task<ActionResult<GlobalSearchResponseDto>> Search(
        [FromQuery(Name = "q")] string? query,
        [FromQuery] int limit = DefaultLimitPerType,
        CancellationToken cancellationToken = default)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length > MaxQueryLength)
            normalized = normalized[..MaxQueryLength];

        if (normalized.Length == 0)
            return Ok(new GlobalSearchResponseDto(string.Empty, 0, []));

        var search = normalized.ToLowerInvariant();
        var perType = Math.Clamp(limit, 1, MaxLimitPerType);
        var userId = GetUserId();
        var workspaceIds = await db.WorkspaceMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => member.WorkspaceId)
            .ToListAsync(cancellationToken);

        if (workspaceIds.Count == 0)
            return Ok(new GlobalSearchResponseDto(normalized, 0, []));

        var groups = new List<GlobalSearchGroupDto>(capacity: 5);

        var projects = await db.Projects.AsNoTracking()
            .Where(project => workspaceIds.Contains(project.WorkspaceId)
                && (project.Name.ToLower().Contains(search)
                    || (project.Description != null && project.Description.ToLower().Contains(search))))
            .OrderByDescending(project => project.UpdatedAt)
            .ThenByDescending(project => project.Id)
            .Take(perType)
            .Select(project => new GlobalSearchResultDto(
                GlobalSearchResultTypes.Project,
                project.Id,
                project.Name,
                project.Description,
                project.Id,
                null,
                null,
                null,
                project.Status,
                project.Type,
                project.CreatedAt,
                project.UpdatedAt))
            .ToListAsync(cancellationToken);
        AddGroup(groups, GlobalSearchResultTypes.Project, projects);

        // Conversations are private to their creator even when their workspace is shared.
        var conversations = await db.Conversations.AsNoTracking()
            .Where(conversation => workspaceIds.Contains(conversation.WorkspaceId)
                && conversation.UserId == userId
                && (conversation.Title.ToLower().Contains(search)
                    || db.ChatMessages.Any(message => message.ConversationId == conversation.Id
                        && (message.Role == ChatMessageRole.User || message.Role == ChatMessageRole.Assistant)
                        && message.Content.ToLower().Contains(search))))
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ThenByDescending(conversation => conversation.Id)
            .Take(perType)
            .Select(conversation => new GlobalSearchResultDto(
                GlobalSearchResultTypes.Conversation,
                conversation.Id,
                conversation.Title,
                null,
                conversation.ProjectId,
                conversation.Id,
                null,
                conversation.Project == null ? null : conversation.Project.Name,
                conversation.Status.ToString(),
                conversation.LastMessageAt.HasValue ? "message" : "conversation",
                conversation.CreatedAt,
                conversation.UpdatedAt))
            .ToListAsync(cancellationToken);
        AddGroup(groups, GlobalSearchResultTypes.Conversation, conversations);

        var assets = await db.Assets.AsNoTracking()
            .Where(asset => workspaceIds.Contains(asset.WorkspaceId)
                && (asset.Name.ToLower().Contains(search)
                    || (asset.Description != null && asset.Description.ToLower().Contains(search))))
            .OrderByDescending(asset => asset.UpdatedAt)
            .ThenByDescending(asset => asset.Id)
            .Take(perType)
            .Select(asset => new GlobalSearchResultDto(
                GlobalSearchResultTypes.Asset,
                asset.Id,
                asset.Name,
                asset.Description,
                asset.ProjectId,
                null,
                asset.Id,
                asset.Project == null ? null : asset.Project.Name,
                asset.Status.ToString(),
                asset.AssetType,
                asset.CreatedAt,
                asset.UpdatedAt))
            .ToListAsync(cancellationToken);
        AddGroup(groups, GlobalSearchResultTypes.Asset, assets);

        // Unscoped files remain user-private; project files are workspace-authorized.
        var files = await db.StoredFiles.AsNoTracking()
            .Where(file => workspaceIds.Contains(file.WorkspaceId)
                && file.Status != StoredFileStatus.Deleted
                && (file.ProjectId != null || file.UserId == userId)
                && (file.OriginalFileName.ToLower().Contains(search)
                    || (file.ExtractedText != null && file.ExtractedText.ToLower().Contains(search))))
            .OrderByDescending(file => file.CreatedAt)
            .ThenByDescending(file => file.Id)
            .Take(perType)
            .Select(file => new GlobalSearchResultDto(
                GlobalSearchResultTypes.File,
                file.Id,
                file.OriginalFileName,
                null,
                file.ProjectId,
                file.ConversationId,
                null,
                file.Project == null ? null : file.Project.Name,
                file.Status.ToString(),
                file.Extension,
                file.CreatedAt,
                file.ProcessedAt ?? file.CreatedAt))
            .ToListAsync(cancellationToken);
        AddGroup(groups, GlobalSearchResultTypes.File, files);

        // Search user-authored titles and prompts, but expose only safe activity metadata.
        var generationRows = await db.GenerationJobs.AsNoTracking()
            .Where(job => workspaceIds.Contains(job.WorkspaceId)
                && ((job.Title != null && job.Title.ToLower().Contains(search))
                    || job.JobType.ToLower().Contains(search)
                    || job.InputJson.ToLower().Contains(search)
                    || (job.Project != null && job.Project.Name.ToLower().Contains(search))))
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Take(perType)
            .Select(job => new
            {
                job.Id,
                job.Title,
                job.ProjectId,
                AssetId = db.Assets.Where(asset => asset.SourceGenerationJobId == job.Id).Select(asset => (Guid?)asset.Id).FirstOrDefault(),
                ProjectName = job.Project == null ? null : job.Project.Name,
                Status = job.Status.ToString(),
                job.JobType,
                job.CreatedAt,
                UpdatedAt = job.CompletedAt ?? job.StartedAt ?? job.QueuedAt ?? job.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        var generation = generationRows.Select(job => new GlobalSearchResultDto(
            GlobalSearchResultTypes.Generation,
            job.Id,
            job.Title ?? HumanizeJobType(job.JobType),
            null,
            job.ProjectId,
            null,
            job.AssetId,
            job.ProjectName,
            job.Status,
            HumanizeJobType(job.JobType),
            job.CreatedAt,
            job.UpdatedAt)).ToList();
        AddGroup(groups, GlobalSearchResultTypes.Generation, generation);

        return Ok(new GlobalSearchResponseDto(normalized, groups.Sum(group => group.Count), groups));
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));

    private static void AddGroup(List<GlobalSearchGroupDto> groups, string type, IReadOnlyList<GlobalSearchResultDto> items)
    {
        if (items.Count > 0) groups.Add(new GlobalSearchGroupDto(type, items.Count, items));
    }

    private static string HumanizeJobType(string jobType) => jobType switch
    {
        GenerationJobTypes.ImageGenerate => "Image generation",
        GenerationJobTypes.DocumentGenerate => "Document generation",
        GenerationJobTypes.PresentationGenerate => "Presentation generation",
        GenerationJobTypes.ResearchGenerate => "Research generation",
        GenerationJobTypes.SocialGenerate => "Social generation",
        GenerationJobTypes.MovieQuickGenerate or GenerationJobTypes.MovieClipGenerate or GenerationJobTypes.MovieAssembly => "Movie generation",
        GenerationJobTypes.MusicGenerate => "Music generation",
        GenerationJobTypes.VoiceGenerate => "Voice generation",
        GenerationJobTypes.SystemTest => "Generation activity",
        _ => "Generation activity",
    };
}
