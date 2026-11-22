using System.Reflection;
using Taslim.Api.Activity;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// GATE-PRIV-01 drift guard. The two creator-private fences live in different lanes
/// (<c>ActivityCenterService.VisibleToMember</c> for the Activity Center and
/// <c>GenerationJobService.IsPrivateExecutionRecord</c> for the job read model) and nothing in the
/// suite tied them together, which is exactly how one could fence Research only while the other
/// fenced Research plus unscoped Presentation plus unscoped Social and both lanes stayed green.
/// This test asserts the two predicates accept the same records over the category matrix, so any
/// future narrowing on either side fails closed here instead of shipping a disclosure seam.
/// Pure in-memory reflection: no database, no provider, no paid path.
/// </summary>
public sealed class CreatorPrivateFenceEquivalenceTests
{
    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static readonly string[] CategoryMatrix =
    [
        GenerationJobTypes.ResearchGenerate,
        GenerationJobTypes.PresentationGenerate,
        GenerationJobTypes.SocialGenerate,
        GenerationJobTypes.ImageGenerate,
        GenerationJobTypes.DocumentGenerate,
        GenerationJobTypes.MusicGenerate,
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Activity_fence_and_job_read_model_fence_accept_the_same_records(bool scoped)
    {
        // Both another member's records (the creator-private candidates) and the principal's own.
        var jobs = CategoryMatrix.Select(jobType => NewJob(jobType, owner: CreatorId, scoped)).Concat(
                   CategoryMatrix.Select(jobType => NewJob(jobType, owner: MemberId, scoped))).ToList();

        AssertSameVisibility(jobs, MemberId);
        AssertSameVisibility(jobs, CreatorId);
    }

    [Fact]
    public void Unscoped_social_presentation_and_research_records_are_creator_private_on_both_surfaces()
    {
        var jobs = new List<GenerationJob>
        {
            NewJob(GenerationJobTypes.ResearchGenerate, owner: CreatorId, scoped: false),
            NewJob(GenerationJobTypes.SocialGenerate, owner: CreatorId, scoped: false),
            NewJob(GenerationJobTypes.PresentationGenerate, owner: CreatorId, scoped: false),
            NewJob(GenerationJobTypes.SocialGenerate, owner: CreatorId, scoped: true),
            NewJob(GenerationJobTypes.PresentationGenerate, owner: CreatorId, scoped: true),
            NewJob(GenerationJobTypes.ImageGenerate, owner: CreatorId, scoped: false),
            NewJob(GenerationJobTypes.SocialGenerate, owner: MemberId, scoped: false),
        };

        var memberVisible = ApplyActivityFence(jobs, MemberId);
        var creatorVisible = ApplyActivityFence(jobs, CreatorId);

        // Intent spelled out: a co-member must not receive another member's no-project
        // research/social/presentation record on either surface, while the creator keeps it.
        foreach (var index in new[] { 0, 1, 2 })
        {
            Assert.DoesNotContain(jobs[index], memberVisible);
            Assert.True((bool)Invoke(typeof(GenerationJobService), "IsPrivateExecutionRecord", jobs[index], MemberId)!,
                $"{jobs[index].JobType} must stay creator-private to a co-member on the job read model surface too.");
            Assert.Contains(jobs[index], creatorVisible);
        }

        // Project-scoped records and intentionally shared image activity stay shared, and the
        // principal's own unscoped record is never hidden from themself.
        Assert.Contains(jobs[3], memberVisible);
        Assert.Contains(jobs[4], memberVisible);
        Assert.Contains(jobs[5], memberVisible);
        Assert.Contains(jobs[6], memberVisible);
    }

    [Fact]
    public void Both_translatable_fences_rely_on_job_types_being_stored_in_canonical_form()
    {
        // The two queryable fences (ActivityCenterService.VisibleToMember and the job read model's
        // mirrored predicate) compare JobType with ordinal != so that EF can translate them to SQL,
        // while the in-memory IsPrivateExecutionRecord they must agree with compares
        // OrdinalIgnoreCase. Those only accept the same records while every persisted JobType is a
        // canonical GenerationJobTypes.Supported value; a mixed-case or padded row would make the
        // SQL side stop matching and the creator-private record would reappear in a co-member's
        // Activity Center - fail open. The enqueue path guarantees it by resolving the request
        // through Supported.First(...) before persisting, so the guarantee is pinned here rather
        // than left implicit: if a canonical constant is ever declared in a form that is not its
        // own trimmed lower-case value, this fails and the fences must be revisited together.
        foreach (var jobType in GenerationJobTypes.Supported)
        {
            Assert.Equal(jobType, jobType.Trim());
            Assert.Equal(jobType, jobType.ToLowerInvariant());
        }
    }

    private static void AssertSameVisibility(List<GenerationJob> jobs, Guid userId)
    {
        var expected = ApplyExecutionFence(jobs, userId).Select(job => job.Id).OrderBy(id => id);
        var actual = ApplyActivityFence(jobs, userId).Select(job => job.Id).OrderBy(id => id);
        Assert.Equal(expected, actual);
    }

    private static List<GenerationJob> ApplyActivityFence(List<GenerationJob> jobs, Guid userId) =>
        ((IQueryable<GenerationJob>)Invoke(typeof(ActivityCenterService), "VisibleToMember", jobs.AsQueryable(), userId)!).ToList();

    private static List<GenerationJob> ApplyExecutionFence(List<GenerationJob> jobs, Guid userId) =>
        jobs.Where(job => !(bool)Invoke(typeof(GenerationJobService), "IsPrivateExecutionRecord", job, userId)!).ToList();

    private static object? Invoke(Type type, string methodName, object first, Guid userId)
    {
        var info = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(info);
        return info!.Invoke(null, new object?[] { first, userId });
    }

    private static GenerationJob NewJob(string jobType, Guid owner, bool scoped) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = WorkspaceId,
        CreatedByUserId = owner,
        ProjectId = scoped ? ProjectId : null,
        JobType = jobType,
        Status = GenerationJobStatus.Succeeded,
        ProgressPercent = 100,
        Title = $"fence matrix {jobType} {(scoped ? "scoped" : "unscoped")}",
        InputJson = "{}",
        CreatedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow,
        ConcurrencyToken = Guid.NewGuid(),
    };
}
