using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieWave2CreativeOutputValidationTests
{
    private readonly Guid projectId = Guid.NewGuid();
    private readonly Guid sourceSceneId = Guid.NewGuid();
    private readonly Guid targetSceneId = Guid.NewGuid();
    private readonly Guid sourceShotId = Guid.NewGuid();
    private readonly Guid targetShotId = Guid.NewGuid();
    private readonly Guid baseRevisionId = Guid.NewGuid();
    private readonly MovieWave2CreativeOutputValidationContext context;

    public MovieWave2CreativeOutputValidationTests()
    {
        context = new MovieWave2CreativeOutputValidationContext
        {
            MovieProjectId = projectId,
            SourceMovieProjectId = projectId,
            SourceSceneId = sourceSceneId,
            SourceShotId = sourceShotId,
            TargetMovieProjectId = projectId,
            TargetSceneId = targetSceneId,
            TargetShotId = targetShotId,
            CurrentBaseRevisionId = baseRevisionId,
            Language = LanguageCodes.English,
            RequireTargetIdentity = true,
            KnownSceneIds = new HashSet<Guid> { targetSceneId },
            KnownShotIds = new HashSet<Guid> { targetShotId },
            LockedCanon =
            [
                new MovieWave2LockedCanonConstraint(null, "guide", "lantern", "red lantern"),
            ],
        };
    }

    [Fact]
    public void Valid_scene_and_shot_output_passes_without_rewriting_content()
    {
        var output = ValidOutput();
        var result = new MovieWave2CreativeOutputValidator().ValidateAndRepair(output, context);

        Assert.True(result.IsValid);
        Assert.Equal(MovieWave2CreativeValidationOutcome.Passed, result.Outcome);
        Assert.Empty(result.Findings);
        Assert.Equal("Mara checks the red lantern.", result.Output!.Scenes![0].Shots![0].Description);
    }

    [Fact]
    public void Empty_and_malformed_structured_output_fail_honestly()
    {
        var validator = new MovieWave2CreativeOutputValidator();
        var empty = validator.ValidateJson("  ", context);
        var malformed = validator.ValidateJson("{not-json", context);
        var schema = validator.ValidateJson("{}", context);

        Assert.Contains(empty.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.EmptyOutput);
        Assert.Contains(malformed.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.MalformedStructuredOutput);
        Assert.Contains(schema.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.SchemaInvalid);
        Assert.Null(malformed.Output);
    }

    [Fact]
    public void Missing_required_fields_and_unreasonable_lengths_are_rejected()
    {
        var output = ValidOutput();
        output.Scenes![0].Title = null;
        output.Scenes[0].Shots![0].Description = new string('x', 8_001);
        output.Scenes[0].DurationSeconds = null;

        var result = new MovieWave2CreativeOutputValidator().ValidateAndRepair(output, context);

        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.RequiredFieldMissing);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.ExcessiveLength);
    }

    [Fact]
    public void Duplicate_scenes_duplicate_shots_and_duplicate_shot_order_are_rejected()
    {
        var output = ValidOutput();
        var scene = output.Scenes![0];
        scene.Shots!.Add(new MovieWave2ShotOutput
        {
            Id = Guid.NewGuid(),
            Order = 1,
            Description = "Mara checks the red lantern.",
            DurationSeconds = 2,
            Category = "close_up",
        });
        output.Scenes.Add(new MovieWave2SceneOutput
        {
            Id = scene.Id,
            Order = 2,
            Title = scene.Title,
            Summary = scene.Summary,
            DurationSeconds = scene.DurationSeconds,
            Shots = [scene.Shots[0]],
        });

        var result = new MovieWave2CreativeOutputValidator().ValidateAndRepair(output, context);

        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.DuplicateScene);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.DuplicateShot);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.DuplicateShotOrder);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Invalid_durations_enums_categories_and_orders_are_rejected()
    {
        var output = ValidOutput();
        output.Scenes![0].DurationSeconds = 1;
        output.Scenes[0].Category = "not-a-scene-category";
        var shot = output.Scenes[0].Shots![0];
        shot.DurationSeconds = 2;
        shot.Category = "not-a-shot-category";
        shot.Status = "not-a-status";
        shot.Order = 0;

        var result = new MovieWave2CreativeOutputValidator().ValidateAndRepair(output, context);

        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.InvalidDuration);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.InvalidCategory);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.InvalidEnum);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.InvalidOrder);
    }

    [Fact]
    public void Source_target_and_stale_base_mismatches_are_rejected()
    {
        var output = ValidOutput();
        output.Source = new MovieWave2CreativeOutputReference { MovieProjectId = Guid.NewGuid(), SceneId = Guid.NewGuid(), ShotId = Guid.NewGuid(), BaseRevisionId = Guid.NewGuid() };
        output.Target = new MovieWave2CreativeOutputReference { MovieProjectId = Guid.NewGuid(), SceneId = Guid.NewGuid(), ShotId = Guid.NewGuid() };
        output.BaseRevisionId = Guid.NewGuid();

        var result = new MovieWave2CreativeOutputValidator().ValidateAndRepair(output, context);

        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.SourceMismatch);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.TargetMismatch);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.StaleBase);
    }

    [Fact]
    public void Locked_canon_conflict_is_rejected_without_substituting_canon_text()
    {
        var output = ValidOutput();
        output.LockedCanonClaims = [new MovieWave2CanonClaim(null, "guide", "lantern", "blue lantern")];
        output.Scenes![0].Summary = "Mara protects the blue lantern instead of the red lantern.";
        var original = output.Scenes[0].Summary;

        var result = new MovieWave2CreativeOutputValidator().ValidateAndRepair(output, context);

        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.LockedCanonConflict);
        Assert.Null(result.Output);
        Assert.Equal(original, output.Scenes[0].Summary);
    }

    [Fact]
    public void Generic_template_and_deterministically_detectable_language_mismatch_are_rejected()
    {
        var output = ValidOutput();
        output.Scenes![0].Shots![0].Description = "[Insert shot description here]";
        var templateResult = new MovieWave2CreativeOutputValidator().ValidateAndRepair(output, context);
        Assert.Contains(templateResult.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.GenericTemplate);

        var arabic = ValidOutput();
        arabic.Language = LanguageCodes.Arabic;
        var languageResult = new MovieWave2CreativeOutputValidator().ValidateAndRepair(arabic, context);
        Assert.Contains(languageResult.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.OutputLanguageMismatch);
    }

    [Fact]
    public void Bounded_repair_only_removes_exact_duplicate_structure_and_never_invents_text()
    {
        var output = ValidOutput();
        output.Scenes!.Add(JsonSerializer.Deserialize<MovieWave2SceneOutput>(JsonSerializer.Serialize(output.Scenes[0]))!);
        var validator = new MovieWave2CreativeOutputValidator(new MovieWave2CreativeOutputValidationOptions { MaxRepairAttempts = 1 });

        var result = validator.ValidateAndRepair(output, context);

        Assert.True(result.IsValid);
        Assert.True(result.WasRepaired);
        var repaired = result.Output!;
        var repairedScenes = repaired.Scenes ?? throw new InvalidOperationException("The repaired output must contain scenes.");
        Assert.Single(repairedScenes);
        var repairedShots = repairedScenes[0].Shots ?? throw new InvalidOperationException("The repaired scene must contain shots.");
        Assert.Equal("Mara checks the red lantern.", repairedShots[0].Description);
        Assert.Contains(result.Findings, item => item.ReasonCode == MovieWave2CreativeValidationReasonCodes.StructuredRepairApplied);
    }

    private MovieWave2CreativeOutput ValidOutput() => new()
    {
        SchemaVersion = MovieWave2CreativeOutputSchema.CurrentVersion,
        MovieProjectId = projectId,
        Language = LanguageCodes.English,
        BaseRevisionId = baseRevisionId,
        Source = new MovieWave2CreativeOutputReference { MovieProjectId = projectId, SceneId = sourceSceneId, ShotId = sourceShotId },
        Target = new MovieWave2CreativeOutputReference { MovieProjectId = projectId, SceneId = targetSceneId, ShotId = targetShotId },
        Scenes =
        [
            new MovieWave2SceneOutput
            {
                Id = targetSceneId,
                Order = 1,
                Title = "Lantern Room",
                Summary = "Mara protects the red lantern before the storm.",
                DurationSeconds = 4,
                Category = "interior",
                Shots =
                [
                    new MovieWave2ShotOutput
                    {
                        Id = targetShotId,
                        Order = 1,
                        Description = "Mara checks the red lantern.",
                        Purpose = "Establish the locked continuity fact.",
                        Subjects = "Mara and the red lantern",
                        LocationSet = "Lighthouse room",
                        DurationSeconds = 3,
                        Category = "close_up",
                        Status = MovieShotStatuses.Planned,
                        SourceShotId = sourceShotId,
                        TargetShotId = targetShotId,
                    },
                ],
            },
        ],
    };
}
