using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Taslim.Api.Movies;

public static class ProviderCapabilityKinds
{
    public const string VideoGeneration = "video-generation";
    public const string VideoUpscaling = "video-upscaling";
    public const string VideoMastering = "video-mastering";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        VideoGeneration,
        VideoUpscaling,
        VideoMastering,
    };
}

public static class ProviderCapabilityFeatures
{
    public const string TextToVideo = "text-to-video";
    public const string ImageToVideo = "image-to-video";
    public const string VideoToVideo = "video-to-video";
    public const string FirstFrame = "first-frame";
    public const string LastFrame = "last-frame";
    public const string MultiKeyframe = "multi-keyframe";
    public const string Extension = "extension";
    public const string Interpolation = "interpolation";
    public const string NativeAudio = "native-audio";
    public const string ReferenceImages = "reference-images";
    public const string Hdr = "hdr";
    public const string ColorManagement = "color-management";
    public const string Denoise = "denoise";
    public const string FrameInterpolation = "frame-interpolation";
}

/// <summary>
/// Server-only configuration for the capability catalog. This is metadata, not provider
/// activation. An entry must still be selected and wired through a provider adapter before
/// any external request can be made.
/// </summary>
public sealed class ProviderCapabilityRegistryOptions
{
    public int SchemaVersion { get; set; } = 1;
    public string CatalogVersion { get; set; } = "unconfigured-v1";
    public DateTime? EffectiveAtUtc { get; set; }
    public string? Source { get; set; } = "none";
    public List<ProviderCapabilityDefinitionOptions> Definitions { get; set; } = [];
}

