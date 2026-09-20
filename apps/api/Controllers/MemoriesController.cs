using System.Security.Claims;
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
public sealed class MemoriesController(TaslimDbContext db, WorkspaceAccessService access) : ControllerBase
{
    [HttpGet("api/workspaces/{workspaceId:guid}/memories")]
    public async Task<IActionResult> List(Guid workspaceId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();

        var memories = await db.PersonalMemories.AsNoTracking()
            .Where(memory => memory.WorkspaceId == workspaceId && memory.UserId == userId && memory.IsActive)
            .OrderByDescending(memory => memory.UpdatedAt)
            .ThenByDescending(memory => memory.CreatedAt)
            .Select(memory => ToDto(memory))
            .ToListAsync(cancellationToken);
        return Ok(memories);
    }

    [HttpPost("api/workspaces/{workspaceId:guid}/memories")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid workspaceId, CreatePersonalMemoryRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        if (!PersonalMemoryCategories.Initial.Contains(request.Category.Trim())) return ApiResults.Validation(this, "Choose a valid memory category.");
        var userId = GetUserId();
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return Forbid();

        var now = DateTime.UtcNow;
        var memory = new PersonalMemory
        {
            Id = Guid.NewGuid(), UserId = userId, WorkspaceId = workspaceId,
            Category = NormalizeCategory(request.Category), Title = request.Title.Trim(), Content = request.Content.Trim(),
            Source = PersonalMemorySources.Manual, IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        db.PersonalMemories.Add(memory);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(List), new { workspaceId }, ToDto(memory));
    }

    [HttpPatch("api/memories/{memoryId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid memoryId, UpdatePersonalMemoryRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        if (!PersonalMemoryCategories.Initial.Contains(request.Category.Trim())) return ApiResults.Validation(this, "Choose a valid memory category.");
        var memory = await db.PersonalMemories.FirstOrDefaultAsync(item => item.Id == memoryId && item.UserId == GetUserId() && item.IsActive, cancellationToken);
        if (memory is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "MEMORY_NOT_FOUND", "Memory not found.");
        if (!await access.IsMemberAsync(GetUserId(), memory.WorkspaceId, cancellationToken)) return Forbid();

        memory.Category = NormalizeCategory(request.Category);
        memory.Title = request.Title.Trim();
        memory.Content = request.Content.Trim();
        memory.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(memory));
    }

    [HttpDelete("api/memories/{memoryId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid memoryId, CancellationToken cancellationToken)
    {
        var memory = await db.PersonalMemories.FirstOrDefaultAsync(item => item.Id == memoryId && item.UserId == GetUserId() && item.IsActive, cancellationToken);
        if (memory is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "MEMORY_NOT_FOUND", "Memory not found.");
        if (!await access.IsMemberAsync(GetUserId(), memory.WorkspaceId, cancellationToken)) return Forbid();

        db.PersonalMemories.Remove(memory);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string NormalizeCategory(string category) => PersonalMemoryCategories.Initial.First(item => string.Equals(item, category.Trim(), StringComparison.OrdinalIgnoreCase));
    private static PersonalMemoryDto ToDto(PersonalMemory memory) => new(memory.Id, memory.WorkspaceId, memory.Category, memory.Title, memory.Content, memory.Source, memory.IsActive, memory.CreatedAt, memory.UpdatedAt);
    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
