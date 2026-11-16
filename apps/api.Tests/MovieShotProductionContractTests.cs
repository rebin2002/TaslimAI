using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotProductionContractTests
{
    [Fact]
    public void Production_contract_serializes_provider_neutral_planning_values_and_round_trips()
    {
        var complexity = new MovieShotComplexityProfile(
            MovieShotComplexityLevels.High,
            ["camera_motion", "continuity", "performance"],
            "Requires deliberate blocking and a repeatable lighting setup.");
        var quality = new MovieShotQualityRequirements(
            MovieQualityLevels.Cinematic,
            ["Readable facial performance", "Stable subject identity"],
            "Protect the approved composition through downstream stages.");
        var output = new MovieShotTargetOutputRequirements(
            "16:9",
            "uhd",
            "24 fps",
            "cinematic-neutral",
            "stereo",
            "video",
            "Keep the target intent independent of the eventual adapter.");

        Assert.Null(MovieShotProductionContractValidation.Validate(
            8,
            MovieShotNarrativeImportance.Primary,
            complexity,
            quality,
            MovieShotContinuitySensitivities.High,
            MovieShotUpscaleSuitabilities.Conditional,
            output));

        var json = MovieShotProductionContractSerialization.ToJson(output);
        var roundTrip = MovieShotProductionContractSerialization.FromJson<MovieShotTargetOutputRequirements>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal("16:9", roundTrip!.AspectRatio);
        Assert.Equal("uhd", roundTrip.ResolutionIntent);
        Assert.Contains("target intent", roundTrip.Notes, StringComparison.Ordinal);
        Assert.Contains("resolutionIntent", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_contract_rejects_unsupported_choices_and_unbounded_profiles()
    {
        var invalid = MovieShotProductionContractValidation.Validate(
            8,
            "hero",
            new MovieShotComplexityProfile("extreme"),
            new MovieShotQualityRequirements("vendor-premium"),
            "fragile",
            "provider-native",
            null);

        Assert.NotNull(invalid);
        Assert.Contains("narrative importance", invalid, StringComparison.OrdinalIgnoreCase);

        var tooManyDrivers = Enumerable.Repeat("driver", 17).ToArray();
        var complexityError = MovieShotProductionContractValidation.Validate(
            null,
            null,
            new MovieShotComplexityProfile(MovieShotComplexityLevels.Moderate, tooManyDrivers),
            null,
            null,
            null,
            null);

        Assert.Equal("Production complexity supports at most 16 drivers.", complexityError);
    }

    [Fact]
    public void Projection_reuses_existing_duration_cinematography_planning_and_approval_provenance()
    {
        var versionId = Guid.NewGuid();
        var sourceVersionId = Guid.NewGuid();
        var shot = new MovieShot
        {
            Id = Guid.NewGuid(),
            DurationSeconds = 12,
            NarrativeImportance = MovieShotNarrativeImportance.Critical,
            ProductionComplexityJson = MovieShotProductionContractSerialization.ToJson(new MovieShotComplexityProfile(MovieShotComplexityLevels.Moderate, ["continuity"])),
            QualityRequirementsJson = MovieShotProductionContractSerialization.ToJson(new MovieShotQualityRequirements(MovieQualityLevels.Studio)),
            ContinuitySensitivity = MovieShotContinuitySensitivities.Locked,
            UpscaleSuitability = MovieShotUpscaleSuitabilities.Preferred,
            TargetOutputRequirementsJson = MovieShotProductionContractSerialization.ToJson(new MovieShotTargetOutputRequirements(ResolutionIntent: "uhd")),
            CinematographyJson = CinematographyIntentValidator.ToJson(new CinematographyIntentSelection(CinematographyIntent.Intimate, "intimate-naturalism", "Protect eyeline.")),
            ProductionStage = MovieProductionStages.ApprovedStoryboard,
            Status = MovieShotStatuses.Approved,
        };
        shot.ProductionVersions.Add(new MovieProductionVersion
        {
            Id = versionId,
            VersionNumber = 2,
            Stage = MovieProductionStages.ApprovedStoryboard,
            Status = MovieProductionVersionStatuses.Approved,
            SourceVersionId = sourceVersionId,
            StageProvenanceJson = "{\"source\":\"shot-plan\"}",
            ReviewedByUserId = Guid.NewGuid(),
            ReviewedAt = DateTime.UtcNow,
        });

        var contract = MovieShotProductionContractProjection.FromShot(shot);

        Assert.Equal(MovieShotProductionContractSchema.CurrentVersion, contract.SchemaVersion);
        Assert.Equal(12, contract.DurationSeconds);
        Assert.Equal(MovieShotNarrativeImportance.Critical, contract.NarrativeImportance);
        Assert.Equal(CinematographyIntent.Intimate, contract.Cinematography!.Intent);
        Assert.Equal(MovieShotComplexityLevels.Moderate, contract.ProductionComplexity!.Level);
        Assert.Equal(MovieQualityLevels.Studio, contract.QualityRequirements!.MinimumLevel);
        Assert.Equal(MovieShotContinuitySensitivities.Locked, contract.ContinuitySensitivity);
        Assert.Equal(MovieShotUpscaleSuitabilities.Preferred, contract.UpscaleSuitability);
        Assert.Equal("uhd", contract.TargetOutputRequirements!.ResolutionIntent);
        Assert.Equal(MovieShotPlanStates.Storyboard, contract.PlanningStatus);
        Assert.Equal(versionId, contract.Approval!.ProductionVersionId);
        Assert.Equal(sourceVersionId, contract.Approval.SourceVersionId);
        Assert.Contains("shot-plan", contract.Approval.StageProvenanceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_serialization_does_not_introduce_provider_or_model_fields()
    {
        var names = typeof(MovieShotProductionContractDto).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Cost", StringComparison.OrdinalIgnoreCase));
    }
}
