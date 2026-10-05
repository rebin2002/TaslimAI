using System.Linq.Expressions;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

/// <summary>
/// Defines the visibility boundary for StoredFile metadata and source content.
/// Project files are shared with workspace members; personal and conversation
/// files remain visible only to their uploader.
/// </summary>
public static class StoredFileVisibility
{
    public static Expression<Func<StoredFile, bool>> ForWorkspaceMember(Guid userId) =>
        file => (file.ProjectId.HasValue && !file.ConversationId.HasValue) || file.UserId == userId;
}
