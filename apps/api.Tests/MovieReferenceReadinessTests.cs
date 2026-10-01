using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieReferenceReadinessTests
{
    [Fact]
    public void Ten_reference_dimensions_are_aggregated_into_an_explainable_blocked_result()
    {
        var items = new[]
        {
            Complete("character"),
            Complete("wardrobe"),
            Blocked("location", overrideAllowed: false),
            Complete("lighting"),
            Warning("props"),
            Complete("spatial_orientation"),
            Complete("camera_plan"),
            Complete("dialogue"),
            Blocked("continuity", overrideAllowed: false),
            Warning("transition_out"),
        };

        Assert.Equal(60, MovieReferenceReadinessPolicy.CalculatePercentage(items));
        Assert.Equal(MovieReferenceReadinessStatuses.Blocked, MovieReferenceReadinessPolicy.DetermineStatus(items));
        Assert.False(MovieReferenceReadinessPolicy.IsOverrideAllowed(items));
    }

    [Fact]
    public void A_blocker_is_safely_overridable_only_when_every_blocked_item_allows_it()
    {
        var safe = new[]
        {
            Complete("character"),
            Blocked("lighting", overrideAllowed: true),
            Blocked("spatial_orientation", overrideAllowed: true),
        };
        var unsafeItems = safe.Append(Blocked("location", overrideAllowed: false)).ToArray();

        Assert.True(MovieReferenceReadinessPolicy.IsOverrideAllowed(safe));
        Assert.False(MovieReferenceReadinessPolicy.IsOverrideAllowed(unsafeItems));
    }

    [Fact]
    public void Warnings_need_review_but_do_not_block_generation()
    {
        var items = new[]
        {
            Complete("character"),
            Warning("dialogue"),
            Warning("transition_out"),
        };

        Assert.Equal(33, MovieReferenceReadinessPolicy.CalculatePercentage(items));
        Assert.Equal(MovieReferenceReadinessStatuses.NeedsReview, MovieReferenceReadinessPolicy.DetermineStatus(items));
        Assert.DoesNotContain(items, item => item.BlocksGeneration);
    }

    private static MovieReferenceReadinessItemDto Complete(string key) =>
        new(key, key, MovieReferenceReadinessItemStates.Complete, true, false, true, $"{key} is complete.");

    private static MovieReferenceReadinessItemDto Warning(string key) =>
        new(key, key, MovieReferenceReadinessItemStates.Warning, false, false, true, $"{key} needs review.");

    private static MovieReferenceReadinessItemDto Blocked(string key, bool overrideAllowed) =>
        new(key, key, MovieReferenceReadinessItemStates.Missing, true, true, overrideAllowed, $"{key} is blocked.");
}
