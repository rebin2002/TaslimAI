using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

// Apply-seam regression tests for the fail-closed PER-SCENE admission ceiling in the Director
// scene-plan ACTION EXECUTOR (#685, #1088 guard lane).
//
// Coverage owned by the sibling lanes is deliberately NOT repeated here:
// - #1088 (MovieDirectorScenePlanPerSceneRenderableCeilingTests) proves the ceiling in-process at
//   the validator seam: Math.Min(targetDuration, MovieDurationBudgetPolicy.MaxShotDurationSeconds).
// - #1078 (MovieDirectorScenePlanValidationTests additions) owns the separate context/target seam.
// - #1083 (MovieDirectorScenePlanPerSceneCeilingTests) is the QA characterisation harness.
// - #1057 (MovieProjectDurationCeilingCompatibilityTests) pins the provider-clip and cost seams.
//
// What is unique here is the one claim the render/billing story actually turns on: rejection must
// happen INSIDE MovieDirectorScenePlanActionExecutor BEFORE it persists any MovieScene row. The
// executor is the only writer of production scenes on this path (MovieDirectorScenePlanExecutor.cs
// returns DIRECTOR_SCENE_PLAN_INVALID at lines 29-31, strictly before db.MovieScenes.Add at line 63
// and SaveChangesAsync at line 76). Shots, queue jobs, preflight reservations, and billed usage are
// all derived downstream from persisted MovieScene rows, so zero scene rows on rejection means
// nothing to queue and nothing billed. No lane today proves that ordering; the only existing
// fail-before-persistence apply test covers the STALE hash path, not an over-length duration.
//
// The discriminating case needs a movie target ABOVE the renderable unit ceiling (7,200 s here,
// the #685 120-minute shape), which is unreachable through the HTTP create-project endpoint while
// the #269 create cap stands. So the project duration is raised directly in the test database,
// and the executor is driven with a hand-admitted payload through the same in-app validator and
// assembler the production execute path resolves from DI. No provider call, no media, no HTTP to
// any external service, no billable usage, no migrations, no user data.
namespace Taslim.Api.Tests;

public sealed class MovieDirectorScenePlanPerSceneCeilingApplyTests : IClassFixture<ScenePlanCeilingApplyFactory>
{
    // The #685 target movie: a two-hour plan, i.e. a configured planning ceiling above the renderable
    // unit. With the #1088 guard the per-scene admission bound is min(7_200, 3_600) = 3_600.
    private const int TwoHourTargetSeconds = 7_200;
    private const int RenderableUnitCeiling = MovieDurationBudgetPolicy.MaxShotDurationSeconds;

    private readonly ScenePlanCeilingApplyFactory factory;

