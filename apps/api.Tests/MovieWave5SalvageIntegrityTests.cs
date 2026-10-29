using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieWave5SalvageIntegrityTests
{
    [Fact]
    public void Salvage_plan_does_not_mix_conflicting_continuity_anchors()
    {
        var shotId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var candidates = new[]
        {
            new MovieWave5SegmentCandidate(Guid.Parse("22222222-2222-2222-2222-222222222222"), "opening", 0m, 2m, true, "anchor-a", "left-to-right"),
            new MovieWave5SegmentCandidate(Guid.Parse("33333333-3333-3333-3333-333333333333"), "middle", 2m, 2m, true, "anchor-b", "right-to-left"),
        };

        var plan = MovieWave5IntegrationContract.BuildSalvagePlan(shotId, candidates, 4m);

        Assert.True(plan.RequiresRegeneration);
        Assert.Single(plan.SelectedSegments);
        var insert = Assert.Single(plan.Inserts);
        Assert.Equal(2m, insert.StartSeconds);
        Assert.Equal(2m, insert.DurationSeconds);
    }
}
