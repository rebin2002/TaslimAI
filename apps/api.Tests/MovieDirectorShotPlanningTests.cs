using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorShotPlanningTests : IClassFixture<MovieDirectorShotPlanningApiFactory>
{
    private readonly MovieDirectorShotPlanningApiFactory factory;
    public MovieDirectorShotPlanningTests(MovieDirectorShotPlanningApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Proposal_contains_production_ready_grounded_shots_then_explicit_apply_appends_without_generation()
    {
        using var client = factory.CreateClient();
        var auth = await MovieDirectorStoryTests.Register(client, "Shot Director");
        var movie = await MovieDirectorStoryTests.CreateMovie(client, auth.PersonalWorkspace.Id, 30);
        await MovieDirectorStoryTests.LockGuide(client, movie.Project.Id);
        var scene = await MovieDirectorStoryTests.SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Harbor", summary = "Mara guards the red lantern at the harbor.", durationSeconds = 12 });

        var proposal = await MovieDirectorStoryTests.SendWithCsrf<DirectorProposalResponse>(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { shotPlanningAction = DirectorShotPlanningActionTypes.ProposeShots, targetSceneId = scene.Id, requestedShotCount = 2 });
        Assert.Equal(DirectorProposalStatuses.PendingApproval, proposal.Proposal.Status);
        Assert.Equal(2, proposal.Proposal.ShotPlan!.Count);
        Assert.All(proposal.Proposal.ShotPlan, shot =>
        {
            Assert.NotEmpty(shot.NarrativePurpose); Assert.NotEmpty(shot.ShotSize); Assert.NotEmpty(shot.Framing); Assert.NotEmpty(shot.CameraAngle);
            Assert.NotEmpty(shot.CameraMovement); Assert.NotEmpty(shot.Subject); Assert.NotEmpty(shot.CharacterAction); Assert.NotEmpty(shot.ExpressionEmotionalState);
            Assert.NotEmpty(shot.Environment); Assert.NotEmpty(shot.Composition); Assert.NotEmpty(shot.LightingIntent); Assert.NotEmpty(shot.DepthBackgroundIntent);
            Assert.NotEmpty(shot.TransitionRelationship); Assert.NotEmpty(shot.DialogueAudioDependency); Assert.NotEmpty(shot.ContinuityRequirements);
            Assert.NotEmpty(shot.VfxRequirements); Assert.NotEmpty(shot.ProductionNotes); Assert.NotEmpty(shot.GroundingEvidence);
        });
        Assert.True(proposal.Proposal.ShotPlan.Sum(item => item.EstimatedDurationSeconds) <= 12);
        Assert.Equal(DirectorShotPlanningActionTypes.ProposeShots, proposal.Proposal.Actions[0].ActionType);
        Assert.Equal(DirectorActionStatuses.PendingApproval, proposal.Proposal.Actions[0].Status);

        var beforeApproval = await MovieDirectorStoryTests.SendWithCsrf(client, HttpMethod.Post, $"/api/movie-director/actions/{proposal.Proposal.Actions[0].Id}/execute", null);
        Assert.Equal(HttpStatusCode.Conflict, beforeApproval.StatusCode);
        var approved = await MovieDirectorStoryTests.SendWithCsrf<DirectorProposalDto>(client, HttpMethod.Post, $"/api/movie-director/proposals/{proposal.Proposal.Id}/approve", null);
        Assert.Equal(DirectorActionStatuses.Ready, approved.Actions[0].Status);
        var applied = await MovieDirectorStoryTests.SendWithCsrf<DirectorActionExecutionResponse>(client, HttpMethod.Post, $"/api/movie-director/actions/{approved.Actions[0].Id}/execute", null);
        Assert.Equal(DirectorActionStatuses.Succeeded, applied.Action.Status);
        Assert.Contains("No media generation", applied.Result.SafeMessage, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var shots = await db.MovieShots.Where(item => item.MovieSceneId == scene.Id).OrderBy(item => item.Sequence).ToListAsync();
        Assert.Equal(2, shots.Count);
        Assert.All(shots, shot => Assert.False(string.IsNullOrWhiteSpace(shot.CinematographyJson)));
        Assert.Empty(await db.GenerationJobs.Where(item => item.WorkspaceId == auth.PersonalWorkspace.Id).ToListAsync());
    }

    [Fact]
    public async Task Approved_plan_is_rejected_as_stale_after_scene_changes_and_does_not_mutate_shots()
    {
        using var client = factory.CreateClient();
        var auth = await MovieDirectorStoryTests.Register(client, "Stale Plan Director");
        var movie = await MovieDirectorStoryTests.CreateMovie(client, auth.PersonalWorkspace.Id, 30);
        await MovieDirectorStoryTests.LockGuide(client, movie.Project.Id);
        var scene = await MovieDirectorStoryTests.SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Harbor", summary = "Mara guards the red lantern at the harbor.", durationSeconds = 12 });
        var proposal = await MovieDirectorStoryTests.SendWithCsrf<DirectorProposalResponse>(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { shotPlanningAction = DirectorShotPlanningActionTypes.ProposeShots, targetSceneId = scene.Id });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var storedScene = await db.MovieScenes.SingleAsync(item => item.Id == scene.Id);
            storedScene.Summary = "Mara no longer guards the red lantern; the harbor is empty.";
            await db.SaveChangesAsync();
        }
        var approved = await MovieDirectorStoryTests.SendWithCsrf<DirectorProposalDto>(client, HttpMethod.Post, $"/api/movie-director/proposals/{proposal.Proposal.Id}/approve", null);
        var applied = await MovieDirectorStoryTests.SendWithCsrf<DirectorActionExecutionResponse>(client, HttpMethod.Post, $"/api/movie-director/actions/{approved.Actions[0].Id}/execute", null);
        Assert.Equal(DirectorActionStatuses.Failed, applied.Action.Status);
        Assert.Equal(DirectorShotPlanningFailureCodes.Stale, applied.Action.FailureCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await verifyDb.MovieShots.Where(item => item.MovieSceneId == scene.Id).ToListAsync());
    }

    [Fact]
    public async Task Shot_planning_requires_director_proposal_authorization()
    {
        using var owner = factory.CreateClient();
        var auth = await MovieDirectorStoryTests.Register(owner, "Private Shot Owner");
        var movie = await MovieDirectorStoryTests.CreateMovie(owner, auth.PersonalWorkspace.Id, 30);
        await MovieDirectorStoryTests.LockGuide(owner, movie.Project.Id);
        var scene = await MovieDirectorStoryTests.SendWithCsrf<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Harbor", summary = "Mara guards the red lantern at the harbor.", durationSeconds = 12 });
        using var other = factory.CreateClient();
        await MovieDirectorStoryTests.Register(other, "Unauthorized Shot Planner");
        var response = await MovieDirectorStoryTests.SendWithCsrf(other, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { shotPlanningAction = DirectorShotPlanningActionTypes.ProposeShots, targetSceneId = scene.Id });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class MovieDirectorShotPlanningFailureTests : IClassFixture<MovieDirectorShotPlanningFailureApiFactory>
{
    private readonly MovieDirectorShotPlanningFailureApiFactory factory;
    public MovieDirectorShotPlanningFailureTests(MovieDirectorShotPlanningFailureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Unavailable_shot_planning_does_not_create_a_proposal_or_fake_shot()
    {
        using var client = factory.CreateClient();
        var auth = await MovieDirectorStoryTests.Register(client, "Unavailable Shot Director");
        var movie = await MovieDirectorStoryTests.CreateMovie(client, auth.PersonalWorkspace.Id, 30);
        await MovieDirectorStoryTests.LockGuide(client, movie.Project.Id);
        var scene = await MovieDirectorStoryTests.SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Harbor", summary = "Mara guards the red lantern at the harbor.", durationSeconds = 12 });
        var response = await MovieDirectorStoryTests.SendWithCsrf(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { shotPlanningAction = DirectorShotPlanningActionTypes.ProposeShots, targetSceneId = scene.Id });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(DirectorShotPlanningFailureCodes.Unavailable, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await db.DirectorProposals.Where(item => item.MovieProjectId == movie.Project.Id).ToListAsync());
        Assert.Empty(await db.MovieShots.Where(item => item.MovieSceneId == scene.Id).ToListAsync());
    }
}

public sealed class MovieDirectorShotPlanningMalformedTests : IClassFixture<MovieDirectorShotPlanningMalformedApiFactory>
{
    private readonly MovieDirectorShotPlanningMalformedApiFactory factory;
    public MovieDirectorShotPlanningMalformedTests(MovieDirectorShotPlanningMalformedApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Malformed_or_generic_shot_output_is_rejected_without_fake_content()
    {
        using var client = factory.CreateClient();
        var auth = await MovieDirectorStoryTests.Register(client, "Malformed Shot Director");
        var movie = await MovieDirectorStoryTests.CreateMovie(client, auth.PersonalWorkspace.Id, 30);
        await MovieDirectorStoryTests.LockGuide(client, movie.Project.Id);
        var scene = await MovieDirectorStoryTests.SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Harbor", summary = "Mara guards the red lantern at the harbor.", durationSeconds = 12 });
        var response = await MovieDirectorStoryTests.SendWithCsrf(client, HttpMethod.Post, $"/api/movie-director/projects/{movie.Project.Id}/proposals", new { shotPlanningAction = DirectorShotPlanningActionTypes.ProposeShots, targetSceneId = scene.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(DirectorShotPlanningFailureCodes.Invalid, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

public abstract class MovieDirectorShotPlanningFactoryBase : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, ValidMovieShotCompletionService>();
        });
    }
}
public sealed class MovieDirectorShotPlanningApiFactory : MovieDirectorShotPlanningFactoryBase;

public sealed class MovieDirectorShotPlanningFailureApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, UnavailableMovieShotCompletionService>();
        });
    }
}
public sealed class MovieDirectorShotPlanningMalformedApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, MalformedMovieShotCompletionService>();
        });
    }
}

