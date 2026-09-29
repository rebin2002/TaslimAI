using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieStoryScenePlanningContextTests : IClassFixture<TaslimApiFactory>
{
    private const string LastSeedBrief = "In the near future, a young farmer lives in a dry village where almost nothing can grow. His grandfather gives him the last seed from an old tree that once covered the valley. The farmer decides to plant it despite everyone telling him it will never survive. After days of protecting it from heat and wind, rain finally arrives and the seed begins to grow. Emotional, hopeful, cinematic.";
    private readonly TaslimApiFactory factory;

    public MovieStoryScenePlanningContextTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Last_seed_context_is_targeted_bounded_and_deterministic()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Scene Planning Last Seed Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, LastSeedBrief, 30, "16:9", "en");
        await Send<MovieGuideHistoryResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });
        var story = await CreateStory(client, movie.Project.Id, [
            Scene("LAST-SEED-1", "EXT. DRY VILLAGE FIELD - DAY", "The young farmer receives the last seed.", [Action("Heat cracks the earth.")]),
            Scene("LAST-SEED-2", "EXT. DRY VILLAGE FIELD - DAY", "The farmer protects the seed from wind.", [Action("The farmer shields the seed.")]),
            Scene("LAST-SEED-3", "EXT. DRY VILLAGE FIELD - RAIN", "Rain begins and the seed grows.", [Action("A green shoot rises.")]),
        ]);
        var targetId = story.CurrentRevision!.Scenes[1].Id;

        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieDirectorContextAssembler>();
        var request = new MovieStoryScenePlanningContextRequest { TargetSceneId = targetId, SurroundingSceneRadius = 1 };
        var first = await assembler.AssembleStoryScenePlanningAsync(movie.Project.Id, request);
        var second = await assembler.AssembleStoryScenePlanningAsync(movie.Project.Id, request);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.SnapshotHash, second!.SnapshotHash);
        Assert.Equal(first.SnapshotJson, second.SnapshotJson);
        Assert.Equal(LastSeedBrief, first.Context.MovieBrief);
        Assert.Equal(30, first.Context.TargetRuntimeSeconds);
        Assert.Equal("en", first.Context.Language);
        Assert.Equal("16:9", first.Context.AspectRatio);
        Assert.Equal(targetId, first.Context.Target!.SceneId);
        Assert.Equal("LAST-SEED-2", first.Context.Target.SceneIdentifier);
        Assert.Equal(3, first.Context.CurrentStory!.Scenes.Count);
        Assert.Contains(first.Context.CurrentStory.Scenes, item => item.IsTarget && item.SceneIdentifier == "LAST-SEED-2");
        Assert.DoesNotContain(first.Context.MissingSections, item => item == "target_scene");
        Assert.True(first.Diagnostics.UsedBytes <= first.Diagnostics.MaxBytes);
        Assert.True(first.Diagnostics.CriticalFactsComplete);
        Assert.NotEmpty(first.Diagnostics.IncludedSourceKinds);
    }

    [Fact]
    public async Task Context_is_relevant_to_target_section_and_never_sends_full_screenplay_history()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Scene Planning Bounds Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "A bounded scene planning movie.", 120, "16:9", "en");
        await Send<MovieGuideHistoryResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });
        var scenes = Enumerable.Range(1, 20)
            .Select(number => Scene($"SECTION-{number:00}", $"INT. ROOM {number} - NIGHT", $"Targetable section {number}.", Enumerable.Range(1, 10).Select(element => Action($"Action {number}-{element} with bounded screenplay material.")).ToArray()))
            .ToArray();
        var story = await CreateStory(client, movie.Project.Id, scenes);

        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieDirectorContextAssembler>();
        var result = await assembler.AssembleScenePlanningAsync(movie.Project.Id, new MovieStoryScenePlanningContextRequest
        {
            TargetSection = "SECTION-10",
            SurroundingSceneRadius = 3,
        });

        Assert.NotNull(result);
        Assert.Equal("SECTION-10", result!.Context.Target!.SceneIdentifier);
        Assert.Equal(5, result.Context.CurrentStory!.Scenes.Count);
        Assert.DoesNotContain(result.Context.CurrentStory.Scenes, item => item.SceneIdentifier == "SECTION-01");
        Assert.DoesNotContain(result.Context.CurrentStory.Scenes, item => item.SceneIdentifier == "SECTION-20");
        Assert.All(result.Context.CurrentStory.Scenes, scene => Assert.InRange(scene.Elements.Count, 0, 6));
        Assert.True(result.SnapshotJson.Length > 0);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.SnapshotJson) <= MovieStoryScenePlanningContextAssembler.MaxSnapshotBytes);
        Assert.Equal(1, result.Diagnostics.RevisionCount);
        Assert.Equal(5, result.Diagnostics.ScreenplaySceneCount);
    }

    [Fact]
    public async Task Planning_rejects_a_target_from_a_superseded_approved_revision()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Scene Planning Stale Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Stale target planning movie.", 60, "16:9", "en");
        await Send<MovieGuideHistoryResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });
        var first = await CreateStory(client, movie.Project.Id, [Scene("OLD", "INT. OLD ROOM - DAY", "Old approved scene.", [Action("Old action.")])]);
        await Send<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{first.CurrentRevisionId}/approve", null);
        var second = await CreateStory(client, movie.Project.Id, [Scene("NEW", "INT. NEW ROOM - DAY", "New approved scene.", [Action("New action.")])]);
        await Send<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{second.CurrentRevisionId}/approve", null);

        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieDirectorContextAssembler>();
        var oldSceneId = first.CurrentRevision!.Scenes[0].Id;
        var exception = await Assert.ThrowsAsync<MovieStoryScenePlanningTargetException>(() => assembler.AssembleStoryScenePlanningAsync(movie.Project.Id, new MovieStoryScenePlanningContextRequest { TargetSceneId = oldSceneId }));

        Assert.Equal("MOVIE_SCENE_PLANNING_STALE_TARGET", exception.Code);
    }

    [Fact]
    public async Task Missing_story_sections_are_diagnosed_without_creating_scene_material()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Scene Planning Missing Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Missing Story movie.", 45, "9:16", "ku");
        await Send<MovieGuideHistoryResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });

        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieStoryScenePlanningContextAssembler>();
        var result = await assembler.AssembleAsync(movie.Project.Id, new MovieStoryScenePlanningContextRequest { TargetRuntimeSeconds = 12 });

        Assert.NotNull(result);
        Assert.Null(result!.Context.CurrentStory);
        Assert.Null(result.Context.ApprovedStory);
        Assert.Contains("story", result.Context.MissingSections);
        Assert.Contains("screenplay", result.Context.MissingSections);
        Assert.Contains("target_scene", result.Context.MissingSections);
        Assert.Equal(12, result.Context.TargetRuntimeSeconds);
        Assert.Equal("ku", result.Context.Language);
        Assert.Equal("9:16", result.Context.AspectRatio);
    }

    [Fact]
    public async Task Target_runtime_language_and_aspect_ratio_are_explicit_context_contract_fields()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Scene Planning Locale Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Locale scene planning movie.", 90, "1:1", "ar");
        await Send<MovieGuideHistoryResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });
        var story = await CreateStory(client, movie.Project.Id, [Scene("LOCALE-1", "EXT. SQUARE - NIGHT", "A grounded scene.", [Action("A lantern glows.")])]);

        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieDirectorContextAssembler>();
        var result = await assembler.AssembleStoryScenePlanningAsync(movie.Project.Id, new MovieStoryScenePlanningContextRequest
        {
            TargetSceneId = story.CurrentRevision!.Scenes[0].Id,
            TargetRuntimeSeconds = 18,
            Language = "ar",
            AspectRatio = "1:1",
        });

        Assert.NotNull(result);
        Assert.Equal(18, result!.Context.TargetRuntimeSeconds);
        Assert.Equal("ar", result.Context.Language);
        Assert.Equal("1:1", result.Context.AspectRatio);
    }

    private static object Scene(string identifier, string slugline, string synopsis, IReadOnlyList<object> elements) => new
    {
        sceneIdentifier = identifier,
        actNumber = 1,
        sequenceNumber = 1,
        slugline,
        synopsis,
        elements,
    };

    private static object Action(string content) => new { elementType = MovieScreenplayElementTypes.Action, content };

    private static async Task<MovieStoryDto> CreateStory(HttpClient client, Guid movieId, IReadOnlyList<object> scenes)
    {
        return await Send<MovieStoryDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/story/revisions", new
        {
            premise = "A grounded choice must survive a changing world.",
            logline = "A determined character protects one important hope before the next irreversible turn.",
            synopsis = "The story moves from a clear setup through one choice to a visible consequence.",
            treatment = "A bounded treatment preserves the established story and scene-scale cause and effect.",
            authorship = MovieStoryAuthorship.Human,
            scenes,
        });
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendRaw(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"scene-planning-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string description, int durationSeconds, string aspectRatio, string language) =>
        await Send<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new { workspaceId, mode = MovieProjectModes.Full, title = $"Scene Planning {Guid.NewGuid():N}", description, durationSeconds, aspectRatio, style = "cinematic", language });

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
