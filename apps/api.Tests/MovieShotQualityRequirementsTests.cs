using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotQualityRequirementsTests
{
    [Fact]
    public void Important_facial_close_up_gets_critical_facial_fidelity()
    {
        var sceneId = Guid.NewGuid();
        var shot = new MovieShot
        {
            Id = Guid.NewGuid(),
            MovieSceneId = sceneId,
            Description = "Important facial close-up of Mara as she reads the letter.",
            Purpose = "Hold on the expression before she speaks.",
            Subjects = "Mara, letter",
            Dialogue = "I remember.",
            DurationSeconds = 4,
        };

        var profile = MovieShotQualityRequirementsPlanner.Plan(shot, Context(sceneId, "A character-driven drama."));

        Assert.Equal(4, profile.QualityRequirements.ScoreFor(MovieShotQualityRequirementKeys.FacialFidelity));
        Assert.True(profile.QualityRequirements.Requirements.Single(item => item.Key == MovieShotQualityRequirementKeys.FacialFidelity).Required);
        Assert.Contains("content:close_framing", profile.QualityRequirements.GroundingSignals);
        Assert.Contains(MovieShotQualityRequirementKeys.FacialFidelity, profile.AdaptiveResolutionDirectorInput.PreservationPriorities);
    }

    [Fact]
    public void Background_landscape_does_not_receive_maximum_facial_fidelity()
    {
        var sceneId = Guid.NewGuid();
        var shot = new MovieShot
        {
            Id = Guid.NewGuid(),
            MovieSceneId = sceneId,
            Description = "A wide background landscape of a quiet mountain valley at dawn.",
            Purpose = "Establish the environment before the story begins.",
            LocationSet = "Mountain valley",
            DurationSeconds = 6,
        };

        var profile = MovieShotQualityRequirementsPlanner.Plan(shot, Context(sceneId, "A natural landscape film."));

        Assert.InRange(profile.QualityRequirements.ScoreFor(MovieShotQualityRequirementKeys.FacialFidelity), 0, 1);
        Assert.Equal(4, profile.QualityRequirements.ScoreFor(MovieShotQualityRequirementKeys.EnvironmentConsistency));
        Assert.DoesNotContain(MovieShotQualityRequirementKeys.FacialFidelity, profile.AdaptiveResolutionDirectorInput.PreservationPriorities);
    }

    [Fact]
    public void Profile_round_trips_and_validates_exactly()
    {
        var sceneId = Guid.NewGuid();
        var profile = MovieShotQualityRequirementsPlanner.Plan(new MovieShot
        {
            Id = Guid.NewGuid(),
            MovieSceneId = sceneId,
            Description = "A slow push toward the readable sign while the courier speaks.",
            Subjects = "Courier and storefront sign",
            LocationSet = "Rainy street",
            CameraMotion = "slow push",
            Dialogue = "The sign is still lit.",
            ContinuityReferences = "Keep the red practical visible.",
            DurationSeconds = 8,
        }, Context(sceneId, "A continuity-led urban film."));

        var json = MovieShotQualityRequirementsPlanner.Serialize(profile);
        Assert.True(MovieShotQualityRequirementsPlanner.TryParse(json, out var parsed));
        Assert.NotNull(parsed);
        Assert.True(MovieShotQualityRequirementsPlanner.TryValidate(parsed, out var error), error);
        Assert.Equal(json, MovieShotQualityRequirementsPlanner.Serialize(parsed!));

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("provider", out _));
        Assert.False(document.RootElement.TryGetProperty("model", out _));
        Assert.False(document.RootElement.TryGetProperty("credits", out _));
        Assert.False(document.RootElement.TryGetProperty("outputResolution", out _));
        Assert.Equal(MovieShotQualityRequirementsPlanner.AdaptiveResolutionContractVersion,
            parsed!.AdaptiveResolutionDirectorInput.ContractVersion);
    }

    [Fact]
    public void Validation_rejects_out_of_sync_level_and_score()
    {
        var sceneId = Guid.NewGuid();
        var profile = MovieShotQualityRequirementsPlanner.Plan(new MovieShot
        {
            Id = Guid.NewGuid(),
            MovieSceneId = sceneId,
            Description = "A locked-off empty room.",
        }, Context(sceneId, "A restrained interior film."));
        var invalidRequirements = profile.QualityRequirements.Requirements
            .Select(item => item.Key == MovieShotQualityRequirementKeys.EnvironmentConsistency
                ? item with { Score = 0 }
                : item)
            .ToArray();
        var invalidQuality = profile.QualityRequirements with { Requirements = invalidRequirements };
        var invalid = profile with
        {
            QualityRequirements = invalidQuality,
            AdaptiveResolutionDirectorInput = profile.AdaptiveResolutionDirectorInput with { QualityRequirements = invalidQuality },
        };

        Assert.False(MovieShotQualityRequirementsPlanner.TryValidate(invalid, out var error));
        Assert.Contains("level does not match", error, StringComparison.OrdinalIgnoreCase);
    }

    private static MovieShotQualityPlanningContext Context(Guid sceneId, string description) => new(
        Guid.NewGuid(), sceneId, "Quality test movie", description, "cinematic",
        "Naturalistic visual language", "Measured camera language", "Cool dawn palette",
        "Keep character wardrobe and hero props continuous.", "A bounded test scene.",
        ["character:Mara remains in the navy coat"], ["world:rainy street remains wet"]);
}