internal sealed class ValidMovieShotCompletionService : IChatCompletionService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        if (request.StructuredOutput?.Name != "taslim_movie_director_shot_planning") return Task.FromResult(new AiGenerationResult("{}", Usage()));
        var content = JsonSerializer.Serialize(new
        {
            summary = "A grounded two-shot plan follows Mara's guarded watch over the red lantern at the harbor.",
            rationale = new[] { "Uses the selected Harbor scene and its named red lantern continuity anchor." },
            shots = new[] { Shot(1, 5, "Mara's watch turns the harbor tension into a readable visual beat."), Shot(2, 5, "The red lantern becomes the transition hinge into the next scene.") },
        });
        return Task.FromResult(new AiGenerationResult(content, Usage()));
    }
    private static AiUsageMetadata Usage() => new("test-shot", "test-shot", 100, null, 300, 0m, 0m, 1, "test-shot", true);
    private static object Shot(int number, int duration, string purpose) => new
    {
        shotNumber = number, narrativePurpose = purpose, estimatedDurationSeconds = duration,
        shotSize = number == 1 ? "medium" : "close-up", framing = "Mara and the red lantern held on the harbor axis", cameraAngle = "eye level",
        cameraMovement = number == 1 ? "measured lateral drift" : "controlled push-in", subject = "Mara and the red lantern",
        characterAction = number == 1 ? "Mara checks the lantern and scans the harbor." : "Mara steadies the lantern as the harbor falls quiet.",
        expressionEmotionalState = number == 1 ? "guarded vigilance" : "quiet resolve", environment = "the harbor", importantProps = new[] { "red lantern" },
        composition = "Mara anchors the foreground while the lantern creates a warm counterpoint against open harbor depth.",
        lightingIntent = "cool ambient harbor light with the red lantern as the motivated warm source",
        depthBackgroundIntent = "keep the harbor readable in layered background depth without inventing a new location",
        transitionRelationship = number == 1 ? "establishes the scene's watchful rhythm" : "hands visual emphasis to the next scene through the lantern's held glow",
        dialogueAudioDependency = "No dialogue; preserve harbor ambience and the lantern's subtle handling sound.",
        continuityRequirements = "Keep Mara, the red lantern, and the harbor relationship consistent with the selected scene.",
        vfxRequirements = "No VFX required beyond practical light continuity.",
        productionNotes = "Proposed shot-plan detail; verify the practical lantern and harbor sound before approval.",
        groundingEvidence = new[] { "Mara guards the red lantern at the harbor." },
    };
}
internal sealed class UnavailableMovieShotCompletionService : IChatCompletionService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => throw new AiProviderUnavailableException();
}
internal sealed class MalformedMovieShotCompletionService : IChatCompletionService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AiGenerationResult("{\"summary\":\"Opening shot\",\"rationale\":[],\"shots\":[]}", new AiUsageMetadata("test-shot", "test-shot", 10, null, 10, 0m, 0m, 1, "test-shot", true)));
}

