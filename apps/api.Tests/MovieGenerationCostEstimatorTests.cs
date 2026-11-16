using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieGenerationCostEstimatorTests
{
    [Fact]
    public void Client_submitted_cost_is_ignored_by_generation_request_binding()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<CreateGenerationJobRequest>(
            "{\"workspaceId\":\"00000000-0000-0000-0000-000000000001\",\"jobType\":\"movie_clip_generate\",\"inputJson\":\"{}\",\"estimatedProviderCostUsd\":9999.99,\"internalCostEstimate\":{\"amountUsd\":9999.99}}",
            options);

        Assert.NotNull(request);
        Assert.Null(request!.EstimatedProviderCostUsd);
        Assert.Null(request.InternalCostEstimate);
    }

    [Fact]
    public async Task Estimates_a_rounded_range_from_persisted_prices_and_retries()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.ProviderCapabilityPricings.Add(Route(
            baseMin: 0.123456789m,
            baseMax: 0.234567891m,
            retryLimit: 4));
        await db.SaveChangesAsync();

        var estimator = new MovieGenerationCostEstimator(db, Options.Create(new MovieGenerationCostEstimatorOptions()));
        var result = await estimator.EstimateAsync(Request(duration: 3, retries: 1), "test-provider");

        Assert.Equal(MovieGenerationCostEstimateStates.Estimated, result.State);
        Assert.Equal(0.74074074m, result.MinimumAmountUsd);
        Assert.Equal(1.40740734m, result.MaximumAmountUsd);
        Assert.Equal(6, result.Components.Single(item => item.Dimension == "generation").Quantity);
        Assert.DoesNotContain("test-provider", result.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-v1", result.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-fixture", result.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_base_price_is_unknown_instead_of_zero()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.ProviderCapabilityPricings.Add(Route());
        await db.SaveChangesAsync();

        var estimator = new MovieGenerationCostEstimator(db, Options.Create(new MovieGenerationCostEstimatorOptions()));
        var result = await estimator.EstimateAsync(Request(), "test-provider");

        Assert.Equal(MovieGenerationCostEstimateStates.Unknown, result.State);
        Assert.Equal(MovieGenerationCostEstimateReasons.PricingMissing, result.Reason);
        Assert.Null(result.MaximumAmountUsd);
    }

    [Fact]
    public async Task Retry_and_upscale_caps_fail_closed_before_large_cost_math()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.ProviderCapabilityPricings.Add(Route(
            baseMin: 0.01m,
            baseMax: 0.02m,
            upscaleMin: 0.01m,
            upscaleMax: 0.02m,
            retryLimit: 99,
            upscaleLimit: 99,
            supportsUpscale: true));
        await db.SaveChangesAsync();

        var estimator = new MovieGenerationCostEstimator(db, Options.Create(new MovieGenerationCostEstimatorOptions
        {
            MaxRetryAttempts = 2,
            MaxUpscalePasses = 2,
        }));
        var retryResult = await estimator.EstimateAsync(Request(retries: 3), "test-provider");
        var upscaleResult = await estimator.EstimateAsync(Request(path: "upscale", upscale: true, upscalePasses: 3), "test-provider");

        Assert.Equal("retry_attempt_cap_exceeded", retryResult.Reason);
        Assert.Equal("upscale_pass_cap_exceeded", upscaleResult.Reason);
        Assert.Null(upscaleResult.MaximumAmountUsd);
    }

    [Fact]
    public async Task No_matching_persisted_capability_is_unevaluated()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.ProviderCapabilityPricings.Add(Route(baseMin: 0.01m, baseMax: 0.02m));
        await db.SaveChangesAsync();

        var estimator = new MovieGenerationCostEstimator(db, Options.Create(new MovieGenerationCostEstimatorOptions()));
        var result = await estimator.EstimateAsync(Request(source: "2160p"), "test-provider");

        Assert.Equal(MovieGenerationCostEstimateStates.Unevaluated, result.State);
        Assert.Equal(MovieGenerationCostEstimateReasons.RouteUnavailable, result.Reason);
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static MovieGenerationCostRequest Request(
        int duration = 5,
        string source = "1080p",
        string path = "native",
        int retries = 0,
        bool upscale = false,
        int upscalePasses = 0) =>
        new(duration, source, "1080p", "Standard", path, retries, upscale, upscalePasses);

    private static ProviderCapabilityPricing Route(
        decimal? baseMin = null,
        decimal? baseMax = null,
        decimal? upscaleMin = null,
        decimal? upscaleMax = null,
        int retryLimit = 8,
        int upscaleLimit = 3,
        bool supportsUpscale = false) => new()
    {
        Id = Guid.NewGuid(),
        CapabilityKey = "movie.video",
        ProviderKey = "test-provider",
        ModelKey = "test-model",
        SourceResolution = "1080p",
        TargetResolution = "1080p",
        QualityTier = "Standard",
        ProcessingPath = upscaleMin.HasValue ? "upscale" : "native",
        SupportsUpscaling = supportsUpscale,
        MaxDurationSeconds = 3600,
        MaxRetryAttempts = retryLimit,
        MaxUpscalePasses = upscaleLimit,
        BasePriceUsdPerSecondMin = baseMin,
        BasePriceUsdPerSecondMax = baseMax,
        UpscalePriceUsdPerSecondMin = upscaleMin,
        UpscalePriceUsdPerSecondMax = upscaleMax,
        Currency = "USD",
        PricingVersion = "test-v1",
        EffectiveAtUtc = DateTime.UtcNow,
        Source = "test-fixture",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
