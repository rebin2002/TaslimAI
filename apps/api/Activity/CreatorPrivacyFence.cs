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
///
/// GATE-PRIV-05 (independent review of the round-17 head): the comparison folds the stored
/// <c>JobType</c> with <see cref="string.ToLower()"/> because the canonical in-memory rule compares
/// <c>OrdinalIgnoreCase</c>, while a bare SQL <c>&lt;&gt;</c> is case-sensitive on the providers that
/// run this product. Without the fold, a mixed-case row persisted outside the normalising enqueue
/// path (an external or legacy writer) was hidden by direct job access yet stayed visible here -
/// fail open. The fold makes the translatable rule accept exactly the rows the canonical rule
/// hides; see <c>CreatorPrivacyFenceMixedCaseHttpTests</c> for the database-backed proof.
/// </summary>
public static class CreatorPrivacyFence
{
    public static IQueryable<GenerationJob> VisibleToMember(IQueryable<GenerationJob> jobs, Guid userId) =>
        jobs.Where(job => job.CreatedByUserId == userId
            // The constants are pinned canonical lowercase by the equivalence suite, so
            // lower(JobType) != constant is the SQL translation of the ignore-case comparison
            // over this domain, and any case variant of a private class fails closed to its
            // creator instead of slipping through on casing.
            || (job.JobType.ToLower() != GenerationJobTypes.ResearchGenerate
                && (job.JobType.ToLower() != GenerationJobTypes.PresentationGenerate || job.ProjectId != null)
                && (job.JobType.ToLower() != GenerationJobTypes.SocialGenerate || job.ProjectId != null)));
}
