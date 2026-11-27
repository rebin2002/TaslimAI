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
        file => ((file.ProjectId.HasValue
                && !file.ConversationId.HasValue
                && file.Project != null
                && file.Project.WorkspaceId == file.WorkspaceId)
            || file.UserId == userId);

    /// <summary>
    /// Report outputs embed the creator's source evidence and remain private even when
    /// their backing file has a project scope. Ordinary project files are unaffected.
    /// </summary>
    public static Expression<Func<StoredFile, bool>> IsNotResearchReportOwnedByAnotherMember(Guid userId) =>
        file => !file.GenerationJobOutputs.Any(output =>
                    output.OutputType == GenerationJobOutputTypes.StoredFile
                    && output.GenerationJob.JobType == GenerationJobTypes.ResearchGenerate
                    && output.GenerationJob.CreatedByUserId != userId);

    /// <summary>
    /// Includes a creator's private Research report outputs in project-scoped file
    /// views and counts without making their StoredFile rows project-shareable.
    /// </summary>
    public static Expression<Func<StoredFile, bool>> InProjectView(Guid projectId, Guid userId) =>
        file => file.ProjectId == projectId
            || (file.UserId == userId
                && file.GenerationJobOutputs.Any(output =>
                    output.OutputType == GenerationJobOutputTypes.StoredFile
                    && output.GenerationJob.JobType == GenerationJobTypes.ResearchGenerate
                    && output.GenerationJob.CreatedByUserId == userId
                    && output.GenerationJob.ProjectId == projectId));
}
