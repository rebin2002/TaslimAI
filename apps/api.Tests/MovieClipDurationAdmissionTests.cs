using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

// Use the real HTTP admission, pricing, persistence and reservation path with
// workers disabled and an explicitly range-limited in-memory video provider.
public sealed class MovieClipDurationApiFactory : MovieOperationalApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>())
            {
                SceneClipDurationRange = new MovieClipDurationRange(2, 10),
            });
        });
    }
}

// A separate test host makes intent denial deterministic without mutating
// the shared duration test fixture or reaching an external video provider.
public sealed class MovieClipIntentDeniedApiFactory : MovieOperationalApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>())
            {
                SceneClipCostIntentSupported = false,
            });
        });
    }
}

// A deliberately small fake prompt budget verifies rejection at HTTP admission,
// without submitting a billable task or depending on external providers.
public sealed class MovieClipPromptLimitedApiFactory : MovieOperationalApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>())
            {
                SceneClipPromptMaxCharacters = 30,
            });
        });
    }
}

// Provider-free fixture whose declared contract mirrors only the Movie beta
// profile under test; non-default output intent must fail before reservation.
public sealed class MovieClipExactProfileApiFactory : MovieOperationalApiFactory
{
    private readonly string? pricingModelKey;

    public MovieClipExactProfileApiFactory(string? pricingModelKey = "fake-model")
    {
        this.pricingModelKey = pricingModelKey;
    }

    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>())
            {
                SceneClipDurationRange = new MovieClipDurationRange(2, 10),
                SceneClipPricingModelKey = pricingModelKey,
                RequiredSceneClipSourceResolution = "720p",
                RequiredSceneClipTargetResolution = "720p",
                RequiredSceneClipQualityTier = "Standard",
                RequiredSceneClipProcessingPath = "native",
                RequiredSceneClipAspectRatio = "16:9",
            });
        });
    }
}

// Deliberately advertise a much wider fake range than real adapters. Without
// an explicit raw-duration guard, the legacy queue path silently clamps bad
// persisted shot/scene values to 1 or 3,600 and could queue altered work.
public sealed class MovieClipNoClampApiFactory : MovieOperationalApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>())
            {
                SceneClipDurationRange = new MovieClipDurationRange(1, 3600),
            });
        });
    }
}

public sealed class MovieClipUnclampedDurationTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 3601)]
    [InlineData(false, 7200)]
    [InlineData(true, 0)]
    [InlineData(true, 7200)]
    public async Task Invalid_stored_shot_or_scene_timing_never_queues_a_truncated_clip(
        bool generateScene, int invalidSeconds)
    {
        using var factory = new MovieClipNoClampApiFactory();
        using (var initialization = factory.Services.CreateScope())
            initialization.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Do not truncate long or invalid clips", sceneDurationSeconds: 5);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
        movie.DurationSeconds = 7200; // Long project plan is independently valid.
        if (generateScene)
        {
            var scene = await db.MovieScenes.SingleAsync(item => item.Id == fixture.Scene.Id);
            scene.DurationSeconds = invalidSeconds;
        }
        else
        {
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            shot.DurationSeconds = invalidSeconds;
        }
        await db.SaveChangesAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var versionsBefore = await db.MovieProductionVersions.CountAsync();
        var key = $"no-truncation-{(generateScene ? "scene" : "shot")}-{invalidSeconds}";
        var path = generateScene
            ? $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate"
            : $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path,
            new { title = "Refuse silently shortened output", confirmationAccepted = true },
            idempotencyKey: key);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        // A zero-length shot is stopped by MovieShotReadiness before reaching
        // provider capability admission; both validation paths must fail closed.
        // Do not weaken readiness just to return a different error code.
        var expectedCode = !generateScene && invalidSeconds == 0
            ? "MOVIE_SHOT_NOT_READY" : "MOVIE_CLIP_DURATION_UNSUPPORTED";
        Assert.Contains(expectedCode,
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == movie.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == key));
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }
}

public sealed class MovieClipDurationAdmissionTests : IClassFixture<MovieClipDurationApiFactory>
{
    private readonly MovieClipDurationApiFactory factory;

