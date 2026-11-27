using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

// Source-lane regression tests for the fail-closed PER-SCENE admission ceiling in
// MovieDirectorScenePlanValidator (#685, Director scene-plan duration seam).
//
// Scope of this file: only the per-scene bound. The separate project/context target checks re-cut by
// #1078 (payload.targetDurationSeconds) are asserted to stay independent and untouched here, and the
// QA harness owned by #1083 (MovieDirectorScenePlanPerSceneCeilingTests) is deliberately not modified
// or duplicated.
//
// The ceiling is Math.Min(targetDuration, MovieDurationBudgetPolicy.MaxShotDurationSeconds) - a hard
// bound the render path can actually produce as one billable unit (render preflight and generation
// orchestration estimate each shot from Math.Clamp(..., 1, 3_600); provider clips are capped by
// ProviderCapabilityPricing.MaxDurationSeconds, default 3_600). It must not be widened by
// MovieDirectorScenePlanningOptions.MaxDurationSeconds, and it must be identical to the previous
// bound whenever the configured ceiling is 3_600 or lower, so all existing behaviour is preserved.
//
// No provider call, no media, no HTTP, no database, no billable usage, no migrations, no user data.
namespace Taslim.Api.Tests;

public sealed class MovieDirectorScenePlanPerSceneRenderableCeilingTests
{
    // The #685 target configuration exercised by QA: a 120-minute configured ceiling.
    private const int TwoHourConfiguredCeiling = 7_200;

    // The largest renderable unit the product actually supports today. This is a real constant from
    // the code under test's own domain, not a restated literal, so the guard cannot drift from it.
    private const int RenderableUnitCeiling = MovieDurationBudgetPolicy.MaxShotDurationSeconds;

