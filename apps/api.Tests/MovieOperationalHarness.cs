using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;
using Xunit.Abstractions;

namespace Taslim.Api.Tests;

/// <summary>
/// A provider-safe Movie V2 test host. The application still uses the real HTTP
/// controllers, authorization, persistence, job worker, output validation, and
/// asset publication path; only IMovieVideoProvider is replaced with a
/// deterministic in-memory adapter.
/// </summary>
public sealed class MovieOperationalApiFactory : GenerationJobsApiFactory
{
    public FakeProviderScenarioCatalog Scenarios { get; } = new();
    public FakeProviderCallLog Calls { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MovieVideo:Enabled", "true");
        builder.UseSetting("MovieVideo:ProviderKey", "fake-movie");
        builder.UseSetting("MovieVideo:StatusPollIntervalSeconds", "0");
        builder.UseSetting("MovieVideo:MaxStatusPolls", "3");
        builder.UseSetting("MovieVideo:MaxTransientRetries", "0");
        builder.UseSetting("MovieVideo:RetryBaseDelaySeconds", "0");
        builder.UseSetting("MovieVideo:RetryMaxDelaySeconds", "0");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Scenarios);
            services.AddSingleton(Calls);
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp =>
                new FakeMovieVideoProvider(
                    sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                    sp.GetRequiredService<FakeProviderCallLog>()));
        });
    }
}

public sealed record MovieOperationalFixture(
    AuthResponse Owner,
    MovieStudioProjectResponse Project,
    MovieV2ActDto Act,
    MovieV2SequenceDto Sequence,
    MovieV2SceneDto Scene,
    MovieShotDto Shot,
    MovieStoryDto Story,
    MovieCharacterDto Character,
    MovieLocationDto Location,
    MovieSetDto Set,
    MoviePropDto Prop,
    MovieSetVariationDto SetVariation,
    MovieGuideRevisionResponse LockedGuide);

public sealed record MovieRequestSample(string Method, string Path, int StatusCode, long ElapsedMilliseconds);

/// <summary>
/// Captures request timing and status for diagnosis. It intentionally records
/// observations instead of enforcing wall-clock thresholds, keeping tests
/// useful in CI and on slower development machines.
/// </summary>
public sealed class MovieOperationalDiagnostics
{
    private readonly List<MovieRequestSample> samples = [];

    public IReadOnlyList<MovieRequestSample> Samples => samples;

    public void Record(string method, string path, HttpStatusCode statusCode, long elapsedMilliseconds) =>
        samples.Add(new MovieRequestSample(method, path, (int)statusCode, elapsedMilliseconds));

    public string Summary() => string.Join(
        Environment.NewLine,
        samples.Select(sample =>
            $"{sample.Method} {sample.Path} -> {sample.StatusCode} ({sample.ElapsedMilliseconds}ms)"));
}

