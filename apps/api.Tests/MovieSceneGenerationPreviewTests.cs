using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Usage;
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
        AddPricingRow(db, now, "fake-model", 0.01m, 0.02m);
        AddPricingRow(db, now, "decoy-expensive-model", 0.50m, 0.60m);
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
        Assert.Equal("fake-movie", preview.ProviderKey);
        Assert.Equal("fake-model", preview.ProviderModelKey);
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

    [Fact]
    public async Task Changed_price_after_preview_rejects_confirmation_before_clip_job_or_usage_reservation()
    {
        using var factory = new MovieClipExactProfileApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Stale provider price", sceneDurationSeconds: 5);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var now = DateTime.UtcNow;
        AddPricingRow(db, now, "fake-model", 0.01m, 0.02m);
        AddPricingRow(db, now, "decoy-expensive-model", 0.50m, 0.60m);
        await db.SaveChangesAsync();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var previewResponse = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generation-preview",
            new { sourceResolution = "720p", targetResolution = "720p", qualityTier = "Standard", processingPath = "native" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<MovieSceneGenerationPreviewDto>();
        Assert.NotNull(preview);
        Assert.True(preview!.CanConfirmCost);
        Assert.Equal(0.10m, preview.CostGuardrails!.EstimatedProviderCostUsd);

        var exactPriceRow = await db.ProviderCapabilityPricings.SingleAsync(item => item.ModelKey == "fake-model");
        exactPriceRow.BasePriceUsdPerSecondMax = 0.03m;
        exactPriceRow.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var estimator = scope.ServiceProvider.GetRequiredService<Taslim.Api.Usage.IMovieGenerationCostEstimator>();
        var changedEstimate = await estimator.EstimateAsync(
            new MovieGenerationCostRequest(5, "720p", "720p", "Standard", "native"),
            "fake-movie",
            selectedModelKey: "fake-model",
            cancellationToken: CancellationToken.None);
        Assert.Equal(0.15m, changedEstimate.MaximumAmountUsd);
        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = true,
                expectedProviderKey = preview.ProviderKey,
                expectedProviderModelKey = preview.ProviderModelKey,
                expectedProviderCostUsd = preview.CostGuardrails!.EstimatedProviderCostUsd,
            },
            idempotencyKey: "changed-movie-price-after-preview");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_STALE", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Confirmed_scene_queue_uses_the_exact_quoted_model_price_and_replays_without_another_reservation()
    {
        using var factory = new MovieClipExactProfileApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Exact model quote and queue", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var now = DateTime.UtcNow;
        AddPricingRow(db, now, "fake-model", 0.01m, 0.02m);
        AddPricingRow(db, now, "decoy-expensive-model", 0.50m, 0.60m);
        await db.SaveChangesAsync();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var previewResponse = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generation-preview",
            new { sourceResolution = "720p", targetResolution = "720p", qualityTier = "Standard", processingPath = "native" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<MovieSceneGenerationPreviewDto>();
        Assert.NotNull(preview);
        Assert.True(preview!.CanConfirmCost);
        Assert.Equal("fake-movie", preview.ProviderKey);
        Assert.Equal("fake-model", preview.ProviderModelKey);
        Assert.Equal(0.10m, preview.CostGuardrails!.EstimatedProviderCostUsd);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());

        const string idempotencyKey = "exact-quoted-model-scene-queue";
        var request = new
        {
            sourceResolution = "720p",
            targetResolution = "720p",
            qualityTier = "Standard",
            processingPath = "native",
            confirmationAccepted = true,
            expectedProviderKey = preview.ProviderKey,
            expectedProviderModelKey = preview.ProviderModelKey,
            expectedProviderCostUsd = preview.CostGuardrails!.EstimatedProviderCostUsd,
        };
        using var queueResponse = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            request,
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, queueResponse.StatusCode);
        var queued = await queueResponse.Content.ReadFromJsonAsync<MovieStudioGenerationResponse>();
        Assert.NotNull(queued);
        var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == queued!.Job.Id);
        var input = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)!;
        Assert.Equal("fake-movie", input.SelectedProviderKey);
        Assert.Equal("fake-model", input.SelectedProviderModelKey);
        Assert.Equal(0.10m, job.EstimatedProviderCostUsd);
        var usageAfterQueue = await db.UsageTransactions.CountAsync();

        using var replayResponse = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = true,
            },
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<MovieStudioGenerationResponse>();
        Assert.NotNull(replay);
        Assert.Equal(queued!.Job.Id, replay!.Job.Id);
        Assert.Equal(queued.ClipId, replay.ClipId);
        Assert.Equal(jobsBefore + 1, await db.GenerationJobs.CountAsync());
        Assert.Equal(clipsBefore + 1, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(usageAfterQueue, await db.UsageTransactions.CountAsync());

        using var mismatchedReplay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = true,
                expectedProviderKey = preview.ProviderKey,
                expectedProviderModelKey = "different-model",
            },
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedReplay.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", await mismatchedReplay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(jobsBefore + 1, await db.GenerationJobs.CountAsync());
        Assert.Equal(clipsBefore + 1, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(usageAfterQueue, await db.UsageTransactions.CountAsync());

        using var changedPriceReplay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = true,
                expectedProviderKey = preview.ProviderKey,
                expectedProviderModelKey = preview.ProviderModelKey,
                expectedProviderCostUsd = 0.11m,
            },
            idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedPriceReplay.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", await changedPriceReplay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(jobsBefore + 1, await db.GenerationJobs.CountAsync());
        Assert.Equal(clipsBefore + 1, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(usageAfterQueue, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Theory]
    [InlineData("fake-movie", "stale-model")]
    [InlineData("stale-provider", "fake-model")]
    public async Task Stale_quote_adapter_identity_rejects_before_clip_job_or_usage_reservation(
        string expectedProviderKey, string expectedModelKey)
    {
        using var factory = new MovieClipExactProfileApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Stale quote identity", sceneDurationSeconds: 5);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = true,
                expectedProviderKey,
                expectedProviderModelKey = expectedModelKey,
            },
            idempotencyKey: "stale-scene-quote-identity");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_STALE", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Nondefault_profile_and_undeclared_model_fail_closed_before_cost_or_generation_side_effects()
    {
        using (var factory = new MovieClipExactProfileApiFactory())
        using (var client = factory.CreateClient())
        {
            var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
                client, title: "Unsupported nondefault profile", sceneDurationSeconds: 5);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
            var jobsBefore = await db.GenerationJobs.CountAsync();
            var usageBefore = await db.UsageTransactions.CountAsync();
            using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
                client,
                HttpMethod.Post,
                $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
                new { sourceResolution = "720p", targetResolution = "1080p", qualityTier = "Standard", processingPath = "native", confirmationAccepted = true },
                idempotencyKey: "unsupported-nondefault-movie-profile");
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
            Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
            Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
            Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
        }

        using (var factory = new MovieClipExactProfileApiFactory(pricingModelKey: null))
        using (var client = factory.CreateClient())
        {
            var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
                client, title: "Unpriced provider model", sceneDurationSeconds: 5);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var usageBefore = await db.UsageTransactions.CountAsync();
            using var previewResponse = await MovieOperationalFixtures.SendWithCsrfAsync(
                client,
                HttpMethod.Post,
                $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generation-preview",
                new { sourceResolution = "720p", targetResolution = "720p", qualityTier = "Standard", processingPath = "native" });
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            var preview = await previewResponse.Content.ReadFromJsonAsync<MovieSceneGenerationPreviewDto>();
            Assert.NotNull(preview);
            Assert.False(preview!.ExecutionProfileVerified);
            Assert.False(preview.CanConfirmCost);
            Assert.Equal("MOVIE_PROVIDER_MODEL_UNVERIFIED", preview.RejectionCode);
            Assert.Null(preview.ProviderModelKey);
            Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
            Assert.False(await db.GenerationJobs.AnyAsync());
            Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
            Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
        }
    }

    [Fact]
    public async Task Fresh_scene_and_shot_queue_require_complete_quote_and_explicit_confirmation()
    {
        using var factory = new MovieClipExactProfileApiFactory();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Fresh legacy quote required", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        AddPricingRow(db, DateTime.UtcNow, "fake-model", 0.01m, 0.02m);
        await db.SaveChangesAsync();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync();

        using var missingTuple = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new { sourceResolution = "720p", targetResolution = "720p", qualityTier = "Standard", processingPath = "native", confirmationAccepted = true },
            idempotencyKey: "missing-scene-quote-tuple");
        Assert.Equal(HttpStatusCode.BadRequest, missingTuple.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_REQUIRED", await missingTuple.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var missingMaximum = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = true,
                expectedProviderKey = "fake-movie",
                expectedProviderModelKey = "fake-model",
            },
            idempotencyKey: "missing-shot-quote-maximum");
        Assert.Equal(HttpStatusCode.BadRequest, missingMaximum.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_REQUIRED", await missingMaximum.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var unconfirmed = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new
            {
                sourceResolution = "720p",
                targetResolution = "720p",
                qualityTier = "Standard",
                processingPath = "native",
                confirmationAccepted = false,
                expectedProviderKey = "fake-movie",
                expectedProviderModelKey = "fake-model",
                expectedProviderCostUsd = 0.10m,
            },
            idempotencyKey: "unconfirmed-shot-quote");
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Contains("MOVIE_SCENE_QUOTE_CONFIRMATION_REQUIRED", await unconfirmed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(snapshotsBefore, await db.MovieCharacterContinuitySnapshots.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    private static void AddPricingRow(
        TaslimDbContext db, DateTime now, string modelKey, decimal minimumPrice, decimal maximumPrice)
    {
        db.ProviderCapabilityPricings.Add(new ProviderCapabilityPricing
        {
            Id = Guid.NewGuid(),
            CapabilityKey = "movie.video",
            ProviderKey = "fake-movie",
            ModelKey = modelKey,
            SourceResolution = "720p",
            TargetResolution = "720p",
            QualityTier = "Standard",
            ProcessingPath = "native",
            SupportsUpscaling = false,
            MaxDurationSeconds = 3600,
            MaxRetryAttempts = 8,
            MaxUpscalePasses = 3,
            BasePriceUsdPerSecondMin = minimumPrice,
            BasePriceUsdPerSecondMax = maximumPrice,
            Currency = "USD",
            PricingVersion = "test-v1",
            EffectiveAtUtc = now,
            Source = "test-fixture",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }
}
