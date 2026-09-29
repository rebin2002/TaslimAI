using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionContinuityApiTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieProductionContinuityApiTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Shot_review_is_read_only_and_workspace_scoped()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Production Continuity Owner");
        var movie = await SendJson<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = "Full",
            title = "Continuity Review",
            description = "A review-only production plan.",
            durationSeconds = 60,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendJson<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new
        {
            title = "Courtyard",
            summary = "A courier crosses the courtyard.",
        });
        var shot = await SendJson<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "The courier crosses the courtyard.",
        });
        var before = await client.GetFromJsonAsync<MovieShotDto>($"/api/movie-studio/shots/{shot.Id}");

        var response = await client.GetAsync($"/api/movie-studio/shots/{shot.Id}/continuity-review");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = (await response.Content.ReadFromJsonAsync<MovieProductionContinuityReviewDto>())!;
        Assert.True(review.ReviewOnly);
        Assert.Equal(movie.Project.Id, review.MovieProjectId);
        Assert.Equal(scene.Id, review.SceneId);
        Assert.Equal(shot.Id, review.ShotId);
        Assert.All(review.Findings, finding =>
        {
            Assert.NotEmpty(finding.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(finding.SuggestedCorrection));
        });

        var after = await client.GetFromJsonAsync<MovieShotDto>($"/api/movie-studio/shots/{shot.Id}");
        Assert.Equal(before!.Description, after!.Description);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Production Continuity Outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/movie-studio/shots/{shot.Id}/continuity-review")).StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"production-continuity-{Guid.NewGuid():N}@example.com",
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
