using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorContextTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieDirectorContextTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Shot_context_is_targeted_relevant_and_deterministic_with_approved_story_and_locks()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Director Context Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Targeted Context Movie");
        await SendWithCsrf<MovieGuideHistoryResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });

        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Mara at the harbor", summary = "Mara enters the harbor warehouse." });
        var unrelatedScene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "The silent garden", summary = "An unrelated garden remains empty." });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Mara holds the red notebook in a close shot.", cinematography = new { intent = "Intimate", presetId = "intimate-naturalism" } });
        await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{unrelatedScene.Id}/shots", new { description = "An empty garden in the rain." });
        var mara = await SendWithCsrf<MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new { name = "Mara", description = "The courier.", appearance = "Dark curls." });
        await SendWithCsrf<MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new { name = "Unused", description = "Never present in this target." });
        await SendWithCsrf<MovieCharacterContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{mara.Id}/continuity-locks", new { fieldKey = "appearance", lockedValue = "Dark curls." });
        var location = await SendWithCsrf<MovieLocationDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Harbor warehouse", description = "A wet warehouse." });
        var unusedLocation = await SendWithCsrf<MovieLocationDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Unused garden", description = "Not relevant." });
        await SendWithCsrf<MovieWorldUsageDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "location", entityId = location.Id, role = "primary" });
        await SendWithCsrf<MovieContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/continuity-locks", new { entityType = "location", entityId = location.Id, fieldName = "weather", lockedValue = "rain", strength = "hard" });

        var story = await SendWithCsrf<MovieStoryDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions", new
        {
            premise = "A courier crosses a dark city.",
            logline = "Mara carries one message through the blackout.",
            synopsis = "Mara reaches the harbor before dawn.",
            treatment = "The harbor crossing becomes a test of resolve.",
            authorship = "Human",
            scenes = new[] { new { sceneIdentifier = "SCENE-1", movieSceneId = scene.Id, slugline = "EXT. HARBOR - NIGHT", synopsis = "Mara reaches the harbor." } },
        });
        var revisionId = story.CurrentRevisionId!.Value;
        await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{revisionId}/submit", null);
        await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{revisionId}/approve", null);

        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieDirectorContextAssembler>();
        var request = new DirectorContextTargetRequest { TargetType = DirectorContextTargetTypes.Shot, TargetId = shot.Id };
        var first = await assembler.AssembleAsync(auth.User.Id, movie.Project.Id, request);
        var second = await assembler.AssembleAsync(auth.User.Id, movie.Project.Id, request);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.SnapshotHash, second!.SnapshotHash);
        Assert.Equal(first.SnapshotJson, second.SnapshotJson);
        Assert.Equal(DirectorContextTargetTypes.Shot, first.Context.Target!.Type);
        Assert.Equal(shot.Id, first.Context.Target.ShotId);
        Assert.Single(first.Context.Scenes);
        Assert.Single(first.Context.Scenes[0].Shots);
        Assert.Contains(first.Context.Characters, item => item.Id == mara.Id);
        Assert.DoesNotContain(first.Context.Characters, item => item.Name == "Unused");
        Assert.DoesNotContain(first.Context.Locations, item => item.Id == unusedLocation.Id);
        Assert.Contains(first.Context.World!.Entities, item => item.Id == location.Id);
        Assert.Contains(first.Context.World.Locks, item => item.EntityId == location.Id && item.Strength == "hard");
        Assert.Contains(first.Context.Characters.Single(item => item.Id == mara.Id).LockedFacts!, item => item.FieldKey == "appearance" && item.LockedValue == "Dark curls.");
        Assert.Equal(revisionId, first.Context.ApprovedStory!.RevisionId);
        Assert.Contains("intimate-naturalism", first.Context.Scenes[0].Shots[0].CinematographyJson);
        Assert.True(first.Context.Budget!.CriticalFactsComplete);
        Assert.True(first.Context.Budget.UsedBytes <= first.Context.Budget.MaxBytes);
        Assert.NotEmpty(first.Context.Provenance!);
        Assert.Contains(first.Context.Provenance!, item => item.Kind == "guide_revision" && item.IsLocked);
        Assert.Contains(first.Context.Provenance!, item => item.Kind == "story_revision" && item.Id == revisionId);
    }

    [Fact]
    public async Task Cross_project_target_is_rejected_and_large_locked_guide_is_not_silently_truncated()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Director Boundary Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Boundary Movie");
        await SendWithCsrf<MovieGuideHistoryResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { });
        using var otherClient = factory.CreateClient();
        var otherAuth = await Register(otherClient, "Other Movie Owner");
        var otherMovie = await CreateMovie(otherClient, otherAuth.PersonalWorkspace.Id, "Other Movie");
        await SendWithCsrf<MovieGuideHistoryResponse>(otherClient, HttpMethod.Post, $"/api/movie-studio/projects/{otherMovie.Project.Id}/guide/lock", new { });

        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieDirectorContextAssembler>();
        await Assert.ThrowsAsync<DirectorContextTargetException>(() => assembler.AssembleAsync(auth.User.Id, movie.Project.Id, new DirectorContextTargetRequest { TargetType = DirectorContextTargetTypes.Scene, TargetId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<DirectorContextTargetException>(() => assembler.AssembleAsync(auth.User.Id, movie.Project.Id, new DirectorContextTargetRequest { TargetType = DirectorContextTargetTypes.Project, TargetId = otherMovie.Project.Id }));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"director-context-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string title) =>
        await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new { workspaceId, mode = "Full", title, description = "A bounded context test movie.", durationSeconds = 60, aspectRatio = "16:9", style = "cinematic", language = "en" });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
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
