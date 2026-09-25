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
        var configuration = KnownChatPricing();
        var catalog = new AiModelCatalog(configuration);
        var calculator = new AiCostCalculator(catalog);
        var usage = new AiUsageMetadata("openai", "gpt-5.6-terra", 1_000, 100, 2_000, null, null, 12, "completed", false);

        var snapshot = calculator.GetPricingSnapshot(usage);

        Assert.NotNull(snapshot);
        Assert.Equal("gpt-5.6-terra", snapshot.Model);
        Assert.Equal("chat-openai-test-v1", snapshot.Version);
        Assert.Equal(2.00m, snapshot.Rates["input"]);
        Assert.Equal(0.20m, snapshot.Rates["cachedInput"]);
        Assert.Equal(12.00m, snapshot.Rates["output"]);
        Assert.True(calculator.Calculate(usage) > 0m);
    }

    [Fact]
    public void Unknown_chat_pricing_does_not_become_zero()
    {
        var calculator = new AiCostCalculator(new AiModelCatalog(new ConfigurationBuilder().Build()));

        var cost = calculator.Calculate(new AiUsageMetadata("unconfigured", "unknown", 100, null, 100, null, null, 0, "completed", false));

        Assert.Null(cost);
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

    [Fact]
    public void Typed_estimator_supports_token_media_and_fixed_cost_dimensions()
    {
        var pricing = Options.Create(new GenerationCostPricingOptions
        {
            Version = "test-v1",
            EffectiveAtUtc = DateTime.UtcNow,
            Source = "test-fixture",
            Providers = new Dictionary<string, GenerationProviderPricingOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider:model"] = new()
                {
                    TextInputUsdPerMillionTokens = 2m,
                    VoiceUsdPerSecond = 0.01m,
                    MusicUsdPerSecond = 0.02m,
                    VideoUsdPerSecond = 0.03m,
                    FixedUsd = 0.50m,
                },
            },
        });
        var estimator = new GenerationCostEstimator(pricing);

        var estimate = estimator.Estimate(new GenerationCostEstimationRequest("provider", "model", InputTokens: 1_000, VoiceDurationSeconds: 10, MusicDurationSeconds: 5, VideoDurationSeconds: 2));

        Assert.True(estimate.IsKnown);
        Assert.Equal(0.762m, estimate.AmountUsd);
        Assert.Equal("test-v1", estimate.PricingVersion);
    }

    [Fact]
    public void Typed_estimator_keeps_unknown_dimension_unknown()
    {
        var estimator = new GenerationCostEstimator(Options.Create(new GenerationCostPricingOptions
        {
            Providers = new Dictionary<string, GenerationProviderPricingOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider"] = new() { VoiceUsdPerSecond = null },
            },
        }));

        var estimate = estimator.Estimate(new GenerationCostEstimationRequest("provider", VoiceDurationSeconds: 30));

        Assert.False(estimate.IsKnown);
        Assert.Null(estimate.AmountUsd);
        Assert.Equal("rate_missing_for_usage_dimension", estimate.UnknownReason);
    }

    [Theory]
    [InlineData("0.50", "0.40", true, null)]
    [InlineData("0.50", "0.60", false, "MAX_ESTIMATED_COST_PER_JOB_EXCEEDED")]
    [InlineData("10.00", "0.60", false, "MAX_CUMULATIVE_ESTIMATED_COST_EXCEEDED")]
    public void Generation_budget_guardrail_enforces_job_and_retry_ceilings(string cumulativeText, string amountText, bool allowed, string? code)
    {
        var cumulative = decimal.Parse(cumulativeText, System.Globalization.CultureInfo.InvariantCulture);
        var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var decision = GenerationBudgetGuardrail.Evaluate(
            new GenerationCostEstimate(true, amount, "USD", "v1", DateTime.UtcNow, "test", []),
            new GenerationBudgetSnapshot(ProviderAttempts: 1, CumulativeEstimatedCostUsd: cumulative, WorkspaceEstimatedCostUsd: 0m, UserEstimatedCostUsd: 0m),
            new GenerationBudgetOptions { Enabled = true, MaxEstimatedCostPerJobUsd = cumulative >= 10m ? 1.00m : 0.50m, MaxCumulativeEstimatedCostPerJobUsd = 10.50m, MaxProviderAttemptsPerJob = 3 });

        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(code, decision.RejectionCode);
    }

    [Fact]
    public void Generation_budget_guardrail_rejects_unknown_when_fail_closed()
    {
        var decision = GenerationBudgetGuardrail.Evaluate(
            GenerationCostEstimate.Unknown("missing_rate"),
            new GenerationBudgetSnapshot(0, 0m, 0m, 0m),
            new GenerationBudgetOptions { Enabled = true, RejectUnknownEstimates = true });

        Assert.False(decision.Allowed);
        Assert.Equal("COST_ESTIMATE_UNKNOWN", decision.RejectionCode);
    }

    [Fact]
    public async Task Duplicate_completion_is_idempotent_and_does_not_charge_customer()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var ledger = new UsageLedgerService(
            db,
            new AiCostCalculator(new AiModelCatalog(KnownChatPricing())),
            new SafeUsageChargingService(),
            new UsageCostControl(db, Options.Create(new UsageControlOptions()), NullLogger<UsageCostControl>.Instance),
            NullLogger<UsageLedgerService>.Instance);
        var transaction = new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), UserId = Guid.NewGuid(), RequestId = "duplicate-completion",
            Feature = UsageFeature.Generation, Provider = "pending", Model = "pending", CreatedAt = DateTime.UtcNow,
        };
        db.UsageTransactions.Add(transaction);
        await db.SaveChangesAsync();
        var usage = new AiUsageMetadata("provider", "model", 10, null, 20, 0.01m, 0.02m, 1, "completed", false);

        await ledger.CompleteAsync(transaction, usage);
        var completedAt = transaction.CompletedAt;
        await ledger.CompleteAsync(transaction, usage);

        Assert.Equal(UsageTransactionStatus.Completed, transaction.Status);
        Assert.Equal(completedAt, transaction.CompletedAt);
        Assert.True(transaction.ProviderCostKnown);
        Assert.Equal(0m, transaction.ChargedAmount);
        Assert.Empty(db.CreditLedgerEntries);
    }

    [Fact]
    public async Task Cancelled_generation_has_no_customer_charge_or_credit_deduction()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var ledger = new UsageLedgerService(
            db,
            new AiCostCalculator(new AiModelCatalog(new ConfigurationBuilder().Build())),
            new SafeUsageChargingService(),
            new UsageCostControl(db, Options.Create(new UsageControlOptions()), NullLogger<UsageCostControl>.Instance),
            NullLogger<UsageLedgerService>.Instance);
        var transaction = new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), UserId = Guid.NewGuid(), RequestId = "cancelled-generation",
            Feature = UsageFeature.Generation, Provider = "pending", Model = "pending", CreatedAt = DateTime.UtcNow,
        };
        db.UsageTransactions.Add(transaction);
        await db.SaveChangesAsync();

        await ledger.CancelAsync(transaction, "GENERATION_CANCELLED");
        await ledger.CompleteAsync(transaction, new AiUsageMetadata("provider", "model", 1, null, 1, 0.01m, 0.02m, 1, "completed", false));

        Assert.Equal(UsageTransactionStatus.Cancelled, transaction.Status);
        Assert.Equal(0m, transaction.ChargedAmount);
        Assert.False(transaction.ProviderCostKnown);
        Assert.Empty(db.CreditLedgerEntries);
    }

    [Fact]
    public void Generation_budget_guardrail_enforces_fallback_attempt_and_workspace_user_ceilings()
    {
        var attemptDecision = GenerationBudgetGuardrail.Evaluate(
            new GenerationCostEstimate(true, 0.01m, "USD", "v1", null, "test", []),
            new GenerationBudgetSnapshot(3, 0m, 0m, 0m),
            new GenerationBudgetOptions { Enabled = true, MaxProviderAttemptsPerJob = 3 });
        var workspaceDecision = GenerationBudgetGuardrail.Evaluate(
            new GenerationCostEstimate(true, 0.01m, "USD", "v1", null, "test", []),
            new GenerationBudgetSnapshot(0, 0m, 1m, 0m),
            new GenerationBudgetOptions { Enabled = true, WorkspaceInternalSafetyCeilingUsd = 1m });
        var userDecision = GenerationBudgetGuardrail.Evaluate(
            new GenerationCostEstimate(true, 0.01m, "USD", "v1", null, "test", []),
            new GenerationBudgetSnapshot(0, 0m, 0m, 1m),
            new GenerationBudgetOptions { Enabled = true, UserInternalSafetyCeilingUsd = 1m });

        Assert.Equal("MAX_PROVIDER_ATTEMPTS_EXCEEDED", attemptDecision.RejectionCode);
        Assert.Equal("WORKSPACE_INTERNAL_SAFETY_CEILING_EXCEEDED", workspaceDecision.RejectionCode);
        Assert.Equal("USER_INTERNAL_SAFETY_CEILING_EXCEEDED", userDecision.RejectionCode);
    }

    private static IConfiguration KnownChatPricing() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Ai:Models:gpt-5.6-terra:ProviderKey"] = "openai",
        ["Ai:Models:gpt-5.6-terra:InputPricePerMillion"] = "2",
        ["Ai:Models:gpt-5.6-terra:CachedInputPricePerMillion"] = "0.2",
        ["Ai:Models:gpt-5.6-terra:OutputPricePerMillion"] = "12",
        ["Ai:Models:gpt-5.6-terra:PricingVersion"] = "chat-openai-test-v1",
        ["Ai:Models:gpt-5.6-terra:PricingEffectiveDateUtc"] = "2026-01-01T00:00:00Z",
        ["Ai:Models:gpt-5.6-terra:PricingSource"] = "test-fixture",
    }).Build();
}
