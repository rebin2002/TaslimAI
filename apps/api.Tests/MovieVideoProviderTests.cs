using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieVideoProviderTests
{
    [Fact]
    public void Unconfigured_provider_is_not_ready_and_exposes_no_vendor_operations()
    {
        var provider = new UnavailableMovieVideoProvider();

        Assert.False(provider.IsAvailable);
        Assert.Equal("unconfigured", provider.Key);
        Assert.Empty(provider.SupportedOperations);
    }

    [Fact]
    public async Task Unconfigured_provider_never_emits_a_fake_submission_or_output()
    {
        var provider = new UnavailableMovieVideoProvider();
        var request = new MovieVideoGenerationRequest(
            MovieStudioOperations.QuickMovie,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            "A short film brief.",
            30,
            "16:9",
            "cinematic",
            "en",
            null,
            null,
            null,
            null);

        await Assert.ThrowsAsync<MovieProviderUnavailableException>(() => provider.SubmitAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<MovieProviderUnavailableException>(() => provider.GetStatusAsync("provider-job", CancellationToken.None));
        await Assert.ThrowsAsync<MovieProviderUnavailableException>(() => provider.RetrieveAsync("provider-job", new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Succeeded, 100), CancellationToken.None));
    }

    [Fact]
    public void Movie_video_defaults_are_bounded_for_restart_safe_polling()
    {
        var options = new MovieVideoOptions();

        Assert.InRange(options.MaxStatusPolls, 1, 10_000);
        Assert.InRange(options.MaxTransientRetries, 0, 8);
        Assert.InRange(options.StatusPollIntervalSeconds, 1, 300);
        Assert.True(options.MaxOutputBytes > 0);
    }
}
