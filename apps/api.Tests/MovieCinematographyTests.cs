using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCinematographyTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieCinematographyTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Presets_normalize_to_structured_intent_and_shot_overrides_do_not_mutate_a_locked_guide()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Cinematography Owner");

        var presets = await client.GetFromJsonAsync<List<CinematographyPreset>>("/api/movie-studio/cinematography/presets");
        Assert.NotNull(presets);
        Assert.Equal(4, presets!.Count);
        Assert.All(presets, preset => Assert.All(preset.CapabilityReferences, reference => Assert.Equal(CinematographyCapabilityClassification.Translated, reference.Classification)));
        var planningValues = await client.GetFromJsonAsync<CinematographyPlanningValueCatalog>("/api/movie-studio/cinematography/planning-values");
        Assert.Contains("close_up", planningValues!.ShotSizes);
        Assert.Contains("match_cut", planningValues.VisualTransitionIntents);

        var created = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Cinematography Bible",
            description = "A project that keeps the guide and shot intent separate.",
            durationSeconds = 30,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
            cinematography = new { intent = CinematographyIntent.Epic },
        });

        Assert.Equal(CinematographyIntent.Epic, created.Project.Guide.CinematographyBible?.Intent);
        Assert.Equal("epic-scale", created.Project.Guide.CinematographyBible?.PresetId);
        Assert.Contains(created.Project.Guide.CinematographyBible!.CapabilityReferences, reference => reference.Classification == CinematographyCapabilityClassification.Translated);

        var locked = await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/lock", new { });
        Assert.Equal(MovieGuideRevisionStatuses.Locked, locked.Revision.Status);

        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/scenes", new { title = "The ridge", summary = "A lone figure looks across the valley." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "A close override on the figure's eyes.",
            cinematography = new
            {
                intent = CinematographyIntent.Intimate,
                presetId = "intimate-naturalism",
                compositionNotes = "Protect breathing room toward the eyeline.",
            },
        });

        Assert.Contains("intimate-naturalism", shot.CinematographyJson, StringComparison.Ordinal);
        Assert.Contains("Protect breathing room", shot.CinematographyJson, StringComparison.Ordinal);

        var loaded = await client.GetFromJsonAsync<MovieStudioProjectDto>($"/api/movie-studio/projects/{created.Project.Id}");
        Assert.NotNull(loaded);
        Assert.Equal(CinematographyIntent.Epic, loaded!.Guide.CinematographyBible?.Intent);
        Assert.NotNull(loaded.Guide.LockedRevisionNumber);
        Assert.Contains(loaded.Scenes.SelectMany(item => item.Shots), item => item.Id == shot.Id && item.CinematographyJson!.Contains("intimate-naturalism", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shot_designer_is_workspace_authorized_and_rejects_unknown_capability_classifications()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Cinematography Private Owner");
        var created = await SendWithCsrf<MovieStudioProjectResponse>(owner, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Private Shot Plan",
            description = "A private cinematography plan.",
            durationSeconds = 20,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/scenes", new { title = "Private scene", summary = "Private scene summary." });

        using var other = factory.CreateClient();
        await Register(other, "Cinematography Intruder");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/movie-studio/projects/{created.Project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrf(other, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Should not persist." })).StatusCode);

        var invalid = await SendWithCsrf(owner, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "Invalid capability classification.",
            cinematography = new
            {
                intent = CinematographyIntent.Natural,
                capabilityReferences = new[] { new { field = "cameraMovement", classification = "ProviderMagic", rationale = "not a supported truth value" } },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_SHOT_INVALID", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Planning_endpoint_is_grounded_in_the_locked_guide_and_persists_a_typed_plan()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Cinematography Planner");
        var created = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Grounded plan",
            description = "A project for deterministic cinematography planning.",
            durationSeconds = 30,
            aspectRatio = "9:16",
            style = "cinematic",
            language = "en",
            cinematography = new { intent = CinematographyIntent.Intimate },
        });
        var locked = await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/lock", new { });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/scenes", new { title = "Confession", summary = "A quiet room holds a difficult truth." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "The character raises her eyes.",
            purpose = "Make the vulnerable confession land through the eyes.",
        });

        var planned = await SendWithCsrf<MovieCinematographyPlanResponse>(client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/cinematography/plan", new
        {
            characterEmotionalPurpose = "Vulnerable confession",
            creativeNotes = "Protect the eyeline toward the listener.",
        });

        Assert.Equal(created.Project.Id, planned.MovieProjectId);
        Assert.True(planned.GuideGrounded);
        Assert.True(planned.CanonPreserved);
        Assert.Equal(locked.Revision.RevisionNumber, planned.LockedGuideRevisionNumber);
        Assert.Contains("ShotSize", planned.AppliedCanonFields);
        Assert.Equal("close_up", planned.Plan.ShotSize);
        Assert.Contains(planned.Plan.Grounding!, item => item.Source == "cinematography_bible" && item.Locked);

        var reloaded = await client.GetFromJsonAsync<MovieShotDto>($"/api/movie-studio/shots/{shot.Id}");
        Assert.NotNull(reloaded?.CinematographyPlan);
        Assert.Equal("close_up", reloaded!.CinematographyPlan!.ShotSize);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName) =>
        await SendWithCsrf<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-cinematography-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });

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
