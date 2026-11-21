using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic regressions for duplicate Movie clips on idempotent scene/shot clip
/// retries. The legacy Studio clip queue persists the clip row before the generation
/// job, so every failure between those two commits used to leave a second queued clip
/// behind: an over-long key, a key claimed by another request, or a crash after the job
/// transaction committed. These tests pin the reconciled behaviour at the real HTTP and
/// database boundary with the worker disabled and the fake Movie provider, so they make
/// no provider call and cannot depend on pricing or provider availability.
/// </summary>
public sealed class MovieStudioClipIdempotencyReconciliationTests : IClassFixture<MovieClipIdempotencyNoWorkerApiFactory>
{
    private readonly MovieClipIdempotencyNoWorkerApiFactory factory;

    public MovieStudioClipIdempotencyReconciliationTests(MovieClipIdempotencyNoWorkerApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Replay_after_the_job_committed_before_the_clip_link_returns_the_canonical_clip_and_repairs_the_link()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie clip link repair window");
        var key = $"movie-link-window-{Guid.NewGuid():N}";
        var payload = new { title = "Canonical link window clip" };
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, payload, null, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = await ReadGenerationAsync(first);
        var claimedClip = await SingleClipAsync(fixture.Project.Project.Id);
        Assert.Equal(accepted.ClipId, claimedClip.Id);
        Assert.Equal(accepted.Job.Id, claimedClip.GenerationJobId);

        // This is the exact row state a crash between the committed job transaction and
        // the clip link write leaves behind: the key is claimed and the clip is not.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var orphaned = await db.MovieClips.SingleAsync(item => item.Id == accepted.ClipId);
            orphaned.GenerationJobId = null;
            await db.SaveChangesAsync();
        }

