using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
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

        var page = ApiPagination.NormalizePage(filter.Page);
        var pageSize = ApiPagination.NormalizePageSize(filter.PageSize);
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
            .ThenByDescending(job => job.Id)
            .Skip(ApiPagination.GetOffset(page, pageSize))
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
            var readState = new ActivityReadState { Id = Guid.NewGuid(), UserId = userId, GenerationJobId = jobId, ReadAt = DateTime.UtcNow };
            db.ActivityReadStates.Add(readState);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The unique (UserId, GenerationJobId) index is the concurrency
                // boundary. A concurrent request may have marked the same job
                // read after the pre-check; treat that winner as an idempotent
                // success while preserving unrelated persistence failures.
                db.Entry(readState).State = EntityState.Detached;
                if (await db.ActivityReadStates.AsNoTracking().AnyAsync(
                        read => read.UserId == userId && read.GenerationJobId == jobId,
                        cancellationToken))
                    return true;
                throw;
            }
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
        var readStates = unreadJobIds.Select(jobId => new ActivityReadState
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GenerationJobId = jobId,
            ReadAt = readAt,
        }).ToArray();
        db.ActivityReadStates.AddRange(readStates);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // As with a single read, a concurrent read-all may have won the
            // unique-key race. Only convert the exception to success when all
            // rows this request intended to create now exist; unrelated errors
            // and partial writes remain failures.
            foreach (var readState in readStates)
                db.Entry(readState).State = EntityState.Detached;

            var persistedCount = await db.ActivityReadStates.AsNoTracking()
                .CountAsync(read => read.UserId == userId && unreadJobIds.Contains(read.GenerationJobId), cancellationToken);
            if (persistedCount == unreadJobIds.Count) return true;
            throw;
        }
        return true;
    }
}
