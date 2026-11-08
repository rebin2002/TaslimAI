using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

// Provider-capable, no-worker host isolates the canonical reference-package
// failure: a missing package must remain a hard blocker even when the video
// adapter otherwise supports the requested short clip and priced output.
public sealed class MovieMissingReferenceWithProviderFactory : MovieOperationalApiFactory
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
            services.RemoveAll<IMovieProductionReferencePackageService>();
            services.AddSingleton<IMovieProductionReferencePackageService, MissingPackageService>();
        });
    }

    private sealed class MissingPackageService : IMovieProductionReferencePackageService
    {
        public Task<MovieProductionReferencePackageDto?> GetForShotAsync(
            Guid userId, Guid shotId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MovieProductionReferencePackageDto?>(null);
    }
}

public sealed class MovieProductionPreflightTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieProductionPreflightTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Preflight_requires_cheap_reference_work_before_production_and_stays_provider_neutral()
    {
        // Exercise missing reference work with a supported provider-sized shot;
        // otherwise an unknown/unsupported video duration correctly takes priority.
        using var isolatedFactory = new MovieClipDurationApiFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = isolatedFactory.CreateClient();
        var auth = await Register(client);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Quick,
            title = "Preflight guard",
            description = "Reference work must precede production.",
            durationSeconds = 20,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes", new
        {
            title = "Opening",
            summary = "A controlled opening.",
            durationSeconds = 5,
        });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "A wide opening frame.",
        });

        var response = await client.GetAsync($"/api/movie-studio/shots/{shot.Id}/production/preflight?targetResolution=1080p&qualityTier=Standard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preflight = (await response.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.False(preflight.CanProceed);
        Assert.Equal(MovieProductionPreflightDecisionCodes.ReferenceWorkRequired, preflight.Decision);
        Assert.Contains(preflight.Checks, item => item.Key == MovieProductionPreflightCheckKeys.ClipDuration && item.Satisfied && item.Required);
        Assert.Contains(preflight.Checks, item => item.Key == MovieProductionPreflightCheckKeys.ProviderAdmission && item.Satisfied && item.Required);
        Assert.Contains(preflight.Checks, item => item.Key == MovieProductionPreflightCheckKeys.Storyboard && !item.Satisfied && item.Required);
        Assert.Contains(preflight.Checks, item => item.Key == MovieProductionPreflightCheckKeys.Keyframe && !item.Satisfied && item.Required);
        Assert.Contains(preflight.Recommendations, item => item.Key == "approve_keyframe");
        Assert.Equal("1080p", preflight.AdaptiveResolution.TargetResolution);
        Assert.DoesNotContain(preflight.Recommendations, item => item.Detail.Contains("provider", StringComparison.OrdinalIgnoreCase));
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"provider\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"model\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task Read_only_production_preflight_honors_explicit_cost_confirmation_without_queueing_work()
    {
        // A confirmed GET changes only the read-only guardrail decision; it
        // must never reserve usage, create a clip or submit to any provider.
        using var baselineFactory = new MovieClipDurationApiFactory();
        using var configuredFactory = baselineFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GenerationBudget:Enabled", "true");
            builder.UseSetting("GenerationBudget:RejectUnknownEstimates", "false");
            builder.UseSetting("GenerationBudget:ExpensiveGenerationWarningThresholdUsd", "0");
            builder.UseSetting("GenerationBudget:RequireConfirmationForExpensiveGeneration", "true");
            builder.UseSetting("GenerationBudget:RequireConfirmationForUnknownEstimates", "true");
        });
        using (var setup = configuredFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = configuredFactory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Cost confirmation preview only", sceneDurationSeconds: 5);
        using var verify = configuredFactory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clipCountBefore = await db.MovieClips.CountAsync();
        var jobCountBefore = await db.GenerationJobs.CountAsync();
        var usageCountBefore = await db.UsageTransactions.CountAsync();
        var providerCallsBefore = verify.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie);

        var path = $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight?targetResolution=720p&qualityTier=Standard";
        using var unconfirmedResponse = await client.GetAsync(path);
        using var confirmedResponse = await client.GetAsync(path + "&confirmationAccepted=true");

        Assert.Equal(HttpStatusCode.OK, unconfirmedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirmedResponse.StatusCode);
        var unconfirmed = (await unconfirmedResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        var confirmed = (await confirmedResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.True(unconfirmed.CostGuardrails.ConfirmationRequired);
        Assert.False(unconfirmed.CostGuardrails.CanProceed);
        Assert.True(confirmed.CostGuardrails.ConfirmationRequired);
        Assert.True(confirmed.CostGuardrails.CanProceed);

        Assert.Equal(clipCountBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobCountBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageCountBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(providerCallsBefore, verify.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public async Task Read_only_preflight_uses_selected_adapter_clip_limits_without_jobs_or_usage(
        int seconds, bool expectedDurationSupport)
    {
        // A two-hour timeline is not a two-hour provider clip. The real read-only
        // endpoint must apply the same fake-adapter boundaries as approved queue
        // admission, including when cost confirmation has been accepted.
        using var isolatedFactory = new MovieClipDurationApiFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = isolatedFactory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selected-adapter preflight", sceneDurationSeconds: seconds);
        using var scope = isolatedFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var project = await db.MovieProjects.SingleAsync(item => item.Id == movie.Project.Project.Id);
        project.DurationSeconds = 7_200;
        await db.SaveChangesAsync();
        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await client.GetAsync(
            $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight?targetResolution=720p&qualityTier=Standard&confirmationAccepted=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = (await response.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.Contains(decision.Checks, check => check.Key == MovieProductionPreflightCheckKeys.ClipDuration
            && check.Required && check.Satisfied == expectedDurationSupport);
        Assert.Contains(decision.Checks, check => check.Key == MovieProductionPreflightCheckKeys.ProviderAdmission
            && check.Required && check.Satisfied == expectedDurationSupport);
        if (!expectedDurationSupport)
        {
            Assert.False(decision.CanProceed);
            Assert.Equal(MovieProductionPreflightDecisionCodes.ClipDurationUnsupported, decision.Decision);
            Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", decision.RejectionCode);
            Assert.Contains(decision.Recommendations, recommendation => recommendation.Key == "shorten_production_shot");
        }
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Read_only_preflight_fails_closed_on_unexecutable_priced_output()
    {
        // A verified duration is not enough when the adapter cannot deliver
        // the quoted source/target resolution and quality combination.
        using var isolatedFactory = new MovieClipIntentDeniedApiFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = isolatedFactory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Unexecutable priced preflight", sceneDurationSeconds: 5);
        using var scope = isolatedFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await client.GetAsync(
            $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight?targetResolution=720p&qualityTier=Standard&confirmationAccepted=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = (await response.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.False(decision.CanProceed);
        Assert.Contains(decision.Checks, check => check.Key == MovieProductionPreflightCheckKeys.ProviderAdmission
            && check.Required && !check.Satisfied);
        Assert.Contains(decision.Recommendations, recommendation => recommendation.Key == "choose_supported_video_profile");
        // Provider capability, not incomplete references, is the primary
        // blocker: extra reference work cannot make this priced intent runnable.
        Assert.Equal(MovieProductionPreflightDecisionCodes.ExecutionIntentUnsupported, decision.Decision);
        Assert.Equal("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED", decision.RejectionCode);
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == movie.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Read_only_preflight_fails_closed_for_missing_or_non_scene_clip_provider_capabilities(
        bool missingDurationRange, bool providerUnavailable)
    {
        // Real HTTP preview with a synthetic adapter: neither an unknown
        // single-clip duration contract nor a Quick-Movie-only adapter may
        // suggest that approved production video can proceed. This endpoint
        // must not persist or reserve billable work, even after confirmation.
        using var isolatedFactory = new MovieClipDurationApiFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = isolatedFactory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Unverified video adapter preflight", sceneDurationSeconds: 5);
        using var scope = isolatedFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var provider = Assert.IsType<FakeMovieVideoProvider>(
            scope.ServiceProvider.GetRequiredService<IMovieVideoProvider>());
        if (missingDurationRange)
            provider.SceneClipDurationRange = null;
        else
            provider.SupportedOperations = [MovieStudioOperations.QuickMovie];
        if (providerUnavailable)
            scope.ServiceProvider.GetRequiredService<FakeProviderScenarioCatalog>()[FakeProviderKind.Movie] =
                FakeProviderScenario.AllProvidersUnavailable;

        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await client.GetAsync(
            $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight?targetResolution=720p&qualityTier=Standard&confirmationAccepted=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = (await response.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.False(decision.CanProceed);
        Assert.Contains(decision.Checks, check =>
            check.Key == MovieProductionPreflightCheckKeys.ProviderAdmission
            && check.Required && !check.Satisfied);
        Assert.Contains(decision.Checks, check =>
            check.Key == MovieProductionPreflightCheckKeys.ClipDuration
            && check.Required && check.Satisfied == !missingDurationRange);
        if (providerUnavailable || !missingDurationRange)
        {
            // A disabled or Quick-Movie-only provider must be reported as the
            // primary blocker even if expensive reference work is incomplete.
            Assert.Equal(MovieProductionPreflightDecisionCodes.ProviderUnavailable, decision.Decision);
            Assert.Equal("MOVIE_PROVIDER_UNAVAILABLE", decision.RejectionCode);
            Assert.DoesNotContain(decision.Recommendations, recommendation => recommendation.Key == "shorten_production_shot");
        }
        else
        {
            // An available adapter with no verified duration range cannot
            // advertise a supported single clip, even after confirmation.
            Assert.Equal(MovieProductionPreflightDecisionCodes.ClipDurationUnsupported, decision.Decision);
            Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", decision.RejectionCode);
            Assert.Contains(decision.Recommendations, recommendation => recommendation.Key == "shorten_production_shot");
        }

        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Read_only_preflight_rejects_oversized_default_prompt_without_jobs_or_usage()
    {
        // Synthetic prompt cap: no paid adapter, background worker or debit.
        using var isolatedFactory = new MovieClipDurationApiFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = isolatedFactory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Prompt capacity preview", sceneDurationSeconds: 5);
        using var scope = isolatedFactory.Services.CreateScope();
        var provider = Assert.IsType<FakeMovieVideoProvider>(
            scope.ServiceProvider.GetRequiredService<IMovieVideoProvider>());
        provider.SceneClipPromptMaxCharacters = 1;
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync();
        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var callsBefore = scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie);

        using var response = await client.GetAsync(
            $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight?targetResolution=720p&qualityTier=Standard&confirmationAccepted=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = (await response.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.False(decision.CanProceed);
        Assert.Equal(MovieProductionPreflightDecisionCodes.PromptCapacityUnsupported, decision.Decision);
        Assert.Equal("MOVIE_CLIP_PROMPT_UNSUPPORTED", decision.RejectionCode);
        Assert.Contains(decision.Checks, check =>
            check.Key == MovieProductionPreflightCheckKeys.PromptCapacity
            && check.Required && !check.Satisfied);
        Assert.Contains(decision.Recommendations, recommendation =>
            recommendation.Key == "shorten_production_prompt");
        Assert.Equal(snapshotsBefore, await db.MovieCharacterContinuitySnapshots.CountAsync());
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(callsBefore, scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public void Reference_package_check_fails_closed_when_package_is_unavailable()
    {
        // Null packages must not become "ready" simply because they cannot
        // contain blocking warnings. This is a pure, provider-free regression.
        var unavailable = MovieProductionPreflightService.BuildReferencePackageCheck(null, []);

        Assert.False(unavailable.Satisfied);
        Assert.True(unavailable.Required);
        Assert.Contains("unavailable", unavailable.Detail, StringComparison.OrdinalIgnoreCase);

        var validPackage = new MovieProductionReferencePackageDto(
            1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "hash", DateTime.UnixEpoch,
            null!, null!, [], [], null!, [], [], null, [], []);
        var valid = MovieProductionPreflightService.BuildReferencePackageCheck(validPackage, []);
        Assert.True(valid.Satisfied);

        var blocked = MovieProductionPreflightService.BuildReferencePackageCheck(
            validPackage,
            [new MovieProductionReferenceWarningDto("hard_continuity_conflict", "error", "Resolve the conflict.")]);
        Assert.False(blocked.Satisfied);
        Assert.Contains("blocking reference conflict", blocked.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_preflight_source_query_aliases_are_equivalent_and_conflicts_fail_closed()
    {
        // The two supported query names refer to one canonical approved
        // production source. Matching values must be equivalent, whereas
        // conflicting values must never silently choose a source or queue work.
        using var isolatedFactory = new MovieClipDurationApiFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = isolatedFactory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Preflight source alias integrity", sceneDurationSeconds: 5);
        var versionsPath = "/api/movie-studio/shots/" + movie.Shot.Id + "/production/versions";
        var candidate = await SendWithCsrf<MovieProductionVersionDto>(
            client, HttpMethod.Post, versionsPath,
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await SendWithCsrf<MovieProductionVersionDto>(
            client, HttpMethod.Post, "/api/movie-studio/production/versions/" + candidate.Id + "/review",
            new { approve = true });
        var keyframe = await SendWithCsrf<MovieProductionVersionDto>(
            client, HttpMethod.Post, versionsPath,
            new { stage = MovieProductionStages.ProductionKeyframe,
                  sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
        var approvedKeyframe = await SendWithCsrf<MovieProductionVersionDto>(
            client, HttpMethod.Post, "/api/movie-studio/production/versions/" + keyframe.Id + "/review",
            new { approve = true });
        var motion = await SendWithCsrf<MovieProductionVersionDto>(
            client, HttpMethod.Post,
            "/api/movie-studio/shots/" + movie.Shot.Id + "/production/motion-preview",
            new { sourceVersionId = approvedKeyframe.Id });
        var approvedMotion = await SendWithCsrf<MovieProductionVersionDto>(
            client, HttpMethod.Post, "/api/movie-studio/production/versions/" + motion.Id + "/review",
            new { approve = true });

        using var verify = isolatedFactory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var providerLog = verify.ServiceProvider.GetRequiredService<FakeProviderCallLog>();
        var callsBefore = providerLog.Calls(FakeProviderKind.Movie);

        var url = "/api/movie-studio/shots/" + movie.Shot.Id
            + "/production/preflight?targetResolution=720p&qualityTier=Standard&confirmationAccepted=true";
        using var canonicalResponse = await client.GetAsync(
            url + "&sourceVersionId=" + approvedMotion.Id);
        using var aliasResponse = await client.GetAsync(
            url + "&sourceProductionVersionId=" + approvedMotion.Id);
        using var matchingResponse = await client.GetAsync(
            url + "&sourceVersionId=" + approvedMotion.Id
                + "&sourceProductionVersionId=" + approvedMotion.Id);
        using var conflictResponse = await client.GetAsync(
            url + "&sourceVersionId=" + approvedMotion.Id
                + "&sourceProductionVersionId=" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, canonicalResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, aliasResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, matchingResponse.StatusCode);
        var canonical = (await canonicalResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        var alias = (await aliasResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        var matching = (await matchingResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.Equal(canonical.Decision, alias.Decision);
        Assert.Equal(canonical.Decision, matching.Decision);
        Assert.Equal(canonical.RejectionCode, alias.RejectionCode);
        Assert.Equal(canonical.RejectionCode, matching.RejectionCode);
        Assert.Equal(canonical.CanProceed, alias.CanProceed);
        Assert.Equal(canonical.CanProceed, matching.CanProceed);
        Assert.Equal(HttpStatusCode.BadRequest, conflictResponse.StatusCode);
        Assert.Contains("MOVIE_PREFLIGHT_SOURCE_CONFLICT",
            await conflictResponse.Content.ReadAsStringAsync());

        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(callsBefore, providerLog.Calls(FakeProviderKind.Movie));
    }

    [Theory]
    [InlineData(5, 120, 7200, 5)]
    [InlineData(null, 12, 7200, 12)]
    [InlineData(null, null, 7200, 60)]
    [InlineData(null, null, 30, 30)]
    [InlineData(3600, 7200, 7200, 3600)]
    public void Adaptive_quality_uses_effective_single_clip_duration_instead_of_one_second_default(
        int? shotSeconds, int? sceneSeconds, int movieSeconds, int expectedSeconds)
    {
        // An inherited scene duration affects adaptive quality and the priced
        // render; it must not be replaced by a one-second quality assumption.
        var shot = new MovieShot
        {
            DurationSeconds = shotSeconds,
            Scene = new MovieScene
            {
                DurationSeconds = sceneSeconds,
                MovieProject = new MovieProject { DurationSeconds = movieSeconds },
            },
        };

        Assert.Equal(expectedSeconds, MovieProductionAdaptivePlanBuilder.ResolveAdaptiveDurationSeconds(shot));

        var inherited = MovieProductionAdaptivePlanBuilder.Build(shot, "1080p", DirectorQualityLevels.Standard);
        shot.DurationSeconds = expectedSeconds;
        var explicitDuration = MovieProductionAdaptivePlanBuilder.Build(shot, "1080p", DirectorQualityLevels.Standard);

        Assert.Equal(explicitDuration.SourceResolution, inherited.SourceResolution);
        Assert.Equal(explicitDuration.Recommendation.QualityConfidence, inherited.Recommendation.QualityConfidence);
        Assert.Equal(explicitDuration.Recommendation.QcEscalationRequired, inherited.Recommendation.QcEscalationRequired);
    }

    [Fact]
    public async Task Authenticated_preflight_denies_missing_reference_package_even_with_capable_provider()
    {
        using var isolatedFactory = new MovieMissingReferenceWithProviderFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = isolatedFactory.CreateClient();
        var auth = await Register(client);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(
            client, HttpMethod.Post, "/api/movie-studio/projects", new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Missing canonical reference package",
                description = "Read-only preflight must fail closed without a reference package.",
                durationSeconds = 30,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });
        var scene = await SendWithCsrf<MovieSceneDto>(
            client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes",
            new { title = "Opening", summary = "An approved-format opening.", durationSeconds = 5 });
        var shot = await SendWithCsrf<MovieShotDto>(
            client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots",
            new { description = "A stable camera view.", durationSeconds = 5 });

        using var scope = isolatedFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var calls = scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>();
        var callsBefore = calls.Calls(FakeProviderKind.Movie);
        var usageBefore = await db.UsageTransactions.CountAsync();
        using var response = await client.GetAsync(
            $"/api/movie-studio/shots/{shot.Id}/production/preflight?targetResolution=720p&qualityTier=Standard&confirmationAccepted=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preflight = (await response.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.False(preflight.CanProceed);
        Assert.Equal(MovieProductionPreflightDecisionCodes.ReferenceWorkRequired, preflight.Decision);
        Assert.Contains(preflight.Checks, check =>
            check.Key == MovieProductionPreflightCheckKeys.ReferencePackage
            && check.Required && !check.Satisfied
            && check.Detail.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preflight.Recommendations, recommendation => recommendation.Key == "rebuild_reference_package");
        Assert.False(await db.MovieClips.AnyAsync(clip => clip.MovieProjectId == project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(job => job.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(callsBefore, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Production_quote_binds_exact_model_and_amount_before_queue_and_replay()
    {
        // A fake adapter plus exact/decoy price rows exercises the real read-only
        // preflight and queue path without starting a worker or contacting a provider.
        using var isolatedFactory = new MovieOperationalNoWorkerFactory();
        using (var setup = isolatedFactory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = isolatedFactory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Model-bound production quote", sceneDurationSeconds: 5);
        var approvedMotion = await MovieOperationalFixtures.CreateApprovedMotionPreviewAsync(client, movie.Shot.Id);

        using var scope = isolatedFactory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TaslimDbContext>();
        var provider = Assert.IsType<FakeMovieVideoProvider>(services.GetRequiredService<IMovieVideoProvider>());
        var modelKey = Assert.IsType<string>(provider.SceneClipPricingModelKey);
        var exactRoute = await MovieOperationalFixtures.AddMoviePricingRouteAsync(
            db, provider.Key, modelKey, 0.01m, 0.02m, 0.01m, 0.02m);
        _ = await MovieOperationalFixtures.AddMoviePricingRouteAsync(
            db, provider.Key, "expensive-decoy-model", 1.00m, 2.00m, 1.00m, 2.00m);
        // The adapter may expose incidental whitespace around an otherwise
        // stable model key; queue and replay must canonicalize it consistently.
        provider.SceneClipPricingModelKey = $" {modelKey} ";

        var preflightPath = $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight"
            + $"?sourceVersionId={approvedMotion.Id}&targetResolution=1080p&qualityTier=Standard";
        var clipCountBefore = await db.MovieClips.CountAsync();
        var jobCountBefore = await db.GenerationJobs.CountAsync();
        var usageCountBefore = await db.UsageTransactions.CountAsync();
        var providerCallsBefore = services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie);

        using var quoteResponse = await client.GetAsync(preflightPath);
        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
        var quote = (await quoteResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        var priceIntent = Assert.IsType<MovieProductionPriceIntentDto>(quote.PriceIntent);
        Assert.True(quote.CanProceed
            || quote.Decision == MovieProductionPreflightDecisionCodes.CostConfirmationRequired,
            quote.RejectionMessage ?? quote.Summary);
        Assert.Equal("USD", priceIntent.Currency);
        Assert.Contains(quote.Checks, check => check.Key == MovieProductionPreflightCheckKeys.PriceIntent
            && check.Required && check.Satisfied);
        var quoteJson = await quoteResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(provider.Key, quoteJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(modelKey, quoteJson, StringComparison.OrdinalIgnoreCase);

        var estimateRequest = new MovieGenerationCostRequest(
            5,
            quote.AdaptiveResolution.SourceResolution,
            quote.AdaptiveResolution.TargetResolution,
            quote.AdaptiveResolution.QualityTier,
            quote.AdaptiveResolution.ProcessingPath);
        var estimator = services.GetRequiredService<IMovieGenerationCostEstimator>();
        var selectedEstimate = await estimator.EstimateAsync(
            estimateRequest, selectedProviderKey: provider.Key, selectedModelKey: modelKey);
        var allModelEstimate = await estimator.EstimateAsync(
            estimateRequest, selectedProviderKey: provider.Key);
        var selectedMaximum = selectedEstimate.MaximumAmountUsd
            ?? throw new InvalidOperationException("The configured exact-model route should produce an estimate.");
        var allModelMaximum = allModelEstimate.MaximumAmountUsd
            ?? throw new InvalidOperationException("The exact and decoy routes should produce an aggregate estimate.");
        Assert.Equal(selectedMaximum, priceIntent.ExpectedMaximumAmountUsd);
        Assert.True(allModelMaximum > priceIntent.ExpectedMaximumAmountUsd,
            "The deliberately expensive decoy model must not influence the selected adapter's quote.");
        Assert.Equal(0, providerCallsBefore);
        Assert.Equal(0, services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie));
        Assert.Equal(clipCountBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobCountBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageCountBefore, await db.UsageTransactions.CountAsync());

        var renderPath = $"/api/movie-studio/shots/{movie.Shot.Id}/production/render";
        var oldQuotePayload = new
        {
            sourceVersionId = approvedMotion.Id,
            label = "Quote-bound render",
            targetResolution = "1080p",
            qualityTier = DirectorQualityLevels.Standard,
            confirmationAccepted = true,
            expectedMaximumAmountUsd = priceIntent.ExpectedMaximumAmountUsd,
            expectedQuoteFingerprint = priceIntent.QuoteFingerprint,
        };

        using var missingQuote = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new { sourceVersionId = approvedMotion.Id, targetResolution = "1080p",
                qualityTier = DirectorQualityLevels.Standard, confirmationAccepted = true },
            idempotencyKey: "production-quote-required");
        Assert.Equal(HttpStatusCode.BadRequest, missingQuote.StatusCode);
        Assert.Contains("MOVIE_PRODUCTION_QUOTE_REQUIRED",
            await missingQuote.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var notConfirmed = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new { sourceVersionId = approvedMotion.Id, targetResolution = "1080p",
                qualityTier = DirectorQualityLevels.Standard, confirmationAccepted = false,
                expectedMaximumAmountUsd = priceIntent.ExpectedMaximumAmountUsd,
                expectedQuoteFingerprint = priceIntent.QuoteFingerprint },
            idempotencyKey: "production-quote-not-confirmed");
        Assert.Equal(HttpStatusCode.BadRequest, notConfirmed.StatusCode);
        Assert.Contains("GENERATION_CONFIRMATION_REQUIRED",
            await notConfirmed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        provider.SceneClipPricingModelKey = null;
        using var missingModelPreviewResponse = await client.GetAsync(preflightPath);
        var missingModelPreview = (await missingModelPreviewResponse.Content
            .ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.Null(missingModelPreview.PriceIntent);
        Assert.Contains(missingModelPreview.Checks, check =>
            check.Key == MovieProductionPreflightCheckKeys.PriceIntent && check.Required && !check.Satisfied);
        using var missingModelQueue = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, oldQuotePayload,
            idempotencyKey: "production-model-identity-required");
        Assert.Equal(HttpStatusCode.BadRequest, missingModelQueue.StatusCode);
        Assert.Contains("MOVIE_PROVIDER_MODEL_UNVERIFIED",
            await missingModelQueue.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        provider.SceneClipPricingModelKey = $" {modelKey} ";

        // Model drift cannot be hidden behind the same maximum amount; the opaque
        // preflight fingerprint binds the internal adapter/model identity.
        provider.SceneClipPricingModelKey = "changed-unpriced-model";
        using var unpricedModelPreflightResponse = await client.GetAsync(preflightPath);
        var unpricedModelPreflight = (await unpricedModelPreflightResponse.Content
            .ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.Null(unpricedModelPreflight.PriceIntent);
        Assert.Contains(unpricedModelPreflight.Checks, check =>
            check.Key == MovieProductionPreflightCheckKeys.PriceIntent && check.Required && !check.Satisfied);
        using var modelDrift = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, oldQuotePayload, idempotencyKey: "production-quote-model-drift");
        Assert.Equal(HttpStatusCode.BadRequest, modelDrift.StatusCode);
        Assert.Contains("MOVIE_PRODUCTION_QUOTE_STALE", await modelDrift.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        provider.SceneClipPricingModelKey = modelKey;

        // A route update after preview also makes the quote stale, before any
        // continuity snapshot, clip, job or usage transaction is persisted.
        exactRoute.BasePriceUsdPerSecondMax = 0.03m;
        exactRoute.UpscalePriceUsdPerSecondMax = 0.03m;
        exactRoute.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        using var amountDrift = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, oldQuotePayload, idempotencyKey: "production-quote-amount-drift");
        Assert.Equal(HttpStatusCode.BadRequest, amountDrift.StatusCode);
        Assert.Contains("MOVIE_PRODUCTION_QUOTE_STALE", await amountDrift.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipCountBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobCountBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageCountBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(providerCallsBefore, services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie));

        using var refreshedResponse = await client.GetAsync(preflightPath);
        var refreshedQuote = (await refreshedResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        var refreshedPriceIntent = Assert.IsType<MovieProductionPriceIntentDto>(refreshedQuote.PriceIntent);
        Assert.NotEqual(priceIntent.QuoteFingerprint, refreshedPriceIntent.QuoteFingerprint);
        var confirmedPayload = new
        {
            sourceVersionId = approvedMotion.Id,
            label = "Quote-bound render",
            targetResolution = "1080p",
            qualityTier = DirectorQualityLevels.Standard,
            confirmationAccepted = true,
            expectedMaximumAmountUsd = refreshedPriceIntent.ExpectedMaximumAmountUsd,
            expectedQuoteFingerprint = refreshedPriceIntent.QuoteFingerprint,
        };
        const string replayKey = "production-exact-quote-parity";
        using var queued = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, confirmedPayload, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var render = (await queued.Content.ReadFromJsonAsync<MovieProductionRenderResponse>())!;
        var queuedJob = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == render.Job.Id);
        var queuedInput = JsonSerializer.Deserialize<MovieGenerationInput>(queuedJob.InputJson);
        Assert.NotNull(queuedInput);
        Assert.Equal(refreshedPriceIntent.ExpectedMaximumAmountUsd, queuedJob.EstimatedProviderCostUsd);
        Assert.Equal(modelKey, queuedInput!.SelectedProviderModelKey);
        Assert.Equal(refreshedPriceIntent.QuoteFingerprint, queuedInput.SelectedProviderQuoteFingerprint);
        var clipsAfterQueue = await db.MovieClips.CountAsync();
        var jobsAfterQueue = await db.GenerationJobs.CountAsync();
        var usageAfterQueue = await db.UsageTransactions.CountAsync();
        Assert.Equal(clipCountBefore + 1, clipsAfterQueue);
        Assert.Equal(jobCountBefore + 1, jobsAfterQueue);
        Assert.Equal(usageCountBefore + 1, usageAfterQueue);

        // A same-key retry of the exact confirmed quote remains idempotent even
        // if a later route update changes today's price; it must return the
        // canonical job without reserving again.
        exactRoute.BasePriceUsdPerSecondMax = 0.04m;
        exactRoute.UpscalePriceUsdPerSecondMax = 0.04m;
        exactRoute.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        using var replay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new
            {
                sourceVersionId = approvedMotion.Id,
                label = "Quote-bound render",
                targetResolution = "1080p",
                qualityTier = DirectorQualityLevels.Standard,
                confirmationAccepted = true,
                expectedMaximumAmountUsd = refreshedPriceIntent.ExpectedMaximumAmountUsd,
                expectedQuoteFingerprint = $" {refreshedPriceIntent.QuoteFingerprint} ",
            }, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayRender = (await replay.Content.ReadFromJsonAsync<MovieProductionRenderResponse>())!;
        Assert.Equal(render.Job.Id, replayRender.Job.Id);
        Assert.Equal(render.ClipId, replayRender.ClipId);
        Assert.Equal(clipsAfterQueue, await db.MovieClips.CountAsync());
        Assert.Equal(jobsAfterQueue, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageAfterQueue, await db.UsageTransactions.CountAsync());
        using var changedPriceReplay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath,
            new
            {
                sourceVersionId = approvedMotion.Id,
                label = "Quote-bound render",
                targetResolution = "1080p",
                qualityTier = DirectorQualityLevels.Standard,
                confirmationAccepted = true,
                expectedMaximumAmountUsd = refreshedPriceIntent.ExpectedMaximumAmountUsd + 0.01m,
                expectedQuoteFingerprint = refreshedPriceIntent.QuoteFingerprint,
            }, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, changedPriceReplay.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await changedPriceReplay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsAfterQueue, await db.MovieClips.CountAsync());
        Assert.Equal(jobsAfterQueue, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageAfterQueue, await db.UsageTransactions.CountAsync());
        Assert.Equal(providerCallsBefore, services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie));
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Preflight Tester",
            email = $"movie-preflight-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