        var replay = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, payload, null, key);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayed = await ReadGenerationAsync(replay);
        Assert.Equal(accepted.ClipId, replayed.ClipId);
        Assert.Equal(accepted.Job.Id, replayed.Job.Id);

        var afterRepair = await SingleClipAsync(fixture.Project.Project.Id);
        Assert.Equal(accepted.ClipId, afterRepair.Id);
        Assert.Equal(accepted.Job.Id, afterRepair.GenerationJobId);
        Assert.Equal(1, await ClipCountAsync(fixture.Project.Project.Id));
        Assert.Equal(1, await JobCountAsync(key));
        Assert.Equal(1, await ReservationCountAsync(accepted.Job.Id));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Repeated_replays_of_one_claimed_key_never_add_a_clip_a_job_or_a_reservation()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie repeated clip replay");
        var key = $"movie-repeat-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        var payload = new { title = "Repeated replay clip" };

        var first = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, payload, null, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = await ReadGenerationAsync(first);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var replay = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, payload, null, key);
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            var replayed = await ReadGenerationAsync(replay);
            Assert.Equal(accepted.ClipId, replayed.ClipId);
            Assert.Equal(accepted.Job.Id, replayed.Job.Id);
        }

        Assert.Equal(1, await ClipCountAsync(fixture.Project.Project.Id));
        Assert.Equal(1, await JobCountAsync(key));
        Assert.Equal(1, await ReservationCountAsync(accepted.Job.Id));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Claimed_key_reused_for_a_different_title_is_rejected_before_a_second_clip_row_can_exist()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie reused clip key");
        var key = $"movie-different-request-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "Canonical title" }, null, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = await ReadGenerationAsync(first);

        var conflict = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "A different paid request" }, null, key);
        await AssertRejectionAsync(conflict, "IDEMPOTENCY_KEY_REUSED");

        var surviving = await SingleClipAsync(fixture.Project.Project.Id);
        Assert.Equal(accepted.ClipId, surviving.Id);
        Assert.Equal(accepted.Job.Id, surviving.GenerationJobId);
        Assert.Equal(1, await JobCountAsync(key));
        Assert.Equal(1, await ReservationCountAsync(accepted.Job.Id));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Claimed_key_reused_for_another_target_is_rejected_without_persisting_a_targetless_clip()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie cross target key");
        var key = $"movie-cross-target-{Guid.NewGuid():N}";

        var first = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate", new { title = "Shot scoped clip" }, null, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // The scene route queues a shot-less clip. It is a different generation target, so
        // the claimed key must not adopt or create a clip for it.
        var conflict = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/scenes/{fixture.Scene.Id}/generate",
            new { title = "Shot scoped clip" }, null, key);
        await AssertRejectionAsync(conflict, "IDEMPOTENCY_KEY_REUSED");

        var surviving = await SingleClipAsync(fixture.Project.Project.Id);
        Assert.NotNull(surviving.MovieShotId);
        Assert.Equal(fixture.Shot.Id, surviving.MovieShotId);
        Assert.Equal(1, await ClipCountAsync(fixture.Project.Project.Id));
        Assert.Equal(1, await JobCountAsync(key));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Overlong_idempotency_key_is_refused_before_any_movie_clip_or_job_row_exists()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie overlong clip key");
        var key = new string('k', MovieStudioClipIdempotency.MaxIdempotencyKeyLength + 1);

        var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate", new { title = "Never queued" }, null, key);
        await AssertRejectionAsync(response, "IDEMPOTENCY_KEY_INVALID");

        Assert.Equal(0, await ClipCountAsync(fixture.Project.Project.Id));
        Assert.Equal(0, await JobCountAsync(key));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Missing_canonical_clip_fails_closed_instead_of_queueing_a_replacement()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie deleted canonical clip");
        var key = $"movie-missing-target-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "Canonical removed clip" }, null, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = await ReadGenerationAsync(first);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.MovieClips.Remove(await db.MovieClips.SingleAsync(item => item.Id == accepted.ClipId));
            await db.SaveChangesAsync();
        }

        var replay = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, path, new { title = "Canonical removed clip" }, null, key);
        await AssertRejectionAsync(replay, "IDEMPOTENCY_TARGET_UNAVAILABLE");
        Assert.Equal(0, await ClipCountAsync(fixture.Project.Project.Id));
        Assert.Equal(1, await JobCountAsync(key));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Same_key_text_on_two_users_workspaces_keeps_each_clip_and_job_private()
    {
        using var owner = factory.CreateClient();
        var ownerFixture = await MovieOperationalFixtures.CreateFullMovieAsync(owner, title: "Movie owned clip key");
        using var other = factory.CreateClient();
        var otherFixture = await MovieOperationalFixtures.CreateFullMovieAsync(other, title: "Movie other clip key");
        var sharedKey = $"movie-shared-text-{Guid.NewGuid():N}";

        var first = await MovieOperationalFixtures.SendWithCsrfAsync(
            owner, HttpMethod.Post, $"/api/movie-studio/shots/{ownerFixture.Shot.Id}/generate", new { title = "Owner clip" }, null, sharedKey);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = await ReadGenerationAsync(first);

        // Idempotency keys are claimed per user. Another user must not receive the first
        // user's clip, and must not be refused a clip of their own.
        var second = await MovieOperationalFixtures.SendWithCsrfAsync(
            other, HttpMethod.Post, $"/api/movie-studio/shots/{otherFixture.Shot.Id}/generate", new { title = "Owner clip" }, null, sharedKey);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var theirs = await ReadGenerationAsync(second);

        Assert.NotEqual(accepted.ClipId, theirs.ClipId);
        Assert.NotEqual(accepted.Job.Id, theirs.Job.Id);
        var ownerClip = await SingleClipAsync(ownerFixture.Project.Project.Id);
        Assert.Equal(accepted.ClipId, ownerClip.Id);
        var otherClip = await SingleClipAsync(otherFixture.Project.Project.Id);
        Assert.Equal(theirs.ClipId, otherClip.Id);
        Assert.Equal(2, await JobCountAsync(sharedKey));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));
    }

    private async Task<MovieStudioGenerationResponse> ReadGenerationAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<MovieStudioGenerationResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task AssertRejectionAsync(HttpResponseMessage response, string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest, $"expected 400, got {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<int> ClipCountAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieClips.AsNoTracking().CountAsync(item => item.MovieProjectId == movieProjectId);
    }

    private async Task<MovieClip> SingleClipAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clips = await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync();
        return Assert.Single(clips);
    }

    private async Task<int> JobCountAsync(string idempotencyKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.GenerationJobs.AsNoTracking().CountAsync(item => item.IdempotencyKey == idempotencyKey);
    }

    private async Task<int> ReservationCountAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.UsageTransactions.AsNoTracking()
            .CountAsync(item => item.RequestId == $"generation:{jobId:N}");
    }
}

