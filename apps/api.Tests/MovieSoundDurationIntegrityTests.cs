using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSoundDurationIntegrityTests
{
    private const int RequestedDurationMilliseconds = 750;

    [Theory]
    [InlineData(0, RequestedDurationMilliseconds)]
    [InlineData(-1, RequestedDurationMilliseconds)]
    [InlineData(RequestedDurationMilliseconds + 1, RequestedDurationMilliseconds)]
    public async Task Invalid_provider_reported_duration_is_rejected_before_publication(int reportedDurationMilliseconds, int maximumDurationMilliseconds)
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = reportedDurationMilliseconds });
        var handler = CreateHandler(provider, maximumDurationMilliseconds);

        await Assert.ThrowsAsync<MovieSoundOutputInvalidException>(() => handler.ExecuteAsync(
            CreateJob(request), new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task Provider_reported_duration_above_absolute_ceiling_is_rejected_before_publication()
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = 86_400_001 });
        var handler = CreateHandler(provider, int.MaxValue);

        await Assert.ThrowsAsync<MovieSoundOutputInvalidException>(() => handler.ExecuteAsync(
            CreateJob(request), new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task Provider_reported_duration_at_configured_maximum_is_accepted()
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = RequestedDurationMilliseconds });
        var handler = CreateHandler(provider, RequestedDurationMilliseconds);

        var result = await handler.ExecuteAsync(CreateJob(request), new Progress<int>(), CancellationToken.None);

        AssertPublishedDuration(result, RequestedDurationMilliseconds);
    }

    [Fact]
    public async Task Missing_provider_duration_uses_valid_requested_duration_before_publication()
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = null });
        var handler = CreateHandler(provider, RequestedDurationMilliseconds);

        var result = await handler.ExecuteAsync(CreateJob(request), new Progress<int>(), CancellationToken.None);

        AssertPublishedDuration(result, request.DurationMilliseconds);
    }

    private static MovieSoundGenerationInput CreateRequest(int durationMilliseconds = RequestedDurationMilliseconds) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null,
        MovieSoundKinds.SoundEffect, MovieSoundLayers.Foreground,
        "A soft latch", durationMilliseconds, null);

    private static GenerationJob CreateJob(MovieSoundGenerationInput request) => new()
    {
        Id = Guid.NewGuid(),
        InputJson = JsonSerializer.Serialize(request),
    };

    private static MovieSoundGenerationJobHandler CreateHandler(FixedMovieSoundProvider provider, int maximumDurationMilliseconds) => new(
        [provider],
        Options.Create(new MovieSoundOptions
        {
            Enabled = true,
            ProviderKey = provider.Key,
            MaxDurationMilliseconds = maximumDurationMilliseconds,
        }),
        NullLogger<MovieSoundGenerationJobHandler>.Instance);

    private static void AssertPublishedDuration(GenerationHandlerResult result, int expectedDurationMilliseconds)
    {
        using var resultDocument = JsonDocument.Parse(result.ResultJson);
        Assert.Equal(expectedDurationMilliseconds, resultDocument.RootElement.GetProperty("durationMilliseconds").GetInt32());

        var output = Assert.Single(result.Outputs);
        Assert.NotNull(output.FileArtifact);
        using var metadataDocument = JsonDocument.Parse(output.MetadataJson!);
        Assert.Equal(expectedDurationMilliseconds, metadataDocument.RootElement.GetProperty("durationMilliseconds").GetInt32());
    }

    private sealed class FixedMovieSoundProvider(MovieSoundProviderResult result) : IMovieSoundProvider
    {
        public string Key => "fixed";

        public Task<MovieSoundProviderResult> GenerateAsync(MovieSoundGenerationInput request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
