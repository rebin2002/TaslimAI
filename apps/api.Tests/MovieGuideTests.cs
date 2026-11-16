using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieGuideTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieGuideTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Structured_guide_is_persisted_versioned_locked_and_exposed_to_director_only_when_authoritative()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Movie Guide Owner");
        var created = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "The Locked Cut",
            description = "A continuity-first short film.",
            durationSeconds = 60,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
            visualLanguage = "Naturalistic 35mm texture",
            cameraLanguage = "Slow, deliberate dolly movement",
            colorAndLighting = "Warm practicals with cool moonlight",
            soundAndNarration = "Sparse diegetic sound",
            continuityRules = "The red notebook remains in the left hand.",
        });
        Assert.Equal(1, created.Project.Guide.CurrentRevisionNumber);

        var initialHistory = await client.GetFromJsonAsync<MovieGuideHistoryResponse>($"/api/movie-studio/projects/{created.Project.Id}/guide/history");
        Assert.NotNull(initialHistory);
        Assert.Equal(1, initialHistory!.CurrentRevisionNumber);
        var initial = Assert.Single(initialHistory.Revisions);
        Assert.Equal(MovieGuideRevisionStatuses.Draft, initial.Status);
        Assert.Equal(MovieGuideSectionTypes.StoryBible, initial.Sections[0].Type);
        Assert.Equal(MovieGuideSectionTypes.ContinuityBible, initial.Sections[^1].Type);

        var revision = await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/revisions", new
        {
            storyBibleJson = "{\"logline\":\"A promise survives a storm.\",\"themes\":[\"trust\"]}",
            characterBibleReferencesJson = "[{\"characterId\":\"00000000-0000-0000-0000-000000000001\",\"role\":\"protagonist\"}]",
            worldBibleReferencesJson = "[{\"worldId\":\"00000000-0000-0000-0000-000000000002\",\"scope\":\"primary\"}]",
            visualBibleJson = "{\"palette\":[\"amber\",\"slate\"],\"texture\":\"35mm\"}",
            cinematographyBibleJson = "{\"lens\":\"50mm\",\"movement\":\"restrained\"}",
            audioBibleJson = "{\"dialogue\":\"intimate\",\"music\":\"minimal\"}",
            continuityBibleJson = "{\"props\":[\"red notebook\"],\"screenDirection\":\"preserve\"}",
        });
        Assert.Equal(2, revision.Revision.RevisionNumber);
        Assert.Equal(MovieGuideRevisionStatuses.Draft, revision.Revision.Status);
        Assert.Contains("red notebook", revision.Revision.Sections.Single(item => item.Type == MovieGuideSectionTypes.ContinuityBible).ContentJson, StringComparison.Ordinal);

        var beforeLock = await client.GetAsync($"/api/movie-studio/projects/{created.Project.Id}/director-context");
        Assert.Equal(HttpStatusCode.Conflict, beforeLock.StatusCode);

        var locked = await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/lock", new { revisionNumber = 2 });
        Assert.Equal(2, locked.Revision.RevisionNumber);
        Assert.Equal(MovieGuideRevisionStatuses.Locked, locked.Revision.Status);
        Assert.True(locked.AuthoritativeContext!.IsAuthoritative);

        var director = await client.GetFromJsonAsync<MovieDirectorContextDto>($"/api/movie-studio/projects/{created.Project.Id}/director-context");
        Assert.NotNull(director);
        Assert.True(director!.IsAuthoritative);
        Assert.Equal(2, director.RevisionNumber);
        Assert.Equal(7, director.Sections.Count);

        var rejectedRevision = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/revisions", new { storyBibleJson = "{}" });
        Assert.Equal(HttpStatusCode.Conflict, rejectedRevision.StatusCode);
        Assert.Contains("MOVIE_GUIDE_LOCKED", await rejectedRevision.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var rejectedLegacyUpdate = await SendWithCsrf(client, HttpMethod.Patch, $"/api/movie-studio/projects/{created.Project.Id}/guide", new { visualLanguage = "must not rewrite" });
        Assert.Equal(HttpStatusCode.Conflict, rejectedLegacyUpdate.StatusCode);

        var history = await client.GetFromJsonAsync<MovieGuideHistoryResponse>($"/api/movie-studio/projects/{created.Project.Id}/guide/history");
        Assert.Equal(2, history!.Revisions.Count);
        Assert.Equal(MovieGuideRevisionStatuses.Locked, history.Revisions.Single(item => item.RevisionNumber == 2).Status);
    }

    [Fact]
    public async Task Guide_can_be_unlocked_before_a_new_revision_and_cross_workspace_reads_are_denied()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Movie Guide Isolation Owner");
        var created = await SendWithCsrf<MovieStudioProjectResponse>(owner, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Private Guide",
            description = "Private movie guide.",
            durationSeconds = 30,
            aspectRatio = "16:9",
            style = "documentary",
            language = "en",
        });
        await SendWithCsrf<MovieGuideRevisionResponse>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/lock", new { });

        var unlocked = await SendWithCsrf<MovieGuideHistoryResponse>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/unlock", null);
        Assert.Null(unlocked.LockedRevisionNumber);
        Assert.All(unlocked.Revisions, item => Assert.Equal(MovieGuideRevisionStatuses.Draft, item.Status));

        var afterUnlock = await SendWithCsrf<MovieGuideRevisionResponse>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{created.Project.Id}/guide/revisions", new
        {
            storyBibleJson = "{\"logline\":\"Updated\"}",
            characterBibleReferencesJson = "[]",
            worldBibleReferencesJson = "[]",
            visualBibleJson = "{}",
            cinematographyBibleJson = "{}",
            audioBibleJson = "{}",
            continuityBibleJson = "{}",
        });
        Assert.Equal(2, afterUnlock.Revision.RevisionNumber);

        using var other = factory.CreateClient();
        await Register(other, "Movie Guide Isolation Other");
        var forbiddenHistory = await other.GetAsync($"/api/movie-studio/projects/{created.Project.Id}/guide/history");
        Assert.Equal(HttpStatusCode.NotFound, forbiddenHistory.StatusCode);
        var forbiddenContext = await other.GetAsync($"/api/movie-studio/projects/{created.Project.Id}/director-context");
        Assert.Equal(HttpStatusCode.NotFound, forbiddenContext.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-guide-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

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
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
