using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorCreativeOutputValidationTests
{
    private readonly Guid revisionId = Guid.NewGuid();
    private readonly Guid sceneId = Guid.NewGuid();
    private readonly Guid elementId = Guid.NewGuid();
    private readonly DirectorStoryBoundedContextDto context;
    private readonly MovieDirectorCreativeOutputValidator validator;

    public MovieDirectorCreativeOutputValidationTests()
    {
        context = new DirectorStoryBoundedContextDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "A lighthouse keeper protects a red lantern before the storm.",
            new DirectorGuideContext("red lantern visual language", "locked wide camera", "blue hour", "quiet sea", "The red lantern remains lit.", 1, true),
            new DirectorStoryRevisionContext(
                revisionId, 1, MovieStoryRevisionStatuses.Draft, MovieStoryAuthorship.Human,
                "Mara protects the red lantern.",
                "Mara protects the red lantern before the storm reaches the lighthouse.",
                "Mara follows the lighthouse rule and faces the storm.",
                "Mara moves from caution to an irreversible choice.",
                [new DirectorScreenplaySceneContext(sceneId, null, "1", 1, 1, "INT. LIGHTHOUSE - NIGHT", "Mara guards the lantern.", [
                    new DirectorScreenplayElementContext(elementId, MovieScreenplayElementTypes.Dialogue, "The lantern stays lit.", "Mara", null),
                ])]),
            null,
            null,
            [new DirectorSceneContext(Guid.NewGuid(), 1, "Lighthouse", "Mara guards the red lantern.", 60, null, [])],
            [new DirectorCharacterContext("Mara", "A careful lighthouse keeper.", null, null)],
            [new DirectorLocationContext("Lighthouse", "A storm-battered lighthouse.", null)],
            DateTime.UnixEpoch,
            Language: "en");
        validator = new MovieDirectorCreativeOutputValidator(
            Options.Create(new MovieDirectorCreativeOutputValidationOptions { MaxRepairAttempts = 1 }),
            NullLogger<MovieDirectorCreativeOutputValidator>.Instance);
    }

    [Fact]
    public void Valid_grounded_output_passes()
    {
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.ImproveLogline, logline: "Mara protects the red lantern before the storm."), context);

        Assert.True(result.IsValid);
        Assert.Equal(DirectorCreativeValidationOutcome.Passed, result.Outcome);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Malformed_structured_json_is_rejected_as_schema_failure()
    {
        var result = validator.ValidateJson("{not-json", context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.SchemaInvalid);
    }

    [Fact]
    public void Required_empty_and_excessive_fields_are_rejected()
    {
        var empty = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.DevelopPremise, premise: " "), context);
        var tooLong = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.DevelopPremise, premise: new string('x', 8_001)), context);

        Assert.Contains(empty.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.EmptyOutput);
        Assert.Contains(tooLong.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.ExcessiveLength);
    }

    [Fact]
    public void Template_like_output_is_rejected()
    {
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.DevelopPremise, premise: "[Insert premise here]"), context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.GenericTemplate);
    }

    [Fact]
    public void Output_without_context_anchor_is_rejected()
    {
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.ImproveLogline, logline: "A spacecraft crosses an unnamed galaxy."), context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.ContextUngrounded);
    }

    [Fact]
    public void Obvious_dialogue_character_mismatch_is_rejected()
    {
        var scene = new MovieStorySceneRequest
        {
            SceneIdentifier = "NEW-SCENE",
            Slugline = "INT. LIGHTHOUSE - NIGHT",
            Synopsis = "Mara guards the red lantern.",
            Elements = [new MovieScreenplayElementRequest { ElementType = MovieScreenplayElementTypes.Dialogue, CharacterName = "Unknown", Content = "The lantern stays lit." }],
        };
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.ProposeScreenplayScene, proposedScene: scene), context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.CharacterMismatch);
    }

    [Fact]
    public void Duplicate_screenplay_elements_receive_one_bounded_structured_repair()
    {
        var scene = new MovieStorySceneRequest
        {
            SceneIdentifier = "NEW-SCENE",
            Slugline = "INT. LIGHTHOUSE - NIGHT",
            Synopsis = "Mara guards the red lantern.",
            Elements = [
                new MovieScreenplayElementRequest { ElementType = MovieScreenplayElementTypes.Action, Content = "Mara checks the red lantern." },
                new MovieScreenplayElementRequest { ElementType = MovieScreenplayElementTypes.Action, Content = "Mara checks the red lantern." },
            ],
        };
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.ProposeScreenplayScene, proposedScene: scene), context);

        Assert.True(result.IsValid);
        Assert.True(result.WasRepaired);
        Assert.Single(result.Output!.ProposedScene!.Elements);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.StructuredRepairApplied);
    }

    [Fact]
    public void Repeated_sentences_are_rejected_when_they_cannot_be_safely_repaired()
    {
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.ImproveLogline, logline: "Mara opens the red gate. Mara opens the red gate."), context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.DuplicateContent);
    }

    [Fact]
    public void Unsupported_locked_canon_claim_is_rejected()
    {
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.ImproveLogline, logline: "The locked canon confirms that the moon is green."), context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.UnsupportedLockedCanonClaim);
    }

    [Fact]
    public void Output_language_mismatch_is_rejected()
    {
        var result = validator.ValidateAndRepair(Payload(DirectorStoryActionTypes.ImproveLogline, logline: "هذه قصة عربية عن البحر."), context);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, item => item.ReasonCode == DirectorCreativeValidationReasonCodes.OutputLanguageMismatch);
    }

    private DirectorStoryActionPayload Payload(
        string action,
        string? premise = null,
        string? logline = null,
        string? synopsis = null,
        string? treatment = null,
        MovieStorySceneRequest? proposedScene = null) =>
        new(
            action,
            revisionId,
            premise,
            logline,
            synopsis,
            treatment,
            proposedScene is null ? null : Guid.NewGuid(),
            proposedScene is null ? null : Guid.NewGuid(),
            proposedScene,
            null,
            [],
            [new DirectorStoryFieldChangeDto(ChangeField(action), null, "existing", CreativeValue(premise, logline, synopsis, treatment, proposedScene))]);

    private static string ChangeField(string action) => action switch
    {
        DirectorStoryActionTypes.DevelopPremise => "premise",
        DirectorStoryActionTypes.ImproveLogline => "logline",
        DirectorStoryActionTypes.ExpandSynopsis or DirectorStoryActionTypes.TightenPacing => "synopsis",
        DirectorStoryActionTypes.CreateOrRefineTreatment => "treatment",
        DirectorStoryActionTypes.ProposeScreenplayScene => "screenplay_scene",
        _ => "logline",
    };

    private static string CreativeValue(string? premise, string? logline, string? synopsis, string? treatment, MovieStorySceneRequest? scene) =>
        premise ?? logline ?? synopsis ?? treatment ?? scene?.Synopsis ?? string.Empty;
}
