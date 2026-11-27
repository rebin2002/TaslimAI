using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotReadinessTests
{
    [Fact]
    public void Archived_timestamp_blocks_readiness_even_when_status_is_stale()
    {
        var shot = new MovieShot
        {
            Id = Guid.NewGuid(),
            ProductionStage = MovieProductionStages.ShotPlan,
            Status = MovieShotStatuses.Approved,
            ArchivedAt = DateTime.UtcNow,
            Description = "Mara crosses the harbor gate.",
            Purpose = "Reveal Mara's guarded decision.",
            Subjects = "Mara",
            LocationSet = "Harbor gate",
            DurationSeconds = 8,
            ProductionRequirements = "Keep the red lantern visible.",
            ContinuityReferences = "Matches the locked harbor continuity.",
            CameraAndFraming = "Medium-wide, eye level",
        };

        var readiness = MovieShotReadiness.Evaluate(shot);

        Assert.False(readiness.Ready);
        Assert.Contains("archived", readiness.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MovieShotPlanStates.Archived, MovieShotReadiness.PlanState(shot));
    }
}
