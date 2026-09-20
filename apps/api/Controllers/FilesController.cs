using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class FilesController(
    TaslimDbContext db,
    WorkspaceAccessService access,
    FileProcessingService processing,
    ILogger<FilesController> logger) : ControllerBase
{
    [HttpPost("workspaces/{workspaceId:guid}/files")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(Guid workspaceId, [FromForm] IFormFile? file, [FromForm] Guid? projectId, [FromForm] Guid? conversationId, CancellationToken cancellationToken)
    {
        if (file is null) return ApiResults.Validation(this, "Choose a file to upload.");
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();
        if (!await ValidateScopeAsync(workspaceId, userId, projectId, conversationId, cancellationToken)) return ApiResults.Error(this, StatusCodes.Status404NotFound, "FILE_SCOPE_NOT_FOUND", "The selected project or conversation was not found.");

        try
        {
            var stored = await processing.UploadAsync(workspaceId, userId, projectId, conversationId, file, cancellationToken);
            return CreatedAtAction(nameof(Get), new { fileId = stored.Id }, FileDtoMapper.ToStoredFileDto(stored));
        }
        catch (FileUploadValidationException exception)
        {
            return ApiResults.Validation(this, exception.Message);
        }
        catch (FileStorageUnavailableException)
        {
            logger.LogWarning("File upload rejected because persistent storage is not configured. WorkspaceId={WorkspaceId}", workspaceId);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "FILE_STORAGE_UNAVAILABLE", "File storage is not configured yet.");
        }
    }

    [HttpGet("workspaces/{workspaceId:guid}/files")]
    public async Task<IActionResult> List(Guid workspaceId, [FromQuery] Guid? projectId, [FromQuery] Guid? conversationId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();
        var files = await db.StoredFiles.AsNoTracking()
            .Where(file => file.WorkspaceId == workspaceId && file.Status != StoredFileStatus.Deleted && (!projectId.HasValue || file.ProjectId == projectId) && (!conversationId.HasValue || file.ConversationId == conversationId))
            .Where(file => (file.ProjectId != null || file.ConversationId != null) || file.UserId == userId)
            .OrderByDescending(file => file.CreatedAt)
            .Select(file => FileDtoMapper.ToStoredFileDto(file))
            .ToListAsync(cancellationToken);
        return Ok(files);
    }

    [HttpGet("files/{fileId:guid}")]
    public async Task<IActionResult> Get(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(item => item.Id == fileId && item.Status != StoredFileStatus.Deleted, cancellationToken);
        if (file is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "FILE_NOT_FOUND", "File not found.");
        if (!await CanAccessAsync(file, cancellationToken)) return Forbid();
        return Ok(FileDtoMapper.ToStoredFileDto(file));
    }

    [HttpDelete("files/{fileId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.StoredFiles.FirstOrDefaultAsync(item => item.Id == fileId && item.Status != StoredFileStatus.Deleted, cancellationToken);
        if (file is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "FILE_NOT_FOUND", "File not found.");
        if (!await CanAccessAsync(file, cancellationToken)) return Forbid();
        try
        {
            await processing.DeleteAsync(file, cancellationToken);
            return NoContent();
        }
        catch (FileStorageUnavailableException)
        {
            logger.LogWarning("File deletion rejected because persistent storage is unavailable. FileId={FileId}; WorkspaceId={WorkspaceId}", file.Id, file.WorkspaceId);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "FILE_STORAGE_UNAVAILABLE", "The file could not be deleted because storage is unavailable.");
        }
    }

    private async Task<bool> ValidateScopeAsync(Guid workspaceId, Guid userId, Guid? projectId, Guid? conversationId, CancellationToken cancellationToken)
    {
        if (projectId is not null && !await db.Projects.AnyAsync(project => project.Id == projectId && project.WorkspaceId == workspaceId, cancellationToken)) return false;
        if (conversationId is not null && !await db.Conversations.AnyAsync(conversation => conversation.Id == conversationId && conversation.WorkspaceId == workspaceId && conversation.UserId == userId, cancellationToken)) return false;
        if (projectId is not null && conversationId is not null && !await db.Conversations.AnyAsync(conversation => conversation.Id == conversationId && conversation.ProjectId == projectId, cancellationToken)) return false;
        return true;
    }

    private async Task<bool> CanAccessAsync(StoredFile file, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, file.WorkspaceId, cancellationToken)) return false;
        if (file.ConversationId is not null || file.ProjectId is null) return file.UserId == userId;
        return true;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
