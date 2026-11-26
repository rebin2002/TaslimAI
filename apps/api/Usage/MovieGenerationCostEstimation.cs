using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

public static class MovieGenerationCostEstimateStates
{
    public const string Estimated = "estimated";
    public const string Unknown = "unknown";
    public const string Unevaluated = "unevaluated";
}

public static class MovieGenerationCostEstimateReasons
{
    public const string RequestInvalid = "request_invalid";
    public const string CapabilityMetadataMissing = "capability_metadata_missing";
    public const string RouteUnavailable = "capability_route_unavailable";
    public const string PricingMissing = "pricing_missing";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string EstimateCapExceeded = "estimate_cap_exceeded";
}

public sealed record MovieGenerationCostComponent(
    string Dimension,
    decimal Quantity,
    string Unit,
    decimal? MinimumUsd,
    decimal? MaximumUsd);

/// <summary>
/// Safe, provider-neutral cost data. It intentionally contains no provider, model, prompt,
/// endpoint, or credential fields and is suitable for persistence in a normal-user preview.
/// </summary>
public sealed record MovieGenerationCostEstimate(
    string State,
    decimal? MinimumAmountUsd,
    decimal? MaximumAmountUsd,
    string Currency,
    [property: JsonIgnore] string? PricingVersion,
    [property: JsonIgnore] DateTime? PricingEffectiveAtUtc,
    [property: JsonIgnore] string? PricingSource,
    IReadOnlyList<MovieGenerationCostComponent> Components,
    string? Reason = null)
{
    public bool IsEstimated => string.Equals(State, MovieGenerationCostEstimateStates.Estimated, StringComparison.Ordinal);
    public decimal? AmountUsd => MaximumAmountUsd;

    public static MovieGenerationCostEstimate Unevaluated(string reason, string currency = UsageCurrencies.Usd) =>
        new(MovieGenerationCostEstimateStates.Unevaluated, null, null, currency, null, null, null, [], reason);

    public static MovieGenerationCostEstimate Unknown(string reason, string currency = UsageCurrencies.Usd) =>
        new(MovieGenerationCostEstimateStates.Unknown, null, null, currency, null, null, null, [], reason);

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}

public static class MovieGenerationCostEstimateAdapter
{
    public static GenerationCostEstimate ToGenerationCostEstimate(this MovieGenerationCostEstimate estimate)
    {
        var components = estimate.Components
            .Select(item => new GenerationCostComponent(
                GenerationCostDimension.ProviderFixed,
                1m,
                item.Unit,
                null,
                item.MaximumUsd,
                item.Dimension))
            .ToArray();

        return new GenerationCostEstimate(
            estimate.IsEstimated && estimate.MaximumAmountUsd.HasValue,
            estimate.MaximumAmountUsd,
            estimate.Currency,
            estimate.PricingVersion,
            estimate.PricingEffectiveAtUtc,
            estimate.PricingSource,
            components,
            estimate.Reason);
    }
}

/// <summary>
/// User intent only. Provider and model selection are server-side resolver inputs.
/// </summary>
public sealed record MovieGenerationCostRequest(
    int DurationSeconds,
    string SourceResolution,
    string TargetResolution,
    string QualityTier,
    string ProcessingPath,
    int RetryAttempts = 0,
    bool UpscalingRequested = false,
    int UpscalePasses = 0);

public sealed class MovieGenerationCostEstimatorOptions
{
    public int MaxDurationSeconds { get; set; } = 3_600;
    public int MaxRetryAttempts { get; set; } = 8;
    public int MaxUpscalePasses { get; set; } = 3;
    public int MaxCandidateRoutes { get; set; } = 32;
    public decimal MaxEstimateUsd { get; set; } = 10_000m;
}

public interface IMovieGenerationCostEstimator
{
    Task<MovieGenerationCostEstimate> EstimateAsync(
        MovieGenerationCostRequest request,
        string? selectedProviderKey = null,
        string? selectedModelKey = null,
        CancellationToken cancellationToken = default);
}