/// <summary>
/// Disables the generation worker so a queued Movie clip stays queued and the fake
/// provider is never called. The clip rows, jobs and cost reservations under test are the
/// ones the reconciliation path is responsible for, not provider output.
/// </summary>
public class MovieClipIdempotencyNoWorkerApiFactory : MovieOperationalApiFactory
{
    protected override bool WorkerEnabled => false;
}
/// <summary>
/// Pure identity rules that decide whether a claimed idempotency key still describes the
/// same Movie clip request. These are the boundaries the HTTP regressions above depend on,
/// pinned directly so no unrelated request field can pass by accident.
/// </summary>
public sealed class MovieStudioClipIdempotencyRuleTests
{
    private const string SceneJson = """{"Id":"11111111-1111-1111-1111-111111111111","Summary":"A courier waits."}""";
    private const string ShotJson = """{"Id":"22222222-2222-2222-2222-222222222222","CameraAndFraming":"Wide","productionContract":{"Status":"StoryboardCandidate","PlanningStatus":"Planned","ProductionStage":"storyboard"}}""";

    [Fact]
    public void Normalize_key_trims_and_refuses_the_shared_job_limit_without_side_effects()
    {
        Assert.Null(MovieStudioClipIdempotency.NormalizeKey(null));
        Assert.Null(MovieStudioClipIdempotency.NormalizeKey("   "));
        Assert.Equal("clip-key", MovieStudioClipIdempotency.NormalizeKey("  clip-key  "));
        var exception = Assert.Throws<GenerationJobValidationException>(
            () => MovieStudioClipIdempotency.NormalizeKey(new string('k', MovieStudioClipIdempotency.MaxIdempotencyKeyLength + 1)));
        Assert.Equal("IDEMPOTENCY_KEY_INVALID", exception.Code);
    }

    [Fact]
    public void Clip_may_only_adopt_the_job_that_claimed_the_key()
    {
        var jobId = Guid.NewGuid();
        Assert.True(MovieStudioClipIdempotency.IsCompatibleClipLink(null, jobId));
        Assert.True(MovieStudioClipIdempotency.IsCompatibleClipLink(jobId, jobId));
        Assert.False(MovieStudioClipIdempotency.IsCompatibleClipLink(Guid.NewGuid(), jobId));
    }

    [Fact]
    public void Only_a_scene_clip_job_can_be_reconciled_into_a_clip_generation()
    {
        var clipId = Guid.NewGuid();
        var (movie, scene, shot) = Target();
        Assert.True(MovieStudioClipIdempotency.JobTargetsClip(Job(Input(movie, scene, shot, clipId)), clipId));
        Assert.False(MovieStudioClipIdempotency.JobTargetsClip(Job(Input(movie, scene, shot, clipId)), Guid.NewGuid()));
        Assert.False(MovieStudioClipIdempotency.JobTargetsClip(
            Job(Input(movie, scene, shot, clipId, operation: MovieStudioOperations.QuickMovie)), clipId));
        Assert.False(MovieStudioClipIdempotency.JobTargetsClip(
            Job(Input(movie, scene, shot, Guid.Empty)), Guid.Empty));
        Assert.False(MovieStudioClipIdempotency.JobTargetsClip(new GenerationJob { InputJson = "{not json" }, clipId));
        Assert.False(MovieStudioClipIdempotency.JobTargetsClip(new GenerationJob(), clipId));
    }

