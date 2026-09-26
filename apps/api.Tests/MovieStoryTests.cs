using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieStoryTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieStoryTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Story_revision_persists_structured_screenplay_and_approval_pointer()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"story-owner-{Guid.NewGuid():N}@example.com");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Story Foundation Movie");
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new
        {
            title = "The first threshold",
            summary = "The protagonist crosses into the unknown.",
        });

        var revisionResponse = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions", new
        {
            premise = "A courier must deliver a message before dawn.",
            logline = "When the city goes dark, a reluctant courier crosses a hostile district to deliver the one message that can stop a war.",
            synopsis = "The courier discovers that the message is connected to their missing sibling.",
            treatment = "Act one establishes the blackout and the impossible delivery. The middle turns the route into a moral test. The ending forces a public choice.",
            authorship = "Human",
            changeSummary = "First human story pass",
            scenes = new[]
            {
                new
                {
                    sceneIdentifier = "ACT-1-SEQUENCE-1-SCENE-1",
                    actNumber = 1,
                    sequenceNumber = 1,
                    movieSceneId = scene.Id,
                    slugline = "EXT. OLD CITY GATE - NIGHT",
                    synopsis = "The courier arrives as the lights fail.",
                    elements = new object[]
                    {
                        new { elementType = "Action", content = "The last streetlamp flickers out." },
                        new { elementType = "Dialogue", content = "We have one hour.", characterName = "MARA", parenthetical = "quietly" },
                    },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, revisionResponse.StatusCode);
        var story = await revisionResponse.Content.ReadFromJsonAsync<MovieStoryDto>();
        Assert.NotNull(story);
        Assert.Equal(movie.Project.Id, story!.MovieProjectId);
        Assert.Equal("When the city goes dark, a reluctant courier crosses a hostile district to deliver the one message that can stop a war.", story.Logline);
        Assert.NotNull(story.CurrentRevision);
        Assert.Equal(1, story.CurrentRevision!.RevisionNumber);
        Assert.Equal("ACT-1-SEQUENCE-1-SCENE-1", story.CurrentRevision.Scenes[0].SceneIdentifier);
        Assert.Equal(scene.Id, story.CurrentRevision.Scenes[0].MovieSceneId);
        Assert.Equal("Dialogue", story.CurrentRevision.Scenes[0].Elements[1].ElementType);
        Assert.Equal("MARA", story.CurrentRevision.Scenes[0].Elements[1].CharacterName);

        var submitted = await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{story.CurrentRevisionId}/submit", null);
        Assert.Equal("Submitted", submitted.Status);
        var approved = await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{story.CurrentRevisionId}/approve", null);
        Assert.Equal("Approved", approved.Status);

        var approvedStory = await client.GetFromJsonAsync<MovieStoryDto>($"/api/movie-studio/projects/{movie.Project.Id}/story");
        Assert.NotNull(approvedStory);
        Assert.Equal("Approved", approvedStory!.ApprovalState);
        Assert.Equal(approved.Id, approvedStory.ApprovedRevisionId);
        Assert.Equal(approved.Id, approvedStory.CurrentRevisionId);
    }

    [Fact]
    public async Task Approved_revision_is_immutable_and_new_human_edit_is_a_new_revision()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"story-revision-owner-{Guid.NewGuid():N}@example.com");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Revision History Movie");
        var first = await CreateRevision(client, movie.Project.Id, "Human");
        await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{first.CurrentRevisionId}/approve", null);

        var second = await CreateRevision(client, movie.Project.Id, "HumanEdited");
        Assert.Equal(2, second.CurrentRevision!.RevisionNumber);
        Assert.Equal(first.CurrentRevisionId, second.CurrentRevision!.ParentRevisionId);

        var immutable = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{first.CurrentRevisionId}/reject", new { reason = "Use the new pass." });
        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
        var body = await immutable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_STORY_REVISION_IMMUTABLE", body.GetProperty("error").GetProperty("code").GetString());

        var revisions = await client.GetFromJsonAsync<List<MovieStoryRevisionSummaryDto>>($"/api/movie-studio/projects/{movie.Project.Id}/story/revisions");
        Assert.NotNull(revisions);
        Assert.Contains(revisions!, item => item.Id == first.CurrentRevisionId && item.Status == "Approved");
        Assert.Contains(revisions!, item => item.Id == second.CurrentRevisionId && item.Status == "Draft");
    }

    [Fact]
    public async Task Story_endpoints_do_not_cross_workspace_boundaries()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, $"story-private-owner-{Guid.NewGuid():N}@example.com");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Private Story Movie");
        using var other = factory.CreateClient();
        await Register(other, $"story-private-other-{Guid.NewGuid():N}@example.com");

        var response = await SendWithCsrf(other, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions", new
        {
            premise = "Private premise", logline = "Private logline", synopsis = "Private synopsis", treatment = "Private treatment", scenes = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Draft_can_be_saved_without_overwriting_the_approved_revision()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"story-draft-owner-{Guid.NewGuid():N}@example.com");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Draft safety movie");
        var first = await CreateRevision(client, movie.Project.Id, MovieStoryAuthorship.Human);
        var approved = await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{first.CurrentRevisionId}/approve", null);
        var second = await CreateRevision(client, movie.Project.Id, MovieStoryAuthorship.HumanEdited);

        var updated = await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Patch, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{second.CurrentRevisionId}", new
        {
            premise = "The approved premise remains untouched.",
            logline = "A new draft tests a more dangerous choice before the city closes its borders.",
            synopsis = "The witness follows a new clue through a changing city.",
            treatment = "The witness moves from secrecy to an irreversible public choice.",
            authorship = MovieStoryAuthorship.HumanEdited,
            changeSummary = "Sharper second pass",
            scenes = Array.Empty<object>(),
        });

        Assert.Equal(second.CurrentRevisionId, updated.Id);
        Assert.Equal("Draft", updated.Status);
        var story = await client.GetFromJsonAsync<MovieStoryDto>($"/api/movie-studio/projects/{movie.Project.Id}/story");
        Assert.NotNull(story);
        Assert.Equal(approved.Id, story!.ApprovedRevisionId);
        Assert.Equal("Approved", story.ApprovedRevision!.Status);
        Assert.Equal("A guarded witness must choose truth over safety before the city closes its borders.", story.ApprovedRevision.Logline);
        Assert.Equal(updated.Id, story.CurrentRevisionId);
        Assert.Equal("A new draft tests a more dangerous choice before the city closes its borders.", story.CurrentRevision!.Logline);

        var immutable = await SendWithCsrf(client, HttpMethod.Patch, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{approved.Id}", new
        {
            premise = "No overwrite", logline = "No overwrite", synopsis = "No overwrite", treatment = "No overwrite", authorship = MovieStoryAuthorship.Human, scenes = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
    }

    [Fact]
    public async Task Story_approval_requires_movie_approval_permission()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, $"story-approval-owner-{Guid.NewGuid():N}@example.com");
        var movie = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Approval authority movie");
        var draft = await CreateRevision(owner, movie.Project.Id, MovieStoryAuthorship.Human);
        using var writer = factory.CreateClient();
        var writerAuth = await Register(writer, $"story-approval-writer-{Guid.NewGuid():N}@example.com");
        await AddWorkspaceMember(ownerAuth.PersonalWorkspace.Id, writerAuth.User.Id);
        var teamResponse = await SendWithCsrf<MovieTeamMemberDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/collaboration/team", new
        {
            userId = writerAuth.User.Id,
            role = MovieTeamRoles.Writer,
            permissions = Array.Empty<string>(),
        });
        Assert.DoesNotContain(MoviePermissions.Approve, teamResponse.Permissions);

        var forbidden = await SendWithCsrf(writer, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{draft.CurrentRevisionId}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var approved = await SendWithCsrf<MovieStoryRevisionDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{draft.CurrentRevisionId}/approve", null);
        Assert.Equal(MovieStoryRevisionStatuses.Approved, approved.Status);
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string title)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId, mode = "Full", title, description = "A movie for story foundation tests.", durationSeconds = 120, aspectRatio = "16:9", style = "cinematic", language = "en",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MovieStudioProjectResponse>())!;
    }

    private static async Task<MovieStoryDto> CreateRevision(HttpClient client, Guid movieProjectId, string authorship)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{movieProjectId}/story/revisions", new
        {
            premise = "A guarded witness must choose truth over safety.", logline = "A guarded witness must choose truth over safety before the city closes its borders.", synopsis = "The witness follows a clue through a changing city.", treatment = "The witness moves from secrecy to an irreversible public choice.", authorship, scenes = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MovieStoryDto>())!;
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Story Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddWorkspaceMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
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
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
