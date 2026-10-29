using Microsoft.EntityFrameworkCore;
using Taslim.Api.Images;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationPersistenceGuardTests
{
    [Fact]
    public void Unrelated_database_failure_is_not_reported_as_an_active_image_job_conflict()
    {
        var exception = new DbUpdateException(
            "Unrelated GenerationJobs check failed while inspecting WorkspaceId, CreatedByUserId, and JobType.",
            new InvalidOperationException("The database schema check failed."));

        Assert.False(ImageGenerationPersistenceGuards.IsActiveJobConflict(exception));
    }

    [Theory]
    [InlineData("SQLite Error 19: UNIQUE constraint failed: GenerationJobs.WorkspaceId, GenerationJobs.CreatedByUserId, GenerationJobs.JobType")]
    [InlineData("Cannot insert duplicate key row in object GenerationJobs for WorkspaceId, CreatedByUserId, JobType")]
    [InlineData("duplicate key value violates unique constraint for GenerationJobs (WorkspaceId, CreatedByUserId, JobType)")]
    public void Unique_active_job_constraint_failures_are_classified_as_conflicts(string message)
    {
        var exception = new DbUpdateException(message, new InvalidOperationException(message));

        Assert.True(ImageGenerationPersistenceGuards.IsActiveJobConflict(exception));
    }

    [Fact]
    public void Active_job_index_name_remains_a_sufficient_conflict_signal()
    {
        var exception = new DbUpdateException(
            "The database rejected the write at IX_GenerationJobs_ImageActiveByUser.",
            new InvalidOperationException("Constraint details unavailable."));

        Assert.True(ImageGenerationPersistenceGuards.IsActiveJobConflict(exception));
    }
}
