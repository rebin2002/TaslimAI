using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

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
