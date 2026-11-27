using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSoundProjectionContractTests
{
    [Fact]
    public void Succeeded_generation_without_published_asset_is_projected_as_failed()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            Status = GenerationJobStatus.Succeeded,
            Assets = [],
        };
        var track = new MovieSoundTrack
        {
            Id = Guid.NewGuid(),
            MovieProjectId = Guid.NewGuid(),
            GenerationJobId = job.Id,
            GenerationJob = job,
            Kind = MovieSoundKinds.SoundEffect,
            Layer = MovieSoundLayers.Foreground,
            Name = "Missing result",
            Description = "A generated result with no published asset.",
            Status = MovieSoundStatuses.Queued,
        };

        var projected = MovieSoundTrackProjection.ToDto(track);

        Assert.Equal(MovieSoundStatuses.Failed, projected.Status);
        Assert.Null(projected.AssetId);
    }
}
