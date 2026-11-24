using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic, offline contracts for the provider-neutral video-editing capability vocabulary
/// introduced in <see cref="ProviderCapabilityKinds"/> and <see cref="ProviderCapabilityFeatures"/>
/// (issue #698, Phase 1 of the architecture spike documented in PR #765).
/// These tests prove the vocabulary exists, is discrete, and fails closed. Registry vocabulary is
/// metadata only: nothing here activates a provider, routes a request, or performs any network call.
/// </summary>
public sealed class MovieVideoEditCapabilityVocabularyTests
{
    private static readonly string[] VideoEditFeatureKeys =
    [
        ProviderCapabilityFeatures.EditInPlace,
        ProviderCapabilityFeatures.RegionWindowEdit,
        ProviderCapabilityFeatures.SourceAudioPassthrough,
        ProviderCapabilityFeatures.IdentityPreserve,
        ProviderCapabilityFeatures.WardrobeTransform,
        ProviderCapabilityFeatures.FrameRateConform,
    ];

    private static readonly Regex KebabCase = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    [Fact]
    public void VideoEditingKindIsRecognizedAlongsideLegacyKinds()
    {
        Assert.Equal("video-editing", ProviderCapabilityKinds.VideoEditing);

        // The new kind is part of the supported vocabulary.
        Assert.Contains(ProviderCapabilityKinds.VideoEditing, ProviderCapabilityKinds.Supported);

        // Legacy kinds are preserved unchanged: adding the editing kind must not evict or rename them.
        Assert.Contains("video-generation", ProviderCapabilityKinds.Supported);
        Assert.Contains("video-upscaling", ProviderCapabilityKinds.Supported);
        Assert.Contains("video-mastering", ProviderCapabilityKinds.Supported);

        // Kind matching remains case-insensitive, consistent with the existing registry behavior.
        Assert.True(ProviderCapabilityKinds.Supported.Contains("VIDEO-EDITING"));
    }

    [Fact]
    public void VideoEditFeatureKeysAreDiscreteNormalizedKebabCaseTokens()
    {
        Assert.Equal(
            [
                "edit-in-place",
                "region-window-edit",
                "source-audio-passthrough",
                "identity-preserve",
                "wardrobe-transform",
                "frame-rate-conform",
            ],
            VideoEditFeatureKeys);

        foreach (var feature in VideoEditFeatureKeys)
        {
            Assert.Matches(KebabCase, feature);
            Assert.True(feature.Length <= 80, $"Feature key '{feature}' exceeds the validator limit of 80 characters.");
        }

        // Discrete and unique: no duplicates, and no collision with any pre-existing feature key.
        Assert.Equal(VideoEditFeatureKeys.Length, VideoEditFeatureKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var legacyFeatureKeys = new[]
        {
            ProviderCapabilityFeatures.TextToVideo,
            ProviderCapabilityFeatures.ImageToVideo,
            ProviderCapabilityFeatures.VideoToVideo,
            ProviderCapabilityFeatures.FirstFrame,
            ProviderCapabilityFeatures.LastFrame,
            ProviderCapabilityFeatures.MultiKeyframe,
            ProviderCapabilityFeatures.Extension,
            ProviderCapabilityFeatures.Interpolation,
            ProviderCapabilityFeatures.NativeAudio,
            ProviderCapabilityFeatures.ReferenceImages,
            ProviderCapabilityFeatures.Hdr,
            ProviderCapabilityFeatures.ColorManagement,
            ProviderCapabilityFeatures.Denoise,
            ProviderCapabilityFeatures.FrameInterpolation,
        };

        Assert.Empty(legacyFeatureKeys.Intersect(VideoEditFeatureKeys, StringComparer.OrdinalIgnoreCase));

        // `frame-rate-conform` must not be confused with the legacy `frame-interpolation` feature.
        Assert.DoesNotContain(ProviderCapabilityFeatures.FrameInterpolation, VideoEditFeatureKeys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProviderCapabilityFeatures.VideoToVideo, VideoEditFeatureKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnconfiguredCatalogRejectsVideoEditingFailClosed()
    {
        // Mirrors the production appsettings baseline: SchemaVersion 1 with no definitions.
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions());

        Assert.Empty(registry.GetAvailable(ProviderCapabilityKinds.VideoEditing));

        var result = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoEditing,
            Width: 1920,
            Height: 1080,
            AspectRatio: "16:9",
            DurationSeconds: 10,
            RequiredFeatures: [ProviderCapabilityFeatures.EditInPlace],
            HasInputAsset: true));

        Assert.False(result.IsSupported);
        Assert.Contains("capability_unavailable", result.FailureCodes);
        Assert.DoesNotContain("capability_invalid", result.FailureCodes);
        Assert.Empty(result.Matches);
    }

