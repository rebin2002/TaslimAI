using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

/// <summary>
/// Stable, machine-readable admission codes for the AI Video Makeover result-preview
/// gate (#698 server-side slice). These names are the contract; UI code must match on
/// <see cref="Code"/> rather than on the human-readable message.
/// </summary>
public static class MovieVideoMakeoverPreviewAdmissionCodes
{
    public const string Admitted = "admitted";
    public const string ProjectNotFound = "project_not_found";
    public const string PrincipalNotWorkspaceMember = "principal_not_workspace_member";
    public const string SourceAssetMissing = "source_asset_missing";
    public const string ResultAssetMissing = "result_asset_missing";
    public const string AssetCrossWorkspace = "asset_cross_workspace";
    public const string AssetCrossProject = "asset_cross_project";
    public const string AssetNotVideo = "asset_not_video";
    public const string PreviewAssetUnavailable = "preview_asset_unavailable";
    public const string ResultBindsSourceToItself = "result_binds_source_to_itself";
    public const string ResultNotBoundToGeneration = "result_not_bound_to_generation";
    public const string ResultJobCrossWorkspace = "result_job_cross_workspace";
    public const string PreviewNotFinished = "preview_not_finished";
}

/// <summary>
/// Outcome of one admission evaluation. The decision is read-only visibility evidence:
/// it exposes only identifiers of already-persisted records and never a rendered
/// media URL, a transformation, or any provider reference. Presenting the before/after
/// comparison remains the responsibility of the result-preview UI slice (#801).
/// </summary>
public sealed record MovieVideoMakeoverPreviewAdmissionDecision(
    bool Admitted,
    string Code,
    string Message,
    Guid? MovieProjectId,
    Guid? SourceAssetId,
    Guid? ResultAssetId);

/// <summary>
/// Deterministic, provider-free evaluation of a Video Makeover result-preview request.
/// A request binds an authorized principal to exactly one Movie project, one existing
/// owned source video asset, and one existing authorized result video asset. Admission
/// is fail-closed: an unknown project, a non-member, a missing/archived asset, an
/// identity that crosses the project's workspace or project scope, a result not backed
/// by a completed generation job, or a self-bound pair (source presented as its own
/// "makeover") is always denied. This evaluator reads no database, calls no provider,
/// queues no job, and cannot render a transformation.
/// </summary>
public static class MovieVideoMakeoverPreviewAdmissionEvaluator
{
    public static MovieVideoMakeoverPreviewAdmissionDecision Evaluate(
        MovieProject? project,
        bool isWorkspaceMember,
        Asset? sourceAsset,
        Asset? resultAsset,
        GenerationJob? resultJob)
    {
        if (!isWorkspaceMember)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.PrincipalNotWorkspaceMember,
                "Only a member of the owning workspace may open this preview.", project, sourceAsset, resultAsset);
        if (project is null)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.ProjectNotFound,
                "The Movie project for this preview no longer exists.", null, sourceAsset, resultAsset);
        if (sourceAsset is null)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.SourceAssetMissing,
                "The source video for this makeover is no longer available.", project, null, resultAsset);
        if (resultAsset is null)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.ResultAssetMissing,
                "The makeover result preview has not been produced yet.", project, sourceAsset, null);
        if (sourceAsset.Id == resultAsset.Id)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.ResultBindsSourceToItself,
                "A result preview must bind a distinct produced asset; the unmodified source cannot stand in for itself.",
                project, sourceAsset, resultAsset);

        foreach (var asset in new[] { sourceAsset, resultAsset })
        {
            if (asset.WorkspaceId != project.WorkspaceId)
                return Denied(MovieVideoMakeoverPreviewAdmissionCodes.AssetCrossWorkspace,
                    "The preview references an asset from a different workspace.", project, sourceAsset, resultAsset);
            if (!string.Equals(asset.AssetType, AssetTypes.Video, StringComparison.OrdinalIgnoreCase))
                return Denied(MovieVideoMakeoverPreviewAdmissionCodes.AssetNotVideo,
                    "Video Makeover previews bind video assets only.", project, sourceAsset, resultAsset);
            if (asset.Status != AssetStatus.Active || asset.ArchivedAt is not null || asset.StoredFileId is null)
                return Denied(MovieVideoMakeoverPreviewAdmissionCodes.PreviewAssetUnavailable,
                    "A preview asset must be active, unarchived, and backed by a stored file.", project, sourceAsset, resultAsset);
            if (asset.ProjectId.HasValue && project.ProjectId.HasValue && asset.ProjectId != project.ProjectId)
                return Denied(MovieVideoMakeoverPreviewAdmissionCodes.AssetCrossProject,
                    "The preview references an asset bound to a different project.", project, sourceAsset, resultAsset);
        }

        if (resultAsset.SourceGenerationJobId is null)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.ResultNotBoundToGeneration,
                "The result asset must come from an existing makeover generation; a result cannot be claimed without one.",
                project, sourceAsset, resultAsset);
        if (resultJob is null || resultJob.Id != resultAsset.SourceGenerationJobId)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.ResultNotBoundToGeneration,
                "The makeover generation linked to this result asset cannot be verified.",
                project, sourceAsset, resultAsset);
        if (resultJob.WorkspaceId != project.WorkspaceId)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.ResultJobCrossWorkspace,
                    "The makeover generation belongs to a different workspace.", project, sourceAsset, resultAsset);
        if (resultJob.Status != GenerationJobStatus.Succeeded)
            return Denied(MovieVideoMakeoverPreviewAdmissionCodes.PreviewNotFinished,
                "The makeover generation has not completed successfully, so there is nothing to preview yet.",
                project, sourceAsset, resultAsset);

        return new MovieVideoMakeoverPreviewAdmissionDecision(
            true,
            MovieVideoMakeoverPreviewAdmissionCodes.Admitted,
            "Admitted: view the existing result asset beside the existing source asset. Nothing here renders a transformation.",
            project.Id,
            sourceAsset.Id,
            resultAsset.Id);
    }

    private static MovieVideoMakeoverPreviewAdmissionDecision Denied(
        string code,
        string message,
        MovieProject? project,
        Asset? sourceAsset,
        Asset? resultAsset) => new(
            false,
            code,
            message,
            project?.Id,
            sourceAsset?.Id,
            resultAsset?.Id);
}

