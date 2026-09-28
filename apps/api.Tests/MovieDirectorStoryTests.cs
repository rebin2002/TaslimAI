using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorStoryTests : IClassFixture<MovieDirectorStoryApiFactory>
{
    private readonly MovieDirectorStoryApiFactory factory;

    public MovieDirectorStoryTests(MovieDirectorStoryApiFactory factory)
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
    public async Task Empty_story_develop_premise_targets_same_movie_project_and_creates_first_ai_revision_after_approval()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Empty Story Director Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        await LockGuide(client, movie.Project.Id);

        var proposal = await SendWithCsrf<DirectorProposalResponse>(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new
        {
            storyAction = DirectorStoryActionTypes.DevelopPremise,
        });

        Assert.Equal(movie.Project.Id, proposal.Proposal.MovieProjectId);
        Assert.Equal(DirectorProposalStatuses.PendingApproval, proposal.Proposal.Status);
        Assert.NotNull(proposal.Proposal.StoryReview);
        Assert.Null(proposal.Proposal.StoryReview!.BaseRevisionId);
        Assert.NotNull(proposal.StoryContext);

        var beforeApproval = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-director/actions/{proposal.Proposal.Actions[0].Id}/execute", null);
        Assert.Equal(HttpStatusCode.Conflict, beforeApproval.StatusCode);
        Assert.Contains("DIRECTOR_APPROVAL_REQUIRED", await beforeApproval.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var approved = await SendWithCsrf<DirectorProposalDto>(client, HttpMethod.Post, $"/api/movie-director/proposals/{proposal.Proposal.Id}/approve", null);
        var applied = await SendWithCsrf<DirectorActionExecutionResponse>(client, HttpMethod.Post, $"/api/movie-director/actions/{approved.Actions[0].Id}/execute", null);
        Assert.Equal(DirectorActionStatuses.Succeeded, applied.Action.Status);

        var story = await client.GetFromJsonAsync<MovieStoryDto>($"/api/movie-studio/projects/{movie.Project.Id}/story");
        Assert.NotNull(story);
        Assert.Equal(movie.Project.Id, story!.MovieProjectId);
        Assert.Equal(MovieStoryAuthorship.AiSuggested, story.CurrentRevision!.Authorship);
        Assert.Equal(MovieStoryRevisionStatuses.Draft, story.CurrentRevision.Status);
        Assert.Contains("bounded Story Director test", story.CurrentRevision.Premise, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_story_without_locked_guide_returns_actionable_prerequisite_not_project_not_found()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Unlocked Story Director Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new
        {
            storyAction = DirectorStoryActionTypes.DevelopPremise,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Lock the Movie Guide before creating a Story Director proposal.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Movie project not found", body, StringComparison.OrdinalIgnoreCase);
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

    internal static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId)
    {
        return await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId, mode = MovieProjectModes.Full, title = $"Director Story {Guid.NewGuid():N}", description = "A bounded Story Director test.", durationSeconds = 120, aspectRatio = "16:9", style = "cinematic", language = "en",
        });
    }

    internal static async Task LockGuide(HttpClient client, Guid projectId)
    {
        await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/guide/lock", new { });
    }

    internal static async Task<MovieStoryDto> CreateStory(HttpClient client, Guid projectId, string authorship)
    {
        return await SendWithCsrf<MovieStoryDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/story/revisions", new
        {
            premise = "A guarded witness must choose truth over safety.", logline = "A guarded witness must choose truth over safety before the city closes its borders.", synopsis = "The witness follows a clue through a changing city.", treatment = "The witness moves from secrecy to an irreversible public choice.", authorship, scenes = Array.Empty<object>(),
        });
    }

    internal static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"director-story-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    internal static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    internal static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

public sealed class MovieDirectorStoryApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, ValidMovieStoryCompletionService>();
        });
    }
}

internal sealed class ValidMovieStoryCompletionService : IChatCompletionService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiGenerationResult(
            "{\"premise\":\"AI generated premise for the bounded Story Director test.\",\"logline\":\"AI generated logline with a consequential choice.\",\"synopsis\":\"AI generated synopsis with a clear escalation.\",\"treatment\":\"AI generated treatment preserving the locked guide.\",\"replacementContent\":\"AI generated replacement passage.\",\"findings\":[],\"proposedScene\":{\"sceneIdentifier\":\"AI-SCENE-1\",\"actNumber\":1,\"sequenceNumber\":1,\"slugline\":\"INT. STORY ROOM - NIGHT\",\"synopsis\":\"AI generated scene synopsis.\",\"elements\":[{\"elementType\":\"Action\",\"content\":\"The choice becomes unavoidable.\"},{\"elementType\":\"Dialogue\",\"characterName\":\"PROTAGONIST\",\"content\":\"Then we do it now.\"}]}}",
            new AiUsageMetadata("test-story", "test-story", 100, null, 200, 0m, 0m, 1, "test-complete", true)));
}

public sealed class MovieDirectorStoryFailureTests : IClassFixture<UnavailableMovieDirectorStoryApiFactory>
{
    private readonly UnavailableMovieDirectorStoryApiFactory factory;

    public MovieDirectorStoryFailureTests(UnavailableMovieDirectorStoryApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Unavailable_story_ai_does_not_mutate_story_or_create_a_fake_revision()
    {
        using var client = factory.CreateClient();
        var auth = await MovieDirectorStoryTests.Register(client, "Unavailable Story Owner");
        var movie = await MovieDirectorStoryTests.CreateMovie(client, auth.PersonalWorkspace.Id);
        await MovieDirectorStoryTests.LockGuide(client, movie.Project.Id);
        var original = await MovieDirectorStoryTests.CreateStory(client, movie.Project.Id, "Human");

        var response = await MovieDirectorStoryTests.SendWithCsrf(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { storyAction = DirectorStoryActionTypes.DevelopPremise });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var story = await client.GetFromJsonAsync<MovieStoryDto>($"/api/movie-studio/projects/{movie.Project.Id}/story");
        Assert.Equal(original.CurrentRevisionId, story!.CurrentRevisionId);
        Assert.Equal(original.Revisions.Count, story.Revisions.Count);
    }
}

public sealed class UnavailableMovieDirectorStoryApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, UnavailableMovieStoryCompletionService>();
        });
    }
}

internal sealed class UnavailableMovieStoryCompletionService : IChatCompletionService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => throw new AiProviderUnavailableException();
}