    public MovieDirectorScenePlanPerSceneCeilingApplyTests(ScenePlanCeilingApplyFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Single_scene_spanning_the_whole_two_hour_target_is_rejected_at_apply_without_creating_scenes()
    {
        // Pre-guard shape (main): target 7,200 s, one scene of 7,200 s passes the old bound
        // scene <= target, is persisted verbatim, and is only then silently clamped to a 3,600 s
        // billable shot at preflight (MovieProductionPreflight.cs Math.Clamp(..., 1, 3_600)) - half
        // the promised runtime rendered and underbilled. Post-guard the apply must fail closed and
        // persist nothing.
        var (projectId, workspaceId, userId) = await SeedTwoHourProjectAsync("Whole-Film Scene Apply Owner");
        var action = await BuildActionAsync(projectId, workspaceId, [DawnScene(1, TwoHourTargetSeconds)]);

        var result = await ExecuteAsync(action, userId);

        Assert.False(result.Succeeded);
        Assert.Equal("DIRECTOR_SCENE_PLAN_INVALID", result.FailureCode);
        Assert.Contains(DirectorScenePlanValidationReasonCodes.RuntimeMismatch, result.FailureReasonCodes!);
        await AssertNoProductionScenesAsync(projectId);
        Assert.Null(result.ResultJson);
    }

    [Fact]
    public async Task Scene_one_second_over_the_renderable_unit_ceiling_is_rejected_at_apply_without_creating_scenes()
    {
        // The exact-boundary sibling: 3,601 s inside a compliant 7,200 s split must be refused while
        // the 3,600 s boundary itself stays admissible (proved by the positive control below), so the
        // failure is specific to the ceiling and not a blanket apply lockout.
        var (projectId, workspaceId, userId) = await SeedTwoHourProjectAsync("One-Second-Over Apply Owner");
        var action = await BuildActionAsync(projectId, workspaceId,
            [DawnScene(1, RenderableUnitCeiling + 1), FutureScene(2, TwoHourTargetSeconds - RenderableUnitCeiling - 1)]);

        var result = await ExecuteAsync(action, userId);

        Assert.False(result.Succeeded);
        Assert.Equal("DIRECTOR_SCENE_PLAN_INVALID", result.FailureCode);
        Assert.Contains(DirectorScenePlanValidationReasonCodes.RuntimeMismatch, result.FailureReasonCodes!);
        await AssertNoProductionScenesAsync(projectId);
    }

    [Fact]
    public async Task Two_hour_plan_split_at_the_renderable_unit_boundary_applies_and_persists_full_durations()
    {
        // Positive control and exact-boundary acceptance at the apply seam: two 3,600 s scenes sum to
        // the 7,200 s target, every scene is renderable as one unit, so the SAME executor and SAME
        // 7,200 s options admit the plan. The persisted scene durations prove nothing was silently
        // truncated on the way in: what was promised is what the downstream queue and billing will
        // see. This case is green on main and must stay green with the guard (no behaviour drift).
        var (projectId, workspaceId, userId) = await SeedTwoHourProjectAsync("Boundary Split Apply Owner");
        var action = await BuildActionAsync(projectId, workspaceId,
            [DawnScene(1, RenderableUnitCeiling), FutureScene(2, RenderableUnitCeiling)]);

        var result = await ExecuteAsync(action, userId);

        Assert.True(result.Succeeded, $"{result.FailureCode} {result.SafeMessage} [{string.Join(",", result.FailureReasonCodes ?? Array.Empty<string>())}]");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var scenes = await db.MovieScenes.Where(item => item.MovieProjectId == projectId)
            .OrderBy(item => item.Sequence).ToListAsync();
        Assert.Equal(2, scenes.Count);
        Assert.All(scenes, scene => Assert.Equal(RenderableUnitCeiling, scene.DurationSeconds));
    }

    // --- wiring ---------------------------------------------------------------------------------

    /// <summary>
    /// Dispatches the action through the same scoped executor the production request pipeline
    /// resolves. The Director service picks the executor with FirstOrDefault over the registered
    /// IDirectorActionExecutor services by ActionType (MovieDirectorServices.cs:819), and
    /// Program.cs registers MovieDirectorScenePlanActionExecutor before the room-awareness
    /// placeholder that shares the action type; mirror that selection and pin it, so this lane
    /// asserts the behaviour of the executor production actually reaches. The scope stays alive
    /// for the whole call because the resolved executor holds the scoped TaslimDbContext.
    /// </summary>
    private async Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, Guid executingUserId)
    {
        using var scope = factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetServices<IDirectorActionExecutor>()
            .First(item => string.Equals(item.ActionType, DirectorActionTypes.ScenePlanning, StringComparison.OrdinalIgnoreCase));
        Assert.IsType<MovieDirectorScenePlanActionExecutor>(executor);
        return await executor.ExecuteAsync(action, executingUserId);
    }