public interface IMovieVideoMakeoverPreviewAdmissionService
{
    /// <summary>
    /// Read-only admission check for opening a Video Makeover result preview.
    /// Loads persisted records, evaluates <see cref="MovieVideoMakeoverPreviewAdmissionEvaluator"/>,
    /// and never creates jobs, calls providers, touches object storage, or mints URLs.
    /// </summary>
    Task<MovieVideoMakeoverPreviewAdmissionDecision> EvaluateAsync(
        Guid userId,
        Guid movieProjectId,
        Guid sourceAssetId,
        Guid resultAssetId,
        CancellationToken cancellationToken);
}

public sealed class MovieVideoMakeoverPreviewAdmissionService(
    TaslimDbContext db,
    WorkspaceAccessService access) : IMovieVideoMakeoverPreviewAdmissionService
{
    public async Task<MovieVideoMakeoverPreviewAdmissionDecision> EvaluateAsync(
        Guid userId,
        Guid movieProjectId,
        Guid sourceAssetId,
        Guid resultAssetId,
        CancellationToken cancellationToken)
    {
        var project = await db.MovieProjects.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null)
            return new MovieVideoMakeoverPreviewAdmissionDecision(
                false,
                MovieVideoMakeoverPreviewAdmissionCodes.ProjectNotFound,
                "The Movie project for this preview no longer exists.",
                null,
                null,
                null);

        var isMember = await access.IsMemberAsync(userId, project.WorkspaceId, cancellationToken);
        var sourceAsset = await db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == sourceAssetId, cancellationToken);
        var resultAsset = await db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == resultAssetId, cancellationToken);
        GenerationJob? resultJob = null;
        if (resultAsset?.SourceGenerationJobId is Guid resultJobId)
            resultJob = await db.GenerationJobs.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == resultJobId, cancellationToken);

        return MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, isMember, sourceAsset, resultAsset, resultJob);
    }
}