public sealed class ProviderCapabilityDefinitionOptions
{
    public string ProviderKey { get; set; } = string.Empty;
    public string? ModelKey { get; set; }
    public string DefinitionVersion { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public List<ProviderResolutionOptions> SupportedResolutions { get; set; } = [];
    public List<string> SupportedAspectRatios { get; set; } = [];
    public List<int> SupportedDurationsSeconds { get; set; } = [];
    public List<string> Features { get; set; } = [];
    public ProviderCapabilityConstraintsOptions Constraints { get; set; } = new();
    public ProviderCapabilityCostMetadataOptions? CostMetadata { get; set; }
    public ProviderCapabilityProvenanceOptions Provenance { get; set; } = new();
}

public sealed class ProviderResolutionOptions
{
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class ProviderCapabilityConstraintsOptions
{
    public int? MinimumDurationSeconds { get; set; }
    public int? MaximumDurationSeconds { get; set; }
    public bool RequiresInputAsset { get; set; }
    public int? MaximumInputWidth { get; set; }
    public int? MaximumInputHeight { get; set; }
    public int? MaximumReferenceAssets { get; set; }
}

/// <summary>
/// A cost hook only identifies the internal pricing rule and its provenance. It does not
/// calculate customer price, charge credits, or authorize a provider call.
/// </summary>
public sealed class ProviderCapabilityCostMetadataOptions
{
    public bool IsKnown { get; set; }
    public string? RateKey { get; set; }
    public string? Unit { get; set; }
    public string Currency { get; set; } = "USD";
    public string? PricingVersion { get; set; }
    public DateTime? PricingEffectiveAtUtc { get; set; }
    public string? PricingSource { get; set; }
}

public sealed class ProviderCapabilityProvenanceOptions
{
    public string Source { get; set; } = string.Empty;
    public string CapturedAtUtc { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public sealed record ProviderResolution(int Width, int Height);

public sealed record ProviderCapabilityConstraints(
    int? MinimumDurationSeconds,
    int? MaximumDurationSeconds,
    bool RequiresInputAsset,
    int? MaximumInputWidth,
    int? MaximumInputHeight,
    int? MaximumReferenceAssets);

public sealed record ProviderCapabilityCostMetadata(
    bool IsKnown,
    string? RateKey,
    string? Unit,
    string Currency,
    string? PricingVersion,
    DateTime? PricingEffectiveAtUtc,
    string? PricingSource);

public sealed record ProviderCapabilityProvenance(
    string Source,
    DateTime CapturedAtUtc,
    string? Notes);

public sealed record ProviderCapabilityDefinition(
    string ProviderKey,
    string? ModelKey,
    string DefinitionVersion,
    string Capability,
    bool IsAvailable,
    IReadOnlyList<ProviderResolution> SupportedResolutions,
    IReadOnlyList<string> SupportedAspectRatios,
    IReadOnlyList<int> SupportedDurationsSeconds,
    IReadOnlySet<string> Features,
    ProviderCapabilityConstraints Constraints,
    ProviderCapabilityCostMetadata? CostMetadata,
    ProviderCapabilityProvenance Provenance);

public sealed record ProviderCapabilityRegistrySnapshot(
    int SchemaVersion,
    string CatalogVersion,
    DateTime? EffectiveAtUtc,
    string? Source,
    IReadOnlyList<ProviderCapabilityDefinition> Definitions);

public sealed record ProviderCapabilityRequest(
    string Capability,
    int? Width = null,
    int? Height = null,
    string? AspectRatio = null,
    int? DurationSeconds = null,
    IReadOnlyCollection<string>? RequiredFeatures = null,
    bool HasInputAsset = false,
    int ReferenceAssetCount = 0,
    int? InputWidth = null,
    int? InputHeight = null);

public sealed record ProviderCapabilityValidationResult(
    bool IsSupported,
    IReadOnlyList<string> FailureCodes,
    IReadOnlyList<ProviderCapabilityDefinition> Matches)
{
    public static ProviderCapabilityValidationResult Unsupported(params string[] codes) =>
        new(false, codes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), []);
}

public interface IProviderCapabilityRegistry
{
    ProviderCapabilityRegistrySnapshot Snapshot { get; }
    IReadOnlyList<ProviderCapabilityDefinition> GetAvailable(string capability);
    ProviderCapabilityValidationResult Validate(ProviderCapabilityRequest request);
}

public sealed class ProviderCapabilityRegistryOptionsValidator : IValidateOptions<ProviderCapabilityRegistryOptions>
{
    private static readonly Regex AspectRatioPattern = new("^[1-9][0-9]*:[1-9][0-9]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public ValidateOptionsResult Validate(string? name, ProviderCapabilityRegistryOptions options)
    {
        var failures = ValidateOptions(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> ValidateOptions(ProviderCapabilityRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.SchemaVersion != 1)
            failures.Add("ProviderCapabilities.SchemaVersion must be 1.");
        RequireText(options.CatalogVersion, "ProviderCapabilities.CatalogVersion", 100, failures);
        if (options.EffectiveAtUtc is { } effectiveAt && effectiveAt.Kind == DateTimeKind.Local)
            failures.Add("ProviderCapabilities.EffectiveAtUtc must be UTC or unspecified.");
        if (options.Definitions is null)
        {
            failures.Add("ProviderCapabilities.Definitions is required.");
            return failures;
        }

        if (options.Definitions.Count > 256)
            failures.Add("ProviderCapabilities.Definitions cannot contain more than 256 entries.");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in options.Definitions)
        {
            if (definition is null)
            {
                failures.Add("ProviderCapabilities.Definitions cannot contain null entries.");
                continue;
            }

            RequireText(definition.ProviderKey, "ProviderCapabilities.Definitions.ProviderKey", 120, failures);
            if (!string.IsNullOrWhiteSpace(definition.ModelKey)) RequireText(definition.ModelKey, "ProviderCapabilities.Definitions.ModelKey", 120, failures);
            RequireText(definition.DefinitionVersion, "ProviderCapabilities.Definitions.DefinitionVersion", 100, failures);
            RequireText(definition.Capability, "ProviderCapabilities.Definitions.Capability", 80, failures);
            if (!ProviderCapabilityKinds.Supported.Contains(definition.Capability?.Trim() ?? string.Empty))
                failures.Add($"Unsupported provider capability '{definition.Capability}'.");

            var normalizedKey = DefinitionKey(definition.ProviderKey, definition.ModelKey, definition.Capability);
            if (!keys.Add(normalizedKey)) failures.Add($"Duplicate provider capability definition '{normalizedKey}'.");

            if (definition.SupportedResolutions is null || definition.SupportedResolutions.Count == 0)
                failures.Add($"Definition '{normalizedKey}' must declare at least one supported resolution.");
            else
            {
                var resolutions = new HashSet<(int Width, int Height)>();
                foreach (var resolution in definition.SupportedResolutions)
                {
                    if (resolution is null || resolution.Width is < 1 or > 16_384 || resolution.Height is < 1 or > 16_384)
                        failures.Add($"Definition '{normalizedKey}' contains an invalid resolution.");
                    else if (!resolutions.Add((resolution.Width, resolution.Height)))
                        failures.Add($"Definition '{normalizedKey}' contains a duplicate resolution.");
                }
            }

            if (definition.SupportedAspectRatios is null || definition.SupportedAspectRatios.Count == 0)
                failures.Add($"Definition '{normalizedKey}' must declare at least one supported aspect ratio.");
            else
            {
                var aspects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var aspect in definition.SupportedAspectRatios)
                {
                    var normalizedAspect = NormalizeAspect(aspect);
                    if (normalizedAspect is null) failures.Add($"Definition '{normalizedKey}' contains an invalid aspect ratio.");
                    else if (!aspects.Add(normalizedAspect)) failures.Add($"Definition '{normalizedKey}' contains a duplicate aspect ratio.");
                }
            }

            if (definition.SupportedDurationsSeconds is null || definition.SupportedDurationsSeconds.Count == 0)
                failures.Add($"Definition '{normalizedKey}' must declare at least one supported duration.");
            else if (definition.SupportedDurationsSeconds.Any(duration => duration is < 1 or > 3_600) || definition.SupportedDurationsSeconds.Distinct().Count() != definition.SupportedDurationsSeconds.Count)
                failures.Add($"Definition '{normalizedKey}' contains invalid or duplicate durations.");

            if (definition.Features is null || definition.Features.Count > 64 || definition.Features.Any(feature => string.IsNullOrWhiteSpace(feature) || feature.Trim().Length > 80 || feature.Trim().Any(char.IsWhiteSpace)))
                failures.Add($"Definition '{normalizedKey}' contains invalid features.");

            ValidateConstraints(definition.Constraints, normalizedKey, failures);
            ValidateCost(definition.CostMetadata, normalizedKey, failures);
            ValidateProvenance(definition.Provenance, normalizedKey, failures);
        }

        return failures;
    }

