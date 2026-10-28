using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Test the real authenticated endpoint with a reference service that cannot
// assemble a canonical package. No generation worker or provider is invoked.
public sealed class MovieMissingReferencePackageApiFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieProductionReferencePackageService>();
            services.AddSingleton<IMovieProductionReferencePackageService, MissingReferencePackageService>();
        });
    }

    private sealed class MissingReferencePackageService : IMovieProductionReferencePackageService
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
        Assert.Contains(preflight.Checks, item => item.Key == MovieProductionPreflightCheckKeys.ProviderAdmission && item.Required);
        Assert.Contains(preflight.Recommendations, item => item.Key == "approve_keyframe");
        Assert.Equal("1080p", preflight.AdaptiveResolution.TargetResolution);
        Assert.DoesNotContain(preflight.Recommendations, item => item.Detail.Contains("provider", StringComparison.OrdinalIgnoreCase));
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"provider\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"model\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authenticated_preflight_rejects_missing_canonical_reference_package_without_creating_work()
    {
        using var unavailableFactory = new MovieMissingReferencePackageApiFactory();
        using var client = unavailableFactory.CreateClient();
        var auth = await Register(client);
        var project = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Quick,
            title = "Unavailable reference preflight",
            description = "Missing reference data cannot authorize production.",
            durationSeconds = 5,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(
            client, HttpMethod.Post, $"/api/movie-studio/projects/{project.Project.Id}/scenes",
            new { title = "Opening", summary = "A reference-safe opening." });
        var shot = await SendWithCsrf<MovieShotDto>(
            client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots",
            new { description = "A stable wide shot.", durationSeconds = 5 });

        using var scope = unavailableFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usageBefore = await db.UsageTransactions.CountAsync();
        using var response = await client.GetAsync(
            $"/api/movie-studio/shots/{shot.Id}/production/preflight?targetResolution=1080p&qualityTier=Standard&confirmationAccepted=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preflight = (await response.Content.ReadFromJsonAsync<MovieProductionPreflightDto>())!;
        Assert.False(preflight.CanProceed);
        Assert.Equal(MovieProductionPreflightDecisionCodes.ReferenceWorkRequired, preflight.Decision);
        Assert.Contains(preflight.Checks, check =>
            check.Key == MovieProductionPreflightCheckKeys.ReferencePackage
            && check.Required && !check.Satisfied
            && check.Detail.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preflight.Recommendations, item => item.Key == "rebuild_reference_package");
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public void Reference_package_check_fails_closed_when_package_is_unavailable()
    {
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Production_provider_preflight_fails_closed_without_enabled_scene_clip_operation(
        bool available, bool supportsSceneClip)
    {
        var check = MovieProductionPreflightService.BuildProviderAdmissionCheck(available, supportsSceneClip);
        Assert.Equal(MovieProductionPreflightCheckKeys.ProviderAdmission, check.Key);
        Assert.True(check.Required);
        Assert.False(check.Satisfied);
        Assert.Contains("provider", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_provider_preflight_accepts_available_scene_clip_operation()
    {
        var check = MovieProductionPreflightService.BuildProviderAdmissionCheck(true, true);
        Assert.True(check.Required);
        Assert.True(check.Satisfied);
        Assert.Contains("queue", check.Detail, StringComparison.OrdinalIgnoreCase);
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