    public MovieClipDurationAdmissionTests(MovieClipDurationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task Supported_boundary_duration_queues_one_clip_without_invoking_provider(int duration)
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: $"Movie supported duration {duration}", sceneDurationSeconds: duration);
        var response = await MovieOperationalFixtures.PostWithConfirmedMovieQuoteAsync<MovieStudioGenerationResponse>(
            factory, client, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Supported clip" },
            idempotencyKey: $"movie-duration-supported-{duration}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(duration, await db.MovieClips.Where(item => item.Id == response.ClipId)
            .Select(item => item.DurationSeconds).SingleAsync());
        var queued = await db.GenerationJobs.SingleAsync(item => item.Id == response.Job.Id);
        var queuedInput = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(queued.InputJson)!;
        Assert.Equal(scope.ServiceProvider.GetRequiredService<IMovieVideoProvider>().Key,
            queuedInput.SelectedProviderKey);
        Assert.Equal(scope.ServiceProvider.GetRequiredService<IMovieVideoProvider>().SceneClipPricingModelKey,
            queuedInput.SelectedProviderModelKey);
    }

    [Fact]
    public async Task Explicit_supported_720p_profile_is_preserved_in_job_and_idempotent_replay()
    {
        // A supported explicit output request must reach the stored job intact,
        // rather than silently being replaced with the legacy 1080p default.
        // This exercises real HTTP admission and persistence with a fake
        // range-limited adapter and a disabled worker: no paid provider calls.
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Explicit priced 720p clip", sceneDurationSeconds: 5);
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        const string key = "movie-explicit-720p-profile-1";
        var requested = new
        {
            title = "Verified 720p native clip",
            sourceResolution = "720p",
            targetResolution = "720p",
            qualityTier = "Standard",
            processingPath = "native",
            confirmationAccepted = true,
        };

        var first = await MovieOperationalFixtures.PostWithConfirmedMovieQuoteAsync<MovieStudioGenerationResponse>(
            factory, client, path, requested, idempotencyKey: key);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == first.Job.Id);
        var input = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)!;
        Assert.Equal("720p", input.SourceResolution);
        Assert.Equal("720p", input.TargetResolution);
        Assert.Equal("Standard", input.QualityTier);
        Assert.Equal("native", input.ProcessingPath);
        Assert.Equal(5, input.DurationSeconds);
        Assert.Equal(GenerationJobStatus.Queued, job.Status);
        var usageAfterFirst = await db.UsageTransactions.CountAsync();

        var replay = await MovieOperationalFixtures.PostWithConfirmedMovieQuoteAsync<MovieStudioGenerationResponse>(
            factory, client, path, requested, idempotencyKey: key);
        Assert.Equal(first.Job.Id, replay.Job.Id);
        Assert.Equal(first.ClipId, replay.ClipId);
        Assert.Single(await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
        Assert.Equal(usageAfterFirst, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Feature_length_movie_and_long_scene_keep_short_shot_within_provider_clip_limit()
    {
        // A long-form movie is a sequence of short clips. The selected shot's
        // duration must take precedence over the containing scene and movie,
        // even when their plans are much longer than one provider submission.
        // No worker is enabled and the fake adapter never receives a request.
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Feature length timeline with short shot", sceneDurationSeconds: 5);
        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
            var scene = await db.MovieScenes.SingleAsync(item => item.Id == fixture.Scene.Id);
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            movie.DurationSeconds = 7_200;
            scene.DurationSeconds = 120;
            shot.DurationSeconds = 5;
            await db.SaveChangesAsync();
        }

        var response = await MovieOperationalFixtures.PostWithConfirmedMovieQuoteAsync<MovieStudioGenerationResponse>(
            factory, client, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Five-second feature film shot" },
            idempotencyKey: "feature-length-short-shot-admission");

        using var scope = factory.Services.CreateScope();
        var verify = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(5, await verify.MovieClips.Where(item => item.Id == response.ClipId)
            .Select(item => item.DurationSeconds).SingleAsync());
        var job = await verify.GenerationJobs.SingleAsync(item => item.Id == response.Job.Id);
        var input = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)!;
        Assert.Equal(5, input.DurationSeconds);
        Assert.Equal(GenerationJobStatus.Queued, job.Status);
    }

    [Fact]
    public async Task Feature_length_plan_does_not_override_unsupported_single_shot_duration()
    {
        // A two-hour movie is assembled from many clips; it does not grant the
        // selected adapter permission to generate an 11-second single shot when
        // that adapter only declares 2-10 seconds per submission.
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Feature length unsupported shot", sceneDurationSeconds: 5);
        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
            var scene = await db.MovieScenes.SingleAsync(item => item.Id == fixture.Scene.Id);
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            movie.DurationSeconds = 7_200;
            scene.DurationSeconds = 120;
            shot.DurationSeconds = 11;
            await db.SaveChangesAsync();
        }

        using var scope = factory.Services.CreateScope();
        var verify = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var initialUsageCount = await verify.UsageTransactions.CountAsync();
        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Unsupported eleven-second feature shot", confirmationAccepted = true },
            idempotencyKey: "feature-length-unsupported-shot");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await verify.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await verify.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "feature-length-unsupported-shot"));
        Assert.Equal(initialUsageCount, await verify.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Feature_length_scene_generation_rejects_one_hundred_twenty_second_single_clip()
    {
        // A 120-second scene in a two-hour project must be split into supported
        // shots; the legacy scene endpoint cannot submit it as one paid clip.
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Feature length scene requires shot splitting", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
        var scene = await db.MovieScenes.SingleAsync(item => item.Id == fixture.Scene.Id);
        movie.DurationSeconds = 7_200;
        scene.DurationSeconds = 120;
        await db.SaveChangesAsync();
        var transactionsBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new { title = "Unsupported full-scene single clip", confirmationAccepted = true },
            idempotencyKey: "feature-length-scene-120-seconds");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "feature-length-scene-120-seconds"));
        Assert.Equal(transactionsBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Unsupported_clip_duration_rejects_before_job_clip_or_usage_reservation()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie duration outside adapter range", sceneDurationSeconds: 11);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var transactionCountBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Unsupported 11-second clip" },
            idempotencyKey: "movie-duration-unsupported-11");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == "movie-duration-unsupported-11"));
        Assert.Equal(transactionCountBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Unsupported_priced_clip_profile_rejects_before_cost_reservation_or_job()
    {
        using var denyingFactory = new MovieClipIntentDeniedApiFactory();
        using (var setup = denyingFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = denyingFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie price execution mismatch", sceneDurationSeconds: 5);
        using var scope = denyingFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var transactionCountBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Unverified provider quality", confirmationAccepted = true },
            idempotencyKey: "movie-output-profile-unsupported");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == "movie-output-profile-unsupported"));
        Assert.Equal(transactionCountBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Oversized_scene_clip_description_rejects_before_job_clip_or_usage_reservation()
    {
        using var promptFactory = new MovieClipPromptLimitedApiFactory();
        using (var setup = promptFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = promptFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Oversized clip prompt admission", sceneDurationSeconds: 5);
        using var scope = promptFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var countBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Unsupported description", confirmationAccepted = true },
            idempotencyKey: "prompt-overflow-safe-admission");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_PROMPT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "prompt-overflow-safe-admission"));
        Assert.Equal(countBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Combined_movie_style_and_direction_overflow_rejects_before_usage_reservation()
    {
        using var promptFactory = new MovieClipPromptLimitedApiFactory();
        using (var setup = promptFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = promptFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Combined creative prompt admission", sceneDurationSeconds: 5);

        using var scope = promptFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
        var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
        // Each section fits the fake provider's 30-character bound separately.
        // They cannot fit together, even before provider-specific headings or
        // continuity directions are appended.
        shot.Description = "A sunlit room";
        movie.Style = "cinematic";
        movie.AdditionalInstructions = new string('x', 20);
        await db.SaveChangesAsync();
        var transactionsBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Impossible combined prompt", confirmationAccepted = true },
            idempotencyKey: "combined-creative-prompt-overflow");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_PROMPT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "combined-creative-prompt-overflow"));
        Assert.Equal(transactionsBefore, await db.UsageTransactions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_render_rejects_single_or_combined_prompt_overflow_before_usage_reservation(bool combinedPromptOnly)
    {
        using var promptFactory = new MovieClipPromptLimitedApiFactory();
        using (var setup = promptFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = promptFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Oversized production prompt admission", sceneDurationSeconds: 5);
        var shotId = fixture.Shot.Id;
        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate,
                compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{keyframe.Id}/review",
            new { approve = true });
        var motion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/motion-preview",
            new { sourceVersionId = approvedKeyframe.Id });
        var approvedMotion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{motion.Id}/review",
            new { approve = true });
        using var scope = promptFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        if (combinedPromptOnly)
        {
            // Neither the render title nor the shot direction individually
            // exceeds capacity, but their combined mandatory creative context
            // does. Run after approvals to exercise queue admission itself.
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
            shot.Description = "Short shot";
            movie.Style = "cinematic";
            movie.AdditionalInstructions = new string('x', 20);
            await db.SaveChangesAsync();
        }
        var countBefore = await db.UsageTransactions.CountAsync();
        var versionCountBefore = await db.MovieProductionVersions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/production/render",
            new { sourceVersionId = approvedMotion.Id, title = "Short", label = "Unsupported description", confirmationAccepted = true },
            idempotencyKey: "production-prompt-overflow-admission");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_PROMPT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "production-prompt-overflow-admission"));
        Assert.Equal(versionCountBefore, await db.MovieProductionVersions.CountAsync());
        Assert.Equal(countBefore, await db.UsageTransactions.CountAsync());
    }

    [Theory]
    [InlineData("720p", "720p", "Standard", "native", "16:9", true)]
    [InlineData("720p", "720p", "Standard", "native", "9:16", true)]
    [InlineData("1080p", "720p", "Standard", "native", "16:9", false)]
    [InlineData("720p", "1080p", "Standard", "native", "16:9", false)]
    [InlineData("720p", "4k", "Standard", "native", "16:9", false)]
    [InlineData("720p", "720p", "Cinematic", "native", "16:9", false)]
    [InlineData("720p", "720p", "Standard", "upscale", "16:9", false)]
    [InlineData("720p", "720p", "Standard", "native", "1:1", false)]
    public void Runway_scene_clip_only_advertises_the_execution_profile_it_submits(
        string source, string target, string quality, string path, string aspect, bool accepted)
    {
        using var httpClient = new HttpClient();
        IMovieVideoProvider provider = new RunwayMovieVideoProvider(
            httpClient, Options.Create(new MovieVideoOptions()),
            NullLogger<RunwayMovieVideoProvider>.Instance, new ProviderUrlPolicy());
        Assert.Equal(accepted, provider.SupportsSceneClipCostIntent(source, target, quality, path, aspect));
    }

    [Fact]
    public void Manus_scene_clip_output_resolution_and_quality_are_not_verified()
    {
        using var httpClient = new HttpClient();
        IMovieVideoProvider provider = new ManusMovieVideoProvider(
            httpClient, Options.Create(new MovieVideoOptions()),
            NullLogger<ManusMovieVideoProvider>.Instance);
        Assert.False(provider.SupportsSceneClipCostIntent("720p", "720p", "Standard", "native", "16:9"));
    }

    [Theory]
    [InlineData("runway", 1, false)]
    [InlineData("runway", 2, true)]
    [InlineData("runway", 10, true)]
    [InlineData("runway", 11, false)]
    [InlineData("manus", 0, false)]
    [InlineData("manus", 1, true)]
    [InlineData("manus", 30, true)]
    [InlineData("manus", 31, false)]
    [InlineData("manus", 60, false)]
    public void Real_adapter_declares_the_same_single_clip_duration_bounds_it_enforces(
        string adapter, int duration, bool supported)
    {
        using var httpClient = new HttpClient();
        IMovieVideoProvider provider = adapter == "runway"
            ? new RunwayMovieVideoProvider(httpClient, Options.Create(new MovieVideoOptions()),
                NullLogger<RunwayMovieVideoProvider>.Instance, new ProviderUrlPolicy())
            : new ManusMovieVideoProvider(httpClient, Options.Create(new MovieVideoOptions()),
                NullLogger<ManusMovieVideoProvider>.Instance);
        Assert.Equal(supported, provider.SceneClipDurationRange?.Supports(duration) == true);
    }
}
