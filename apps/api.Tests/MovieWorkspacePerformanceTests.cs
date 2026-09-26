using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieWorkspacePerformanceTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieWorkspacePerformanceTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Workspace_read_model_returns_compact_module_projection_and_headers()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var movie = await SendWithCsrf<Taslim.Api.Movies.MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = "Full",
            title = "Read model budget",
            description = "A deterministic workspace projection test.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Opening", summary = "A compact scene summary." });
        await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new { name = "Mara", description = "A durable character card." });
        await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Ferry terminal", description = "A rain-dark terminal." });

        var overview = await client.GetAsync($"/api/movie-studio/projects/{movie.Project.Id}/workspace?module=overview");
        var overviewBody = await overview.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Equal("workspace-v1", overview.Headers.GetValues("X-Movie-Read-Model").Single());
        Assert.Equal("overview", overview.Headers.GetValues("X-Movie-Read-Module").Single());
        Assert.True(overviewBody.Length < 8_000, $"Overview payload was {overviewBody.Length} bytes.");
        using (var overviewJson = JsonDocument.Parse(overviewBody))
        {
            var project = overviewJson.RootElement.GetProperty("project");
            Assert.Empty(project.GetProperty("characters").EnumerateArray());
            Assert.Empty(project.GetProperty("locations").EnumerateArray());
            Assert.Equal(1, project.GetProperty("scenes").GetArrayLength());
            Assert.DoesNotContain("metadataJson", overviewBody, StringComparison.Ordinal);
            Assert.DoesNotContain("referenceAssetId", overviewBody, StringComparison.Ordinal);
            Assert.DoesNotContain("states", overviewBody, StringComparison.Ordinal);
        }

        var cast = await client.GetFromJsonAsync<Taslim.Api.Movies.MovieWorkspaceResponse>($"/api/movie-studio/projects/{movie.Project.Id}/workspace?module=cast");
        Assert.NotNull(cast);
        Assert.Equal("cast", cast!.Module);
        Assert.Single(cast.Project.Characters);
        Assert.Empty(cast.Project.Locations);

        var outsider = factory.CreateClient();
        await Register(outsider);
        using var outsiderResponse = await outsider.GetAsync($"/api/movie-studio/projects/{movie.Project.Id}/workspace?module=world");
        Assert.Equal(HttpStatusCode.NotFound, outsiderResponse.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client) =>
        await SendWithCsrf<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Workspace Read Tester",
            email = $"movie-workspace-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
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
