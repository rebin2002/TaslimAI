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
        var id = Guid.NewGuid();
        var result = Validate(CreateContext(id), Scene(1, id), Scene(2, id));
        AssertDuplicate(result);
    }

    [Fact]
    public void Screenplay_links_and_explicit_targets_share_production_scene_identity()
    {
        var movie = Guid.NewGuid();
        var first = Source(movie);
        var second = Source(movie);
        var context = CreateContext(movie, first, second);
        AssertDuplicate(Validate(context, Scene(1, movie), Scene(2, source: first.Id)));
        AssertDuplicate(Validate(context, Scene(1, source: first.Id), Scene(2, source: second.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_new_scenes_can_be_planned_with_or_without_the_same_unlinked_screenplay_source(bool useSource)
    {
        var screenplay = Source(null);
        var context = CreateContext(Guid.NewGuid(), screenplay);
        Guid? source = useSource ? screenplay.Id : null;
        var result = Validate(context, Scene(1, source: source), Scene(2, source: source));
        Assert.True(result.IsValid, string.Join(", ", result.ReasonCodes));
    }

    [Fact]
    public void Explicit_movie_target_takes_precedence_over_a_different_screenplay_link()
    {
        var explicitTarget = Guid.NewGuid();
        var linkedTarget = Guid.NewGuid();
        var screenplay = Source(linkedTarget);
        var context = CreateContext(explicitTarget, screenplay);
        var result = Validate(context, Scene(1, explicitTarget, screenplay.Id), Scene(2, source: screenplay.Id));
        Assert.True(result.IsValid, string.Join(", ", result.ReasonCodes));
    }

    [Fact]
    public void Unknown_scene_references_keep_their_source_validation_findings()
    {
        var context = CreateContext(Guid.NewGuid());
        var result = Validate(context, Scene(1, Guid.NewGuid()), Scene(2, source: Guid.NewGuid()));
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorScenePlanValidationReasonCodes.InvalidSource && item.Field == "existingMovieSceneId");
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorScenePlanValidationReasonCodes.InvalidSource && item.Field == "sourceScreenplaySceneId");
        Assert.DoesNotContain(result.Findings, item => item.ReasonCode == DirectorScenePlanValidationReasonCodes.DuplicateTarget);
    }

    private static void AssertDuplicate(DirectorScenePlanValidationResult result)
    {
        Assert.False(result.IsValid);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(DirectorScenePlanValidationReasonCodes.DuplicateTarget, finding.ReasonCode);
        Assert.Equal("target", finding.Field);
    }

    private static DirectorScenePlanValidationResult Validate(DirectorStoryBoundedContextDto context, params DirectorSceneProposal[] scenes)
    {
        var payload = new DirectorScenePlanActionPayload(DirectorScenePlanActionTypes.PlanScenes,
            context.CurrentRevision!.RevisionId, "snapshot", "en", "16:9", 30, 30,
            DirectorQualityLevels.Fast, DirectorQualityLevels.Fast, null, scenes);
        var validator = new MovieDirectorScenePlanValidator(Options.Create(new MovieDirectorScenePlanningOptions()),
            NullLogger<MovieDirectorScenePlanValidator>.Instance);
        return validator.Validate(payload, context, "snapshot");
    }

    private static DirectorScreenplaySceneContext Source(Guid? movieSceneId) =>
        new(Guid.NewGuid(), movieSceneId, "S1", 1, 1, "EXT. DRY RIVERBED – DAWN", "Mara protects the seed.", []);

    private static DirectorSceneProposal Scene(int sequence, Guid? existing = null, Guid? source = null) => new(
        sequence,
        $"Seed at dawn {sequence}",
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
        ExistingMovieSceneId: existing, SourceScreenplaySceneId: source);

    private static DirectorStoryBoundedContextDto CreateContext(Guid movieSceneId, params DirectorScreenplaySceneContext[] sources) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Mara protects the last seed at the dry riverbed.",
        new DirectorGuideContext("blue hour naturalism", "slow camera", "blue hour light", "wind", "The seed remains in Mara's left hand."),
        new DirectorStoryRevisionContext(Guid.NewGuid(), 1, MovieStoryRevisionStatuses.Approved, MovieStoryAuthorship.Human, "Mara protects the last seed.", "Mara carries the seed.", "The last seed crosses the dry riverbed.", "Mara chooses renewal.", sources),
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
