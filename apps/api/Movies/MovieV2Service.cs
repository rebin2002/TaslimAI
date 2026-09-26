using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieV2Service
{
    Task<MovieV2HierarchyDto?> GetHierarchyAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieV2ActDto?> AddActAsync(Guid userId, Guid movieProjectId, MovieV2ActRequest request, CancellationToken cancellationToken);
    Task<MovieV2SequenceDto?> AddSequenceAsync(Guid userId, Guid actId, MovieV2SequenceRequest request, CancellationToken cancellationToken);
    Task<MovieV2SceneDto?> AddSceneAsync(Guid userId, Guid sequenceId, MovieV2SceneRequest request, CancellationToken cancellationToken);
    Task<MovieV2TakeDto?> AddTakeAsync(Guid userId, Guid shotId, MovieV2TakeRequest request, CancellationToken cancellationToken);
    Task<MovieV2HierarchyDto?> UpdateSettingsAsync(Guid userId, Guid movieProjectId, MovieV2SettingsRequest request, CancellationToken cancellationToken);
    Task<MovieV2HierarchyDto?> SetProjectArchivedAsync(Guid userId, Guid movieProjectId, bool archived, CancellationToken cancellationToken);
    Task SetStatusAsync(Guid userId, string entity, Guid id, string status, CancellationToken cancellationToken);
    Task SelectTakeAsync(Guid userId, Guid takeId, bool finalize, CancellationToken cancellationToken);
    Task<MovieV2TakeDto?> ApproveTakeAsync(Guid userId, Guid takeId, MovieV2ApprovalRequest request, CancellationToken cancellationToken);
    Task ReorderAsync(Guid userId, string entity, Guid id, int sequence, CancellationToken cancellationToken);
}

