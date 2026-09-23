using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Activity;

public interface IActivityCenterService
{
    Task<ActivityListDto?> ListAsync(Guid userId, ActivityFilter filter, CancellationToken cancellationToken = default);
    Task<int?> GetUnreadCountAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<bool> MarkReadAsync(Guid userId, Guid jobId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<bool> MarkAllReadAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default);
}

public sealed class ActivityCenterService(TaslimDbContext db, WorkspaceAccessService access) : IActivityCenterService
{
    public async Task<ActivityListDto?> ListAsync(Guid userId, ActivityFilter filter, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, filter.WorkspaceId, cancellationToken)) return null;

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var query = db.GenerationJobs.AsNoTracking().Where(job => job.WorkspaceId == filter.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim();
            query = status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                ? query.Where(job => job.Status == GenerationJobStatus.Succeeded)
                : status.Equals("Queued", StringComparison.OrdinalIgnoreCase)
                    ? query.Where(job => job.Status == GenerationJobStatus.Pending || job.Status == GenerationJobStatus.Queued)
                    : Enum.TryParse<GenerationJobStatus>(status, true, out var parsed)
                        ? query.Where(job => job.Status == parsed)
                        : query.Where(job => false);
        }
        if (!string.IsNullOrWhiteSpace(filter.JobType))
            query = query.Where(job => job.JobType == filter.JobType);

        var totalCount = await query.CountAsync(cancellationToken);
        var unreadCount = await db.GenerationJobs.AsNoTracking()
            .Where(job => job.WorkspaceId == filter.WorkspaceId)
            .Where(job => !db.ActivityReadStates.Any(read => read.UserId == userId && read.GenerationJobId == job.Id))
            .CountAsync(cancellationToken);
        var jobs = await query
            .OrderByDescending(job => job.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(job => new
            {
                Job = job,
                IsRead = db.ActivityReadStates.Any(read => read.UserId == userId && read.GenerationJobId == job.Id),
                AssetId = db.Assets.Where(asset => asset.SourceGenerationJobId == job.Id).Select(asset => (Guid?)asset.Id).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return new ActivityListDto(
            jobs.Select(item => ActivityContractMapper.ToDto(item.Job, item.IsRead, item.AssetId)).ToArray(),
            page,
            pageSize,
            totalCount,
            (int)Math.Ceiling(totalCount / (double)pageSize),
            unreadCount);
    }

    public async Task<int?> GetUnreadCountAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return null;
        return await db.GenerationJobs.AsNoTracking()
            .Where(job => job.WorkspaceId == workspaceId)
            .Where(job => !db.ActivityReadStates.Any(read => read.UserId == userId && read.GenerationJobId == job.Id))
            .CountAsync(cancellationToken);
    }

    public async Task<bool> MarkReadAsync(Guid userId, Guid jobId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return false;
        var jobExists = await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.Id == jobId && job.WorkspaceId == workspaceId, cancellationToken);
        if (!jobExists) return false;
        var alreadyRead = await db.ActivityReadStates.AnyAsync(read => read.UserId == userId && read.GenerationJobId == jobId, cancellationToken);
        if (!alreadyRead)
        {
            db.ActivityReadStates.Add(new ActivityReadState { Id = Guid.NewGuid(), UserId = userId, GenerationJobId = jobId, ReadAt = DateTime.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> MarkAllReadAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, workspaceId, cancellationToken)) return false;
        var unreadJobIds = await db.GenerationJobs.AsNoTracking()
            .Where(job => job.WorkspaceId == workspaceId)
            .Where(job => !db.ActivityReadStates.Any(read => read.UserId == userId && read.GenerationJobId == job.Id))
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);
        if (unreadJobIds.Count == 0) return true;
        var readAt = DateTime.UtcNow;
        db.ActivityReadStates.AddRange(unreadJobIds.Select(jobId => new ActivityReadState { Id = Guid.NewGuid(), UserId = userId, GenerationJobId = jobId, ReadAt = readAt }));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
