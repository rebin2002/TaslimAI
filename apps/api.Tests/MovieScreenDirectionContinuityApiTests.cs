using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieScreenDirectionContinuityApiTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieScreenDirectionContinuityApiTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Shot_screen_direction_round_trips_and_review_returns_non_destructive_recommendations()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var movie = await SendJson<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = "Full",
            title = "Screen Direction API",
            description = "Screen direction contract test.",
            durationSeconds = 30,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendJson<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Hallway", summary = "A subject crosses a hallway." });
        await SendJson<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "The subject exits at screen right.",
            screenDirection = new { axis = "hallway-a", orientation = "side_a", exitDirection = "screen_right", spatialAnchor = "door" },
        });
        var second = await SendJson<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "The subject enters from the wrong side.",
            screenDirection = new { axis = "hallway-b", orientation = "side_a", entranceDirection = "screen_right", spatialAnchor = "window" },
        });

        Assert.Equal("hallway-b", second.ScreenDirection?.Axis);
        var response = await client.GetAsync($"/api/movie-studio/shots/{second.Id}/continuity-review");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = (await response.Content.ReadFromJsonAsync<MovieProductionContinuityReviewDto>())!;
        Assert.True(review.ReviewOnly);
        Assert.Contains(review.Findings, item => item.Category == DirectorStoryFindingCategories.ScreenDirectionAxis);
        Assert.Contains(review.Findings, item => item.Category == DirectorStoryFindingCategories.ScreenDirectionEntranceExit);
        Assert.Contains(review.Findings, item => item.Category == DirectorStoryFindingCategories.ScreenDirectionGeography);
        Assert.Contains(review.ScreenDirectionRecommendations ?? [], item => item.Kind == "edit" && !item.AutomaticRewrite);
        Assert.Contains(review.ScreenDirectionRecommendations ?? [], item => item.Kind == "insert" && !item.AutomaticRewrite);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Screen Direction Owner",
            email = $"screen-direction-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendJson<T>(HttpClient client, HttpMethod method, string path, object payload)
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