    [Fact]
    public void Priced_request_replays_and_shot_workflow_movement_does_not_break_the_replay()
    {
        var (movie, scene, shot) = Target();
        var priced = Job(Input(movie, scene, shot, Guid.NewGuid()));
        Assert.True(Matches(movie, scene, shot, priced));

        // The production contract restates stage and approval movement, which advances as a
        // side effect of queueing, reviewing or rendering other takes of the same shot.
        var driftedContract = """{"Id":"22222222-2222-2222-2222-222222222222","CameraAndFraming":"Wide","productionContract":{"Status":"ApprovedKeyframe","PlanningStatus":"Ready","ProductionStage":"approved_keyframe"}}""";
        Assert.True(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, shot, "Clip title", SceneJson, driftedContract));
        // Stripping that projection must not blind the check to the shot content itself.
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, shot, "Clip title", SceneJson, driftedContract.Replace("Wide", "Tight")));
    }

    [Fact]
    public void Every_priced_input_edit_makes_a_claimed_key_a_different_request()
    {
        var (movie, scene, shot) = Target();
        var priced = Job(Input(movie, scene, shot, Guid.NewGuid()));

        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, shot, "A different paid title", SceneJson, ShotJson));
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, shot, "   ", SceneJson, ShotJson));

        var retimed = Target();
        retimed.Shot.DurationSeconds = 45;
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, retimed.Movie, retimed.Scene, retimed.Shot, "Clip title", SceneJson, ShotJson));

        var rewritten = Target();
        rewritten.Shot.Description = "The courier waits in the rain.";
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, rewritten.Movie, rewritten.Scene, rewritten.Shot, "Clip title", SceneJson, ShotJson));

        var restyled = Target();
        restyled.Movie.Style = "noir";
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, restyled.Movie, restyled.Scene, restyled.Shot, "Clip title", SceneJson, ShotJson));

        var revoiced = Target();
        revoiced.Movie.Language = "ur";
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, revoiced.Movie, revoiced.Scene, revoiced.Shot, "Clip title", SceneJson, ShotJson));

        var redirected = Target();
        redirected.Movie.AdditionalInstructions = "Handheld only.";
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, redirected.Movie, redirected.Scene, redirected.Shot, "Clip title", SceneJson, ShotJson));

        var reshot = Target();
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, reshot.Movie, reshot.Scene, reshot.Shot, "Clip title", SceneJson, ShotJson));
        var rescened = Target();
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, rescened.Movie, rescened.Scene, shot, "Clip title", SceneJson, ShotJson));

        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, shot, "Clip title", SceneJson.Replace("waits", "leaves"), ShotJson));
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, shot, "Clip title", SceneJson, ShotJson.Replace("Wide", "Tight")));
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, null, "Clip title", SceneJson, null));
    }

    [Fact]
    public void Shotless_scene_clip_request_replays_only_against_a_shotless_priced_request()
    {
        var (movie, scene, _) = Target();
        var priced = Job(Input(movie, scene, null, Guid.NewGuid()));
        Assert.True(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, null, "Clip title", SceneJson, null));
        Assert.False(MovieStudioClipIdempotency.MatchesRequest(priced, movie, scene, Shot(scene), "Clip title", SceneJson, ShotJson));
    }

    private static bool Matches(MovieProject movie, MovieScene scene, MovieShot shot, GenerationJob job) =>
        MovieStudioClipIdempotency.MatchesRequest(job, movie, scene, shot, "Clip title", SceneJson, ShotJson);

    private static (MovieProject Movie, MovieScene Scene, MovieShot Shot) Target()
    {
        var movie = new MovieProject
        {
            Id = Guid.NewGuid(),
            Title = "Movie title",
            DurationSeconds = 30,
            AspectRatio = "16:9",
            Style = "cinematic",
            Language = "en",
            AdditionalInstructions = "Keep it warm",
        };
        var scene = new MovieScene
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movie.Id,
            Sequence = 1,
            Title = "Warehouse",
            Summary = "A courier waits.",
            DurationSeconds = 30,
        };
        return (movie, scene, Shot(scene));
    }

    private static MovieShot Shot(MovieScene scene) => new()
    {
        Id = Guid.NewGuid(),
        MovieSceneId = scene.Id,
        Sequence = 1,
        Description = "The courier waits.",
        DurationSeconds = 30,
    };

    private static string Input(
        MovieProject movie,
        MovieScene scene,
        MovieShot? shot,
        Guid clipId,
        string operation = MovieStudioOperations.SceneClip,
        string? sceneJson = SceneJson,
        string? shotJson = ShotJson) =>
        JsonSerializer.Serialize(new MovieGenerationInput(
            operation,
            movie.Id,
            clipId,
            scene.Id,
            shot?.Id,
            shot?.Description ?? scene.Summary,
            MovieStudioClipIdempotency.ResolveDurationSeconds(movie, scene, shot),
            movie.AspectRatio,
            movie.Style,
            movie.Language,
            movie.AdditionalInstructions,
            "continuity-guide",
            sceneJson,
            shot is null ? null : shotJson));

    private static GenerationJob Job(string inputJson) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Clip title",
        InputJson = inputJson,
    };
}
