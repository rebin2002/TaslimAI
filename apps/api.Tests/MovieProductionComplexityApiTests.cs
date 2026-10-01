using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionComplexityApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieProductionComplexityApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Valid_profile_is_versioned_and_consumable_from_shot_contract()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var movie = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Complexity API test",
            description = "Provider-neutral profile persistence.",
            durationSeconds = 30,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Scene", summary = "Profile scene." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Profile shot." });

        var saved = await SendWithCsrf<MovieProductionComplexityAssessmentDto>(client, HttpMethod.Put, $"/api/movie-studio/shots/{shot.Id}/production-complexity", ProfilePayload(80, MovieProductionComplexityBands.High));
        Assert.Equal(1, saved.Version);
        Assert.Equal(80, saved.Profile.OverallScore);
        Assert.Equal(MovieProductionComplexityBands.High, saved.Profile.OverallBand);
        Assert.Equal(MovieProductionComplexitySources.Manual, saved.Source);

        var loaded = await client.GetFromJsonAsync<MovieShotDto>($"/api/movie-studio/shots/{shot.Id}");
        Assert.NotNull(loaded!.ProductionComplexity);
        Assert.Equal(saved.Id, loaded.ProductionComplexity!.Id);

        var second = await SendWithCsrf<MovieProductionComplexityAssessmentDto>(client, HttpMethod.Put, $"/api/movie-studio/shots/{shot.Id}/production-complexity", ProfilePayload(20, MovieProductionComplexityBands.Low));
        Assert.Equal(2, second.Version);
        var latest = await client.GetFromJsonAsync<MovieProductionComplexityAssessmentDto>($"/api/movie-studio/shots/{shot.Id}/production-complexity");
        Assert.Equal(HttpStatusCode.OK, latest is null ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        Assert.Equal(2, latest!.Version);
    }

    [Fact]
    public async Task Contradictory_profile_is_rejected_without_persisting_an_assessment()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var movie = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Invalid complexity API test",
            description = "Reject contradictory profile.",
            durationSeconds = 30,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Scene", summary = "Profile scene." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Profile shot." });

        var response = await SendWithCsrf(client, HttpMethod.Put, $"/api/movie-studio/shots/{shot.Id}/production-complexity", ProfilePayload(100, MovieProductionComplexityBands.Low));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var missing = await client.GetAsync($"/api/movie-studio/shots/{shot.Id}/production-complexity");
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
    }

    private async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Complexity tester",
            email = $"movie-complexity-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static object ProfilePayload(int value, string band) => new
    {
        motionComplexity = value,
        cameraComplexity = value,
        faceImportance = value,
        handBodyInteractionComplexity = value,
        fineDetailImportance = value,
        environmentComplexity = value,
        vfxComplexity = value,
        continuitySensitivity = value,
        textSignageSensitivity = value,
        dialogueLipSyncDependency = value,
        durationComplexity = value,
        declaredOverallBand = band,
    };

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
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
