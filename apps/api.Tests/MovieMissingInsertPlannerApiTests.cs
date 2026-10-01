using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieMissingInsertPlannerApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieMissingInsertPlannerApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Planner_route_returns_a_read_only_grounding_report_without_a_timeline()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"insert-planner-{Guid.NewGuid():N}@example.com");
        var movie = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Insert planner API",
            description = "A bounded API route test.",
            durationSeconds = 12,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });

        using var response = await client.GetAsync($"/api/movie-studio/projects/{movie.Project.Id}/insert-planner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MovieMissingInsertPlanDto>();
        Assert.NotNull(result);
        Assert.Equal(MovieMissingInsertPlanner.ContractVersion, result!.ContractVersion);
        Assert.Equal(movie.Project.Id, result.MovieProjectId);
        Assert.Equal("Missing", result.TimelineStatus);
        Assert.False(result.CanonicalTimelineChanged);
        Assert.False(result.GenerationQueued);
        Assert.Empty(result.Proposals);
        Assert.Contains(result.Warnings, item => item.Code == "timeline_missing");
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var token = await GetCsrf(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Insert Planner Tester",
                email,
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var token = await GetCsrf(client);
        using var request = new HttpRequestMessage(method, path)
        {
            Content = payload is null ? null : JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrf(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return body.GetProperty("token").GetString()!;
    }
}
