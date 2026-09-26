using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorStoryTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieDirectorStoryTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Story_proposal_requires_approval_then_creates_ai_suggested_editable_revision()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Story Director Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        await LockGuide(client, movie.Project.Id);
        var original = await CreateStory(client, movie.Project.Id, "Human");
        await SendWithCsrf<MovieStoryRevisionDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions/{original.CurrentRevisionId}/approve", null);

        var proposal = await SendWithCsrf<DirectorProposalResponse>(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new
        {
            storyAction = DirectorStoryActionTypes.ImproveLogline,
        });
        Assert.Equal(DirectorProposalStatuses.PendingApproval, proposal.Proposal.Status);
        Assert.NotNull(proposal.Proposal.StoryReview);
        Assert.Equal("logline", proposal.Proposal.StoryReview!.Changes[0].Field);
        Assert.Equal(DirectorActionStatuses.PendingApproval, proposal.Proposal.Actions[0].Status);
        Assert.NotNull(proposal.StoryContext);

        var beforeApproval = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-director/actions/{proposal.Proposal.Actions[0].Id}/execute", null);
        Assert.Equal(HttpStatusCode.Conflict, beforeApproval.StatusCode);
        Assert.Contains("DIRECTOR_APPROVAL_REQUIRED", await beforeApproval.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var approved = await SendWithCsrf<DirectorProposalDto>(client, HttpMethod.Post, $"/api/movie-director/proposals/{proposal.Proposal.Id}/approve", null);
        Assert.Equal(DirectorProposalStatuses.Approved, approved.Status);
        Assert.Equal(DirectorActionStatuses.Ready, approved.Actions[0].Status);

        var applied = await SendWithCsrf<DirectorActionExecutionResponse>(client, HttpMethod.Post, $"/api/movie-director/actions/{approved.Actions[0].Id}/execute", null);
        Assert.Equal(DirectorActionStatuses.Succeeded, applied.Action.Status);
        var story = await client.GetFromJsonAsync<MovieStoryDto>($"/api/movie-studio/projects/{movie.Project.Id}/story");
        Assert.NotNull(story);
        Assert.Equal(original.CurrentRevisionId, story!.ApprovedRevisionId);
        Assert.NotEqual(story.ApprovedRevisionId, story.CurrentRevisionId);
        Assert.Equal(MovieStoryAuthorship.AiSuggested, story.CurrentRevision!.Authorship);
        Assert.Equal(MovieStoryRevisionStatuses.Draft, story.CurrentRevision.Status);
        Assert.Equal(original.Logline, story.ApprovedRevision!.Logline);
        Assert.NotEqual(story.ApprovedRevision.Logline, story.CurrentRevision.Logline);
    }

    [Fact]
    public async Task Rejected_story_proposal_cancels_action_and_does_not_change_story()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Story Director Rejector");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        await LockGuide(client, movie.Project.Id);
        var storyBefore = await CreateStory(client, movie.Project.Id, "Human");
        var proposal = await SendWithCsrf<DirectorProposalResponse>(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { storyAction = DirectorStoryActionTypes.DevelopPremise });
        var rejected = await SendWithCsrf<DirectorProposalDto>(client, HttpMethod.Post, $"/api/movie-director/proposals/{proposal.Proposal.Id}/reject", null);
        Assert.Equal(DirectorProposalStatuses.Rejected, rejected.Status);
        Assert.Equal(DirectorActionStatuses.Cancelled, rejected.Actions[0].Status);
        var storyAfter = await client.GetFromJsonAsync<MovieStoryDto>($"/api/movie-studio/projects/{movie.Project.Id}/story");
        Assert.Equal(storyBefore.CurrentRevisionId, storyAfter!.CurrentRevisionId);
        Assert.Single(storyAfter.Revisions);
    }

    [Fact]
    public async Task Story_director_proposals_do_not_cross_workspace_boundaries()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Story Director Private Owner");
        var movie = await CreateMovie(owner, auth.PersonalWorkspace.Id);
        await LockGuide(owner, movie.Project.Id);
        using var other = factory.CreateClient();
        await Register(other, "Story Director Private Other");
        var response = await SendWithCsrf(other, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { storyAction = DirectorStoryActionTypes.ImproveLogline });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId)
    {
        return await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId, mode = MovieProjectModes.Full, title = $"Director Story {Guid.NewGuid():N}", description = "A bounded Story Director test.", durationSeconds = 120, aspectRatio = "16:9", style = "cinematic", language = "en",
        });
    }

    private static async Task LockGuide(HttpClient client, Guid projectId)
    {
        await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/guide/lock", new { });
    }

    private static async Task<MovieStoryDto> CreateStory(HttpClient client, Guid projectId, string authorship)
    {
        return await SendWithCsrf<MovieStoryDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/story/revisions", new
        {
            premise = "A guarded witness must choose truth over safety.", logline = "A guarded witness must choose truth over safety before the city closes its borders.", synopsis = "The witness follows a clue through a changing city.", treatment = "The witness moves from secrecy to an irreversible public choice.", authorship, scenes = Array.Empty<object>(),
        });
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"director-story-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