    [Theory]
    [InlineData("video-edit")]
    [InlineData("editing")]
    [InlineData("video-makeover")]
    [InlineData("video-editing-v2")]
    [InlineData("")]
    public void UnknownOrMisspelledEditingKindsAreInvalidNotRoutable(string capability)
    {
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions());

        var result = registry.Validate(new ProviderCapabilityRequest(
            capability,
            RequiredFeatures: [ProviderCapabilityFeatures.EditInPlace],
            HasInputAsset: true));

        Assert.False(result.IsSupported);
        Assert.Contains("capability_invalid", result.FailureCodes);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void VocabularyAloneDoesNotActivateProviders()
    {
        // A hypothetical, clearly non-production definition that declares the editing kind but stays
        // unavailable. The validator must accept the new kind (it is in the supported vocabulary) and
        // the registry must still refuse to surface it: declaring vocabulary is not an activation.
        var definition = EditDefinition(ProviderCapabilityKinds.VideoEditing, isAvailable: false,
            [ProviderCapabilityFeatures.EditInPlace]);
        var options = new ProviderCapabilityRegistryOptions { Definitions = [definition] };

        Assert.Empty(ProviderCapabilityRegistryOptionsValidator.ValidateOptions(options));

        var registry = CreateRegistry(options);

        Assert.Single(registry.Snapshot.Definitions);
        Assert.False(registry.Snapshot.Definitions[0].IsAvailable);
        Assert.Empty(registry.GetAvailable(ProviderCapabilityKinds.VideoEditing));

        var result = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoEditing,
            Width: 1920,
            Height: 1080,
            AspectRatio: "16:9",
            DurationSeconds: 10,
            RequiredFeatures: [ProviderCapabilityFeatures.EditInPlace],
            HasInputAsset: true));

        Assert.False(result.IsSupported);
        Assert.Contains("capability_unavailable", result.FailureCodes);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void AvailableEditDefinitionRemainsGatedByDiscreteFeatures()
    {
        // Even if a hypothetical entry were ever marked available, requests must match the exact
        // discrete feature keys; claiming one edit feature never implies another.
        var definition = EditDefinition(ProviderCapabilityKinds.VideoEditing, isAvailable: true,
            [ProviderCapabilityFeatures.EditInPlace, ProviderCapabilityFeatures.SourceAudioPassthrough]);
        definition.Provenance = Provenance("internal-test-fixture");
        var registry = CreateRegistry(new ProviderCapabilityRegistryOptions { Definitions = [definition] });

        var narrow = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoEditing,
            Width: 1920,
            Height: 1080,
            AspectRatio: "16:9",
            DurationSeconds: 10,
            RequiredFeatures: [ProviderCapabilityFeatures.EditInPlace],
            HasInputAsset: true));
        Assert.True(narrow.IsSupported);
        Assert.Single(narrow.Matches);

        var implied = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoEditing,
            Width: 1920,
            Height: 1080,
            AspectRatio: "16:9",
            DurationSeconds: 10,
            RequiredFeatures: [ProviderCapabilityFeatures.EditInPlace, ProviderCapabilityFeatures.WardrobeTransform],
            HasInputAsset: true));
        Assert.False(implied.IsSupported);
        Assert.Contains("feature_unsupported", implied.FailureCodes);
        Assert.Empty(implied.Matches);
    }

    [Fact]
    public void LegacyGenerationCatalogRemainsValidAndSupportedAfterVocabularyExpansion()
    {
        // Snapshot compatibility: a pre-existing generation-only catalog must validate and route
        // exactly as before; the editing vocabulary is purely additive metadata.
        var definition = EditDefinition(ProviderCapabilityKinds.VideoGeneration, isAvailable: true,
            [ProviderCapabilityFeatures.TextToVideo, ProviderCapabilityFeatures.ImageToVideo]);
        var options = new ProviderCapabilityRegistryOptions
        {
            CatalogVersion = "video-catalog-2026-09-29",
            EffectiveAtUtc = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
            Source = "internal-benchmark-snapshot",
            Definitions = [definition],
        };

        Assert.Empty(ProviderCapabilityRegistryOptionsValidator.ValidateOptions(options));

        var registry = CreateRegistry(options);

        Assert.Equal(1, registry.Snapshot.SchemaVersion);
        Assert.Equal("video-catalog-2026-09-29", registry.Snapshot.CatalogVersion);
        Assert.Single(registry.GetAvailable(ProviderCapabilityKinds.VideoGeneration));

        var result = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoGeneration,
            Width: 1920,
            Height: 1080,
            AspectRatio: "16:9",
            DurationSeconds: 5,
            RequiredFeatures: [ProviderCapabilityFeatures.TextToVideo]));

        Assert.True(result.IsSupported);
        Assert.Single(result.Matches);

        // The presence of a generation-only catalog still rejects editing requests fail-closed.
        var editAttempt = registry.Validate(new ProviderCapabilityRequest(
            ProviderCapabilityKinds.VideoEditing,
            Width: 1920,
            Height: 1080,
            AspectRatio: "16:9",
            DurationSeconds: 5,
            RequiredFeatures: [ProviderCapabilityFeatures.EditInPlace],
            HasInputAsset: true));
        Assert.False(editAttempt.IsSupported);
        Assert.Contains("capability_unavailable", editAttempt.FailureCodes);
        Assert.Empty(editAttempt.Matches);
    }

    private static ProviderCapabilityRegistry CreateRegistry(ProviderCapabilityRegistryOptions options) =>
        new(Options.Create(options));

    private static ProviderCapabilityDefinitionOptions EditDefinition(
        string capability,
        bool isAvailable,
        string[] features) => new()
        {
            ProviderKey = "fixture-provider-not-a-real-provider",
            ModelKey = "fixture-model",
            DefinitionVersion = "vocabulary-fixture-2026-11-13",
            Capability = capability,
            IsAvailable = isAvailable,
            SupportedResolutions = [new ProviderResolutionOptions { Width = 1920, Height = 1080 }],
            SupportedAspectRatios = ["16:9"],
            SupportedDurationsSeconds = [5, 10],
            Features = [.. features],
            Constraints = new ProviderCapabilityConstraintsOptions
            {
                RequiresInputAsset = capability == ProviderCapabilityKinds.VideoEditing,
                MaximumInputWidth = 3840,
                MaximumInputHeight = 2160,
                MinimumDurationSeconds = 1,
                MaximumDurationSeconds = 30,
            },
            Provenance = Provenance("offline-test-fixture"),
        };

    private static ProviderCapabilityProvenanceOptions Provenance(string source) => new()
    {
        Source = source,
        CapturedAtUtc = DateTime.Parse("2026-11-13T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToString("O", CultureInfo.InvariantCulture),
        Notes = "Vocabulary-only fixture. Not a provider activation and not product configuration.",
    };
}
