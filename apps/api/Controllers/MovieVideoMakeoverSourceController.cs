using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

/// <summary>
/// Authenticated, workspace-private, read-only source catalogue for AI Video
/// Makeover input selection (issue #698 slice; the server-side descriptor that
/// #811 identified as the missing half of safe attach). Exposes only facts that
/// are persisted authoritatively today; every unprovable fact is reported as
/// unverified so the #775 draft admission can fail closed. No provider call, no
/// cost admission, no mutation of any other slice's files or routes.
/// </summary>
[ApiController]
[Authorize]
[Route("api/movie-studio/projects/{projectId:guid}/video-makeover-sources")]
public sealed class MovieVideoMakeoverSourceController(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration) : ControllerBase
{
    /// <summary>
    /// Defensive page bound: Makeover drafts attach exactly one source, so a
    /// bounded catalogue is sufficient and keeps the response budget predictable.
    /// </summary>
    private const int MaxRows = 200;

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken cancellationToken)
    {
        var access = await ResolveProjectAccessAsync(projectId, cancellationToken);
        if (access is null) return ProjectNotFound();

        var (requesterUserId, workspaceId) = access.Value;
        var visibleToRequester = StoredFileVisibility.ForWorkspaceMember(requesterUserId).Compile();

        var clips = await db.MovieClips.AsNoTracking()
            .Where(clip => clip.MovieProjectId == projectId)
            .OrderBy(clip => clip.CreatedAt)
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);
        var truncated = clips.Count > MaxRows;
        if (truncated) clips.RemoveAt(MaxRows);

        var (assetsByClip, filesByClip) = await LoadEvidenceAsync(clips, cancellationToken);

        var sources = new List<MovieVideoMakeoverSourceDto>(clips.Count);
        foreach (var clip in clips)
        {
            var asset = assetsByClip.GetValueOrDefault(clip.Id);
            var storedFile = filesByClip.GetValueOrDefault(clip.Id);
            if (MovieVideoMakeoverSourceEvaluator.ShouldWithhold(workspaceId, asset, storedFile, visibleToRequester))
                continue; // cross-workspace or requester-invisible link: not disclosed at all

            sources.Add(MovieVideoMakeoverSourceEvaluator.Evaluate(
                clip, projectId, asset, storedFile, requesterUserId, DateTime.UtcNow));
        }

        Response.Headers["X-Movie-Read-Model"] = MovieVideoMakeoverSourceCodes.ReadModel;
        return Ok(new MovieVideoMakeoverSourceResponse(
            "video-makeover-sources",
            projectId,
            workspaceId,
            DateTime.UtcNow,
            clips.Count,
            sources.Count,
            truncated,
            sources));
    }

    [HttpGet("{clipId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid clipId, CancellationToken cancellationToken)
    {
        var access = await ResolveProjectAccessAsync(projectId, cancellationToken);
        if (access is null) return ProjectNotFound();

        var (requesterUserId, workspaceId) = access.Value;
        var clip = await db.MovieClips.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == clipId && item.MovieProjectId == projectId, cancellationToken);
        if (clip is null) return SourceNotFound();

        var (assetsByClip, filesByClip) = await LoadEvidenceAsync([clip], cancellationToken);
        var asset = assetsByClip.GetValueOrDefault(clip.Id);
        var storedFile = filesByClip.GetValueOrDefault(clip.Id);
        var visibleToRequester = StoredFileVisibility.ForWorkspaceMember(requesterUserId).Compile();
        if (MovieVideoMakeoverSourceEvaluator.ShouldWithhold(workspaceId, asset, storedFile, visibleToRequester))
            return SourceNotFound();

        Response.Headers["X-Movie-Read-Model"] = MovieVideoMakeoverSourceCodes.ReadModel;
        return Ok(MovieVideoMakeoverSourceEvaluator.Evaluate(
            clip, projectId, asset, storedFile, requesterUserId, DateTime.UtcNow));
    }

    /// <summary>
    /// Requires the established Movie collaboration View permission over the project
    /// (workspace membership + project team), identical to the existing read models.
    /// Returns null - never a partial answer - when the project or access is absent.
    /// </summary>
    private async Task<(Guid RequesterUserId, Guid WorkspaceId)?> ResolveProjectAccessAsync(
        Guid projectId, CancellationToken cancellationToken)
    {
        var requester = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
        var project = await db.MovieProjects.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null) return null;
        if (!await collaboration.HasPermissionAsync(requester, projectId, MoviePermissions.View, cancellationToken))
            return null;
        return (requester, project.WorkspaceId);
    }

    /// <summary>
    /// Batch-resolves the referenced asset and stored-file rows per clip. A clip may
    /// link its file directly or through its asset; both id sets are fetched once so
    /// the endpoint is two queries beyond the clip page regardless of row count.
    /// </summary>
    private async Task<(Dictionary<Guid, Asset> Assets, Dictionary<Guid, StoredFile> Files)> LoadEvidenceAsync(
        IReadOnlyList<MovieClip> clips, CancellationToken cancellationToken)
    {
        var assetIds = clips
            .Where(clip => clip.AssetId.HasValue)
            .Select(clip => clip.AssetId!.Value)
            .Distinct()
            .ToList();
        var assetsById = assetIds.Count == 0
            ? new Dictionary<Guid, Asset>()
            : await db.Assets.AsNoTracking()
                .Where(asset => assetIds.Contains(asset.Id))
                .ToDictionaryAsync(asset => asset.Id, cancellationToken);

        var fileIds = clips
            .Select(clip => clip.StoredFileId
                ?? (clip.AssetId is { } assetIdForFile && assetsById.TryGetValue(assetIdForFile, out var linkedAsset)
                    ? linkedAsset.StoredFileId
                    : null))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var filesById = fileIds.Count == 0
            ? new Dictionary<Guid, StoredFile>()
            : await db.StoredFiles.AsNoTracking()
                .Where(file => fileIds.Contains(file.Id))
                .ToDictionaryAsync(file => file.Id, cancellationToken);

        var assetsByClip = new Dictionary<Guid, Asset>(clips.Count);
        var filesByClip = new Dictionary<Guid, StoredFile>(clips.Count);
        foreach (var clip in clips)
        {
            Asset? asset = clip.AssetId is { } assetId && assetsById.TryGetValue(assetId, out var foundAsset) ? foundAsset : null;
            if (asset is not null) assetsByClip[clip.Id] = asset;
            var fileId = clip.StoredFileId ?? asset?.StoredFileId;
            if (fileId is { } id && filesById.TryGetValue(id, out var file))
                filesByClip[clip.Id] = file;
        }

        return (assetsByClip, filesByClip);
    }

    private IActionResult ProjectNotFound() =>
        ApiResults.Error(this, StatusCodes.Status404NotFound, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.");

    private IActionResult SourceNotFound() =>
        ApiResults.Error(this, StatusCodes.Status404NotFound, "MOVIE_MAKEOVER_SOURCE_NOT_FOUND", "Movie Makeover source not found.");
}
