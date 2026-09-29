using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProviderCapabilityRegistryTests
{
    [Fact]
    public void Empty_default_catalog_is_safe_and_has_no_available_capabilities()
    {
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions());

        Assert.Equal(1, registry.Snapshot.SchemaVersion);
        Assert.Empty(registry.Snapshot.Definitions);
        Assert.Empty(registry.GetAvailable(ProviderCapabilityKinds.VideoGeneration));
        Assert.False(registry.Validate(new ProviderCapabilityRequest(ProviderCapabilityKinds.VideoGeneration)).IsSupported);
    }

    [Fact]
    public void Matching_request_returns_internal_candidate_with_normalized_metadata()
    {
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions
        {
            CatalogVersion = "video-catalog-2026-09-29",
            EffectiveAtUtc = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
            Source = "internal-benchmark-snapshot",
            Definitions = [Definition(definition =>
            {
                definition.ProviderKey = "Provider-A";
                definition.ModelKey = "Model-1";
                definition.Features = [ProviderCapabilityFeatures.TextToVideo, " HDR "];
                definition.SupportedAspectRatios = ["16:9", " 1:1 "];
                definition.CostMetadata = new ProviderCapabilityCostMetadataOptions
                {
                    IsKnown = true,
                    RateKey = "provider-a:model-1:video-per-second",
                    Unit = "second",
                    PricingVersion = "pricing-2026-09",
                    PricingSource = "internal-price-snapshot",
                    PricingEffectiveAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                };
            })],
        });

        var result = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoGeneration,
            Width: 1920,
            Height: 1080,
            AspectRatio: "16:9",
            DurationSeconds: 5,
            RequiredFeatures: [ProviderCapabilityFeatures.TextToVideo, "hdr"]));

        Assert.True(result.IsSupported);
        var match = Assert.Single(result.Matches);
        Assert.Equal("Provider-A", match.ProviderKey);
        Assert.Equal("Model-1", match.ModelKey);
        Assert.Contains("hdr", match.Features);
        Assert.Equal("1:1", match.SupportedAspectRatios[1]);
        Assert.Equal("provider-a:model-1:video-per-second", match.CostMetadata!.RateKey);
        Assert.Equal("pricing-2026-09", match.CostMetadata.PricingVersion);
        Assert.Equal(DateTimeKind.Utc, match.Provenance.CapturedAtUtc.Kind);
    }

    [Fact]
    public void Unsupported_dimensions_features_and_constraints_fail_without_provider_details_in_codes()
    {
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions { Definitions = [Definition()] });

        var result = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoGeneration,
            Width: 3840,
            Height: 2160,
            AspectRatio: "4:3",
            DurationSeconds: 20,
            RequiredFeatures: [ProviderCapabilityFeatures.NativeAudio],
            HasInputAsset: false,
            ReferenceAssetCount: 3));

        Assert.False(result.IsSupported);
        Assert.Contains("resolution_unsupported", result.FailureCodes);
        Assert.DoesNotContain("provider-a", string.Join("|", result.FailureCodes), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model-1", string.Join("|", result.FailureCodes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Input_asset_and_reference_limits_are_enforced()
    {
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions
        {
            Definitions = [Definition(definition =>
            {
                definition.Constraints = new ProviderCapabilityConstraintsOptions
                {
                    RequiresInputAsset = true,
                    MaximumReferenceAssets = 1,
                    MaximumInputWidth = 1280,
                    MaximumInputHeight = 720,
                    MinimumDurationSeconds = 2,
                    MaximumDurationSeconds = 10,
                };
            })],
        });

        var missingInput = registry.Validate(new ProviderCapabilityRequest(ProviderCapabilityKinds.VideoGeneration, 1920, 1080, "16:9", 5));
        Assert.False(missingInput.IsSupported);
        Assert.Contains("input_asset_required", missingInput.FailureCodes);

        var inputTooLarge = registry.Validate(new ProviderCapabilityRequest(ProviderCapabilityKinds.VideoGeneration, 1920, 1080, "16:9", 5, HasInputAsset: true, InputWidth: 1920, InputHeight: 1080));
        Assert.False(inputTooLarge.IsSupported);
        Assert.Contains("input_resolution_unsupported", inputTooLarge.FailureCodes);

        var tooManyReferences = registry.Validate(new ProviderCapabilityRequest(ProviderCapabilityKinds.VideoGeneration, 1920, 1080, "16:9", 5, HasInputAsset: true, ReferenceAssetCount: 2));
        Assert.False(tooManyReferences.IsSupported);
        Assert.Contains("reference_asset_count_unsupported", tooManyReferences.FailureCodes);
    }

    [Fact]
    public void Unavailable_definitions_are_not_candidates_and_invalid_options_are_rejected()
    {
        var unavailable = Definition(definition => definition.IsAvailable = false);
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions { Definitions = [unavailable] });
        Assert.Empty(registry.GetAvailable(ProviderCapabilityKinds.VideoGeneration));

        var invalid = new ProviderCapabilityRegistryOptions
        {
            Definitions = [Definition(definition =>
            {
                definition.SupportedAspectRatios = ["16/9"];
                definition.SupportedResolutions = [new ProviderResolutionOptions { Width = 1920, Height = 1080 }, new ProviderResolutionOptions { Width = 1920, Height = 1080 }];
            })],
        };

        var failures = ProviderCapabilityRegistryOptionsValidator.ValidateOptions(invalid);
        Assert.Contains(failures, failure => failure.Contains("invalid aspect ratio", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(failures, failure => failure.Contains("duplicate resolution", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<OptionsValidationException>(() => CreateRegistry(invalid));
    }

    private static ProviderCapabilityRegistry CreateRegistry(ProviderCapabilityRegistryOptions options) =>
        new(Options.Create(options));

    private static ProviderCapabilityDefinitionOptions Definition(Action<ProviderCapabilityDefinitionOptions>? customize = null)
    {
        var definition = new ProviderCapabilityDefinitionOptions
        {
            ProviderKey = "provider-a",
            ModelKey = "model-1",
            DefinitionVersion = "capability-2026-09-29",
            Capability = ProviderCapabilityKinds.VideoGeneration,
            IsAvailable = true,
            SupportedResolutions = [new ProviderResolutionOptions { Width = 1920, Height = 1080 }, new ProviderResolutionOptions { Width = 1280, Height = 720 }],
            SupportedAspectRatios = ["16:9", "1:1"],
            SupportedDurationsSeconds = [5, 10],
            Features = [ProviderCapabilityFeatures.TextToVideo, ProviderCapabilityFeatures.ImageToVideo],
            Constraints = new ProviderCapabilityConstraintsOptions { MaximumReferenceAssets = 2 },
            Provenance = new ProviderCapabilityProvenanceOptions
            {
                Source = "internal-benchmark-snapshot",
                CapturedAtUtc = "2026-09-29T00:00:00Z",
                Notes = "test fixture",
            },
        };
        customize?.Invoke(definition);
        return definition;
    }
}
