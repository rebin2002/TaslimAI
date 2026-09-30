using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorScenePlanningTests : IClassFixture<ScenePlanApiFactory>
{
    private readonly ScenePlanApiFactory factory;

    public MovieDirectorScenePlanningTests(ScenePlanApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Grounded_thirty_second_scene_plan_is_reviewable_and_apply_is_explicit()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Scene Plan Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        await CreateCharacter(client, movie.Project.Id);
        await CreateStory(client, movie.Project.Id);
        await LockGuide(client, movie.Project.Id);

        var proposal = await SendWithCsrf<DirectorProposalResponse>(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new
        {
            scenePlanAction = DirectorScenePlanActionTypes.PlanScenes,
            requestedQuality = DirectorQualityLevels.Standard,
        });

        Assert.Equal(DirectorProposalStatuses.PendingApproval, proposal.Proposal.Status);
        Assert.Equal(DirectorActionTypes.ScenePlanning, proposal.Proposal.Actions.Single().ActionType);
        Assert.NotNull(proposal.Proposal.ScenePlan);
        Assert.Equal(30, proposal.Proposal.ScenePlan!.TargetDurationSeconds);
        Assert.Equal(30, proposal.Proposal.ScenePlan.TotalEstimatedDurationSeconds);
        Assert.InRange(proposal.Proposal.ScenePlan.Scenes.Count, 1, 6);
        Assert.Contains(proposal.Proposal.ScenePlan.Scenes, item => item.ParticipatingCharacters.Contains("Mara"));
        Assert.Empty((await client.GetFromJsonAsync<MovieStudioProjectDto>($"/api/movie-studio/projects/{movie.Project.Id}"))!.Scenes);

        var approved = await SendWithCsrf<DirectorProposalDto>(client, HttpMethod.Post, $"/api/movie-director/proposals/{proposal.Proposal.Id}/approve", null);
        Assert.Equal(DirectorProposalStatuses.Approved, approved.Status);
        Assert.Equal(DirectorActionStatuses.Ready, approved.Actions.Single().Status);
        Assert.Empty((await client.GetFromJsonAsync<MovieStudioProjectDto>($"/api/movie-studio/projects/{movie.Project.Id}"))!.Scenes);

        var applied = await SendWithCsrf<DirectorActionExecutionResponse>(client, HttpMethod.Post, $"/api/movie-director/actions/{approved.Actions.Single().Id}/execute", null);
        Assert.Equal(DirectorActionStatuses.Succeeded, applied.Action.Status);
        Assert.Contains("applied", applied.Result.SafeMessage, StringComparison.OrdinalIgnoreCase);

        var after = await client.GetFromJsonAsync<MovieStudioProjectDto>($"/api/movie-studio/projects/{movie.Project.Id}");
        Assert.NotNull(after);
        Assert.Equal(3, after.Scenes.Count);
        Assert.Equal(30, after.Scenes.Sum(item => item.DurationSeconds ?? 0));
        Assert.Contains(after.Scenes, item => item.Title == "Seed at Dawn");
    }

    [Fact]
    public async Task Scene_plan_requires_workspace_authorization()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Private Scene Plan Owner");
        var movie = await CreateMovie(owner, auth.PersonalWorkspace.Id);
        await CreateStory(owner, movie.Project.Id);
        await LockGuide(owner, movie.Project.Id);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Scene Plan Outsider");
        var response = await SendWithCsrf(outsider, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { scenePlanAction = DirectorScenePlanActionTypes.PlanScenes });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Stale_scene_plan_is_rejected_at_apply_without_creating_scenes()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Stale Scene Plan Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        await CreateCharacter(client, movie.Project.Id);
        await CreateStory(client, movie.Project.Id);
        await LockGuide(client, movie.Project.Id);
        var proposal = await SendWithCsrf<DirectorProposalResponse>(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { scenePlanAction = DirectorScenePlanActionTypes.PlanScenes });
        var approved = await SendWithCsrf<DirectorProposalDto>(client, HttpMethod.Post, $"/api/movie-director/proposals/{proposal.Proposal.Id}/approve", null);

        await SendWithCsrf<MovieStoryDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/story/revisions", new
        {
            premise = "The last seed waits under a changed sky.", logline = "Mara protects the last seed after the guide changes.", synopsis = "The last seed becomes a public choice.", treatment = "Mara carries the last seed through the changed riverbed.", authorship = MovieStoryAuthorship.Human, scenes = Array.Empty<object>(),
        });

        var applied = await SendWithCsrf<DirectorActionExecutionResponse>(client, HttpMethod.Post, $"/api/movie-director/actions/{approved.Actions.Single().Id}/execute", null);
        Assert.Equal(DirectorActionStatuses.Failed, applied.Action.Status);
        Assert.Equal("DIRECTOR_SCENE_PLAN_STALE", applied.Action.FailureCode);
        Assert.Empty((await client.GetFromJsonAsync<MovieStudioProjectDto>($"/api/movie-studio/projects/{movie.Project.Id}"))!.Scenes);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"scene-plan-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId) => await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
    {
        workspaceId, mode = MovieProjectModes.Full, title = $"Last Seed {Guid.NewGuid():N}", description = "Mara protects the last seed at the dry riverbed.", durationSeconds = 30, aspectRatio = "16:9", style = "cinematic", language = "en",
        visualLanguage = "blue hour naturalism", cameraLanguage = "slow observational movement", colorAndLighting = "blue hour light", soundAndNarration = "wind and restrained silence", continuityRules = "The last seed remains in Mara's left hand.",
    });

    private static async Task CreateCharacter(HttpClient client, Guid projectId) => await SendWithCsrf<MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/characters", new { name = "Mara", description = "A careful seed keeper.", appearance = "Dust-marked jacket." });

    private static async Task<MovieStoryDto> CreateStory(HttpClient client, Guid projectId) =>
        await SendWithCsrf<MovieStoryDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/story/revisions", new
        {
            premise = "Mara protects the last seed at the dry riverbed before the drought closes the valley.",
            logline = "Mara must carry the last seed through the dry riverbed before dawn.",
            synopsis = "Mara discovers the last seed, crosses the dry riverbed, and chooses to share its future with the valley.",
            treatment = "Mara moves from guarded protection to a public act of renewal around the last seed.",
            authorship = MovieStoryAuthorship.Human,
            scenes = Array.Empty<object>(),
        });

    private static async Task LockGuide(HttpClient client, Guid projectId) => await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{projectId}/guide/lock", new { });

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

