using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieShotImportanceClassifierTests
{
    private readonly MovieShotImportanceClassifier classifier = new();

    [Fact]
    public void High_stakes_story_action_is_hero_with_grounded_evidence()
    {
        var result = classifier.Classify(Context(
            description: "Mara reveals the truth and takes the decisive action.",
            sceneSummary: "The turning point arrives at the harbor.",
            storySynopsis: "The major reveal forces Mara into the final confrontation."));

        Assert.Equal(MovieShotImportanceLevels.Hero, result.Classification);
        Assert.True(result.Confidence >= 0.84m);
        Assert.Contains(result.Evidence, item => item.Source == "shot" && item.Signal == "hero_narrative");
        Assert.Contains(result.Evidence, item => item.Source == "story" && item.Signal == "hero_narrative");
        Assert.Contains("bounded Story/Scene/Guide context", result.Reasoning);
    }

    [Fact]
    public void Cinematic_camera_language_alone_does_not_make_a_shot_hero()
    {
        var result = classifier.Classify(Context(
            description: "A traveler walks through a quiet corridor.",
            cameraAndFraming: "Cinematic extreme close-up with hero lighting and anamorphic bokeh."));

        Assert.Equal(MovieShotImportanceLevels.Standard, result.Classification);
        Assert.DoesNotContain(result.Evidence, item => item.Signal == "hero_narrative");
        Assert.Contains("Cinematic-looking fields do not raise importance", result.Reasoning);
    }

    [Fact]
    public void Emotional_close_up_is_important_but_not_hero_without_high_stakes_story_evidence()
    {
        var result = classifier.Classify(Context(
            description: "Tears gather as Mara remembers home.",
            purpose: "Hold the emotional beat.",
            cameraAndFraming: "Close-up."));

        Assert.Equal(MovieShotImportanceLevels.Important, result.Classification);
        Assert.Contains(result.Evidence, item => item.Signal == "emotional_close_up");
        Assert.DoesNotContain(result.Evidence, item => item.Signal == "hero_narrative");
    }

    [Fact]
    public void Explicit_background_insert_is_utility_background()
    {
        var result = classifier.Classify(Context(
            description: "Background insert of rain on the empty street.",
            purpose: "Ambient transition between scenes."));

        Assert.Equal(MovieShotImportanceLevels.UtilityBackground, result.Classification);
        Assert.Contains(result.Evidence, item => item.Signal == "utility_signal");
    }

    [Fact]
    public void Classification_is_deterministic_for_the_same_context()
    {
        var context = Context(
            description: "The courier enters the station and discovers the evidence.",
            sceneSummary: "An important introduction to the station's threat.",
            storySynopsis: "The courier must decide whether to continue.");

        var first = classifier.Classify(context);
        var second = classifier.Classify(context);

        Assert.Equal(first.Classification, second.Classification);
        Assert.Equal(first.Reasoning, second.Reasoning);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Evidence, second.Evidence);
    }

    private static MovieShotImportanceContext Context(
        string description,
        string? purpose = null,
        string? cameraAndFraming = null,
        string? sceneSummary = null,
        string? storySynopsis = null) => new(
            description,
            purpose,
            "Mara",
            null,
            null,
            cameraAndFraming,
            null,
            null,
            null,
            null,
            null,
            new MovieShotImportanceSceneContext("Harbor", sceneSummary ?? "A harbor scene.", null, null, null),
            storySynopsis is null ? null : new MovieShotImportanceStoryContext("A courier crosses a dark city.", "Mara carries one message.", storySynopsis, "The courier's story.", null, null, null),
            new MovieShotImportanceGuideContext("Grounded visual language.", "Restrained camera language.", "Cool palette.", "Sparse sound.", "Protect continuity.", "{}", "{}", "{\"intent\":\"cinematic\"}", "{}", "{}", 1, true));
}

public sealed class MovieShotImportanceApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieShotImportanceApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Importance_endpoint_returns_director_assessment_and_persists_user_override()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var movie = await Send<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Importance API test",
            description = "A deterministic importance test movie.",
            durationSeconds = 60,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await Send<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Harbor", summary = "The turning point at the harbor." });
        var shot = await Send<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Mara reveals the truth and takes the decisive action.", purpose = "Major reveal." });

        var assessed = await client.GetFromJsonAsync<MovieShotImportanceDto>($"/api/movie-studio/shots/{shot.Id}/importance");
        Assert.NotNull(assessed);
        Assert.Equal(MovieShotImportanceLevels.Hero, assessed!.Classification);
        Assert.Equal("director", assessed.Source);
        Assert.NotEmpty(assessed.Evidence);

        var overridden = await Send<MovieShotImportanceDto>(client, HttpMethod.Patch, $"/api/movie-studio/shots/{shot.Id}/importance", new { classification = MovieShotImportanceLevels.UtilityBackground });
        Assert.Equal(MovieShotImportanceLevels.UtilityBackground, overridden.Classification);
        Assert.Equal(MovieShotImportanceLevels.Hero, overridden.DirectorClassification);
        Assert.Equal("user_override", overridden.Source);

        var cleared = await Send<MovieShotImportanceDto>(client, HttpMethod.Patch, $"/api/movie-studio/shots/{shot.Id}/importance", new { classification = (string?)null });
        Assert.Equal(MovieShotImportanceLevels.Hero, cleared.Classification);
        Assert.Equal("director", cleared.Source);
    }

    private async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendRaw(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Importance Owner",
            email = $"importance-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> Send<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var response = await SendRaw(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendRaw(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
