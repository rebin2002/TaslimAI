using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;

namespace Taslim.Api.Activity;

/// <summary>
/// Shared, SQL-translatable record-visibility policy for generation-job read surfaces.
/// Callers remain responsible for the ordinary workspace/project membership check; this
/// fence only determines which rows an authorized member may see.
/// </summary>
public static class CreatorPrivacyFence
{
    public const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";
    public const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Research records remain creator-private even when project-scoped. Presentation and Social
    /// records are creator-private only when unscoped; project-scoped records preserve the existing
    /// workspace-sharing contract. PostgreSQL folds the ASCII job identifiers under its built-in
    /// C collation so a column-level locale cannot change classification; SQLite retains its
    /// built-in ASCII-only lower() behavior for these identifiers.
    /// </summary>
    public static IQueryable<GenerationJob> VisibleToMember(
        IQueryable<GenerationJob> jobs,
        Guid userId,
        string? providerName) => providerName switch
    {
        PostgreSqlProviderName => VisibleToMemberOnPostgreSql(jobs, userId),
        SqliteProviderName => VisibleToMemberOnSqlite(jobs, userId),
        _ => throw new NotSupportedException($"Creator privacy SQL is not configured for provider '{providerName ?? "<null>"}'."),
    };

    private static IQueryable<GenerationJob> VisibleToMemberOnPostgreSql(IQueryable<GenerationJob> jobs, Guid userId) =>
        jobs.Where(job => job.CreatedByUserId == userId
            || (EF.Functions.Collate(job.JobType, "C").ToLower() != GenerationJobTypes.ResearchGenerate
                && (EF.Functions.Collate(job.JobType, "C").ToLower() != GenerationJobTypes.PresentationGenerate || job.ProjectId != null)
                && (EF.Functions.Collate(job.JobType, "C").ToLower() != GenerationJobTypes.SocialGenerate || job.ProjectId != null)));

    private static IQueryable<GenerationJob> VisibleToMemberOnSqlite(IQueryable<GenerationJob> jobs, Guid userId) =>
        jobs.Where(job => job.CreatedByUserId == userId
            || (job.JobType.ToLower() != GenerationJobTypes.ResearchGenerate
                && (job.JobType.ToLower() != GenerationJobTypes.PresentationGenerate || job.ProjectId != null)
                && (job.JobType.ToLower() != GenerationJobTypes.SocialGenerate || job.ProjectId != null)));
}
