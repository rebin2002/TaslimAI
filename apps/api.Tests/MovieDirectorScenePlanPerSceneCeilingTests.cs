using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

// A valid two-hour total does not excuse an invalid individual scene.
namespace Taslim.Api.Tests;

public sealed class MovieDirectorScenePlanPerSceneCeilingTests
{
    // The #685 target configuration: a 120-minute ceiling, which is the whole point of the feature.
    private const int TwoHourCeilingSeconds = 7_200;
    private const int RenderableUnitCeilingSeconds = 3_600;

    [Fact]
    public void Admission_rejects_one_scene_spanning_the_whole_two_hour_film_without_returning_a_plan()
    {
        var (result, _) = Advertise(TwoHourCeilingSeconds, Scene(1, TwoHourCeilingSeconds));

        Assert.False(result.IsValid);
        Assert.Null(result.Output);
        Assert.Contains(result.Findings, finding => finding.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch
            && finding.Field == "estimatedDurationSeconds");
    }

    [Fact]
    public void Admission_rejects_an_oversized_scene_even_when_both_scenes_sum_to_the_target()
    {
        var (result, _) = Advertise(TwoHourCeilingSeconds,
            Scene(1, RenderableUnitCeilingSeconds + 1), Scene(2, TwoHourCeilingSeconds - RenderableUnitCeilingSeconds - 1));

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding => finding.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch
            && finding.Field == "estimatedDurationSeconds");
        Assert.DoesNotContain(result.Findings, finding => finding.Field == "totalEstimatedDurationSeconds");
    }

    [Theory]
    [InlineData(3_601)]
    [InlineData(4_200)]
    [InlineData(7_200)]
    public void Admission_should_reject_a_scene_longer_than_the_renderable_unit_ceiling(int sceneDurationSeconds)
    {
        var scenes = new List<DirectorSceneProposal> { Scene(1, sceneDurationSeconds) };
        var remaining = TwoHourCeilingSeconds - sceneDurationSeconds;
        if (remaining > 0) scenes.Add(Scene(2, remaining));

        var (result, _) = Advertise(contextDuration: TwoHourCeilingSeconds, scenes.ToArray());

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding =>
            finding.ReasonCode == DirectorScenePlanValidationReasonCodes.RuntimeMismatch
            && finding.Field == "estimatedDurationSeconds");
    }

    [Fact]
    public void Admission_rejects_a_scene_below_the_one_second_floor()
    {
        foreach (var duration in new[] { 0, -5 })
        {
            var (result, _) = Advertise(contextDuration: TwoHourCeilingSeconds,
                Scene(1, duration), Scene(2, TwoHourCeilingSeconds - duration));
            Assert.False(result.IsValid, $"scene duration {duration} must not be admitted.");
        }
    }

    [Fact]
    public void Admission_rejects_a_scene_plan_whose_declared_total_is_not_the_sum_of_its_scenes()
    {
        var validator = Validator(TwoHourCeilingSeconds);
        var context = Context(TwoHourCeilingSeconds);
        var payload = Build(context, TwoHourCeilingSeconds, declaredTotal: TwoHourCeilingSeconds, Scene(1, 100));

        var result = validator.Validate(payload, context);

        Assert.False(result.IsValid);
        Assert.Contains(result.ReasonCodes,
            code => code == DirectorScenePlanValidationReasonCodes.RuntimeMismatch);
    }

    [Fact]
    public void Admission_accepts_a_two_hour_plan_split_into_provider_compliant_scenes()
    {
        // The shape #685 actually wants: 24 scenes of 300 s, every scene under the renderable ceiling.
        var scenes = Enumerable.Range(1, 24).Select(i => Scene(i, 300)).ToArray();

        var (result, _) = Advertise(contextDuration: TwoHourCeilingSeconds, scenes);

        Assert.True(result.IsValid, string.Join(", ", result.Findings.Select(f => $"{f.ReasonCode}:{f.Field}")));
        Assert.All(scenes, scene =>
            Assert.True(scene.EstimatedDurationSeconds <= RenderableUnitCeilingSeconds));
    }

    private static (DirectorScenePlanValidationResult Result, DirectorScenePlanActionPayload Payload) Advertise(
        int contextDuration, params DirectorSceneProposal[] scenes)
    {
        var validator = Validator(TwoHourCeilingSeconds);
        var context = Context(contextDuration);
        var payload = Build(context, contextDuration, scenes.Sum(s => s.EstimatedDurationSeconds), scenes);
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