    /// <summary>
    /// Builds an APPROVED-STATE scene-plan action for the given scenes with a fresh snapshot hash,
    /// mirroring what the proposals API persists - the point is to exercise the apply path itself.
    /// The DirectorAction is materialised in memory: the executor only reads PayloadJson and
    /// MovieProjectId, so nothing about the asserted failure depends on action persistence.
    /// </summary>
    private async Task<DirectorAction> BuildActionAsync(Guid projectId, Guid workspaceId, IReadOnlyList<DirectorSceneProposal> scenes)
    {
        using var scope = factory.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<MovieDirectorContextAssembler>();
        var assembled = await assembler.AssembleStoryAsync(projectId, null, null);
        Assert.NotNull(assembled);
        var payload = new DirectorScenePlanActionPayload(
            DirectorScenePlanActionTypes.PlanScenes,
            assembled.Value.Context.CurrentRevision?.RevisionId ?? assembled.Value.Context.ApprovedRevision?.RevisionId,
            assembled.Value.SnapshotHash, "en", "16:9",
            TwoHourTargetSeconds, scenes.Sum(item => item.EstimatedDurationSeconds),
            DirectorQualityLevels.Fast, "Fast", null, scenes);
        return new DirectorAction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MovieProjectId = projectId,
            DirectorProposalId = Guid.NewGuid(),
            ActionType = DirectorActionTypes.ScenePlanning,
            Status = DirectorActionStatuses.Ready,
            ApprovalRequired = false,
            PayloadJson = JsonSerializer.Serialize(payload, DirectorJson.Options),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private async Task AssertNoProductionScenesAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.MovieScenes.CountAsync(item => item.MovieProjectId == projectId));
    }

    private async Task<(Guid ProjectId, Guid WorkspaceId, Guid UserId)> SeedTwoHourProjectAsync(string displayName)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, displayName);
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        await CreateCharacter(client, movie.Project.Id);
        await CreateStory(client, movie.Project.Id);
        await LockGuide(client, movie.Project.Id);
        // The #269 project-create cap still refuses a 7,200 s create request; this lane is about the
        // scene-plan apply seam, so the raised duration is seeded directly in the test database.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var project = await db.MovieProjects.SingleAsync(item => item.Id == movie.Project.Id);
        project.DurationSeconds = TwoHourTargetSeconds;
        await db.SaveChangesAsync();
        return (movie.Project.Id, auth.PersonalWorkspace.Id, auth.User.Id);
    }

    // Scene texts are the same grounded phrasings the in-process suites use against the seeded story
    // ("Mara protects the last seed at the dry riverbed..."), so every non-duration finding
    // (grounding, language, placeholder, payload budget) passes and duration is the only variable.
    private static DirectorSceneProposal DawnScene(int sequence, int durationSeconds) => new(
        sequence, "Seed at Dawn", "Mara protects the last seed.",
        "Mara discovers the last seed at the dry riverbed.", "Dry riverbed", "Dawn",
        ["Mara"], "Move from fear to responsibility.", durationSeconds,
        "Open from silence into wind.", ["The last seed remains in Mara's left hand."],
        "A restrained close beginning with blue hour light.");

    private static DirectorSceneProposal FutureScene(int sequence, int durationSeconds) => new(
        sequence, "Shared Future", "Mara chooses renewal for the valley.",
        "Mara shares the last seed's future with the valley.", "Dry riverbed", "Morning",
        ["Mara"], "Resolve fear as collective hope.", durationSeconds,
        "Resolve on the sound of wind.", ["The seed is still accounted for before the final choice."],
        "Hold the final image in blue hour naturalism without spectacle.");

    // --- HTTP seeding helpers (same flow as MovieDirectorScenePlanningTests) ---------------------

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"scene-plan-apply-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId) => await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
    {
        workspaceId,
        mode = MovieProjectModes.Full,
        title = $"Last Seed Apply {Guid.NewGuid():N}",
        description = "Mara protects the last seed at the dry riverbed.",
        durationSeconds = 30,
        aspectRatio = "16:9",
        style = "cinematic",
        language = "en",
        visualLanguage = "blue hour naturalism",
        cameraLanguage = "slow observational movement",
        colorAndLighting = "blue hour light",
        soundAndNarration = "wind and restrained silence",
        continuityRules = "The last seed remains in Mara's left hand.",
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

/// <summary>
/// Same in-app API factory the scene-plan suite uses (fake AI core, Sqlite in-memory), with the
/// planning options raised to the #685 120-minute target so the renderable-unit ceiling is the
/// binding constraint at the apply seam. Validator-level behaviour at this setting is already owned
/// by #1088's file; this factory exists to drive the executor through the real DI wiring.
/// </summary>
public sealed class ScenePlanCeilingApplyFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, ValidScenePlanCompletionService>();
            services.PostConfigure<MovieDirectorScenePlanningOptions>(options => options.MaxDurationSeconds = 7_200);
        });
    }
}
