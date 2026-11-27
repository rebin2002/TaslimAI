using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotBenchmarkTemporalIntegrityTests
{
    [Fact]
    public void Empirical_evidence_rejects_future_observed_at_timestamps()
    {
        var request = new MovieShotBenchmarkRequest(
            ["candidate-a"],
            [new(MovieShotBenchmarkTypes.Motion, 100)]);
        var evidence = new MovieShotBenchmarkEvidence(
            "candidate-a",
            MovieShotBenchmarkTypes.Motion,
            0.80m,
            5,
            0.75m,
            new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "bench-1",
            "future-study");

        var errors = MovieShotBenchmarkScorer.Validate(request, [evidence]);

        Assert.Contains(errors, item => item.Code == "future_timestamp" && item.Field.Contains("ObservedAtUtc", StringComparison.Ordinal));
    }
}
