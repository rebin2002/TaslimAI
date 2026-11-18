using System.Linq.Expressions;
using Taslim.Api.Domain;

namespace Taslim.Api.Assets;

/// <summary>
/// Defines which asset metadata and backing files a workspace member may use.
/// Project assets are shared with workspace members, with one provenance exception:
/// Research report assets stay creator-private so the asset axis converges on the
/// creator-private research execution record (GenerationJobService.IsPrivateExecutionRecord),
/// because the report document embeds the synthesized source evidence of that creator's
/// run (#936). Personal assets and assets backed by personal or conversation-scoped
/// files remain private to the uploader. Invalid cross-workspace relationships are not
/// visible to any member.
/// </summary>
public static class AssetVisibility
{
    public static Expression<Func<Asset, bool>> ForWorkspaceMember(Guid userId) =>
        asset => (asset.AssetType != AssetTypes.Research || asset.CreatedByUserId == userId)
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
