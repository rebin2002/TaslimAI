using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic synthetic pins for the storyboard shot frame-rate and duration metadata that
/// <see cref="MovieShotProductionContractValidation"/> is responsible for. Every case is a
/// constructed literal: no database, provider, private data, or paid service is touched.
///
/// The invariant under test is that what the DTO validator accepts is what the shot write path
/// stores. <c>MovieStudioService.AddShotAsync</c> and <c>MovieStudioService.UpdateShotPlanningAsync</c>
/// validate the submitted <see cref="MovieShotTargetOutputRequirements"/> object and then persist that
/// same object verbatim through <see cref="MovieShotProductionContractSerialization.ToJson"/>, so a
/// bound measured on a trimmed view of a value does not describe the value that is stored and later
/// returned as <c>productionContract.targetOutputRequirements</c>.
/// </summary>
public sealed class MovieStoryboardShotMetadataValidationTests
{
    // TargetOutputRequirementsJson, ProductionComplexityJson and QualityRequirementsJson are
    // varchar(20000) columns; see TaslimDbContext.OnModelCreating.
    private const int StoredColumnBound = 20_000;
    private const int IntentBound = 120;
    private const int AspectRatioBound = 20;
    private const int NotesBound = 2_000;
    private const string IntentBoundMessage = "Target output intents must be 120 characters or fewer.";
    private const string DurationBoundMessage = "Shot duration must be between 1 second and 60 minutes.";

    private static MovieShotTargetOutputRequirements FrameRateOutput(string frameRateIntent) =>
        new("16:9", "uhd", frameRateIntent, "cinematic-neutral", "stereo", "video", "Intent only; the eventual adapter resolves capability.");

    private static string? ValidateFrameRate(string frameRateIntent) =>
        MovieShotProductionContractValidation.Validate(8, "primary", null, null, null, null, FrameRateOutput(frameRateIntent));

    private static MovieShotTargetOutputRequirements RoundTripStored(MovieShotTargetOutputRequirements output)
    {
        var json = MovieShotProductionContractSerialization.ToJson(output)
            ?? throw new InvalidOperationException("The shot write path always serializes the submitted object.");
        return MovieShotProductionContractSerialization.FromJson<MovieShotTargetOutputRequirements>(json)
            ?? throw new InvalidOperationException("The serialized contract did not round trip.");
    }

    [Fact]
    public void Frame_rate_intent_within_the_bound_is_accepted()
    {
        Assert.Null(ValidateFrameRate("24 fps"));
        Assert.Null(ValidateFrameRate("  24 fps with natural motion  "));
        Assert.Null(ValidateFrameRate(new string('x', IntentBound)));
    }

    [Fact]
    public void Frame_rate_intent_longer_than_the_bound_is_rejected()
    {
        Assert.Equal(IntentBoundMessage, ValidateFrameRate(new string('x', IntentBound + 1)));
    }

    [Fact]
    public void Every_accepted_frame_rate_intent_is_stored_inside_the_documented_bound()
    {
        // A padded value reads as in-bounds after trimming but is persisted verbatim by the shot
        // write path, so the storyboard shot frame-rate metadata would be stored over-long.
        var candidates = new[]
        {
            new string(' ', IntentBound) + new string('x', 100),
            new string('\n', 30) + new string('x', 95),
            new string('x', 100) + new string('\t', 40),
        };

        foreach (var frameRateIntent in candidates)
        {
            Assert.True(frameRateIntent.Trim().Length <= IntentBound);

            var output = FrameRateOutput(frameRateIntent);
            if (MovieShotProductionContractValidation.Validate(8, "primary", null, null, null, null, output) is not null)
            {
                continue;
            }

            var stored = RoundTripStored(output);
            Assert.True(
                stored.FrameRateIntent!.Length <= IntentBound,
                $"The DTO validator accepted a {stored.FrameRateIntent.Length}-character frame rate intent " +
                $"whose trimmed view is {stored.FrameRateIntent.Trim().Length} characters, and the shot write " +
                $"path stores exactly that value outside the documented {IntentBound}-character bound.");
        }
    }

    [Fact]
    public void Every_accepted_target_output_aspect_ratio_is_stored_inside_the_documented_bound()
    {
        var candidates = new[]
        {
            new string(' ', AspectRatioBound) + "16:9",
            "9:16" + new string(' ', 25),
        };

        foreach (var aspectRatio in candidates)
        {
            var output = new MovieShotTargetOutputRequirements(aspectRatio);
            if (MovieShotProductionContractValidation.Validate(8, null, null, null, null, null, output) is not null)
            {
                continue;
            }

            var stored = RoundTripStored(output);
            Assert.True(
                stored.AspectRatio!.Length <= AspectRatioBound,
                $"The DTO validator accepted a {stored.AspectRatio.Length}-character aspect ratio outside the " +
                $"documented {AspectRatioBound}-character bound.");
        }
    }

