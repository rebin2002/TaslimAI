using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieWorldContinuityService
{
    Task<MovieWorldContinuitySnapshotDto?> GetProjectAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default);
    Task<MovieWorldContinuitySnapshotDto?> GetSceneAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken = default);
    Task<MovieWorldContinuitySnapshotDto?> GetShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default);
}

public sealed class MovieWorldContinuityService(
    TaslimDbContext db,
    WorkspaceAccessService access,
    MovieWorldContinuityProjector projector) : IMovieWorldContinuityService
{
    public async Task<MovieWorldContinuitySnapshotDto?> GetProjectAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        return movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)
            ? null
            : await projector.ProjectAsync(movieProjectId, cancellationToken: cancellationToken);
    }

    public async Task<MovieWorldContinuitySnapshotDto?> GetSceneAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken = default)
    {
        var scene = await db.MovieScenes.AsNoTracking().Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        return scene is null || !await access.IsMemberAsync(userId, scene.MovieProject.WorkspaceId, cancellationToken)
            ? null
            : await projector.ProjectAsync(scene.MovieProjectId, scene.Id, null, cancellationToken);
    }

    public async Task<MovieWorldContinuitySnapshotDto?> GetShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default)
    {
        var shot = await db.MovieShots.AsNoTracking().Include(item => item.Scene).ThenInclude(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        return shot is null || !await access.IsMemberAsync(userId, shot.Scene.MovieProject.WorkspaceId, cancellationToken)
            ? null
            : await projector.ProjectAsync(shot.Scene.MovieProjectId, shot.Scene.Id, shot.Id, cancellationToken);
    }
}
