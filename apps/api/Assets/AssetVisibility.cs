using System.Linq.Expressions;
using Taslim.Api.Domain;

namespace Taslim.Api.Assets;

/// <summary>
/// Defines which asset metadata and backing files a workspace member may use.
/// Project assets are shared with workspace members, except Research reports produced by
/// a ResearchGenerate job: their content embeds the creator's source evidence, so the
/// report asset converges on the creator-private execution record (#936). Research-typed
/// assets without that provenance and other project assets keep normal project sharing.
/// Personal assets and assets backed by personal or conversation-scoped files remain
/// private to the uploader. Invalid cross-workspace relationships are not visible to any
/// member.
/// </summary>
public static class AssetVisibility
{
    public static Expression<Func<Asset, bool>> ForWorkspaceMember(Guid userId) =>
        asset => (asset.AssetType != AssetTypes.Research
                || asset.SourceGenerationJobId == null
                || asset.SourceGenerationJob!.JobType != GenerationJobTypes.ResearchGenerate
                || asset.CreatedByUserId == userId)
            && ((asset.ProjectId.HasValue
                && asset.Project != null
                && asset.Project.WorkspaceId == asset.WorkspaceId)
            || (!asset.ProjectId.HasValue && asset.CreatedByUserId == userId))
            && (asset.CreatedByUserId == userId
                || !asset.StoredFileId.HasValue
                || (asset.StoredFile!.WorkspaceId == asset.WorkspaceId
                    && asset.StoredFile.ProjectId.HasValue
                    && !asset.StoredFile.ConversationId.HasValue
                    && asset.StoredFile.Project != null
                    && asset.StoredFile.Project.WorkspaceId == asset.WorkspaceId));
}
