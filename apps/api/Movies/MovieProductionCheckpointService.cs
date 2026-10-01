using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieProductionCheckpointService
{
    Task<MovieProductionCheckpointDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieProductionRecoveryResponse?> RecoverAsync(Guid userId, Guid movieProjectId, MovieProductionRecoveryRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
}

public sealed class MovieProductionCheckpointService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IGenerationJobService jobs) : IMovieProductionCheckpointService
{
    public async Task<MovieProductionCheckpointDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return await BuildAndPersistAsync(movie, cancellationToken);
    }

    public async Task<MovieProductionRecoveryResponse?> RecoverAsync(
        Guid userId,
        Guid movieProjectId,
        MovieProductionRecoveryRequest request,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Generate, cancellationToken)) return null;
        if (request.GenerationJobId == Guid.Empty)
            throw new MovieProductionRecoveryException("RECOVERY_JOB_REQUIRED", "Choose an interrupted production pass to recover.");

        var current = await BuildProjectionAsync(movie, cancellationToken);
        var action = current.RecoveryActions.FirstOrDefault(item => item.ActionId == request.GenerationJobId);
        if (action is null)
            throw new MovieProductionRecoveryException("RECOVERY_ACTION_STALE", "This production pass is no longer recoverable from the current checkpoint.");

        var source = await db.GenerationJobs.FirstOrDefaultAsync(item => item.Id == request.GenerationJobId, cancellationToken);
        if (source is null || source.WorkspaceId != movie.WorkspaceId || !GenerationJobTypes.MovieTypes.Contains(source.JobType))
            throw new MovieProductionRecoveryException("RECOVERY_JOB_INVALID", "This production pass is not part of the movie project.");
        if (source.Status is not (GenerationJobStatus.Failed or GenerationJobStatus.Cancelled))
            throw new MovieProductionRecoveryException("RECOVERY_ACTION_STALE", "This production pass is no longer recoverable from the current checkpoint.");
        if (await HasPublishedOutputAsync(source.Id, movie.Id, cancellationToken))
            throw new MovieProductionRecoveryException("RECOVERY_OUTPUT_EXISTS", "A publishable output already exists for this production pass. No duplicate recovery was created.");

        // A second browser click, tab, or worker delivery must reuse the active retry
        // rather than creating another GenerationJob or usage transaction.
        var existingRetry = await db.GenerationJobs.AsNoTracking()
            .Where(item => item.RetryOfJobId == source.Id)
            .Where(item => item.Status == GenerationJobStatus.Pending || item.Status == GenerationJobStatus.Queued || item.Status == GenerationJobStatus.Running || item.Status == GenerationJobStatus.Succeeded)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        GenerationJob? retry = existingRetry;
        if (retry is null)
        {
            retry = await jobs.RetryAsync(userId, source.Id, cancellationToken, idempotencyKey ?? $"movie-checkpoint-recovery:{movieProjectId:N}:{source.Id:N}");
            if (retry is null) throw new MovieProductionRecoveryException("RECOVERY_JOB_NOT_FOUND", "The production pass is not available for recovery.");
        }

        var versions = await db.MovieProductionVersions
            .Include(item => item.MovieShot).ThenInclude(item => item.Scene)
            .Where(item => item.GenerationJobId == source.Id && item.MovieShot.Scene.MovieProjectId == movieProjectId)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var version in versions)
        {
            version.GenerationJobId = retry.Id;
            version.UpdatedAt = now;
            db.MovieProductionStageTransitions.Add(new MovieProductionStageTransition
            {
                Id = Guid.NewGuid(),
                MovieShotId = version.MovieShotId,
                MovieProductionVersionId = version.Id,
                FromStage = version.Stage,
                ToStage = version.Stage,
                EventType = "recovered",
                Reason = "Interrupted production pass recovered.",
                SourceVersionId = version.SourceVersionId,
                GenerationJobId = retry.Id,
                ActorUserId = userId,
                CreatedAt = now,
            });
        }
        if (versions.Count > 0) await db.SaveChangesAsync(cancellationToken);

        var checkpoint = await BuildAndPersistAsync(movie, cancellationToken, now, userId);
        return new MovieProductionRecoveryResponse(checkpoint, GenerationJobContractMapper.ToMovieDto(retry));
    }

    private async Task<MovieProductionCheckpointDto> BuildAndPersistAsync(MovieProject movie, CancellationToken cancellationToken, DateTime? recoveredAt = null, Guid? recoveredByUserId = null)
    {
        var projection = await BuildProjectionAsync(movie, cancellationToken);
        var checkpoint = await db.MovieProductionCheckpoints.FirstOrDefaultAsync(item => item.MovieProjectId == movie.Id, cancellationToken);
        if (checkpoint is null)
        {
            checkpoint = new MovieProductionCheckpoint
            {
                Id = Guid.NewGuid(),
                MovieProjectId = movie.Id,
                Version = 1,
            };
            db.MovieProductionCheckpoints.Add(checkpoint);
        }
        else if (!string.Equals(checkpoint.SnapshotHash, projection.SnapshotHash(), StringComparison.Ordinal))
        {
            checkpoint.Version++;
        }
        checkpoint.State = projection.State;
        checkpoint.ProgressPercent = projection.ProgressPercent;
        checkpoint.TotalShots = projection.TotalShots;
        checkpoint.CompletedShots = projection.CompletedShots;
        checkpoint.RunningShots = projection.RunningShots;
        checkpoint.BlockedShots = projection.BlockedShots;
        checkpoint.RecoverableShots = projection.RecoverableShots;
        checkpoint.PendingApprovalShots = projection.PendingApprovalShots;
        checkpoint.SnapshotHash = projection.SnapshotHash();
        checkpoint.ObservedAt = DateTime.UtcNow;
        if (recoveredAt.HasValue)
        {
            checkpoint.LastRecoveredAt = recoveredAt;
            checkpoint.LastRecoveredByUserId = recoveredByUserId;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(checkpoint, projection);
    }

    private async Task<MovieProductionCheckpointProjection> BuildProjectionAsync(MovieProject movie, CancellationToken cancellationToken)
    {
        var shots = await db.MovieShots.AsNoTracking().AsSplitQuery()
            .Include(item => item.Scene)
            .Include(item => item.ProductionVersions)
            .Include(item => item.Takes)
            .Include(item => item.Clips)
            .Where(item => item.Scene.MovieProjectId == movie.Id && item.Status != MovieShotStatuses.Archived)
            .OrderBy(item => item.Scene.Sequence).ThenBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken);

        var linkedJobIds = shots.SelectMany(item => item.ProductionVersions.Select(version => version.GenerationJobId)
                .Concat(item.Takes.Select(take => take.GenerationJobId))
                .Concat(item.Clips.Select(clip => clip.GenerationJobId)))
            .Where(item => item.HasValue).Select(item => item!.Value).ToHashSet();
        var candidateJobs = linkedJobIds.Count == 0
            ? []
            : await db.GenerationJobs.AsNoTracking()
                .Include(item => item.Outputs)
                .Include(item => item.Assets)
                .Where(item => item.WorkspaceId == movie.WorkspaceId
                    && (item.JobType == GenerationJobTypes.MovieClipGenerate || item.JobType == GenerationJobTypes.MovieQuickGenerate || item.JobType == GenerationJobTypes.MovieAssembly))
                .ToListAsync(cancellationToken);
        var jobsById = candidateJobs.ToDictionary(item => item.Id);
        var relatedJobs = new Dictionary<Guid, GenerationJob>();
        var pendingIds = new Queue<Guid>(linkedJobIds);
        while (pendingIds.Count > 0)
        {
            var id = pendingIds.Dequeue();
            if (!jobsById.TryGetValue(id, out var job) || !relatedJobs.TryAdd(id, job)) continue;
            foreach (var child in candidateJobs.Where(item => item.RetryOfJobId == id)) pendingIds.Enqueue(child.Id);
        }

        var items = new List<MovieProductionCheckpointItemDto>(shots.Length);
        foreach (var shot in shots)
        {
            var snapshotJobs = shot.ProductionVersions.Select(item => item.GenerationJobId)
                .Concat(shot.Takes.Select(item => item.GenerationJobId))
                .Concat(shot.Clips.Select(item => item.GenerationJobId))
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .Distinct()
                .Where(relatedJobs.ContainsKey)
                .Select(id =>
                {
                    var job = relatedJobs[id];
                    var hasOutput = job.Assets.Count > 0 || job.Outputs.Any(output => output.StoredFileId.HasValue)
                        || shot.Clips.Any(clip => clip.GenerationJobId == id && (clip.AssetId.HasValue || clip.StoredFileId.HasValue))
                        || shot.ProductionVersions.Any(version => version.GenerationJobId == id && version.AssetId.HasValue)
                        || shot.Takes.Any(take => take.GenerationJobId == id && take.AssetId.HasValue);
                    return new MovieProductionCheckpointJobSnapshot(id, job.Status.ToString(), hasOutput);
                })
                .ToArray();
            var snapshotTakes = shot.Takes.Select(item => new MovieProductionCheckpointTakeSnapshot(
                item.Id,
                item.Status,
                shot.SelectedTakeId == item.Id || item.SelectedAt.HasValue,
                shot.FinalTakeId == item.Id || item.FinalizedAt.HasValue,
                item.AssetId.HasValue,
                item.GenerationJobId)).ToArray();
            var snapshotVersions = shot.ProductionVersions.Select(item => new MovieProductionCheckpointVersionSnapshot(
                item.Id,
                item.Stage,
                item.Status,
                item.GenerationJobId,
                item.AssetId.HasValue)).ToArray();
            var label = $"Scene {shot.Scene.Sequence} · Shot {shot.Sequence}";
            items.Add(MovieProductionCheckpointAnalyzer.AnalyzeShot(
                shot.Id,
                shot.Scene.Title,
                shot.Scene.Sequence,
                shot.Sequence,
                label,
                shot.Status,
                shot.UpdatedAt,
                snapshotVersions,
                snapshotTakes,
                snapshotJobs));
        }

        var recoveryActions = items
            .Where(item => item.RecoveryJobId.HasValue)
            .Select(item => new MovieProductionRecoveryActionDto(
                item.RecoveryJobId!.Value,
                item.ShotId,
                item.SceneTitle,
                item.Label,
                MovieProductionCheckpointActions.Retry,
                item.BlockedReason ?? "The previous production pass can be recovered safely."))
            .GroupBy(item => item.ActionId)
            .Select(group => group.First())
            .ToArray();
        return MovieProductionCheckpointAnalyzer.Summarize(items, recoveryActions);
    }

    private async Task<bool> HasPublishedOutputAsync(Guid jobId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        return await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.Id == jobId && (item.Assets.Any() || item.Outputs.Any(output => output.StoredFileId.HasValue)), cancellationToken)
            || await db.MovieClips.AsNoTracking().AnyAsync(item => item.MovieProjectId == movieProjectId && item.GenerationJobId == jobId && (item.AssetId.HasValue || item.StoredFileId.HasValue), cancellationToken)
            || await db.MovieProductionVersions.AsNoTracking().AnyAsync(item => item.MovieShot.Scene.MovieProjectId == movieProjectId && item.GenerationJobId == jobId && item.AssetId.HasValue, cancellationToken);
    }

    private static MovieProductionCheckpointDto ToDto(MovieProductionCheckpoint checkpoint, MovieProductionCheckpointProjection projection) =>
        new(checkpoint.MovieProjectId, checkpoint.Version, projection.State, projection.ProgressPercent, projection.TotalShots, projection.CompletedShots, projection.RunningShots, projection.BlockedShots, projection.RecoverableShots, projection.PendingApprovalShots, checkpoint.ObservedAt, checkpoint.LastRecoveredAt, projection.Items, projection.RecoveryActions);
}
