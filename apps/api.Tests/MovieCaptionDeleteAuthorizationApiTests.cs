using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCaptionDeleteAuthorizationApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieCaptionDeleteAuthorizationApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Outsider_caption_delete_returns_forbidden_instead_of_an_unhandled_server_error()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Caption Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id);
        var track = await CreateTrack(owner, project.Project.Id);
        var cue = await CreateCue(owner, track.Id);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Caption Outsider");

        using var response = await SendWithCsrf(
            outsider,
            HttpMethod.Delete,
            $"/api/movie-studio/caption-cues/{cue.Id}",
            null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"caption-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateProject(HttpClient client, Guid workspaceId)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = MovieProjectModes.Full,
            title = "Caption authorization",
            description = "Provider-free caption authorization test.",
            durationSeconds = 10,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieStudioProjectResponse>())!;
    }

    private static async Task<MovieCaptionTrackDto> CreateTrack(HttpClient client, Guid projectId)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/caption-tracks", new
        {
            name = "English",
            language = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieCaptionTrackDto>())!;
    }

    private static async Task<MovieCaptionCueDto> CreateCue(HttpClient client, Guid trackId)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/caption-tracks/{trackId}/cues", new
        {
            sequence = 1,
            startTimecode = "00:00:01.000",
            endTimecode = "00:00:02.000",
            text = "Private cue",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieCaptionCueDto>())!;
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
