using Taslim.Api.Domain;

namespace Taslim.Api.Activity;

/// <summary>
/// Shared, SQL-translatable record-visibility policy for generation-job read surfaces.
/// Callers remain responsible for the ordinary workspace/project membership check; this
/// fence only determines which rows an authorized member may see.
/// </summary>
public static class CreatorPrivacyFence
{
    /// <summary>
    /// Research records remain private to their creator even when project-scoped. Presentation
    /// and Social records are creator-private only when unscoped; project-scoped records preserve
    /// the existing workspace-sharing contract. All other job types keep their existing visibility.
    /// </summary>
    public static IQueryable<GenerationJob> VisibleToMember(IQueryable<GenerationJob> jobs, Guid userId) =>
        jobs.Where(job => job.CreatedByUserId == userId
            || (job.JobType.ToLower() != GenerationJobTypes.ResearchGenerate
                && (job.JobType.ToLower() != GenerationJobTypes.PresentationGenerate || job.ProjectId != null)
                && (job.JobType.ToLower() != GenerationJobTypes.SocialGenerate || job.ProjectId != null)));
}
