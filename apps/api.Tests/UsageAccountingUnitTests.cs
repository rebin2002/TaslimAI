using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class UsageAccountingUnitTests
{
    [Fact]
    public void Chat_cost_calculation_returns_a_versioned_rate_snapshot()
    {
        var configuration = new ConfigurationBuilder().Build();
        var catalog = new AiModelCatalog(configuration);
        var calculator = new AiCostCalculator(catalog);
        var usage = new AiUsageMetadata("openai", "gpt-5.6-terra", 1_000, 100, 2_000, null, null, 12, "completed", false);

        var snapshot = calculator.GetPricingSnapshot(usage);

        Assert.NotNull(snapshot);
        Assert.Equal("gpt-5.6-terra", snapshot.Model);
        Assert.Equal("chat-openai-2026-09-22", snapshot.Version);
        Assert.Equal(2.00m, snapshot.Rates["input"]);
        Assert.Equal(0.20m, snapshot.Rates["cachedInput"]);
        Assert.Equal(12.00m, snapshot.Rates["output"]);
        Assert.True(calculator.Calculate(usage) > 0m);
    }

    [Fact]
    public async Task Guardrail_is_disabled_by_default_and_does_not_reject_preflight()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var control = new UsageCostControl(db, Options.Create(new UsageControlOptions { GuardrailsEnabled = false, MaxEstimatedProviderCostPerGenerationUsd = 0.01m }), NullLogger<UsageCostControl>.Instance);

        var result = await control.CheckPreflightAsync(Guid.NewGuid(), UsageFeature.Image, 10m);

        Assert.True(result.Allowed);
        Assert.Equal(10m, result.EstimatedProviderCostUsd);
    }

    [Fact]
    public async Task Enabled_guardrail_rejects_estimates_over_single_operation_limit()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var control = new UsageCostControl(db, Options.Create(new UsageControlOptions { GuardrailsEnabled = true, MaxEstimatedProviderCostPerGenerationUsd = 0.01m }), NullLogger<UsageCostControl>.Instance);

        var result = await control.CheckPreflightAsync(Guid.NewGuid(), UsageFeature.Image, 0.02m);

        Assert.False(result.Allowed);
        Assert.Equal("COST_ESTIMATE_EXCEEDS_LIMIT", result.RejectionCode);
    }
}
