using System.Linq.Expressions;
using Taslim.Api.Domain;

namespace Taslim.Api.Assets;

/// <summary>
/// Defines which asset metadata and backing files a workspace member may use.
/// Project assets are shared with workspace members; personal assets and assets
/// backed by personal or conversation-scoped files remain private to the uploader.
/// </summary>
public static class AssetVisibility
{
    public static Expression<Func<Asset, bool>> ForWorkspaceMember(Guid userId) =>
        asset => (asset.ProjectId.HasValue || asset.CreatedByUserId == userId)
            && (asset.CreatedByUserId == userId
                || !asset.StoredFileId.HasValue
                || (asset.StoredFile!.WorkspaceId == asset.WorkspaceId
                    && (asset.StoredFile.UserId == userId
                        || (asset.StoredFile.ProjectId.HasValue && !asset.StoredFile.ConversationId.HasValue))));
}
