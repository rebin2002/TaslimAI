using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        using var client = factory.CreateClient();
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
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == movie.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_only_preflight_fails_closed_for_missing_or_non_scene_clip_provider_capabilities(
        bool missingDurationRange)
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
        if (missingDurationRange)
        {
            Assert.Equal(MovieProductionPreflightDecisionCodes.ClipDurationUnsupported, decision.Decision);
            Assert.Equal("MOVIE_CLIP_DURATION_UNSUPPORTED", decision.RejectionCode);
        }

        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie));
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
