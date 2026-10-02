using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
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
