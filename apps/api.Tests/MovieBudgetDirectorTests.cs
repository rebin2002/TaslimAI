using System.Text.Json;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieBudgetDirectorTests
{
    [Fact]
    public async Task Compares_naive_high_cost_path_with_draft_select_master_path()
    {
        var service = CreateService();
        var result = await service.EstimateAsync(new MovieBudgetDirectorRequest
        {
            DurationSeconds = 5,
            ShotCount = 4,
            CandidatePassesPerShot = 3,
            ApprovedReferenceReuseCount = 1,
            SalvagedSelectCount = 1,
            MissingInsertCount = 2,
            MissingInsertDurationSeconds = 2,
            SelectedTakeCount = 1,
            SelectedOnlyMastering = true,
        });

        Assert.Equal(MovieBudgetDirectorStates.Estimated, result.State);
        Assert.Equal(600m, result.NaivePath.MaximumAmountUsd);
        Assert.Equal(84m, result.OptimizedPath.MaximumAmountUsd);
        Assert.Equal(516m, result.Savings.MaximumAmountUsd);
        Assert.Equal(86m, result.Savings.MaximumPercent);
        Assert.True(result.Savings.IsEstimate);
        Assert.False(result.Savings.ActualSavingsAvailable);
        Assert.Null(result.Savings.ActualSavingsUsd);
        Assert.Contains(result.Plan, item => item.Key == "approved_reference_reuse");
        Assert.Contains(result.Plan, item => item.Key == "salvaged_selects");
        Assert.Contains(result.Plan, item => item.Key == "targeted_inserts");
        Assert.Contains(result.Plan, item => item.Key == "selected_only_mastering");
    }

    [Fact]
    public async Task Does_not_turn_missing_capability_pricing_into_zero_savings()
    {
        var service = CreateService(unknownPath: "native");
        var result = await service.EstimateAsync(new MovieBudgetDirectorRequest
        {
            DurationSeconds = 5,
            ShotCount = 2,
            CandidatePassesPerShot = 2,
            SelectedTakeCount = 1,
        });

        Assert.Equal(MovieBudgetDirectorStates.Unknown, result.State);
        Assert.Equal(MovieBudgetDirectorStates.Unknown, result.Savings.State);
        Assert.Null(result.Savings.MaximumAmountUsd);
        Assert.False(result.Savings.ActualSavingsAvailable);
        Assert.Contains("complete pricing", result.Savings.Basis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Response_contains_no_provider_or_model_identifiers_and_labels_estimates()
    {
        var service = CreateService();
        var result = await service.EstimateAsync(new MovieBudgetDirectorRequest
        {
            DurationSeconds = 5,
            ShotCount = 1,
            CandidatePassesPerShot = 1,
            SelectedTakeCount = 1,
        });
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("Estimated avoided provider cost range; not actual savings", json);
        Assert.DoesNotContain("test-provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_invalid_planning_counts_before_cost_estimation()
    {
        var service = CreateService();
        var request = new MovieBudgetDirectorRequest { DurationSeconds = 5, ShotCount = 2, ApprovedReferenceReuseCount = 3 };

        var exception = await Assert.ThrowsAsync<MovieBudgetDirectorValidationException>(() => service.EstimateAsync(request));

        Assert.Equal(MovieBudgetDirectorReasons.RequestInvalid, exception.Code);
    }

    private static MovieBudgetDirectorService CreateService(string? unknownPath = null) =>
        new(null!, new FakeEstimator(unknownPath), null!);

    private sealed class FakeEstimator(string? unknownPath) : IMovieGenerationCostEstimator
    {
        public Task<MovieGenerationCostEstimate> EstimateAsync(
            MovieGenerationCostRequest request,
            string? selectedProviderKey = null,
            string? selectedModelKey = null,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(request.ProcessingPath, unknownPath, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(MovieGenerationCostEstimate.Unknown(MovieGenerationCostEstimateReasons.PricingMissing));

            var rate = string.Equals(request.ProcessingPath, "native", StringComparison.OrdinalIgnoreCase) ? 1m : 10m;
            var amount = rate * request.DurationSeconds;
            return Task.FromResult(new MovieGenerationCostEstimate(
                MovieGenerationCostEstimateStates.Estimated,
                amount,
                amount,
                "USD",
                "server-only-pricing",
                DateTime.UtcNow,
                "test-fixture",
                [new MovieGenerationCostComponent("generation", request.DurationSeconds, "seconds_per_attempt", amount, amount)]));
        }
    }
}