public sealed class MovieDirectorShotPlanningValidatorTests
{
    [Fact]
    public void Proposed_duration_cannot_exceed_remaining_scene_runtime()
    {
        var context = Context(10);
        var exception = Assert.Throws<DirectorShotPlanningException>(() => DirectorShotPlanningValidator.ValidateAndNormalize([Shot(1, 11)], context, 1, ["Harbor"]));
        Assert.Equal(DirectorShotPlanningFailureCodes.Invalid, exception.Code);
        Assert.Contains("runtime", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Deterministic_fake_shot_language_is_rejected()
    {
        var context = Context(20);
        var fake = Shot(1, 4) with { CharacterAction = "Character walks into frame." };
        var exception = Assert.Throws<DirectorShotPlanningException>(() => DirectorShotPlanningValidator.ValidateAndNormalize([fake], context, 1, ["Harbor"]));
        Assert.Equal(DirectorShotPlanningFailureCodes.Invalid, exception.Code);
        Assert.Contains("generic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    private static MovieShotPlanningContext Context(int duration)
    {
        var scene = new DirectorSceneContext(Guid.NewGuid(), 1, "Harbor", "Mara guards the red lantern at the harbor.", duration, null, []);
        var project = new DirectorContextDto(Guid.NewGuid(), Guid.NewGuid(), "Test movie", "A bounded movie.", duration, "16:9", "cinematic", "en", new DirectorGuideContext("visual", "camera", "light", "sound", "Mara and the harbor are locked."), [scene], [], [], DateTime.UnixEpoch);
        return new MovieShotPlanningContext(project, scene, [], "snapshot", "hash", 0);
    }
    private static DirectorShotProposalDto Shot(int number, int duration) => new(number, "Turn the harbor watch into a readable narrative beat.", duration, "medium", "Mara framed with the red lantern", "eye level", "controlled drift", "Mara and the red lantern", "Mara checks the lantern.", "guarded vigilance", "the harbor", ["red lantern"], "Mara anchors the foreground against harbor depth.", "cool ambient light with warm lantern motivation", "layered harbor depth", "hands focus to the next beat", "No dialogue; preserve harbor ambience.", "Keep Mara, the red lantern, and harbor continuity.", "No VFX required.", "Proposed planning detail for review.", ["Harbor"]);
}
