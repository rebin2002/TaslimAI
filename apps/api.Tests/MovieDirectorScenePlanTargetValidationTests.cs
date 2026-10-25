using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorScenePlanTargetValidationTests
{
    [Fact]
    public void Duplicate_existing_movie_scene_targets_are_rejected_before_apply()
    {
        var movieSceneId = Guid.NewGuid();
        var context = CreateContext(movieSceneId);
        var payload = new DirectorScenePlanActionPayload(
            DirectorScenePlanActionTypes.PlanScenes,
            context.CurrentRevision!.RevisionId,
            "snapshot",
            "en",
            "16:9",
            30,
            30,
            DirectorQualityLevels.Fast,
            DirectorQualityLevels.Fast,
            null,
            [
                Scene(1, "Seed at dawn", movieSceneId),
                Scene(2, "Choice at dusk", movieSceneId),
            ]);

        var validator = new MovieDirectorScenePlanValidator(
            Options.Create(new MovieDirectorScenePlanningOptions()),
            NullLogger<MovieDirectorScenePlanValidator>.Instance);

        var result = validator.Validate(payload, context, "snapshot");

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorScenePlanValidationReasonCodes.DuplicateTarget && item.Field == "target");
    }

    private static DirectorSceneProposal Scene(int sequence, string title, Guid existingMovieSceneId) => new(
        sequence,
        title,
        "Mara protects the last seed.",
        "Mara discovers the seed at the dry riverbed.",
        "Dry riverbed",
        "Dawn",
        ["Mara"],
        "Mara chooses responsibility.",
        15,
        "Continue the blue hour naturalism.",
        ["The seed remains in Mara's left hand."],
        "Use slow camera movement and blue hour light.",
        ExistingMovieSceneId: existingMovieSceneId);

    private static DirectorStoryBoundedContextDto CreateContext(Guid movieSceneId) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Mara protects the last seed at the dry riverbed.",
        new DirectorGuideContext("blue hour naturalism", "slow camera", "blue hour light", "wind", "The seed remains in Mara's left hand."),
        new DirectorStoryRevisionContext(Guid.NewGuid(), 1, MovieStoryRevisionStatuses.Approved, MovieStoryAuthorship.Human, "Mara protects the last seed.", "Mara carries the seed.", "The last seed crosses the dry riverbed.", "Mara chooses renewal.", []),
        null,
        null,
        [new DirectorSceneContext(movieSceneId, 1, "Dry riverbed", "Mara protects the last seed at the dry riverbed.", 30, null, [])],
        [new DirectorCharacterContext("Mara", "A seed keeper.", null, null)],
        [new DirectorLocationContext("Dry riverbed", "A dry riverbed.", null)],
        DateTime.UnixEpoch,
        DurationSeconds: 30,
        Language: "en",
        AspectRatio: "16:9");
}