    private static void ValidateConstraints(ProviderCapabilityConstraintsOptions? constraints, string key, ICollection<string> failures)
    {
        if (constraints is null)
        {
            failures.Add($"Definition '{key}' must declare constraints.");
            return;
        }

        if (constraints.MinimumDurationSeconds is < 1 or > 3_600 || constraints.MaximumDurationSeconds is < 1 or > 3_600 ||
            (constraints.MinimumDurationSeconds.HasValue && constraints.MaximumDurationSeconds.HasValue && constraints.MinimumDurationSeconds > constraints.MaximumDurationSeconds))
            failures.Add($"Definition '{key}' contains invalid duration constraints.");
        if (constraints.MaximumInputWidth is < 1 or > 16_384 || constraints.MaximumInputHeight is < 1 or > 16_384 || constraints.MaximumReferenceAssets is < 0 or > 64)
            failures.Add($"Definition '{key}' contains invalid input constraints.");
    }

    private static void ValidateCost(ProviderCapabilityCostMetadataOptions? cost, string key, ICollection<string> failures)
    {
        if (cost is null) return;
        if (cost.IsKnown)
        {
            RequireText(cost.RateKey, $"Definition '{key}' cost RateKey", 160, failures);
            RequireText(cost.Unit, $"Definition '{key}' cost Unit", 40, failures);
            RequireText(cost.PricingVersion, $"Definition '{key}' cost PricingVersion", 100, failures);
            RequireText(cost.PricingSource, $"Definition '{key}' cost PricingSource", 500, failures);
        }
        if (string.IsNullOrWhiteSpace(cost.Currency) || cost.Currency.Trim().Length > 12)
            failures.Add($"Definition '{key}' cost Currency is invalid.");
        if (cost.PricingEffectiveAtUtc is { } effectiveAt && effectiveAt.Kind == DateTimeKind.Local)
            failures.Add($"Definition '{key}' cost PricingEffectiveAtUtc must be UTC or unspecified.");
    }

