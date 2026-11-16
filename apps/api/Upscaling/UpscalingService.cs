using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Upscaling;

public sealed class UpscalingJobOptions
{
    public int MaxRetryCount { get; set; } = 2;
}

public sealed class UpscalingValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class UpscalingForbiddenException : Exception;

public interface IUpscalingJobService
{
    Task<UpscalingJob> CreateAsync(
        Guid userId,
        CreateUpscalingJobRequest request,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null,
        string? requestId = null);

    Task<UpscalingJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    Task<UpscalingJobListDto?> ListAsync(Guid userId, UpscalingJobFilter filter, CancellationToken cancellationToken = default);
    Task<UpscalingJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    Task<UpscalingJob?> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    Task<UpscalingAttempt?> BeginExecutionAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task CompleteAsync(Guid jobId, Guid attemptId, Guid outputAssetId, string outputProvenanceJson, CancellationToken cancellationToken = default);
    Task FailAsync(Guid jobId, Guid attemptId, string failureCode, bool retryable, CancellationToken cancellationToken = default);
    Task<UpscalingJob?> ReviewQualityAsync(Guid userId, Guid jobId, ReviewUpscalingQualityRequest request, CancellationToken cancellationToken = default);
}

public sealed class UpscalingJobService(
    TaslimDbContext db,
    WorkspaceAccessService access,
    IOptions<UpscalingJobOptions> options) : IUpscalingJobService
{
    private readonly UpscalingJobOptions settings = options.Value;

    public async Task<UpscalingJob> CreateAsync(
        Guid userId,
        CreateUpscalingJobRequest request,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null,
        string? requestId = null)
    {
        if (!UpscalingTargetResolutions.TryParse(request.TargetResolution, out var targetResolution))
            throw new UpscalingValidationException(UpscalingJobErrorCodes.TargetResolutionUnsupported, "Choose 1080p, 2K, or 4K.");
        if (!await access.IsMemberAsync(userId, request.WorkspaceId, cancellationToken))
            throw new UpscalingForbiddenException();
        if (request.ProjectId.HasValue && !await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == request.WorkspaceId, cancellationToken))
            throw new UpscalingValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");

        var source = await db.Assets
            .Include(asset => asset.StoredFile)
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.Id == request.SourceAssetId && asset.WorkspaceId == request.WorkspaceId, cancellationToken);
        if (source is null)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.SourceAssetNotFound, "The selected source asset could not be found.");
        if (source.AssetType is not (AssetTypes.Image or AssetTypes.Video))
            throw new UpscalingValidationException(UpscalingJobErrorCodes.SourceAssetUnsupported, "The selected source asset cannot be upscaled.");
        if (source.StoredFileId is null || source.StoredFile is null || source.StoredFile.Status != StoredFileStatus.Ready)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.SourceAssetUnavailable, "The selected source asset is not ready.");

        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedTitle = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        var fingerprint = ComputeRequestFingerprint(
            request.WorkspaceId,
            request.ProjectId,
            request.SourceAssetId,
            source.StoredFileId.Value,
            source.UpdatedAt,
            targetResolution,
            normalizedTitle);
        if (normalizedKey is not null)
        {
            var existing = await db.UpscalingJobs.AsNoTracking().FirstOrDefaultAsync(
                job => job.CreatedByUserId == userId && job.IdempotencyKey == normalizedKey,
                cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new UpscalingValidationException(UpscalingJobErrorCodes.IdempotencyKeyReused, "This idempotency key was already used for a different upscaling request.");
                return await GetRequiredAsync(existing.Id, cancellationToken);
            }
        }

        var now = DateTime.UtcNow;
        var job = new UpscalingJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            CreatedByUserId = userId,
            SourceAssetId = source.Id,
            TargetResolution = targetResolution,
            Status = UpscalingJobStatus.Pending,
            Title = normalizedTitle,
            IdempotencyKey = normalizedKey,
            RequestFingerprint = fingerprint,
            SourceProvenanceJson = JsonSerializer.Serialize(new
            {
                assetId = source.Id,
                storedFileId = source.StoredFileId,
                assetType = source.AssetType,
                contentType = source.StoredFile.ContentType,
                sizeBytes = source.StoredFile.SizeBytes,
                assetUpdatedAt = source.UpdatedAt,
            }),
            RetryCount = 0,
            MaxRetryCount = Math.Clamp(settings.MaxRetryCount, 0, 10),
            ProgressPercent = 0,
            CreatedAt = now,
        };
        db.UpscalingJobs.Add(job);
        await using var transaction = normalizedKey is null ? null : await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException) when (normalizedKey is not null)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            db.Entry(job).State = EntityState.Detached;
            var existing = await db.UpscalingJobs.AsNoTracking().FirstOrDefaultAsync(
                item => item.CreatedByUserId == userId && item.IdempotencyKey == normalizedKey,
                cancellationToken);
            if (existing is null) throw;
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new UpscalingValidationException(UpscalingJobErrorCodes.IdempotencyKeyReused, "This idempotency key was already used for a different upscaling request.");
            return await GetRequiredAsync(existing.Id, cancellationToken);
        }
    }

    public async Task<UpscalingJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await Query().FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        return job is not null && await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken) ? job : null;
    }

    public async Task<UpscalingJobListDto?> ListAsync(Guid userId, UpscalingJobFilter filter, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, filter.WorkspaceId, cancellationToken)) return null;
        var page = ApiPagination.NormalizePage(filter.Page);
        var pageSize = ApiPagination.NormalizePageSize(filter.PageSize);
        var query = db.UpscalingJobs.AsNoTracking().Where(job => job.WorkspaceId == filter.WorkspaceId);
        if (filter.Status.HasValue) query = query.Where(job => job.Status == filter.Status.Value);
        if (!string.IsNullOrWhiteSpace(filter.TargetResolution))
        {
            if (!UpscalingTargetResolutions.TryParse(filter.TargetResolution, out var target))
                throw new UpscalingValidationException(UpscalingJobErrorCodes.TargetResolutionUnsupported, "Choose 1080p, 2K, or 4K.");
            query = query.Where(job => job.TargetResolution == target);
        }
        var totalCount = await query.CountAsync(cancellationToken);
        var jobs = await query
            .Include(job => job.QualityHandoffs)
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Skip(ApiPagination.GetOffset(page, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new UpscalingJobListDto(jobs.Select(UpscalingContractMapper.ToDto).ToArray(), page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    public async Task<UpscalingJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await Query(tracking: true).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken)) return null;
        if (job.Status is not (UpscalingJobStatus.Failed or UpscalingJobStatus.QualityControlRejected or UpscalingJobStatus.Cancelled))
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "Only a failed, rejected, or cancelled job can be retried.");
        var latestAttempt = job.Attempts.OrderByDescending(item => item.AttemptNumber).FirstOrDefault();
        if (job.Status == UpscalingJobStatus.Failed && latestAttempt is not null && !latestAttempt.Retryable)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "This failure is not eligible for retry.");
        if (job.RetryCount >= job.MaxRetryCount)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.RetryLimitReached, "This upscaling job has reached its retry limit.");

        job.RetryCount++;
        job.Status = UpscalingJobStatus.Queued;
        job.QueuedAt = DateTime.UtcNow;
        job.StartedAt = null;
        job.QualityControlAt = null;
        job.CompletedAt = null;
        job.FailedAt = null;
        job.CancelledAt = null;
        job.OutputAssetId = null;
        job.OutputProvenanceJson = null;
        job.LastErrorCode = null;
        job.LastErrorMessage = null;
        job.ProgressPercent = 0;
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<UpscalingJob?> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await Query(tracking: true).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken)) return null;
        if (job.Status is not (UpscalingJobStatus.Pending or UpscalingJobStatus.Queued))
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "This upscaling job can no longer be cancelled.");
        job.Status = UpscalingJobStatus.Cancelled;
        job.LastErrorCode = UpscalingJobErrorCodes.Cancelled;
        job.LastErrorMessage = "The upscaling job was cancelled.";
        job.CancelledAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<UpscalingAttempt?> BeginExecutionAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.UpscalingJobs.FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.Status is not (UpscalingJobStatus.Pending or UpscalingJobStatus.Queued)) return null;
        var now = DateTime.UtcNow;
        var attempt = new UpscalingAttempt
        {
            Id = Guid.NewGuid(),
            UpscalingJobId = job.Id,
            AttemptNumber = job.RetryCount + 1,
            IdempotencyKey = $"upscaling:{job.Id:N}:attempt:{job.RetryCount + 1}",
            Status = UpscalingAttemptStatus.Started,
            StartedAt = now,
        };
        job.Status = UpscalingJobStatus.Running;
        job.StartedAt = now;
        job.ProgressPercent = 1;
        job.CurrentAttemptId = attempt.Id;
        job.LastErrorCode = null;
        job.LastErrorMessage = null;
        db.UpscalingAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task CompleteAsync(Guid jobId, Guid attemptId, Guid outputAssetId, string outputProvenanceJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputProvenanceJson) || outputProvenanceJson.Length > 20_000)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.OutputAssetInvalid, "Output provenance is invalid.");
        var job = await db.UpscalingJobs
            .Include(item => item.Attempts)
            .FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.Status != UpscalingJobStatus.Running || job.CurrentAttemptId != attemptId)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "The upscaling job is not executing.");
        var output = await db.Assets.AsNoTracking().FirstOrDefaultAsync(item => item.Id == outputAssetId && item.WorkspaceId == job.WorkspaceId, cancellationToken);
        if (output is null || output.StoredFileId is null || output.AssetType is not (AssetTypes.Image or AssetTypes.Video))
            throw new UpscalingValidationException(UpscalingJobErrorCodes.OutputAssetInvalid, "The upscaling output asset is invalid.");
        var attempt = job.Attempts.SingleOrDefault(item => item.Id == attemptId);
        if (attempt is null || attempt.Status != UpscalingAttemptStatus.Started)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "The upscaling attempt is no longer active.");

        var now = DateTime.UtcNow;
        attempt.Status = UpscalingAttemptStatus.Succeeded;
        attempt.CompletedAt = now;
        job.Status = UpscalingJobStatus.QualityControlPending;
        job.OutputAssetId = outputAssetId;
        job.OutputProvenanceJson = outputProvenanceJson;
        job.ProgressPercent = 100;
        job.QualityControlAt = now;
        job.LastErrorCode = UpscalingJobErrorCodes.QualityControlPending;
        job.LastErrorMessage = "The upscaled output is ready for quality review.";
        db.UpscalingQualityHandoffs.Add(new UpscalingQualityHandoff
        {
            Id = Guid.NewGuid(),
            UpscalingJobId = job.Id,
            SourceAssetId = job.SourceAssetId,
            OutputAssetId = outputAssetId,
            TargetResolution = job.TargetResolution,
            Status = UpscalingQualityStatus.Pending,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(Guid jobId, Guid attemptId, string failureCode, bool retryable, CancellationToken cancellationToken = default)
    {
        var job = await db.UpscalingJobs.Include(item => item.Attempts).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.Status != UpscalingJobStatus.Running || job.CurrentAttemptId != attemptId)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "The upscaling job is not executing.");
        var attempt = job.Attempts.SingleOrDefault(item => item.Id == attemptId);
        if (attempt is null || attempt.Status != UpscalingAttemptStatus.Started)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "The upscaling attempt is no longer active.");
        var safeCode = string.IsNullOrWhiteSpace(failureCode) ? "UPSCALING_EXECUTION_FAILED" : failureCode.Trim()[..Math.Min(100, failureCode.Trim().Length)];
        var now = DateTime.UtcNow;
        attempt.Status = UpscalingAttemptStatus.Failed;
        attempt.FailureCode = safeCode;
        attempt.Retryable = retryable;
        attempt.CompletedAt = now;
        job.Status = UpscalingJobStatus.Failed;
        job.LastErrorCode = safeCode;
        job.LastErrorMessage = "The upscaling job could not be completed.";
        job.FailedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<UpscalingJob?> ReviewQualityAsync(Guid userId, Guid jobId, ReviewUpscalingQualityRequest request, CancellationToken cancellationToken = default)
    {
        var job = await Query(tracking: true).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || !await access.IsMemberAsync(userId, job.WorkspaceId, cancellationToken)) return null;
        if (job.Status != UpscalingJobStatus.QualityControlPending)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "This upscaling job is not awaiting quality review.");
        var handoff = job.QualityHandoffs.OrderByDescending(item => item.CreatedAt).FirstOrDefault(item => item.Status is UpscalingQualityStatus.Pending or UpscalingQualityStatus.InReview)
            ?? throw new UpscalingValidationException(UpscalingJobErrorCodes.InvalidLifecycleTransition, "The quality handoff is no longer available.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var now = DateTime.UtcNow;
        handoff.Status = request.Approved ? UpscalingQualityStatus.Approved : UpscalingQualityStatus.Rejected;
        handoff.ReviewNote = note;
        handoff.ReviewedByUserId = userId;
        handoff.ReviewedAt = now;
        if (request.Approved)
        {
            job.Status = UpscalingJobStatus.Completed;
            job.CompletedAt = now;
            job.LastErrorCode = null;
            job.LastErrorMessage = null;
        }
        else
        {
            job.Status = UpscalingJobStatus.QualityControlRejected;
            job.LastErrorCode = UpscalingJobErrorCodes.QualityControlRejected;
            job.LastErrorMessage = "The upscaled output did not pass quality review.";
            job.FailedAt = now;
            var attempt = job.Attempts.OrderByDescending(item => item.AttemptNumber).FirstOrDefault();
            if (attempt is not null) attempt.Status = UpscalingAttemptStatus.QualityControlRejected;
        }
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private IQueryable<UpscalingJob> Query(bool tracking = false)
    {
        var query = db.UpscalingJobs
            .Include(job => job.QualityHandoffs)
            .Include(job => job.Attempts)
            .AsSplitQuery()
            .AsQueryable();
        return tracking ? query : query.AsNoTrackingWithIdentityResolution();
    }

    private async Task<UpscalingJob> GetRequiredAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return await Query().FirstAsync(job => job.Id == jobId, cancellationToken);
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        var normalized = idempotencyKey?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > 80)
            throw new UpscalingValidationException(UpscalingJobErrorCodes.IdempotencyKeyInvalid, "The idempotency key is too long.");
        return normalized;
    }

    private static string ComputeRequestFingerprint(Guid workspaceId, Guid? projectId, Guid sourceAssetId, Guid sourceStoredFileId, DateTime sourceUpdatedAt, string targetResolution, string? title) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            workspaceId,
            projectId,
            sourceAssetId,
            sourceStoredFileId,
            sourceUpdatedAt,
            targetResolution,
            title,
        }))));
}