public sealed class MovieV2Service(TaslimDbContext db, WorkspaceAccessService access, MovieCollaborationAccess? collaboration = null) : IMovieV2Service
{
    public async Task<MovieV2HierarchyDto?> GetHierarchyAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var movie = await HierarchyQuery().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await HasPermissionAsync(userId, movie.Id, MoviePermissions.View, movie.WorkspaceId, cancellationToken)) return null;
        return ToDto(movie);
    }

    public async Task<MovieV2ActDto?> AddActAsync(Guid userId, Guid movieProjectId, MovieV2ActRequest request, CancellationToken cancellationToken)
    {
        var movie = await AuthorizedMovieAsync(userId, movieProjectId, cancellationToken);
        ValidateText(request.Title, 160, "Act title");
        if (movie is null) return null;
        var now = DateTime.UtcNow;
        var act = new MovieAct { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, Sequence = await NextSequenceAsync(db.MovieActs.Where(item => item.MovieProjectId == movieProjectId)), Title = request.Title.Trim(), Summary = Clean(request.Summary), CreatedAt = now, UpdatedAt = now };
        db.MovieActs.Add(act);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(act, []);
    }

    public async Task<MovieV2SequenceDto?> AddSequenceAsync(Guid userId, Guid actId, MovieV2SequenceRequest request, CancellationToken cancellationToken)
    {
        var act = await db.MovieActs.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == actId, cancellationToken);
        if (act is null || !await access.IsMemberAsync(userId, act.MovieProject.WorkspaceId, cancellationToken)) return null;
        ValidateText(request.Title, 160, "Sequence title");
        var now = DateTime.UtcNow;
        var sequence = new MovieSequence { Id = Guid.NewGuid(), MovieActId = actId, Sequence = await NextSequenceAsync(db.MovieSequences.Where(item => item.MovieActId == actId)), Title = request.Title.Trim(), Summary = Clean(request.Summary), CreatedAt = now, UpdatedAt = now };
        db.MovieSequences.Add(sequence);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(sequence, []);
    }

    public async Task<MovieV2SceneDto?> AddSceneAsync(Guid userId, Guid sequenceId, MovieV2SceneRequest request, CancellationToken cancellationToken)
    {
        var sequence = await db.MovieSequences.Include(item => item.MovieAct).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == sequenceId, cancellationToken);
        if (sequence is null || !await access.IsMemberAsync(userId, sequence.MovieAct.MovieProject.WorkspaceId, cancellationToken)) return null;
        ValidateText(request.Title, 160, "Scene title");
        ValidateText(request.Summary, 8_000, "Scene summary");
        var now = DateTime.UtcNow;
        var scene = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = sequence.MovieAct.MovieProjectId, MovieSequenceId = sequenceId, Sequence = await NextSequenceAsync(db.MovieScenes.Where(item => item.MovieSequenceId == sequenceId)), Title = request.Title.Trim(), Summary = request.Summary.Trim(), CreatedAt = now, UpdatedAt = now };
        db.MovieScenes.Add(scene);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(scene, []);
    }

    public async Task<MovieV2TakeDto?> AddTakeAsync(Guid userId, Guid shotId, MovieV2TakeRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Edit, shot.Scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        ValidateQuality(request.QualityLevel);
        if (request.Notes?.Length > 4_000) throw new MovieV2ValidationException("Take notes must be 4,000 characters or fewer.");
        if (request.MovieClipId.HasValue && !await db.MovieClips.AnyAsync(item => item.Id == request.MovieClipId && item.MovieProjectId == shot.Scene.MovieProjectId && item.MovieShotId == shotId, cancellationToken))
            throw new MovieV2ValidationException("The selected clip does not belong to this shot.");
        if (request.GenerationJobId.HasValue && !await db.GenerationJobs.AnyAsync(item => item.Id == request.GenerationJobId && item.WorkspaceId == shot.Scene.MovieProject.WorkspaceId, cancellationToken))
            throw new MovieV2ValidationException("The selected generation job does not belong to this workspace.");
        if (request.AssetId.HasValue && !await db.Assets.AnyAsync(item => item.Id == request.AssetId && item.WorkspaceId == shot.Scene.MovieProject.WorkspaceId, cancellationToken))
            throw new MovieV2ValidationException("The selected asset does not belong to this workspace.");
        var version = (await db.MovieTakes.Where(item => item.MovieShotId == shotId).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1;
        var now = DateTime.UtcNow;
        var take = new MovieTake { Id = Guid.NewGuid(), MovieShotId = shotId, VersionNumber = version, Label = string.IsNullOrWhiteSpace(request.Label) ? $"Take {version}" : request.Label.Trim(), QualityLevel = request.QualityLevel.Trim(), AutoDirectorEnabled = request.AutoDirectorEnabled, MovieClipId = request.MovieClipId, GenerationJobId = request.GenerationJobId, AssetId = request.AssetId, Notes = Clean(request.Notes), CreatedAt = now, UpdatedAt = now };
        db.MovieTakes.Add(take);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(take);
    }

    public async Task<MovieV2HierarchyDto?> UpdateSettingsAsync(Guid userId, Guid movieProjectId, MovieV2SettingsRequest request, CancellationToken cancellationToken)
    {
        var movie = await AuthorizedMovieAsync(userId, movieProjectId, cancellationToken);
        if (movie is null) return null;
        if (request.ProductionStatus is not null) ValidateStatus(request.ProductionStatus, MovieProductionStatuses.Supported, "production status");
        if (request.QualityLevel is not null) ValidateQuality(request.QualityLevel);
        var now = DateTime.UtcNow;
        if (request.ProductionStatus is not null && !string.Equals(movie.ProductionStatus, request.ProductionStatus.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            movie.ProductionStatus = request.ProductionStatus.Trim();
            movie.StatusChangedAt = now;
            movie.StatusChangedByUserId = userId;
            movie.Status = movie.ProductionStatus == MovieProductionStatuses.Archived ? MovieProjectStatuses.Archived : MovieProjectStatuses.Active;
            movie.ArchivedAt = movie.ProductionStatus == MovieProductionStatuses.Archived ? now : null;
        }
        if (request.QualityLevel is not null) movie.QualityLevel = request.QualityLevel.Trim();
        if (request.AutoDirectorEnabled.HasValue) movie.AutoDirectorEnabled = request.AutoDirectorEnabled.Value;
        movie.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetHierarchyAsync(userId, movieProjectId, cancellationToken);
    }

    public Task<MovieV2HierarchyDto?> SetProjectArchivedAsync(Guid userId, Guid movieProjectId, bool archived, CancellationToken cancellationToken) =>
        UpdateSettingsAsync(userId, movieProjectId, new MovieV2SettingsRequest { ProductionStatus = archived ? MovieProductionStatuses.Archived : MovieProductionStatuses.Draft }, cancellationToken);

    public async Task SetStatusAsync(Guid userId, string entity, Guid id, string status, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        switch (entity.ToLowerInvariant())
        {
            case "act":
                var act = await db.MovieActs.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
                await EnsureAuthorizedAsync(act?.MovieProject.WorkspaceId, userId, cancellationToken);
                ValidateStatus(status, MovieHierarchyStatuses.Supported, "act status");
                act!.Status = status.Trim(); act.StatusChangedAt = now; act.StatusChangedByUserId = userId; act.ArchivedAt = IsArchived(status) ? now : null; act.ArchivedByUserId = IsArchived(status) ? userId : null; act.UpdatedAt = now;
                break;
            case "sequence":
                var sequence = await db.MovieSequences.Include(item => item.MovieAct).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
                await EnsureAuthorizedAsync(sequence?.MovieAct.MovieProject.WorkspaceId, userId, cancellationToken);
                ValidateStatus(status, MovieHierarchyStatuses.Supported, "sequence status");
                sequence!.Status = status.Trim(); sequence.StatusChangedAt = now; sequence.StatusChangedByUserId = userId; sequence.ArchivedAt = IsArchived(status) ? now : null; sequence.ArchivedByUserId = IsArchived(status) ? userId : null; sequence.UpdatedAt = now;
                break;
            case "scene":
                var scene = await db.MovieScenes.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
                await EnsureAuthorizedAsync(scene?.MovieProject.WorkspaceId, userId, cancellationToken);
                ValidateStatus(status, MovieHierarchyStatuses.Supported, "scene status");
                scene!.Status = status.Trim(); scene.ArchivedAt = IsArchived(status) ? now : null; scene.UpdatedAt = now;
                break;
            case "shot":
                var shot = await db.MovieShots.Include(item => item.Scene).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
                await EnsureAuthorizedAsync(shot?.Scene.MovieProject.WorkspaceId, userId, cancellationToken);
                ValidateStatus(status, MovieShotStatuses.Supported, "shot status");
                shot!.Status = status.Trim(); shot.ArchivedAt = IsArchived(status) ? now : null; shot.UpdatedAt = now;
                break;
            case "take":
                var take = await db.MovieTakes.Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
                await EnsureAuthorizedAsync(take?.MovieShot.Scene.MovieProject.WorkspaceId, userId, cancellationToken);
                ValidateStatus(status, MovieTakeStatuses.Supported, "take status");
                take!.Status = status.Trim(); take.StatusChangedAt = now; take.StatusChangedByUserId = userId; take.ArchivedAt = IsArchived(status) ? now : null; take.ArchivedByUserId = IsArchived(status) ? userId : null; take.UpdatedAt = now;
                break;
            default: throw new MovieV2ValidationException("Unsupported hierarchy entity.");
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SelectTakeAsync(Guid userId, Guid takeId, bool finalize, CancellationToken cancellationToken)
    {
        var take = await db.MovieTakes.Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == takeId, cancellationToken);
        await EnsureAuthorizedAsync(take?.MovieShot.Scene.MovieProject.WorkspaceId, userId, cancellationToken, take?.MovieShot.Scene.MovieProjectId, finalize ? MoviePermissions.FinalApproval : MoviePermissions.Approve);
        if (take!.Status is MovieTakeStatuses.Archived or MovieTakeStatuses.Rejected) throw new MovieV2ValidationException("Only an active, non-rejected take can be selected.");
        var now = DateTime.UtcNow;
        var shot = take.MovieShot;
        var previous = await db.MovieTakes.Where(item => item.MovieShotId == shot.Id && item.Id != takeId && (finalize ? item.FinalizedAt != null : item.SelectedAt != null)).ToListAsync(cancellationToken);
        foreach (var item in previous) { if (finalize) item.FinalizedAt = null; else item.SelectedAt = null; item.UpdatedAt = now; }
        if (finalize)
        {
            if (take.Status != MovieTakeStatuses.Approved) throw new MovieV2ValidationException("A take must be approved before it can be finalized.");
            var previousSelected = await db.MovieTakes.Where(item => item.MovieShotId == shot.Id && item.Id != takeId && item.SelectedAt != null).ToListAsync(cancellationToken);
            foreach (var item in previousSelected) { item.SelectedAt = null; item.UpdatedAt = now; }
            shot.SelectedTakeId = take.Id;
            shot.FinalTakeId = take.Id;
            take.SelectedAt = take.SelectedAt ?? now;
            take.SelectedByUserId = take.SelectedByUserId ?? userId;
            take.FinalizedAt = now;
            take.FinalizedByUserId = userId;
        }
        else { shot.SelectedTakeId = take.Id; take.SelectedAt = now; take.SelectedByUserId = userId; }
        shot.UpdatedAt = now; take.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MovieV2TakeDto?> ApproveTakeAsync(Guid userId, Guid takeId, MovieV2ApprovalRequest request, CancellationToken cancellationToken)
    {
        var take = await db.MovieTakes.Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject).Include(item => item.Approvals).Include(item => item.GenerationJob).ThenInclude(item => item!.ProviderAttempts).Include(item => item.GenerationJob).ThenInclude(item => item!.Assets).FirstOrDefaultAsync(item => item.Id == takeId, cancellationToken);
        await EnsureAuthorizedAsync(take?.MovieShot.Scene.MovieProject.WorkspaceId, userId, cancellationToken, take?.MovieShot.Scene.MovieProjectId, MoviePermissions.Approve);
        if (request.Decision is not (MovieApprovalDecisions.Approved or MovieApprovalDecisions.Rejected)) throw new MovieV2ValidationException("Decision must be Approved or Rejected.");
        if (request.Comment?.Length > 4_000) throw new MovieV2ValidationException("Approval comments must be 4,000 characters or fewer.");
        var now = DateTime.UtcNow;
        db.MovieTakeApprovals.Add(new MovieTakeApproval { Id = Guid.NewGuid(), MovieTakeId = take!.Id, UserId = userId, Decision = request.Decision, Comment = Clean(request.Comment), CreatedAt = now });
        take.Status = request.Decision == MovieApprovalDecisions.Approved ? MovieTakeStatuses.Approved : MovieTakeStatuses.Rejected;
        take.StatusChangedAt = now; take.StatusChangedByUserId = userId; take.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(take);
    }

    public async Task ReorderAsync(Guid userId, string entity, Guid id, int sequence, CancellationToken cancellationToken)
    {
        if (sequence < 1) throw new MovieV2ValidationException("Sequence must be 1 or greater.");
        var now = DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        switch (entity.ToLowerInvariant())
        {
            case "act":
                var act = await db.MovieActs.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken); await EnsureAuthorizedAsync(act?.MovieProject.WorkspaceId, userId, cancellationToken); var acts = await db.MovieActs.Where(item => item.MovieProjectId == act!.MovieProjectId).OrderBy(item => item.Sequence).ToListAsync(cancellationToken); await MoveAsync(acts, act!, sequence, now, (item, position) => item.Sequence = position, (item, changedAt) => item.UpdatedAt = changedAt, cancellationToken); break;
            case "sequence":
                var seq = await db.MovieSequences.Include(item => item.MovieAct).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken); await EnsureAuthorizedAsync(seq?.MovieAct.MovieProject.WorkspaceId, userId, cancellationToken); var seqs = await db.MovieSequences.Where(item => item.MovieActId == seq!.MovieActId).OrderBy(item => item.Sequence).ToListAsync(cancellationToken); await MoveAsync(seqs, seq!, sequence, now, (item, position) => item.Sequence = position, (item, changedAt) => item.UpdatedAt = changedAt, cancellationToken); break;
            case "scene":
                var scene = await db.MovieScenes.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken); await EnsureAuthorizedAsync(scene?.MovieProject.WorkspaceId, userId, cancellationToken); var scenes = await db.MovieScenes.Where(item => item.MovieSequenceId == scene!.MovieSequenceId).OrderBy(item => item.Sequence).ToListAsync(cancellationToken); await MoveAsync(scenes, scene!, sequence, now, (item, position) => item.Sequence = position, (item, changedAt) => item.UpdatedAt = changedAt, cancellationToken); break;
            case "shot":
                var shot = await db.MovieShots.Include(item => item.Scene).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken); await EnsureAuthorizedAsync(shot?.Scene.MovieProject.WorkspaceId, userId, cancellationToken); var shots = await db.MovieShots.Where(item => item.MovieSceneId == shot!.MovieSceneId).OrderBy(item => item.Sequence).ToListAsync(cancellationToken); await MoveAsync(shots, shot!, sequence, now, (item, position) => item.Sequence = position, (item, changedAt) => item.UpdatedAt = changedAt, cancellationToken); break;
            case "take":
                var take = await db.MovieTakes.Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == id, cancellationToken); await EnsureAuthorizedAsync(take?.MovieShot.Scene.MovieProject.WorkspaceId, userId, cancellationToken); var takes = await db.MovieTakes.Where(item => item.MovieShotId == take!.MovieShotId).OrderBy(item => item.VersionNumber).ToListAsync(cancellationToken); await MoveAsync(takes, take!, sequence, now, (item, position) => item.VersionNumber = position, (item, changedAt) => item.UpdatedAt = changedAt, cancellationToken); break;
            default: throw new MovieV2ValidationException("Unsupported hierarchy entity.");
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<MovieProject?> AuthorizedMovieAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken) ? null : movie;
    }

    private IQueryable<MovieProject> HierarchyQuery() => db.MovieProjects.AsNoTracking()
        .Include(item => item.Acts).ThenInclude(item => item.Sequences).ThenInclude(item => item.Scenes).ThenInclude(item => item.Shots).ThenInclude(item => item.Takes).ThenInclude(item => item.Approvals)
        .Include(item => item.Acts).ThenInclude(item => item.Sequences).ThenInclude(item => item.Scenes).ThenInclude(item => item.Shots).ThenInclude(item => item.Takes).ThenInclude(item => item.GenerationJob).ThenInclude(item => item!.ProviderAttempts)
        .Include(item => item.Acts).ThenInclude(item => item.Sequences).ThenInclude(item => item.Scenes).ThenInclude(item => item.Shots).ThenInclude(item => item.Takes).ThenInclude(item => item.GenerationJob).ThenInclude(item => item!.Assets);
    private static async Task<int> NextSequenceAsync<T>(IQueryable<T> query) where T : class => await query.CountAsync() + 1;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsArchived(string status) => string.Equals(status, MovieHierarchyStatuses.Archived, StringComparison.OrdinalIgnoreCase) || string.Equals(status, MovieShotStatuses.Archived, StringComparison.OrdinalIgnoreCase) || string.Equals(status, MovieTakeStatuses.Archived, StringComparison.OrdinalIgnoreCase);
    private static void ValidateQuality(string value) => ValidateStatus(value, MovieQualityLevels.Supported, "quality level");
    private static void ValidateStatus(string value, IReadOnlySet<string> supported, string field) { if (string.IsNullOrWhiteSpace(value) || !supported.Contains(value.Trim())) throw new MovieV2ValidationException($"Choose a supported {field}."); }
    private static void ValidateText(string value, int maxLength, string field) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength) throw new MovieV2ValidationException($"{field} is required and must be {maxLength} characters or fewer."); }
    private async Task<bool> HasPermissionAsync(Guid userId, Guid movieProjectId, string permission, Guid workspaceId, CancellationToken cancellationToken) => collaboration is null ? await access.IsMemberAsync(userId, workspaceId, cancellationToken) : await collaboration.HasPermissionAsync(userId, movieProjectId, permission, cancellationToken);
    private async Task EnsureAuthorizedAsync(Guid? workspaceId, Guid userId, CancellationToken cancellationToken, Guid? movieProjectId = null, string? permission = null) { if (!workspaceId.HasValue || (movieProjectId.HasValue && permission is not null ? !await HasPermissionAsync(userId, movieProjectId.Value, permission, workspaceId.Value, cancellationToken) : !await access.IsMemberAsync(userId, workspaceId.Value, cancellationToken))) throw new MovieV2NotFoundException(); }

    private async Task MoveAsync<T>(List<T> items, T target, int requested, DateTime now, Action<T, int> assign, Action<T, DateTime> touch, CancellationToken cancellationToken) where T : class
    {
        items.Remove(target); items.Insert(Math.Clamp(requested - 1, 0, items.Count), target);
        for (var i = 0; i < items.Count; i++) { assign(items[i], -(i + 1)); touch(items[i], now); }
        await db.SaveChangesAsync(cancellationToken);
        for (var i = 0; i < items.Count; i++) assign(items[i], i + 1);
    }

    private static MovieV2HierarchyDto ToDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.Title, movie.ProductionStatus, movie.QualityLevel, movie.AutoDirectorEnabled, movie.StatusChangedAt, movie.ArchivedAt, movie.CreatedAt, movie.UpdatedAt, movie.Acts.OrderBy(item => item.Sequence).Select(item => ToDto(item)).ToArray());
    private static MovieV2ActDto ToDto(MovieAct act, IReadOnlyList<MovieV2SequenceDto> sequences) => new(act.Id, act.Sequence, act.Title, act.Summary, act.Status, act.ArchivedAt, act.CreatedAt, act.UpdatedAt, sequences);
    private static MovieV2SequenceDto ToDto(MovieSequence sequence, IReadOnlyList<MovieV2SceneDto> scenes) => new(sequence.Id, sequence.Sequence, sequence.Title, sequence.Summary, sequence.Status, sequence.ArchivedAt, sequence.CreatedAt, sequence.UpdatedAt, scenes);
    private static MovieV2SceneDto ToDto(MovieScene scene, IReadOnlyList<MovieV2ShotDto> shots) => new(scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.Status, scene.MovieSequenceId, scene.ArchivedAt, scene.CreatedAt, scene.UpdatedAt, shots);
    private static MovieV2ShotDto ToDto(MovieShot shot, IReadOnlyList<MovieV2TakeDto> takes) => new(shot.Id, shot.Sequence, shot.Description, shot.Status, shot.SelectedTakeId, shot.FinalTakeId, shot.ArchivedAt, shot.CreatedAt, shot.UpdatedAt, takes.OrderBy(item => item.VersionNumber).ToArray());
    private static MovieV2TakeDto ToDto(MovieTake take) => new(take.Id, take.MovieShotId, take.VersionNumber, take.Label, take.Status, take.QualityLevel, take.AutoDirectorEnabled, take.MovieClipId, take.GenerationJobId, take.AssetId, take.Notes, take.SelectedAt, take.FinalizedAt, take.CreatedAt, take.UpdatedAt, take.Approvals.OrderByDescending(item => item.CreatedAt).Select(item => new MovieV2TakeApprovalDto(item.Id, item.UserId, item.Decision, item.Comment, item.CreatedAt)).ToArray(), MovieProductionProjection.ToExecution(take.GenerationJob));
    private static MovieV2ActDto ToDto(MovieAct act) => ToDto(act, act.Sequences.OrderBy(item => item.Sequence).Select(item => ToDto(item, item.Scenes.OrderBy(scene => scene.Sequence).Select(scene => ToDto(scene, scene.Shots.OrderBy(shot => shot.Sequence).Select(shot => ToDto(shot, shot.Takes.Select(ToDto).ToArray())).ToArray())).ToArray())).ToArray());
}

public sealed class MovieV2ValidationException(string message) : Exception(message);
public sealed class MovieV2NotFoundException : Exception;
