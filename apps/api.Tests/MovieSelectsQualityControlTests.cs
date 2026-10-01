using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSelectsQualityControlTests
{
    private readonly MovieSelectsQualityControlService qc = new(
        Options.Create(new MovieSelectsQualityControlOptions()));

    [Fact]
    public void Complete_clean_segment_recommends_the_original_range_without_a_score()
    {
        var decision = qc.Evaluate(Request(Segment("shot-1", new(0m, 5m))));

        Assert.Equal(MovieSelectsQualityControlService.ContractVersion, decision.ContractVersion);
        Assert.Equal(MovieSelectsQcActions.RecommendSelects, decision.Action);
        Assert.False(decision.RequiresHumanReview);
        Assert.Equal([new MovieSelectsQcSelect("shot-1", new(0m, 5m))], decision.RecommendedSelects);
        Assert.DoesNotContain("score", JsonSerializer.Serialize(decision), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocking_issue_salvages_clean_ranges_before_and_after_the_issue()
    {
        var segment = Segment(
            "shot-1",
            new(0m, 5m),
            new MovieSelectsQcIssue(
                MovieSelectsQcIssueCategories.FaceIdentity,
                MovieSelectsQcSeverities.Blocking,
                "FACE_IDENTITY_UNSTABLE",
                new(2m, 3m),
                "inspection-frame-02"));

        var decision = qc.Evaluate(Request(segment));

        Assert.Equal(MovieSelectsQcActions.RecommendSelects, decision.Action);
        Assert.False(decision.RequiresHumanReview);
        Assert.Equal(
            [
                new MovieSelectsQcSelect("shot-1", new(0m, 2m)),
                new MovieSelectsQcSelect("shot-1", new(3m, 5m)),
            ],
            decision.RecommendedSelects);
        Assert.Contains(decision.Segments[0].Issues, issue => issue.Code == "FACE_IDENTITY_UNSTABLE");
    }

    [Fact]
    public void Warning_issue_keeps_a_select_recommendation_but_requires_human_review()
    {
        var segment = Segment(
            "shot-1",
            new(0m, 5m),
            new MovieSelectsQcIssue(
                MovieSelectsQcIssueCategories.Framing,
                MovieSelectsQcSeverities.Warning,
                "FRAMING_DRIFT",
                new(1m, 2m)));

        var decision = qc.Evaluate(Request(segment));

        Assert.Equal(MovieSelectsQcActions.RequireReview, decision.Action);
        Assert.True(decision.RequiresHumanReview);
        Assert.Equal([new MovieSelectsQcSelect("shot-1", new(0m, 5m))], decision.RecommendedSelects);
    }

    [Fact]
    public void Text_signage_is_not_required_unless_the_shot_declares_it_relevant()
    {
        var clean = qc.Evaluate(Request(Segment("shot-1", new(0m, 5m))));
        Assert.Equal(MovieSelectsQcActions.RecommendSelects, clean.Action);

        var requirements = new MovieSelectsQcRequirements(
            [.. MovieSelectsQcIssueCategories.DefaultRelevant, MovieSelectsQcIssueCategories.TextSignage]);
        var signageRequired = qc.Evaluate(new MovieSelectsQcRequest(
            [Segment("shot-1", new(0m, 5m))], requirements));

        Assert.Equal(MovieSelectsQcActions.RequireReview, signageRequired.Action);
        Assert.True(signageRequired.RequiresHumanReview);
        Assert.Empty(signageRequired.RecommendedSelects);
        Assert.Contains(MovieSelectsQcReasonCodes.EvidenceMissing, signageRequired.ReasonCodes);
    }

    [Fact]
    public void Missing_required_evidence_does_not_claim_a_clean_select()
    {
        var segment = Segment("shot-1", new(0m, 5m), checks: [
            new(MovieSelectsQcIssueCategories.FaceIdentity, MovieSelectsQcCheckStatuses.Missing)]);

        var decision = qc.Evaluate(Request(segment));

        Assert.Equal(MovieSelectsQcActions.RequireReview, decision.Action);
        Assert.Empty(decision.RecommendedSelects);
        Assert.Contains(MovieSelectsQcReasonCodes.EvidenceMissing, decision.ReasonCodes);
    }

    [Fact]
    public void Fully_blocked_segment_has_no_usable_range_and_never_requests_regeneration()
    {
        var segment = Segment(
            "shot-1",
            new(0m, 5m),
            new MovieSelectsQcIssue(
                MovieSelectsQcIssueCategories.Artifact,
                MovieSelectsQcSeverities.Blocking,
                "ARTIFACT_FULL_RANGE",
                new(0m, 5m)));

        var decision = qc.Evaluate(Request(segment));

        Assert.Equal(MovieSelectsQcActions.NoUsableRange, decision.Action);
        Assert.True(decision.RequiresHumanReview);
        Assert.Empty(decision.RecommendedSelects);
        Assert.DoesNotContain("regenerat", JsonSerializer.Serialize(decision), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_blocking_issue_outside_the_declared_segment_is_invalid_evidence()
    {
        var segment = Segment(
            "shot-1",
            new(1m, 4m),
            new MovieSelectsQcIssue(
                MovieSelectsQcIssueCategories.Continuity,
                MovieSelectsQcSeverities.Blocking,
                "CONTINUITY_BREAK",
                new(0m, 2m)));

        var decision = qc.Evaluate(Request(segment));

        Assert.Equal(MovieSelectsQcActions.RequireReview, decision.Action);
        Assert.Empty(decision.RecommendedSelects);
        Assert.Contains(MovieSelectsQcReasonCodes.EvidenceInvalid, decision.ReasonCodes);
    }

    [Fact]
    public void Same_evidence_produces_byte_stable_json_and_no_provider_metadata()
    {
        var request = Request(
            Segment(
                "b-segment",
                new(3m, 7m),
                new MovieSelectsQcIssue(
                    MovieSelectsQcIssueCategories.Motion,
                    MovieSelectsQcSeverities.Blocking,
                    "MOTION_BREAK",
                    new(5m, 6m))),
            Segment("a-segment", new(0m, 2m)));

        var first = JsonSerializer.Serialize(qc.Evaluate(request));
        var second = JsonSerializer.Serialize(qc.Evaluate(request));

        Assert.Equal(first, second);
        Assert.DoesNotContain("provider", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", first, StringComparison.OrdinalIgnoreCase);
    }

    private static MovieSelectsQcRequest Request(params MovieSelectsQcSegmentEvidence[] segments) =>
        new(segments);

    private static MovieSelectsQcSegmentEvidence Segment(
        string id,
        MovieSelectsQcRange range,
        params MovieSelectsQcIssue[] issues) =>
        Segment(id, range, [
            Check(MovieSelectsQcIssueCategories.FaceIdentity, issues),
            Check(MovieSelectsQcIssueCategories.Motion, issues),
            Check(MovieSelectsQcIssueCategories.Artifact, issues),
            Check(MovieSelectsQcIssueCategories.Continuity, issues),
            Check(MovieSelectsQcIssueCategories.Framing, issues),
        ], issues);

    private static MovieSelectsQcCheck Check(string category, IReadOnlyList<MovieSelectsQcIssue> issues) =>
        new(category, issues.Any(issue => issue.Category == category)
            ? MovieSelectsQcCheckStatuses.Issue
            : MovieSelectsQcCheckStatuses.Passed);

    private static MovieSelectsQcSegmentEvidence Segment(
        string id,
        MovieSelectsQcRange range,
        IReadOnlyList<MovieSelectsQcCheck> checks,
        params MovieSelectsQcIssue[] issues) =>
        new(id, range, checks, issues);
}
