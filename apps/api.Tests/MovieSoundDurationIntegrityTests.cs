using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSoundDurationIntegrityTests
{
    [Fact]
    public async Task Provider_reported_non_positive_duration_is_rejected_before_persistence()
    {
        var request = new MovieSoundGenerationInput(
            Guid.NewGuid(), Guid.NewGuid(), null, null,
            MovieSoundKinds.SoundEffect, MovieSoundLayers.Foreground,
            "A soft latch", 750, null);
        var valid = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(valid with { DurationMilliseconds = 0 });
        var handler = new MovieSoundGenerationJobHandler(
            [provider],
            Options.Create(new MovieSoundOptions { Enabled = true, ProviderKey = provider.Key, MaxDurationMilliseconds = 750 }),
            NullLogger<MovieSoundGenerationJobHandler>.Instance);
        var job = new GenerationJob { Id = Guid.NewGuid(), InputJson = JsonSerializer.Serialize(request) };

        await Assert.ThrowsAsync<MovieSoundOutputInvalidException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));
    }

    private sealed class FixedMovieSoundProvider(MovieSoundProviderResult result) : IMovieSoundProvider
    {
        public string Key => "fixed";
        public Task<MovieSoundProviderResult> GenerateAsync(MovieSoundGenerationInput request, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