    private static void ValidateProvenance(ProviderCapabilityProvenanceOptions? provenance, string key, ICollection<string> failures)
    {
        if (provenance is null)
        {
            failures.Add($"Definition '{key}' must declare provenance.");
            return;
        }
        RequireText(provenance.Source, $"Definition '{key}' provenance Source", 500, failures);
        RequireText(provenance.CapturedAtUtc, $"Definition '{key}' provenance CapturedAtUtc", 80, failures);
        if (!DateTime.TryParse(provenance.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            failures.Add($"Definition '{key}' provenance CapturedAtUtc must be an ISO-8601 timestamp.");
        if (provenance.Notes?.Length > 1_000)
            failures.Add($"Definition '{key}' provenance Notes is too long.");
    }

    private static void RequireText(string? value, string field, int maxLength, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
            failures.Add($"{field} is required and must be at most {maxLength} characters.");
    }

    internal static string DefinitionKey(string? providerKey, string? modelKey, string? capability) =>
        $"{providerKey?.Trim().ToLowerInvariant()}:{modelKey?.Trim().ToLowerInvariant()}:{capability?.Trim().ToLowerInvariant()}";

    internal static string? NormalizeAspect(string? aspect)
    {
        var normalized = aspect?.Trim();
        return normalized is not null && AspectRatioPattern.IsMatch(normalized) ? normalized : null;
    }
}

public sealed class ProviderCapabilityRegistry(IOptions<ProviderCapabilityRegistryOptions> options) : IProviderCapabilityRegistry
{
    private readonly ProviderCapabilityRegistrySnapshot snapshot = BuildSnapshot(options.Value);

    public ProviderCapabilityRegistrySnapshot Snapshot => snapshot;

    public IReadOnlyList<ProviderCapabilityDefinition> GetAvailable(string capability)
    {
        var normalizedCapability = capability?.Trim() ?? string.Empty;
        return snapshot.Definitions
            .Where(definition => definition.IsAvailable && string.Equals(definition.Capability, normalizedCapability, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public ProviderCapabilityValidationResult Validate(ProviderCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capability = request.Capability?.Trim() ?? string.Empty;
        if (!ProviderCapabilityKinds.Supported.Contains(capability)) return ProviderCapabilityValidationResult.Unsupported("capability_invalid");
        if (request.Width.HasValue != request.Height.HasValue) return ProviderCapabilityValidationResult.Unsupported("resolution_incomplete");
        if (request.Width is < 1 or > 16_384 || request.Height is < 1 or > 16_384) return ProviderCapabilityValidationResult.Unsupported("resolution_invalid");
        if (request.InputWidth.HasValue != request.InputHeight.HasValue) return ProviderCapabilityValidationResult.Unsupported("input_resolution_incomplete");
        if (request.InputWidth is < 1 or > 16_384 || request.InputHeight is < 1 or > 16_384) return ProviderCapabilityValidationResult.Unsupported("input_resolution_invalid");
        if (request.DurationSeconds is < 1 or > 3_600) return ProviderCapabilityValidationResult.Unsupported("duration_invalid");
        if (request.ReferenceAssetCount is < 0 or > 64) return ProviderCapabilityValidationResult.Unsupported("reference_asset_count_invalid");

        var requiredFeatures = (request.RequiredFeatures ?? []).Where(feature => !string.IsNullOrWhiteSpace(feature)).Select(feature => feature.Trim().ToLowerInvariant()).Distinct().ToArray();
        var matches = new List<ProviderCapabilityDefinition>();
        var failureCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in GetAvailable(capability))
        {
            if (request.Width.HasValue && !definition.SupportedResolutions.Any(resolution => resolution.Width == request.Width && resolution.Height == request.Height))
            {
                failureCodes.Add("resolution_unsupported");
                continue;
            }
            if (request.AspectRatio is not null && !definition.SupportedAspectRatios.Contains(request.AspectRatio.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                failureCodes.Add("aspect_ratio_unsupported");
                continue;
            }
            if (request.DurationSeconds.HasValue && (!definition.SupportedDurationsSeconds.Contains(request.DurationSeconds.Value) ||
                (definition.Constraints.MinimumDurationSeconds.HasValue && request.DurationSeconds < definition.Constraints.MinimumDurationSeconds) ||
                (definition.Constraints.MaximumDurationSeconds.HasValue && request.DurationSeconds > definition.Constraints.MaximumDurationSeconds)))
            {
                failureCodes.Add("duration_unsupported");
                continue;
            }
            if (requiredFeatures.Any(feature => !definition.Features.Contains(feature)))
            {
                failureCodes.Add("feature_unsupported");
                continue;
            }
            if (definition.Constraints.RequiresInputAsset && !request.HasInputAsset)
            {
                failureCodes.Add("input_asset_required");
                continue;
            }
            if ((definition.Constraints.MaximumInputWidth is { } maxInputWidth && request.InputWidth > maxInputWidth) ||
                (definition.Constraints.MaximumInputHeight is { } maxInputHeight && request.InputHeight > maxInputHeight))
            {
                failureCodes.Add("input_resolution_unsupported");
                continue;
            }
            if (definition.Constraints.MaximumReferenceAssets is { } maxReferences && request.ReferenceAssetCount > maxReferences)
            {
                failureCodes.Add("reference_asset_count_unsupported");
                continue;
            }
            matches.Add(definition);
        }

        return matches.Count > 0
            ? new(true, [], matches)
            : new(false, failureCodes.Count == 0 ? ["capability_unavailable"] : failureCodes.ToArray(), []);
    }

    private static ProviderCapabilityRegistrySnapshot BuildSnapshot(ProviderCapabilityRegistryOptions settings)
    {
        var failures = ProviderCapabilityRegistryOptionsValidator.ValidateOptions(settings);
        if (failures.Count > 0) throw new OptionsValidationException(nameof(ProviderCapabilityRegistryOptions), typeof(ProviderCapabilityRegistryOptions), failures);

        var definitions = settings.Definitions.Select(definition => new ProviderCapabilityDefinition(
            definition.ProviderKey.Trim(),
            string.IsNullOrWhiteSpace(definition.ModelKey) ? null : definition.ModelKey.Trim(),
            definition.DefinitionVersion.Trim(),
            definition.Capability.Trim().ToLowerInvariant(),
            definition.IsAvailable,
            definition.SupportedResolutions.Select(resolution => new ProviderResolution(resolution.Width, resolution.Height)).ToArray(),
            definition.SupportedAspectRatios.Select(aspect => ProviderCapabilityRegistryOptionsValidator.NormalizeAspect(aspect)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            definition.SupportedDurationsSeconds.Distinct().OrderBy(duration => duration).ToArray(),
            definition.Features.Select(feature => feature.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            new ProviderCapabilityConstraints(
                definition.Constraints.MinimumDurationSeconds,
                definition.Constraints.MaximumDurationSeconds,
                definition.Constraints.RequiresInputAsset,
                definition.Constraints.MaximumInputWidth,
                definition.Constraints.MaximumInputHeight,
                definition.Constraints.MaximumReferenceAssets),
            definition.CostMetadata is null ? null : new ProviderCapabilityCostMetadata(
                definition.CostMetadata.IsKnown,
                definition.CostMetadata.RateKey?.Trim(),
                definition.CostMetadata.Unit?.Trim(),
                definition.CostMetadata.Currency.Trim().ToUpperInvariant(),
                definition.CostMetadata.PricingVersion?.Trim(),
                ToUtc(definition.CostMetadata.PricingEffectiveAtUtc),
                definition.CostMetadata.PricingSource?.Trim()),
            new ProviderCapabilityProvenance(
                definition.Provenance.Source.Trim(),
                ParseUtc(definition.Provenance.CapturedAtUtc),
                definition.Provenance.Notes?.Trim()))).ToArray();

        return new(
            settings.SchemaVersion,
            settings.CatalogVersion.Trim(),
            ToUtc(settings.EffectiveAtUtc),
            settings.Source?.Trim(),
            definitions);
    }

    private static DateTime ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime;

    private static DateTime? ToUtc(DateTime? value)
    {
        if (!value.HasValue) return null;
        return value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : value.Value.ToUniversalTime();
    }
}