public sealed class ScenePlanApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, ValidScenePlanCompletionService>();
        });
    }
}

internal sealed class ValidScenePlanCompletionService : IChatCompletionService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        var content = JsonSerializer.Serialize(new
        {
            language = "en",
            aspectRatio = "16:9",
            targetDurationSeconds = 30,
            scenes = new[]
            {
                new { sequence = 1, title = "Seed at Dawn", narrativePurpose = "Mara protects the last seed.", storyBeat = "Mara discovers the last seed at the dry riverbed.", locationEnvironment = "Dry riverbed", timeOfDay = "Dawn", participatingCharacters = new[] { "Mara" }, emotionalObjective = "Move from fear to responsibility.", estimatedDurationSeconds = 8, transitionRelationship = "Open from silence into wind.", continuityRequirements = new[] { "The last seed remains in Mara's left hand." }, productionIntent = "A restrained close beginning with blue hour light.", sourceScreenplaySceneId = (Guid?)null, existingMovieSceneId = (Guid?)null },
                new { sequence = 2, title = "Crossing", narrativePurpose = "Mara carries the last seed through danger.", storyBeat = "Mara crosses the dry riverbed before dawn.", locationEnvironment = "Dry riverbed", timeOfDay = "Early morning", participatingCharacters = new[] { "Mara" }, emotionalObjective = "Make the choice costly.", estimatedDurationSeconds = 12, transitionRelationship = "Wind carries the movement forward.", continuityRequirements = new[] { "The last seed stays protected in Mara's left hand." }, productionIntent = "A measured tracking passage with observational movement.", sourceScreenplaySceneId = (Guid?)null, existingMovieSceneId = (Guid?)null },
                new { sequence = 3, title = "Shared Future", narrativePurpose = "Mara chooses renewal for the valley.", storyBeat = "Mara shares the last seed's future with the valley.", locationEnvironment = "Dry riverbed", timeOfDay = "Morning", participatingCharacters = new[] { "Mara" }, emotionalObjective = "Resolve fear as collective hope.", estimatedDurationSeconds = 10, transitionRelationship = "Resolve on the sound of wind.", continuityRequirements = new[] { "The seed is still accounted for before the final choice." }, productionIntent = "Hold the final image in blue hour naturalism without spectacle.", sourceScreenplaySceneId = (Guid?)null, existingMovieSceneId = (Guid?)null },
            },
        });
        return Task.FromResult(new AiGenerationResult(content, new AiUsageMetadata("scene-plan-test", "scene-plan-test", 100, null, 200, 0m, 0m, 1, "test-complete", true)));
    }
}