public sealed class MovieGenerationCostEstimator(
    TaslimDbContext db,
    IOptions<MovieGenerationCostEstimatorOptions> options) : IMovieGenerationCostEstimator
{
    private const string MovieVideoCapability = "movie.video";
    private readonly MovieGenerationCostEstimatorOptions settings = options.Value;

    public async Task<MovieGenerationCostEstimate> EstimateAsync(
        MovieGenerationCostRequest request,
        string? selectedProviderKey = null,
        string? selectedModelKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request)) return MovieGenerationCostEstimate.Unknown(MovieGenerationCostEstimateReasons.RequestInvalid);
        if (request.DurationSeconds > Math.Max(1, settings.MaxDurationSeconds))
            return MovieGenerationCostEstimate.Unknown("duration_cap_exceeded");
        if (request.RetryAttempts > Math.Max(0, settings.MaxRetryAttempts))
            return MovieGenerationCostEstimate.Unknown("retry_attempt_cap_exceeded");
        if (request.UpscalePasses > Math.Max(0, settings.MaxUpscalePasses))
            return MovieGenerationCostEstimate.Unknown("upscale_pass_cap_exceeded");

        var duration = request.DurationSeconds;
        var retryAttempts = request.RetryAttempts;
        var upscalePasses = request.UpscalingRequested || RequiresUpscaling(request.ProcessingPath)
            ? Math.Max(1, request.UpscalePasses)
            : 0;
        if (upscalePasses > Math.Max(0, settings.MaxUpscalePasses))
            return MovieGenerationCostEstimate.Unknown("upscale_pass_cap_exceeded");

        var provider = Normalize(selectedProviderKey);
        var model = Normalize(selectedModelKey);
        var rows = await db.ProviderCapabilityPricings.AsNoTracking()
            .Where(item => item.IsActive && item.CapabilityKey == MovieVideoCapability)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return MovieGenerationCostEstimate.Unevaluated(MovieGenerationCostEstimateReasons.CapabilityMetadataMissing);

        var candidates = rows.Where(item =>
                (provider is null || Normalize(item.ProviderKey) == provider)
                && (model is null || Normalize(item.ModelKey) == model)
                && Matches(item.SourceResolution, request.SourceResolution)
                && Matches(item.TargetResolution, request.TargetResolution)
                && Matches(item.QualityTier, request.QualityTier)
                && Matches(item.ProcessingPath, request.ProcessingPath)
                && duration <= Math.Min(Math.Max(1, item.MaxDurationSeconds), Math.Max(1, settings.MaxDurationSeconds))
                && retryAttempts <= Math.Min(Math.Max(0, item.MaxRetryAttempts), Math.Max(0, settings.MaxRetryAttempts))
                && upscalePasses <= Math.Min(Math.Max(0, item.MaxUpscalePasses), Math.Max(0, settings.MaxUpscalePasses))
                && (!RequiresUpscaling(request.ProcessingPath) && !request.UpscalingRequested || item.SupportsUpscaling))
            .Take(Math.Max(1, settings.MaxCandidateRoutes))
            .ToArray();
        if (candidates.Length == 0) return MovieGenerationCostEstimate.Unevaluated(MovieGenerationCostEstimateReasons.RouteUnavailable);

        var routeResults = new List<RouteResult>(candidates.Length);
        var hadMissingPricing = false;
        foreach (var candidate in candidates)
        {
            var basePrice = PricePair(candidate.BasePriceUsdPerSecondMin, candidate.BasePriceUsdPerSecondMax);
            var baseFixed = PricePair(candidate.BasePriceUsdFixedMin, candidate.BasePriceUsdFixedMax);
            var upscalePrice = PricePair(candidate.UpscalePriceUsdPerSecondMin, candidate.UpscalePriceUsdPerSecondMax);
            var upscaleFixed = PricePair(candidate.UpscalePriceUsdFixedMin, candidate.UpscalePriceUsdFixedMax);
            var requiresUpscale = upscalePasses > 0;
            if (basePrice is null || (requiresUpscale && upscalePrice is null))
            {
                hadMissingPricing = true;
                continue;
            }

            var attempts = retryAttempts + 1m;
            var generationCost = Add(baseFixed ?? new PriceRange(0m, 0m), Multiply(basePrice, duration));
            var baseTotal = Multiply(generationCost, attempts);
            var upscaleTotal = requiresUpscale
                ? Multiply(Add(upscaleFixed ?? new PriceRange(0m, 0m), Multiply(upscalePrice!, duration)), upscalePasses)
                : PricePair(0m, 0m)!;
            routeResults.Add(new RouteResult(
                Add(baseTotal, upscaleTotal),
                new Dictionary<string, PriceRange>(StringComparer.Ordinal)
                {
                    ["generation"] = baseTotal,
                    ["upscaling"] = upscaleTotal,
                },
                NormalizeCurrency(candidate.Currency),
                string.IsNullOrWhiteSpace(candidate.PricingVersion) ? null : candidate.PricingVersion.Trim(),
                candidate.EffectiveAtUtc == default ? null : candidate.EffectiveAtUtc,
                string.IsNullOrWhiteSpace(candidate.Source) ? null : candidate.Source.Trim()));
        }

        if (routeResults.Count == 0)
            return MovieGenerationCostEstimate.Unknown(MovieGenerationCostEstimateReasons.PricingMissing);
        var currencies = routeResults.Select(item => item.Currency).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (currencies.Length != 1)
            return MovieGenerationCostEstimate.Unknown(MovieGenerationCostEstimateReasons.CurrencyMismatch);

        var minimum = Round(routeResults.Min(item => item.Total.Minimum));
        var maximum = Round(routeResults.Max(item => item.Total.Maximum));
        if (maximum > Math.Max(0m, settings.MaxEstimateUsd))
            return MovieGenerationCostEstimate.Unknown(MovieGenerationCostEstimateReasons.EstimateCapExceeded, currencies[0]);
        if (hadMissingPricing)
            return MovieGenerationCostEstimate.Unknown(MovieGenerationCostEstimateReasons.PricingMissing, currencies[0]);

        var dimensions = routeResults.SelectMany(item => item.Components.Keys).Distinct(StringComparer.Ordinal)
            .Select(dimension => new MovieGenerationCostComponent(
                dimension,
                dimension == "generation" ? duration * (retryAttempts + 1m) : duration * upscalePasses,
                dimension == "generation" ? "seconds_per_attempt" : "seconds_per_pass",
                Round(routeResults.Min(item => item.Components[dimension].Minimum)),
                Round(routeResults.Max(item => item.Components[dimension].Maximum))))
            .ToArray();
        var metadata = routeResults[0];
        return new MovieGenerationCostEstimate(
            MovieGenerationCostEstimateStates.Estimated,
            minimum,
            maximum,
            currencies[0],
            routeResults.Select(item => item.PricingVersion).FirstOrDefault(item => item is not null),
            routeResults.Select(item => item.EffectiveAtUtc).FirstOrDefault(item => item.HasValue),
            routeResults.Select(item => item.PricingSource).FirstOrDefault(item => item is not null),
            dimensions);
    }

    private bool IsValidRequest(MovieGenerationCostRequest request) =>
        request.DurationSeconds > 0
        && request.DurationSeconds <= int.MaxValue
        && request.RetryAttempts >= 0
        && request.RetryAttempts <= int.MaxValue
        && request.UpscalePasses >= 0
        && request.UpscalePasses <= int.MaxValue
        && !string.IsNullOrWhiteSpace(request.SourceResolution)
        && !string.IsNullOrWhiteSpace(request.TargetResolution)
        && !string.IsNullOrWhiteSpace(request.QualityTier)
        && !string.IsNullOrWhiteSpace(request.ProcessingPath);

    private static bool RequiresUpscaling(string path) =>
        string.Equals(path.Trim(), "upscale", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.Trim(), "native_high_quality_then_4k_master", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string stored, string requested) =>
        string.IsNullOrWhiteSpace(stored) || string.Equals(stored.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static string NormalizeCurrency(string? value) => string.IsNullOrWhiteSpace(value) ? UsageCurrencies.Usd : value.Trim().ToUpperInvariant();
    private static decimal Round(decimal value) => decimal.Round(Math.Max(0m, value), 8, MidpointRounding.AwayFromZero);
    private static PriceRange Add(PriceRange left, PriceRange right) => new(Round(left.Minimum + right.Minimum), Round(left.Maximum + right.Maximum));
    private static PriceRange Multiply(PriceRange value, decimal factor) => new(Round(value.Minimum * factor), Round(value.Maximum * factor));

    private static PriceRange? PricePair(decimal? minimum, decimal? maximum)
    {
        if (!minimum.HasValue && !maximum.HasValue) return null;
        if (minimum is < 0m || maximum is < 0m) return null;
        var low = minimum ?? maximum!.Value;
        var high = maximum ?? minimum!.Value;
        return low <= high ? new(Round(low), Round(high)) : null;
    }

    private sealed record PriceRange(decimal Minimum, decimal Maximum);
    private sealed record RouteResult(PriceRange Total, IReadOnlyDictionary<string, PriceRange> Components, string Currency, string? PricingVersion, DateTime? EffectiveAtUtc, string? PricingSource);
}