    [Fact]
    public void Admission_rejects_a_scene_one_second_over_the_renderable_unit_ceiling()
    {
        // The core gap: this used to be admitted whenever the movie target was larger.
        var (result, _) = Advertise(TwoHourConfiguredCeiling, Scene(1, RenderableUnitCeiling + 1), Scene(2, TwoHourConfiguredCeiling - RenderableUnitCeiling - 1));

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding =>
            finding.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch
            && finding.Field == "estimatedDurationSeconds");
    }

    [Fact]
    public void Admission_rejects_a_single_scene_spanning_the_whole_configured_target()
    {
        // A 7,200 s scene would be persisted verbatim and then estimated as a 3,600 s billable shot:
        // half the promised runtime silently lost and underbilled. Admission now refuses the shape.
        var (result, _) = Advertise(TwoHourConfiguredCeiling, Scene(1, TwoHourConfiguredCeiling));

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding =>
            finding.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch
            && finding.Field == "estimatedDurationSeconds");
    }

    [Theory]
    [InlineData(3_700)]
    [InlineData(5_000)]
    [InlineData(7_200)]
    public void Admission_rejects_any_over_length_scene_whatever_the_rest_of_the_plan_is(int overlongSeconds)
    {
        // The guard is per scene, not an aggregate check: a compliant tail scene does not excuse it.
        var others = Enumerable.Range(1, 3).Select(i => Scene(i + 1, (TwoHourConfiguredCeiling - overlongSeconds) / 3)).ToArray();
        var scenes = new[] { Scene(1, overlongSeconds) }.Concat(others).ToArray();

        var (result, _) = Advertise(TwoHourConfiguredCeiling, scenes);

        Assert.False(result.IsValid, $"a {overlongSeconds} s scene exceeds the {RenderableUnitCeiling} s renderable unit ceiling.");
        Assert.Contains(result.Findings, finding =>
            finding.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch
            && finding.Field == "estimatedDurationSeconds");
    }

    [Fact]
    public void A_wider_configured_ceiling_does_not_widen_the_per_scene_ceiling()
    {
        // Fail closed: MaxDurationSeconds is a movie-target bound, never a licence to admit an
        // unrenderable scene. 5,000 s is inside the configured target and outside the renderable unit.
        var scenes = new[] { Scene(1, 5_000), Scene(2, 2_200) };

        var (result, _) = Advertise(TwoHourConfiguredCeiling, scenes);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding =>
            finding.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch
            && finding.Field == "estimatedDurationSeconds");
        Assert.DoesNotContain(result.Findings, finding => finding.Field == "targetDurationSeconds");
    }

    [Fact]
    public void Admission_still_accepts_a_scene_exactly_at_the_renderable_unit_ceiling()
    {
        // Boundary preserved: 3,600 s was renderable before this guard and stays renderable now.
        var (result, _) = Advertise(TwoHourConfiguredCeiling, Scene(1, RenderableUnitCeiling), Scene(2, TwoHourConfiguredCeiling - RenderableUnitCeiling));

        Assert.True(result.IsValid, string.Join(", ", result.Findings.Select(f => $"{f.ReasonCode}:{f.Field}")));
    }

    [Fact]
    public void Admission_accepts_the_provider_compliant_two_hour_split()
    {
        // The shape #685 actually needs to stay admissible once the 120-minute target is enabled:
        // 24 scenes of 300 s, every scene inside the renderable unit ceiling.
        var scenes = Enumerable.Range(1, 24).Select(i => Scene(i, 300)).ToArray();

        var (result, _) = Advertise(TwoHourConfiguredCeiling, scenes);

        Assert.True(result.IsValid, string.Join(", ", result.Findings.Select(f => $"{f.ReasonCode}:{f.Field}")));
        Assert.All(scenes, scene => Assert.True(scene.EstimatedDurationSeconds <= RenderableUnitCeiling));
    }

    [Fact]
    public void Per_scene_guard_does_not_consume_the_separate_context_target_check()
    {
        // The two seams must stay independent (#1078 owns targetDurationSeconds, this lane owns
        // estimatedDurationSeconds). A plan with a correct target and an over-length scene is rejected
        // only for the scene, never by re-labelling the target finding.
        var (result, payload) = Advertise(TwoHourConfiguredCeiling, Scene(1, TwoHourConfiguredCeiling));

        Assert.Equal(TwoHourConfiguredCeiling, payload.TargetDurationSeconds);
        Assert.False(result.IsValid);
        Assert.DoesNotContain(result.Findings, finding => finding.Field == "targetDurationSeconds");
        Assert.Contains(result.Findings, finding => finding.Field == "estimatedDurationSeconds");
    }

    [Fact]
    public void Default_3600_second_configuration_is_unchanged()
    {
        // All existing behaviour at the shipped ceiling is byte-for-byte the same decision: the whole
        // movie may still be a single 3,600 s scene, because min(target, ceiling) == target there.
        var (valid, _) = Advertise(MovieDurationBudgetPolicy.MaxShotDurationSeconds, Scene(1, MovieDurationBudgetPolicy.MaxShotDurationSeconds));
        Assert.True(valid.IsValid, string.Join(", ", valid.Findings.Select(f => $"{f.ReasonCode}:{f.Field}")));

        // And an over-target scene was, and remains, rejected at the default ceiling.
        var validator = Validator(MovieDurationBudgetPolicy.MaxShotDurationSeconds);
        var context = Context(MovieDurationBudgetPolicy.MaxShotDurationSeconds);
        var over = Build(context, context.DurationSeconds, RenderableUnitCeiling + 1, Scene(1, RenderableUnitCeiling + 1));
        Assert.False(validator.Validate(over, context).IsValid);
    }

    [Fact]
    public void Per_scene_floor_still_rejects_zero_and_negative_durations()
    {
        foreach (var duration in new[] { 0, -5 })
        {
            var (result, _) = Advertise(TwoHourConfiguredCeiling, Scene(1, duration), Scene(2, TwoHourConfiguredCeiling - duration));
            Assert.False(result.IsValid, $"scene duration {duration} must not be admitted.");
        }
    }

    [Fact]
    public void Guard_reports_the_existing_runtime_reason_code_without_introducing_a_new_one()
    {
        // Deliberate reuse of SCENE_PLAN_RUNTIME_MISMATCH: no new reason code, no frontend or contract
        // surface to change in this lane.
        var (result, _) = Advertise(TwoHourConfiguredCeiling, Scene(1, TwoHourConfiguredCeiling));

        Assert.False(result.IsValid);
        Assert.Contains(DirectorScenePlanValidationReasonCodes.RuntimeMismatch, result.ReasonCodes);
    }

    private static (DirectorScenePlanValidationResult Result, DirectorScenePlanActionPayload Payload) Advertise(
        int configuredMaxDurationSeconds, params DirectorSceneProposal[] scenes)
    {
        var validator = Validator(configuredMaxDurationSeconds);
        var context = Context(configuredMaxDurationSeconds);
        var payload = Build(context, context.DurationSeconds, scenes.Sum(s => s.EstimatedDurationSeconds), scenes);
        return (validator.Validate(payload, context), payload);
    }

    private static DirectorScenePlanActionPayload Build(
        DirectorStoryBoundedContextDto context, int targetDurationSeconds, int declaredTotal,
        params DirectorSceneProposal[] scenes) => new(
            DirectorScenePlanActionTypes.PlanScenes, context.CurrentRevision!.RevisionId,
            "snapshot", "en", "16:9", targetDurationSeconds, declaredTotal,
            DirectorQualityLevels.Fast, "Fast", null, scenes);

    private static IMovieDirectorScenePlanValidator Validator(int maxDurationSeconds) =>
        new MovieDirectorScenePlanValidator(
            Options.Create(new MovieDirectorScenePlanningOptions { MaxDurationSeconds = maxDurationSeconds }),
            NullLogger<MovieDirectorScenePlanValidator>.Instance);

    private static DirectorSceneProposal Scene(int sequence, int duration) => new(
        sequence, "Seed at Dawn", "Mara protects the last seed.",
        "Mara discovers the seed at the dry riverbed.", "Dry riverbed", "Dawn",
        ["Mara"], "Move from fear to responsibility.", duration,
        "Open from silence into wind.", ["The last seed remains in Mara's left hand."],
        "Use blue hour naturalism.");

    private static DirectorStoryBoundedContextDto Context(int durationSeconds) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Mara protects the last seed at the dry riverbed.",
        new DirectorGuideContext("blue hour naturalism", "slow camera", "blue hour light", "wind",
            "The seed remains in Mara's left hand."),
        new DirectorStoryRevisionContext(Guid.NewGuid(), 1, MovieStoryRevisionStatuses.Approved,
            MovieStoryAuthorship.Human, "Mara protects the last seed.", "Mara carries the seed.",
            "The last seed crosses the dry riverbed.", "Mara chooses renewal.", []),
        null, null, [],
        [new DirectorCharacterContext("Mara", "A seed keeper.", null, null)],
        [new DirectorLocationContext("Dry riverbed", "A dry riverbed.", null)],
        DateTime.UnixEpoch,
        DurationSeconds: durationSeconds, Language: "en", AspectRatio: "16:9");
}