/// <summary>
/// Builds the minimum real records needed to exercise the integrated Movie V2
/// journey. It is deliberately API-facing so later UI branches can call the
/// same deterministic contract without inventing production-only shortcuts.
/// </summary>
public static class MovieOperationalFixtures
{
    public static async Task<MovieOperationalFixture> CreateFullMovieAsync(
        HttpClient client,
        MovieOperationalDiagnostics? diagnostics = null,
        string title = "Operational E2E Feature")
    {
        var owner = await RegisterAsync(client, "Movie Operational Owner", diagnostics);
        var project = await PostAsync<MovieStudioProjectResponse>(client, "/api/movie-studio/projects", new
        {
            workspaceId = owner.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title,
            description = "A deterministic operational Movie V2 journey.",
            durationSeconds = 30,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
            visualLanguage = "Naturalistic 35mm texture",
            cameraLanguage = "Measured dolly movement",
            colorAndLighting = "Warm practicals with cool moonlight",
            soundAndNarration = "Sparse diegetic sound",
            continuityRules = "The brass compass remains in the left hand.",
        }, diagnostics);

        var act = await PostAsync<MovieV2ActDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/acts", new
        {
            title = "Act One",
            summary = "The first irreversible choice.",
        }, diagnostics);
        var sequence = await PostAsync<MovieV2SequenceDto>(client, $"/api/movie-studio/acts/{act.Id}/sequences", new
        {
            title = "The Arrival",
            summary = "The courier reaches the harbor.",
        }, diagnostics);
        var scene = await PostAsync<MovieV2SceneDto>(client, $"/api/movie-studio/sequences/{sequence.Id}/scenes", new
        {
            title = "Blue Hour Harbor",
            summary = "The courier enters the rain-soaked warehouse.",
        }, diagnostics);
        var shot = await PostAsync<MovieShotDto>(client, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "A slow push toward the compass on a crate.",
            cameraAndFraming = "24mm wide, subject left",
            cameraMotion = "slow push",
            durationSeconds = 5,
            visualContinuityNotes = "Keep the red practical in frame.",
            cinematography = new
            {
                intent = "natural",
                shotSize = "wide",
                focalLength = "24mm",
                lensIntent = "environmental",
                apertureDepthOfField = "deep focus",
                cameraAngle = "eye level",
                cameraMovement = "slow push",
                frameRateIntent = "24fps",
                lighting = "warm practicals",
                paletteLook = "teal and amber",
                compositionNotes = "Subject left, leading lines to the compass.",
            },
        }, diagnostics);

        var story = await PostAsync<MovieStoryDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/story/revisions", new
        {
            premise = "A courier must deliver a message before dawn.",
            logline = "When the city goes dark, a reluctant courier crosses a hostile district to deliver one message.",
            synopsis = "The courier discovers that the message is connected to a missing sibling.",
            treatment = "The route becomes a moral test and ends in a public choice.",
            authorship = MovieStoryAuthorship.Human,
            changeSummary = "Operational human story pass",
            scenes = new[]
            {
                new
                {
                    sceneIdentifier = "ACT-1-SEQUENCE-1-SCENE-1",
                    actNumber = 1,
                    sequenceNumber = 1,
                    movieSceneId = scene.Id,
                    slugline = "EXT. HARBOR WAREHOUSE - BLUE HOUR",
                    synopsis = "The courier arrives as the lights fail.",
                    elements = new object[]
                    {
                        new { elementType = MovieScreenplayElementTypes.Action, content = "The last streetlamp flickers out." },
                        new { elementType = MovieScreenplayElementTypes.Dialogue, content = "We have one hour.", characterName = "MARA", parenthetical = "quietly" },
                    },
                },
            },
        }, diagnostics);
        _ = await PostAsync<MovieStoryRevisionDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/story/revisions/{story.CurrentRevisionId}/submit", null, diagnostics);
        _ = await PostAsync<MovieStoryRevisionDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/story/revisions/{story.CurrentRevisionId}/approve", null, diagnostics);

        var character = await PostAsync<MovieCharacterDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/characters", new
        {
            name = "Mara",
            role = "Courier",
            description = "A guarded courier who refuses to abandon the message.",
            appearance = "Short dark hair and a weathered navy coat.",
            wardrobe = "Navy coat, brass compass, worn boots.",
            voiceAndPerformance = "Quiet, deliberate, increasingly urgent.",
            continuityNotes = "Compass stays in the left hand.",
        }, diagnostics);
        var location = await PostAsync<MovieLocationDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/locations", new
        {
            name = "Old Harbor",
            description = "A repeatable waterfront location under repair.",
            visualContinuityNotes = "Rust-red cranes remain on the east horizon.",
        }, diagnostics);
        var set = await PostAsync<MovieSetDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/sets", new
        {
            name = "Harbor Warehouse",
            description = "A practical warehouse interior.",
            environmentType = "practical",
            movieLocationId = location.Id,
            visualDescription = "Wet concrete and sodium spill.",
            timeOfDay = "blue hour",
            weather = "light rain",
        }, diagnostics);
        var setVariation = await PostAsync<MovieSetVariationDto>(client, $"/api/movie-studio/sets/{set.Id}/variations", new
        {
            name = "Storm night",
            timeOfDay = "night",
            weather = "heavy rain",
            lighting = "cold moonlight and sodium spill",
            isDefault = true,
        }, diagnostics);
        var prop = await PostAsync<MoviePropDto>(client, $"/api/movie-studio/projects/{project.Project.Id}/props", new
        {
            name = "Brass Compass",
            description = "A worn brass compass with a cracked glass face.",
            category = "hero prop",
            continuityNotes = "The crack faces camera in close shots.",
        }, diagnostics);
        _ = await PostAsync<MovieWorldUsageDto>(client, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new
        {
            entityType = MovieWorldEntityTypes.Set,
            entityId = set.Id,
            role = "primary environment",
        }, diagnostics);
        _ = await PostAsync<MovieWorldUsageDto>(client, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new
        {
            entityType = MovieWorldEntityTypes.Prop,
            entityId = prop.Id,
            movieShotId = shot.Id,
            role = "hero prop",
        }, diagnostics);

        var guide = await PostAsync<MovieGuideRevisionResponse>(client, $"/api/movie-studio/projects/{project.Project.Id}/guide/lock", new { }, diagnostics);
        return new MovieOperationalFixture(owner, project, act, sequence, scene, shot, story, character, location, set, prop, setVariation, guide);
    }

    public static async Task<AuthResponse> RegisterAsync(
        HttpClient client,
        string displayName,
        MovieOperationalDiagnostics? diagnostics = null)
    {
        return await PostAsync<AuthResponse>(client, "/api/auth/register", new
        {
            displayName,
            email = $"movie-operational-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, diagnostics);
    }

    public static async Task<T> PostAsync<T>(
        HttpClient client,
        string path,
        object? payload,
        MovieOperationalDiagnostics? diagnostics = null,
        string? idempotencyKey = null)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, path, payload, diagnostics, idempotencyKey);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST {path} failed with {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonOptions)!;
    }

    public static async Task<HttpResponseMessage> SendWithCsrfAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? payload,
        MovieOperationalDiagnostics? diagnostics = null,
        string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        var stopwatch = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        stopwatch.Stop();
        diagnostics?.Record(method.Method, path, response.StatusCode, stopwatch.ElapsedMilliseconds);
        return response;
    }

    public static async Task<T> GetAsync<T>(
        HttpClient client,
        string path,
        MovieOperationalDiagnostics? diagnostics = null)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync(path);
        stopwatch.Stop();
        diagnostics?.Record(HttpMethod.Get.Method, path, response.StatusCode, stopwatch.ElapsedMilliseconds);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path} failed with {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class MovieOperationalE2ETests : IClassFixture<MovieOperationalApiFactory>
{
    private readonly MovieOperationalApiFactory factory;
    private readonly ITestOutputHelper output;

    public MovieOperationalE2ETests(MovieOperationalApiFactory factory, ITestOutputHelper output)
    {
        this.factory = factory;
        this.output = output;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Full_movie_operational_journey_persists_approved_story_world_production_take_and_director_boundary()
    {
        using var client = factory.CreateClient();
        var diagnostics = new MovieOperationalDiagnostics();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, diagnostics);

        var storyboardCandidate = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/production/versions", new
            {
                stage = MovieProductionStages.StoryboardCandidate,
                label = "Deterministic storyboard candidate",
                compositionJson = "{\"blocking\":\"subject-left\",\"seed\":42}",
                stageProvenanceJson = "{\"source\":\"deterministic-test-path\"}",
            }, diagnostics);
        Assert.Equal(MovieProductionStages.StoryboardCandidate, storyboardCandidate.Stage);
        Assert.Equal(MovieProductionVersionStatuses.PendingApproval, storyboardCandidate.Status);
        Assert.Null(storyboardCandidate.AssetId);
        Assert.Null(storyboardCandidate.GenerationJobId);

        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/production/versions/{storyboardCandidate.Id}/review", new
            { approve = true, reason = "Storyboard composition is approved." }, diagnostics);
        Assert.Equal(MovieProductionStages.ApprovedStoryboard, approvedStoryboard.Stage);
        Assert.Equal(MovieProductionVersionStatuses.Approved, approvedStoryboard.Status);

        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/production/versions", new
            {
                stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = storyboardCandidate.Id,
                compositionJson = "{\"frame\":\"approved-keyframe\",\"seed\":42}",
                stageProvenanceJson = "{\"source\":\"approved-storyboard\"}",
            }, diagnostics);
        Assert.Equal(MovieProductionStages.ProductionKeyframe, keyframe.Stage);
        Assert.Equal(storyboardCandidate.Id, keyframe.SourceVersionId);
        Assert.Null(keyframe.AssetId);

        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/production/versions/{keyframe.Id}/review", new
            { approve = true, reason = "Keyframe continuity is approved." }, diagnostics);
        Assert.Equal(MovieProductionStages.ApprovedKeyframe, approvedKeyframe.Stage);

        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.QueuedRunningCompleted;
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate", new { title = "Deterministic shot output" }, diagnostics, "movie-operational-shot-001");
        var completedJob = await WaitForTerminalAsync(client, generated.Job.Id, diagnostics);
        Assert.Equal("Succeeded", completedJob.Status);
        Assert.Single(completedJob.Outputs);

        MovieStudioProjectDto? persistedAfterGeneration = null;
        MovieClipDto? persistedClip = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            persistedAfterGeneration = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(client,
                $"/api/movie-studio/projects/{fixture.Project.Project.Id}", diagnostics);
            persistedClip = persistedAfterGeneration.Clips.Concat(persistedAfterGeneration.Scenes
                    .SelectMany(scene => scene.Clips.Concat(scene.Shots.SelectMany(shot => shot.Clips))))
                .SingleOrDefault(item => item.Id == generated.ClipId);
            if (persistedClip?.AssetId is not null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(persistedClip);
        Assert.Equal(generated.Job.Id, persistedClip.GenerationJobId);
        Assert.NotNull(persistedClip.AssetId);

        var take = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/takes", new
            {
                label = "Deterministic production take",
                qualityLevel = MovieQualityLevels.Standard,
                autoDirectorEnabled = false,
                movieClipId = generated.ClipId,
                generationJobId = generated.Job.Id,
                assetId = persistedClip.AssetId,
                notes = "Backed by the deterministic Movie Asset record.",
            }, diagnostics);
        Assert.Equal(persistedClip.AssetId, take.AssetId);
        var approvedTake = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/takes/{take.Id}/approvals", new { decision = MovieApprovalDecisions.Approved, comment = "Take is reviewable." }, diagnostics);
        Assert.Equal(MovieTakeStatuses.Approved, approvedTake.Status);
        using (var selectResponse = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{take.Id}/select", null, diagnostics))
            Assert.Equal(HttpStatusCode.NoContent, selectResponse.StatusCode);
        using (var finalizeResponse = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{take.Id}/finalize", null, diagnostics))
            Assert.Equal(HttpStatusCode.NoContent, finalizeResponse.StatusCode);

        var hierarchy = await MovieOperationalFixtures.GetAsync<MovieV2HierarchyDto>(client,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/hierarchy", diagnostics);
        var hierarchyTake = hierarchy.Acts.Single().Sequences.Single().Scenes.Single().Shots.Single().Takes.Single();
        Assert.Equal(take.Id, hierarchyTake.Id);
        Assert.NotNull(hierarchyTake.FinalizedAt);

        var proposalResponse = await MovieOperationalFixtures.PostAsync<DirectorProposalResponse>(client,
            $"/api/movie-director/projects/{fixture.Project.Project.Id}/proposals", new
            {
                shotId = fixture.Shot.Id,
                goal = "Prepare the approved harbor shot",
                importance = 80,
                complexity = 60,
                budgetSensitivity = 20,
                requestedQuality = DirectorQualityLevels.Auto,
                budgetLimitUsd = 5,
            }, diagnostics);
        var proposal = proposalResponse.Proposal;
        var action = Assert.Single(proposal.Actions);
        Assert.Equal(DirectorProposalStatuses.PendingApproval, proposal.Status);
        Assert.Equal(DirectorActionStatuses.PendingApproval, action.Status);
        using (var beforeApproval = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-director/actions/{action.Id}/execute", null, diagnostics))
        {
            Assert.Equal(HttpStatusCode.Conflict, beforeApproval.StatusCode);
            Assert.Contains("DIRECTOR_APPROVAL_REQUIRED", await beforeApproval.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var approvedProposal = await MovieOperationalFixtures.PostAsync<DirectorProposalDto>(client,
            $"/api/movie-director/proposals/{proposal.Id}/approve", null, diagnostics);
        Assert.Equal(DirectorProposalStatuses.Approved, approvedProposal.Status);
        Assert.Equal(DirectorActionStatuses.Ready, approvedProposal.Actions.Single().Status);

        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var executed = await MovieOperationalFixtures.PostAsync<DirectorActionExecutionResponse>(client,
            $"/api/movie-director/actions/{action.Id}/execute", null, diagnostics);
        Assert.Equal(DirectorActionStatuses.Succeeded, executed.Action.Status);
        Assert.Contains("queued the shot", executed.Result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        var directorJob = await FindLatestMovieJobAsync(fixture.Owner.User.Id, fixture.Project.Project.Id);
        var directorTerminal = await WaitForTerminalAsync(client, directorJob.Id, diagnostics);
        Assert.Equal("Succeeded", directorTerminal.Status);

        var afterReload = await MovieOperationalFixtures.GetAsync<MovieStudioProjectDto>(client,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}", diagnostics);
        Assert.Equal(fixture.Project.Project.Id, afterReload.Id);
        Assert.Contains(afterReload.Characters, item => item.Id == fixture.Character.Id && item.Name == "Mara");
        Assert.Contains(afterReload.World.Locations, item => item.Id == fixture.Location.Id);
        Assert.Contains(afterReload.World.Sets, item => item.Id == fixture.Set.Id && item.Variations.Any(variation => variation.Id == fixture.SetVariation.Id && variation.IsDefault));
        Assert.Contains(afterReload.World.Props, item => item.Id == fixture.Prop.Id);
        Assert.Contains(afterReload.Scenes, item => item.Id == fixture.Scene.Id && item.Shots.Any(item => item.Id == fixture.Shot.Id));
        Assert.Contains(afterReload.Scenes.SelectMany(item => item.Shots), item => item.ProductionVersions.Any(version => version.Id == storyboardCandidate.Id && version.Stage == MovieProductionStages.ApprovedStoryboard));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.True(await db.Assets.AsNoTracking().AnyAsync(item => item.Id == persistedClip.AssetId && item.SourceGenerationJobId == generated.Job.Id));
        Assert.DoesNotContain(await db.MovieProductionVersions.AsNoTracking().Where(item => item.MovieShotId == fixture.Shot.Id).ToListAsync(), version =>
            (version.Stage is MovieProductionStages.StoryboardCandidate or MovieProductionStages.ProductionKeyframe)
            && (version.AssetId is not null || version.GenerationJobId is not null));
        Assert.True(factory.Calls.Calls(FakeProviderKind.Movie) > 0);
        Assert.DoesNotContain(diagnostics.Samples, sample => sample.StatusCode >= 500);
        output.WriteLine("Movie operational diagnostics:");
        output.WriteLine(diagnostics.Summary());
    }

    [Fact]
    public async Task Owner_editor_reviewer_commenter_and_cross_workspace_users_keep_separate_boundaries()
    {
        using var owner = factory.CreateClient();
        var diagnostics = new MovieOperationalDiagnostics();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(owner, diagnostics, "Role Matrix Movie");
        using var editor = factory.CreateClient();
        var editorAuth = await MovieOperationalFixtures.RegisterAsync(editor, "Movie Operational Editor", diagnostics);
        using var reviewer = factory.CreateClient();
        var reviewerAuth = await MovieOperationalFixtures.RegisterAsync(reviewer, "Movie Operational Reviewer", diagnostics);
        using var outsider = factory.CreateClient();
        await MovieOperationalFixtures.RegisterAsync(outsider, "Movie Operational Outsider", diagnostics);

        await AddWorkspaceMemberAsync(fixture.Owner.PersonalWorkspace.Id, editorAuth.User.Id);
        await AddWorkspaceMemberAsync(fixture.Owner.PersonalWorkspace.Id, reviewerAuth.User.Id);
        _ = await MovieOperationalFixtures.PostAsync<MovieTeamMemberDto>(owner,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/collaboration/team", new
            { userId = editorAuth.User.Id, role = MovieTeamRoles.Editor, permissions = Array.Empty<string>() }, diagnostics);
        _ = await MovieOperationalFixtures.PostAsync<MovieTeamMemberDto>(owner,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/collaboration/team", new
            {
                userId = reviewerAuth.User.Id,
                role = MovieTeamRoles.Writer,
                permissions = Array.Empty<string>(),
                permissionOverrides = new[] { new { permission = MoviePermissions.Approve, granted = true } },
            }, diagnostics);

        var editorView = await MovieOperationalFixtures.GetAsync<MovieCollaborationDto>(editor,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/collaboration", diagnostics);
        Assert.Contains(MoviePermissions.Edit, editorView.CurrentUserPermissions);
        var editorScene = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(editor,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes", new
            { title = "Editor addition", summary = "An editor-like member can persist planning edits." }, diagnostics);
        Assert.Equal("Editor addition", editorScene.Title);

        var reviewerView = await MovieOperationalFixtures.GetAsync<MovieCollaborationDto>(reviewer,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/collaboration", diagnostics);
        Assert.Contains(MoviePermissions.Comment, reviewerView.CurrentUserPermissions);
        Assert.Contains(MoviePermissions.Approve, reviewerView.CurrentUserPermissions);
        Assert.DoesNotContain(MoviePermissions.Generate, reviewerView.CurrentUserPermissions);
        var comment = await MovieOperationalFixtures.PostAsync<MovieCommentDto>(reviewer,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/collaboration/comments", new
            {
                targetType = MovieCollaborationTargetTypes.Scene,
                targetId = fixture.Scene.Id,
                body = "Reviewer note: preserve the compass continuity.",
                mentionedUserIds = Array.Empty<Guid>(),
            }, diagnostics);
        Assert.Equal(reviewerAuth.User.Id, comment.AuthorUserId);
        var review = await MovieOperationalFixtures.PostAsync<MovieReviewDto>(owner,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/collaboration/reviews", new
            {
                targetType = MovieCollaborationTargetTypes.Scene,
                targetId = fixture.Scene.Id,
                reviewerUserId = reviewerAuth.User.Id,
                isFinal = false,
                requestNote = "Please review the scene continuity.",
            }, diagnostics);
        var decision = await MovieOperationalFixtures.PostAsync<MovieReviewDto>(reviewer,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/collaboration/reviews/{review.Id}/decision", new
            { status = MovieReviewStatuses.Approved, decisionNote = "Continuity is acceptable." }, diagnostics);
        Assert.Equal(MovieReviewStatuses.Approved, decision.Status);

        using (var reviewerGenerate = await MovieOperationalFixtures.SendWithCsrfAsync(reviewer, HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate", new { title = "Must not generate" }, diagnostics))
            Assert.Equal(HttpStatusCode.NotFound, reviewerGenerate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/movie-studio/projects/{fixture.Project.Project.Id}")).StatusCode);
        using (var outsiderMutation = await MovieOperationalFixtures.SendWithCsrfAsync(outsider, HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes", new { title = "Cross project", summary = "Must not persist." }, diagnostics))
            Assert.Equal(HttpStatusCode.NotFound, outsiderMutation.StatusCode);

        output.WriteLine("Role-matrix diagnostics:");
        output.WriteLine(diagnostics.Summary());
    }

    [Fact]
    public async Task Quick_movie_remains_a_small_persisted_plan_without_full_movie_approval_records()
    {
        using var client = factory.CreateClient();
        var diagnostics = new MovieOperationalDiagnostics();
        var auth = await MovieOperationalFixtures.RegisterAsync(client, "Quick Movie Regression", diagnostics);
        var quick = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(client, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Quick,
            title = "Quick Movie Regression",
            description = "A focused quick movie brief.",
            durationSeconds = 15,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        }, diagnostics);
        Assert.Equal(MovieProjectModes.Quick, quick.Project.Mode);
        Assert.Null(quick.Job);
        Assert.Single(quick.Project.Scenes);
        Assert.Single(quick.Project.Scenes[0].Shots);
        Assert.Empty(quick.Project.Characters);
        Assert.Empty(quick.Project.World.Locations);
        Assert.Empty(quick.Project.World.Sets);
        Assert.Empty(quick.Project.World.Props);
        Assert.Equal(MovieProductionStages.ShotPlan, quick.Project.Scenes[0].Shots[0].ProductionStage);
        Assert.Empty(quick.Project.Scenes[0].Shots[0].ProductionVersions);
        Assert.Empty(quick.Project.Clips);
        Assert.All(diagnostics.Samples, sample => Assert.InRange(sample.StatusCode, 200, 299));
        output.WriteLine(diagnostics.Summary());
    }

    private async Task AddWorkspaceMemberAsync(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId, MovieOperationalDiagnostics diagnostics)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var current = await MovieOperationalFixtures.GetAsync<GenerationJobDto>(client, $"/api/generation/jobs/{jobId}", diagnostics);
            if (current.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }

    private async Task<GenerationJobDto> FindLatestMovieJobAsync(Guid userId, Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.AsNoTracking()
            .Where(item => item.CreatedByUserId == userId
                && item.JobType == GenerationJobTypes.MovieClipGenerate
                && item.InputJson.Contains(movieProjectId.ToString()))
            .OrderByDescending(item => item.CreatedAt)
            .FirstAsync();
        return GenerationJobContractMapper.ToDto(job);
    }
}
