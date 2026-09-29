using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCinematographyPlanningTests
{
    [Fact]
    public void Structured_plan_accepts_supported_values_and_round_trips_without_provider_fields()
    {
        var plan = new CinematographyShotPlan(
            "close_up", "single", "eye_level", "three_quarter", "push_in", "isolate", "portrait_natural", "shallow", "subject_locked", "soft_motivated", "eyes", "cut",
            "Keep the eyeline open.",
            [new CinematographyGroundingReference("story", "The confession is the emotional turn.")]);

        Assert.Null(CinematographyShotPlanValidator.Validate(plan));
        var json = CinematographyShotPlanValidator.ToJson(plan);
        var roundTrip = CinematographyShotPlanValidator.FromJson(json);

        Assert.NotNull(roundTrip);
        Assert.Equal("close_up", roundTrip!.ShotSize);
        Assert.Equal("eyes", roundTrip.SubjectEmphasis);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_plan_rejects_values_outside_the_closed_catalog()
    {
        var invalid = new CinematographyShotPlan(
            "provider_magic", "single", "eye_level", "front", "locked_off", "establish", "neutral", "deep", "subject_locked", "naturalistic", "primary_subject", "cut");

        Assert.Equal("Every cinematography plan field must use a supported structured value.", CinematographyShotPlanValidator.Validate(invalid));
    }

    [Fact]
    public void Planner_grounds_decisions_and_locked_guide_canon_wins_over_inference_and_overrides()
    {
        var lockedCanon = new CinematographyShotPlan(
            "close_up", "single", "eye_level", "front", "locked_off", "isolate", "portrait_natural", "shallow", "selective", "soft_motivated", "eyes", "hold",
            "Locked eyeline rule.");
        var context = new CinematographyPlanningContext(
            "9:16",
            "A guarded character finally confesses what she knows.",
            "INT. QUIET ROOM — NIGHT; the scene holds on the silence.",
            "Her eyes lift toward the listener.",
            "Vulnerable confession; make the eyes carry the turn.",
            "Portrait-forward framing with a protected eyeline.",
            lockedCanon,
            3,
            ["The character remains seated.", "Screen direction stays left to right."],
            "A previous medium shot holds on the listener.");
        var requested = lockedCanon with
        {
            ShotSize = "wide",
            CameraMovement = "crane",
            LightingIntent = "high_key",
            CreativeNotes = "Optional planning note."
        };

        var result = MovieCinematographyPlanner.Plan(context, requested);

        Assert.True(result.GuideGrounded);
        Assert.True(result.CanonPreserved);
        Assert.Equal(3, result.Plan.LockedGuideRevisionNumber);
        Assert.Equal("close_up", result.Plan.ShotSize);
        Assert.Equal("locked_off", result.Plan.CameraMovement);
        Assert.Equal("soft_motivated", result.Plan.LightingIntent);
        Assert.Contains("ShotSize", result.AppliedCanonFields);
        Assert.Contains(result.Plan.Grounding!, item => item.Source == "story");
        Assert.Contains(result.Plan.Grounding!, item => item.Source == "cinematography_bible" && item.Locked);
        Assert.Contains(result.Plan.Grounding!, item => item.Source == "continuity" && item.Locked);
    }
}
