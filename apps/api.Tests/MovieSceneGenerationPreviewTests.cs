using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSceneGenerationPreviewTests
{
    [Fact]
    public async Task Supported_profile_returns_exact_price_quote_without_clip_job_reservation_or_provider_call()
    {
        using var factory = new MovieClipDurationApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Verified scene quote", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var now = DateTime.UtcNow;
        db.ProviderCapabilityPricings.Add(new ProviderCapabilityPricing
        {
            Id = Guid.NewGuid(),
            CapabilityKey = "movie.video",
            ProviderKey = "fake-movie",
            ModelKey = "fake-model",
            SourceResolution = "720p",
            TargetResolution = "720p",
            QualityTier = "Standard",
            ProcessingPath = "native",
            SupportsUpscaling = false,
            MaxDurationSeconds = 3600,
            MaxRetryAttempts = 8,
            MaxUpscalePasses = 3,
            BasePriceUsdPerSecondMin = 0.01m,
            BasePriceUsdPerSecondMax = 0.02m,
            Currency = "USD",
            PricingVersion = "test-v1",
            EffectiveAtUtc = now,
            Source = "test-fixture",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generation-preview",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<MovieSceneGenerationPreviewDto>();
        Assert.NotNull(preview);
        Assert.Equal(5, preview!.DurationSeconds);
        Assert.Equal("720p", preview.SourceResolution);
        Assert.Equal("720p", preview.TargetResolution);
        Assert.Equal("Standard", preview.QualityTier);
        Assert.Equal("native", preview.ProcessingPath);
        Assert.True(preview.ExecutionProfileVerified);
        Assert.True(preview.CanConfirmCost);
        Assert.NotNull(preview.CostGuardrails);
        Assert.True(preview.CostGuardrails!.EstimatedProviderCostKnown);
        Assert.Equal(0.10m, preview.CostGuardrails.EstimatedProviderCostUsd);
        Assert.Equal("USD", preview.CostGuardrails.Currency);

        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Unsupported_profile_is_rejected_before_cost_quote_or_any_generation_side_effect()
    {
        using var factory = new MovieClipIntentDeniedApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Unsupported scene quote", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generation-preview",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<MovieSceneGenerationPreviewDto>();
        Assert.NotNull(preview);
        Assert.False(preview!.ExecutionProfileVerified);
        Assert.False(preview.CanConfirmCost);
        Assert.Equal("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED", preview.RejectionCode);
        Assert.Null(preview.CostGuardrails);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }
}
