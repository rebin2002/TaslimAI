using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieGenerationLifecycleTests
{
    [Fact]
    public void Canonical_take_states_are_supported_without_exposing_provider_details()
    {
        var expected = new[]
        {
            MovieTakeStatuses.Planned,
            MovieTakeStatuses.Queued,
            MovieTakeStatuses.Running,
            MovieTakeStatuses.Succeeded,
            MovieTakeStatuses.Failed,
            MovieTakeStatuses.Cancelled,
            MovieTakeStatuses.Selected,
            MovieTakeStatuses.Superseded,
        };

        Assert.All(expected, status => Assert.Contains(status, MovieTakeStatuses.Supported));
        Assert.Equal(MovieTakeStatuses.Planned, MovieTakeStatuses.Draft);
        Assert.Equal(MovieTakeStatuses.Queued, MovieTakeStatuses.Generating);
        Assert.Equal(MovieTakeStatuses.Succeeded, MovieTakeStatuses.Ready);
    }

    [Theory]
    [InlineData(MovieTakeStatuses.Planned, MovieTakeStatuses.Queued, true)]
    [InlineData(MovieTakeStatuses.Queued, MovieTakeStatuses.Running, true)]
    [InlineData(MovieTakeStatuses.Running, MovieTakeStatuses.Succeeded, true)]
    [InlineData(MovieTakeStatuses.Running, MovieTakeStatuses.Failed, true)]
    [InlineData(MovieTakeStatuses.Running, MovieTakeStatuses.Cancelled, true)]
    [InlineData(MovieTakeStatuses.Succeeded, MovieTakeStatuses.Selected, true)]
    [InlineData(MovieTakeStatuses.Selected, MovieTakeStatuses.Superseded, true)]
    [InlineData(MovieTakeStatuses.Succeeded, MovieTakeStatuses.Running, false)]
    [InlineData(MovieTakeStatuses.Failed, MovieTakeStatuses.Succeeded, false)]
    [InlineData(MovieTakeStatuses.Cancelled, MovieTakeStatuses.Succeeded, false)]
    public void Lifecycle_policy_allows_only_forward_safe_transitions(string from, string to, bool expected)
    {
        Assert.Equal(expected, MovieTakeLifecycle.CanTransition(from, to));
    }

    [Fact]
    public void A_take_is_publishable_only_when_both_asset_and_file_are_real()
    {
        Assert.True(MovieTakeLifecycle.HasPublishedAsset(Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(MovieTakeLifecycle.HasPublishedAsset(null, Guid.NewGuid()));
        Assert.False(MovieTakeLifecycle.HasPublishedAsset(Guid.NewGuid(), null));
    }
}