    [Fact]
    public void Whitespace_padding_cannot_push_the_stored_target_output_contract_past_the_database_column_bound()
    {
        // The persisted JSON column is varchar(20000). Padding a field with whitespace keeps the
        // trimmed view in bounds while the serialized payload grows past the column, which turns a
        // client-supplied shot into a storage failure instead of a 400 validation response.
        var candidates = new MovieShotTargetOutputRequirements[]
        {
            new("16:9", ResolutionIntent: new string(' ', StoredColumnBound) + "uhd"),
            new("16:9", FrameRateIntent: new string(' ', StoredColumnBound) + "24 fps"),
            new("16:9", Notes: new string(' ', StoredColumnBound) + "intent"),
        };

        foreach (var output in candidates)
        {
            if (MovieShotProductionContractValidation.Validate(8, null, null, null, null, null, output) is not null)
            {
                continue;
            }

            var json = MovieShotProductionContractSerialization.ToJson(output)!;
            Assert.True(
                json.Length <= StoredColumnBound,
                $"The DTO validator accepted a target-output payload of {json.Length} characters, which does " +
                $"not fit the {StoredColumnBound}-character column the shot write path stores it in.");
        }
    }

    [Fact]
    public void Whitespace_padding_cannot_push_the_stored_planning_profiles_past_the_database_column_bound()
    {
        var cases = new (string Label, MovieShotComplexityProfile? Complexity, MovieShotQualityRequirements? Quality)[]
        {
            ("complexity notes", new MovieShotComplexityProfile("low", null, new string(' ', StoredColumnBound) + "notes"), null),
            ("quality notes", null, new MovieShotQualityRequirements(MovieQualityLevels.Standard, null, new string(' ', StoredColumnBound) + "notes")),
        };

        foreach (var (label, complexity, quality) in cases)
        {
            if (MovieShotProductionContractValidation.Validate(8, null, complexity, quality, null, null, null) is not null)
            {
                continue;
            }

            var json = MovieShotProductionContractSerialization.ToJson((object?)complexity ?? quality!)!;
            Assert.True(
                json.Length <= StoredColumnBound,
                $"The DTO validator accepted {label} of {json.Length} characters, which does not fit the " +
                $"{StoredColumnBound}-character column the shot write path stores them in.");
        }
    }

    [Fact]
    public void Every_accepted_complexity_driver_is_stored_inside_the_documented_bound()
    {
        var driver = new string(' ', IntentBound) + new string('d', 100);
        var complexity = new MovieShotComplexityProfile(MovieShotComplexityLevels.Moderate, [driver]);

        if (MovieShotProductionContractValidation.Validate(8, null, complexity, null, null, null, null) is not null)
        {
            return;
        }

        var stored = MovieShotProductionContractSerialization.FromJson<MovieShotComplexityProfile>(
            MovieShotProductionContractSerialization.ToJson(complexity)!)!;

        Assert.True(
            stored.Drivers![0].Length <= IntentBound,
            $"The DTO validator accepted a {stored.Drivers![0].Length}-character complexity driver outside the " +
            $"documented {IntentBound}-character bound.");
    }

    [Fact]
    public void Over_bound_notes_are_rejected_on_the_characters_that_are_actually_stored()
    {
        Assert.Equal(
            "Production complexity notes must be 2,000 characters or fewer.",
            MovieShotProductionContractValidation.Validate(
                8, null, new MovieShotComplexityProfile(MovieShotComplexityLevels.Low, null, new string('n', NotesBound + 1)), null, null, null, null));

        Assert.Equal(
            "Target output notes must be 2,000 characters or fewer.",
            MovieShotProductionContractValidation.Validate(
                8, null, null, null, null, null, new MovieShotTargetOutputRequirements("16:9", Notes: new string('n', NotesBound + 1))));
    }

    [Fact]
    public void Shot_duration_acceptance_agrees_with_the_shared_runtime_policy()
    {
        foreach (var duration in new int?[] { null, 1, 60, 3_600, 3_601, 0, -1, 86_400, int.MaxValue })
        {
            var policy = MovieDurationBudgetPolicy.ValidateShotDuration(duration);
            var contract = MovieShotProductionContractValidation.Validate(duration, null, null, null, null, null, null);

            Assert.Equal(policy is null, contract is null);
        }
    }

    [Fact]
    public void Shot_duration_ceiling_follows_the_shared_runtime_policy_constant()
    {
        Assert.Equal(
            DurationBoundMessage,
            MovieShotProductionContractValidation.Validate(
                MovieDurationBudgetPolicy.MaxShotDurationSeconds + 1, null, null, null, null, null, null));

        Assert.Null(
            MovieShotProductionContractValidation.Validate(
                MovieDurationBudgetPolicy.MaxShotDurationSeconds, null, null, null, null, null, null));
    }
}
