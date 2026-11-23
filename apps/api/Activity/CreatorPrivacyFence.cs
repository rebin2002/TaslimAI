using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;

namespace Taslim.Api.Activity;

/// <summary>
/// The single translatable form of the creator-private invariant shipped by
/// <c>GenerationJobExecution.IsPrivateExecutionRecord</c> (and mirrored by
/// <c>GenerationJobService.IsPrivateExecutionRecord</c>) for every read surface that projects a
/// <see cref="GenerationJob"/> at a member: the job list, the Activity Center feed and its totals,
/// the unread badge, the direct read endpoint, mark-all-read, and the project overview
/// (<c>ProjectsController.Overview</c>).
///
/// A Research record embeds report text derived from its creator's private uploads, so it stays
/// private to its creator even when it is project-scoped; a Presentation or Social record is
/// private only while it carries no project, because a project-scoped job keeps the intentional
/// workspace sharing surface for both the record and the asset it exports.
///
/// This helper exists so those surfaces cannot disagree about who may see a record. Each one used to
/// carry its own copy of the predicate, and the copies drifted: the job read model hid a
/// project-scoped research record while the project overview still counted and rendered it, letting
/// a co-member read another member's private record through a fourth path. <c>GATE-PRIV</c> coverage:
/// <c>CreatorPrivateFenceEquivalenceTests</c> proves this predicate is equivalent to the canonical
/// one, and the <c>Activity*CreatorPrivacyHttpTests</c> / <c>ProjectOverviewCreatorPrivacyHttpTests</c>
/// families pin the observable behaviour of every surface that uses it.
/// </summary>
public static class CreatorPrivacyFence
{
    public static IQueryable<GenerationJob> VisibleToMember(IQueryable<GenerationJob> jobs, Guid userId) =>
        jobs.Where(job => job.CreatedByUserId == userId
            || (job.JobType != GenerationJobTypes.ResearchGenerate
                && (job.JobType != GenerationJobTypes.PresentationGenerate || job.ProjectId != null)
                && (job.JobType != GenerationJobTypes.SocialGenerate || job.ProjectId != null)));
}
