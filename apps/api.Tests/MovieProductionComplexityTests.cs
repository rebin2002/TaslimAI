using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionComplexityTests
{
    [Fact]
    public void Low_profile_is_valid_and_explainable()
    {
        var result = MovieProductionComplexityValidator.Validate(Profile(0));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Profile);
        Assert.Equal(0, result.Profile!.OverallScore);
        Assert.Equal(MovieProductionComplexityBands.Low, result.Profile.OverallBand);
        Assert.Equal(11, result.Profile.Reasons.Count);
        Assert.All(result.Profile.Reasons, reason => Assert.Equal(MovieProductionComplexityBands.Low, reason.Band));
    }

    [Fact]
    public void High_profile_is_valid_and_explainable()
    {
        var request = Profile(100);
        request.DeclaredOverallBand = MovieProductionComplexityBands.High;
        request.Evidence =
        [
            new(MovieProductionComplexityDimensions.MotionComplexity, "Fast subject movement", "The subject crosses the frame while the camera tracks."),
            new(MovieProductionComplexityDimensions.VfxComplexity, "Visible atmospheric effect", "A dense practical-and-digital storm is required."),
        ];

        var result = MovieProductionComplexityValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(100, result.Profile!.OverallScore);
        Assert.Equal(MovieProductionComplexityBands.High, result.Profile.OverallBand);
        Assert.Contains(result.Profile.Reasons, reason => reason.Dimension == MovieProductionComplexityDimensions.MotionComplexity && reason.Evidence!.Contains("tracks", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_dimension_is_rejected_instead_of_defaulting_to_low()
    {
        var request = Profile(20);
        request.DialogueLipSyncDependency = null;

        var result = MovieProductionComplexityValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding => finding.Code == MovieProductionComplexityValidationCodes.MissingField && finding.Field == MovieProductionComplexityDimensions.DialogueLipSyncDependency);
    }

    [Fact]
    public void Malformed_json_is_rejected_deterministically()
    {
        var result = MovieProductionComplexityValidator.ValidateJson("{\"motionComplexity\":not-json}");

        Assert.False(result.IsValid);
        Assert.Contains(MovieProductionComplexityValidationCodes.MalformedJson, result.ReasonCodes);
    }

    [Fact]
    public void Contradictory_declared_band_is_rejected()
    {
        var request = Profile(100);
        request.DeclaredOverallBand = MovieProductionComplexityBands.Low;

        var result = MovieProductionComplexityValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, finding => finding.Code == MovieProductionComplexityValidationCodes.ContradictoryProfile);
    }

    [Fact]
    public void Canonical_profile_round_trips_without_provider_or_cost_fields()
    {
        var result = MovieProductionComplexityValidator.Validate(Profile(55));
        var json = JsonSerializer.Serialize(result.Profile, MovieProductionComplexityValidator.JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<MovieProductionComplexityProfile>(json, MovieProductionComplexityValidator.JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(result.Profile!.OverallScore, roundTrip!.OverallScore);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolution", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MovieProductionComplexityProfileRequest Profile(int value) => new()
    {
        MotionComplexity = value,
        CameraComplexity = value,
        FaceImportance = value,
        HandBodyInteractionComplexity = value,
        FineDetailImportance = value,
        EnvironmentComplexity = value,
        VfxComplexity = value,
        ContinuitySensitivity = value,
        TextSignageSensitivity = value,
        DialogueLipSyncDependency = value,
        DurationComplexity = value,
    };
}
