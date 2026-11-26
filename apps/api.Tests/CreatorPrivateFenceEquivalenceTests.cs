using System.Reflection;
using Taslim.Api.Activity;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// GATE-PRIV-01 drift guard. Activity Center, project overview and the generation-job list share
/// one provider-aware SQL predicate (<c>CreatorPrivacyFence.VisibleToMember</c>); direct reads use
/// the in-memory <c>GenerationJobService.IsPrivateExecutionRecord</c> rule. This test asserts that
/// the shared query predicate and direct-read rule accept the same records over the category matrix,
/// so future narrowing cannot ship a disclosure seam. The in-memory invocation uses SQLite's
/// ASCII-folding branch; provider-specific SQL behavior is exercised by the SQLite and PostgreSQL
/// integration tests.
/// Pure in-memory evaluation: no database connection or paid path.
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

    [Theory]
    [InlineData("Research.Generate")]
    [InlineData("RESEARCH.GENERATE")]
    [InlineData("Presentation.Generate")]
    [InlineData("SOCIAL.GENERATE")]
    public void Activity_fence_and_job_read_model_fence_agree_on_mixed_case_job_type_variants(string variant)
    {
        // GATE-PRIV-05 (independent review finding on the round-17 head): the canonical in-memory
        // rule compares JobType OrdinalIgnoreCase, but the translatable SQL fences compared it
        // ordinal-case-sensitive, so a row persisted with a case variant - the only way that
        // happens is an external or legacy writer bypassing the enqueue normalisation - was hidden
        // by direct job access yet stayed visible on the translatable surfaces. The fences must
        // accept the same records for every casing of the private classes, on both viewpoints.
        var jobs = new[]
        {
            NewJob(variant, owner: CreatorId, scoped: false),
            NewJob(variant, owner: CreatorId, scoped: true),
            NewJob(variant, owner: MemberId, scoped: false),
        }.ToList();

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
    public void Shared_translatable_fence_uses_canonical_job_type_constants()
    {
        // The shared SQL predicate folds persisted JobType values using PostgreSQL C collation or
        // SQLite's ASCII lower(); both compare against these canonical lowercase constants. The
        // mixed-case equivalence theory above and provider integration tests pin the behavior. The
        // enqueue path additionally persists canonical values by resolving requests through
        // Supported.First(...) before saving.
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
        CreatorPrivacyFence.VisibleToMember(jobs.AsQueryable(), userId, CreatorPrivacyFence.SqliteProviderName).ToList();

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
