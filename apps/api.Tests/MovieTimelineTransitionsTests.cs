using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieTimelineTransitionsTests
{
    private static readonly Guid TimelineId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ClipA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ClipB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid ClipC = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid CutId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid DissolveId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid FadeInId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid FadeOutId = Guid.Parse("30000000-0000-0000-0000-000000000004");

    [Fact]
    public void Canonical_timeline_accepts_cut_dissolve_and_both_fade_edges()
    {
        var result = MovieTimelineValidator.Validate(CanonicalTimeline());

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(item => item.Message)));
    }

    [Fact]
    public void Dissolve_must_match_the_exact_overlap_between_adjacent_clips()
    {
        var timeline = CanonicalTimeline() with
        {
            Clips = [
                new MovieTimelineClipContract(ClipA, Guid.NewGuid(), 1, 0m, 10m),
                new MovieTimelineClipContract(ClipB, Guid.NewGuid(), 2, 10m, 8m),
                new MovieTimelineClipContract(ClipC, Guid.NewGuid(), 3, 17m, 6m),
            ],
        };

        var result = MovieTimelineValidator.Validate(timeline);

        Assert.Contains(result.Errors, item => item.Code == MovieTimelineValidationCodes.TransitionBoundaryInvalid);
    }

    [Fact]
    public void Fade_in_and_fade_out_require_one_clip_edge_only()
    {
        var timeline = CanonicalTimeline() with
        {
            Transitions = [
                new MovieTimelineTransitionContract(FadeInId, MovieTimelineTransitionTypes.FadeIn, ClipA, ClipA, 0m, 1m),
                new MovieTimelineTransitionContract(FadeOutId, MovieTimelineTransitionTypes.FadeOut, null, ClipC, 22m, 2m),
            ],
        };

        var result = MovieTimelineValidator.Validate(timeline);

        Assert.True(result.Errors.Count(item => item.Code == MovieTimelineValidationCodes.TransitionEndpointInvalid) >= 2);
    }

    [Fact]
    public void Director_recommendation_is_not_applicable_without_user_override()
    {
        var timeline = CanonicalTimeline();
        var recommendation = new MovieDirectorTransitionRecommendationContract(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            TimelineId,
            timeline.Version,
            FadeOutId,
            timeline.Transitions.Single(item => item.Id == FadeOutId),
            "The final image should leave the viewer in a quieter visual state.");
        var decision = new MovieTimelineEditDecisionContract(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            TimelineId,
            timeline.Version,
            MovieTimelineEditActions.Update,
            FadeOutId,
            timeline.Transitions.Single(item => item.Id == FadeOutId),
            recommendation,
            null);

        var result = MovieTimelineValidator.ValidateEditDecision(timeline, decision);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, item => item.Code == MovieTimelineValidationCodes.OverrideRequired);
        Assert.Throws<MovieTimelineValidationException>(() => MovieTimelineAuthority.ApplyUserOverride(timeline, decision));
    }

    [Fact]
    public void User_override_creates_a_new_authoritative_version_and_keeps_recommendation_provenance_separate()
    {
        var timeline = CanonicalTimeline();
        var recommendation = new MovieDirectorTransitionRecommendationContract(
            Guid.Parse("40000000-0000-0000-0000-000000000002"), TimelineId, timeline.Version, FadeOutId,
            new MovieTimelineTransitionContract(FadeOutId, MovieTimelineTransitionTypes.FadeOut, ClipC, null, 18m, 4m),
            "Extend the final fade for the closing beat.");
        var overrideRecord = new MovieUserTransitionOverrideContract(
            Guid.Parse("60000000-0000-0000-0000-000000000001"), TimelineId, timeline.Version, FadeOutId,
            new MovieTimelineTransitionContract(FadeOutId, MovieTimelineTransitionTypes.FadeOut, ClipC, null, 18m, 4m),
            "Keep the longer fade for the user-approved final cut.",
            recommendation.RecommendationId);
        var decision = new MovieTimelineEditDecisionContract(
            Guid.Parse("50000000-0000-0000-0000-000000000002"), TimelineId, timeline.Version,
            MovieTimelineEditActions.Update, FadeOutId, overrideRecord.ProposedTransition, recommendation, overrideRecord);

        var next = MovieTimelineAuthority.ApplyUserOverride(timeline, decision);

        Assert.Equal(timeline.Version + 1, next.Version);
        Assert.Equal(4m, next.Transitions.Single(item => item.Id == FadeOutId).DurationSeconds);
        Assert.Equal(18m, next.Transitions.Single(item => item.Id == FadeOutId).StartSeconds);
        Assert.Equal(timeline.TimelineId, next.TimelineId);
        Assert.Equal(4, next.Transitions.Count);
    }

    [Fact]
    public void Stale_decisions_are_rejected_before_any_timeline_change()
    {
        var timeline = CanonicalTimeline();
        var decision = new MovieTimelineEditDecisionContract(
            Guid.Parse("50000000-0000-0000-0000-000000000003"), TimelineId, timeline.Version - 1,
            MovieTimelineEditActions.Remove, FadeInId, null, null,
            new MovieUserTransitionOverrideContract(Guid.Parse("60000000-0000-0000-0000-000000000002"), TimelineId, timeline.Version - 1, FadeInId, null, "Remove the opening fade."));

        var result = MovieTimelineValidator.ValidateEditDecision(timeline, decision);

        Assert.Contains(result.Errors, item => item.Code == MovieTimelineValidationCodes.TimelineVersionConflict);
    }

    [Fact]
    public void Validation_errors_are_stable_in_path_then_code_order()
    {
        var timeline = new MovieCanonicalTimelineContract(Guid.Empty, 0, [
            new MovieTimelineClipContract(Guid.Empty, Guid.Empty, 0, -1m, 0m),
        ], [
            new MovieTimelineTransitionContract(Guid.Empty, "unknown", null, null, -1m, -1m),
        ]);

        var first = MovieTimelineValidator.Validate(timeline);
        var second = MovieTimelineValidator.Validate(timeline);

        Assert.Equal(first.Errors, second.Errors);
        Assert.Equal(first.Errors.OrderBy(item => item.Path).ThenBy(item => item.Code), first.Errors);
    }

    private static MovieCanonicalTimelineContract CanonicalTimeline() => new(
        TimelineId,
        7,
        [
            new MovieTimelineClipContract(ClipA, Guid.Parse("70000000-0000-0000-0000-000000000001"), 1, 0m, 10m),
            new MovieTimelineClipContract(ClipB, Guid.Parse("70000000-0000-0000-0000-000000000002"), 2, 10m, 8m),
            new MovieTimelineClipContract(ClipC, Guid.Parse("70000000-0000-0000-0000-000000000003"), 3, 16m, 6m),
        ],
        [
            new MovieTimelineTransitionContract(CutId, MovieTimelineTransitionTypes.Cut, ClipA, ClipB, 10m, 0m),
            new MovieTimelineTransitionContract(DissolveId, MovieTimelineTransitionTypes.Dissolve, ClipB, ClipC, 16m, 2m),
            new MovieTimelineTransitionContract(FadeInId, MovieTimelineTransitionTypes.FadeIn, null, ClipA, 0m, 1m),
            new MovieTimelineTransitionContract(FadeOutId, MovieTimelineTransitionTypes.FadeOut, ClipC, null, 20m, 2m),
        ]);
}
