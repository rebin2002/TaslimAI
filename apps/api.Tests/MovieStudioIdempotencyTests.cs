using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieOperationalNoWorkerFactory : MovieOperationalApiFactory
{
    protected override bool WorkerEnabled => false;
}

public sealed class MovieStudioIdempotencyTests : IClassFixture<MovieOperationalNoWorkerFactory>
{
    private readonly MovieOperationalNoWorkerFactory factory;

    public MovieStudioIdempotencyTests(MovieOperationalNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Identical_keys_for_different_users_remain_independent()
    {
        // Job idempotency is user-scoped, not global. A user choosing the
        // same key as another must never receive the other's private clip or
        // reuse their usage reservation. Workers and paid providers are off.
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var firstMovie = await MovieOperationalFixtures.CreateFullMovieAsync(
            firstClient, title: "First user's private movie");
        var secondMovie = await MovieOperationalFixtures.CreateFullMovieAsync(
            secondClient, title: "Second user's private movie");
        Assert.NotEqual(firstMovie.Owner.User.Id, secondMovie.Owner.User.Id);
        Assert.NotEqual(firstMovie.Owner.PersonalWorkspace.Id, secondMovie.Owner.PersonalWorkspace.Id);

        var key = $"movie-separate-user-scope-{Guid.NewGuid():N}";
        var firstPath = $"/api/movie-studio/shots/{firstMovie.Shot.Id}/generate";
        var secondPath = $"/api/movie-studio/shots/{secondMovie.Shot.Id}/generate";
        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            firstClient, firstPath, new { title = "First private clip" }, idempotencyKey: key);
        var second = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            secondClient, secondPath, new { title = "Second private clip" }, idempotencyKey: key);

        Assert.NotEqual(first.ClipId, second.ClipId);
        Assert.NotEqual(first.Job.Id, second.Job.Id);
        var firstReplay = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            firstClient, firstPath, new { title = "First private clip" }, idempotencyKey: key);
        var secondReplay = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            secondClient, secondPath, new { title = "Second private clip" }, idempotencyKey: key);
        Assert.Equal(first.ClipId, firstReplay.ClipId);
        Assert.Equal(second.ClipId, secondReplay.ClipId);
        Assert.Equal(first.Job.Id, firstReplay.Job.Id);
        Assert.Equal(second.Job.Id, secondReplay.Job.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var jobs = await db.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, item => item.Id == first.Job.Id
            && item.CreatedByUserId == firstMovie.Owner.User.Id);
        Assert.Contains(jobs, item => item.Id == second.Job.Id
            && item.CreatedByUserId == secondMovie.Owner.User.Id);
        var firstClip = await db.MovieClips.AsNoTracking()
            .SingleAsync(item => item.MovieProjectId == firstMovie.Project.Project.Id);
        var secondClip = await db.MovieClips.AsNoTracking()
            .SingleAsync(item => item.MovieProjectId == secondMovie.Project.Project.Id);
        Assert.Equal(first.ClipId, firstClip.Id);
        Assert.Equal(second.ClipId, secondClip.Id);
        Assert.Equal(first.Job.Id, firstClip.GenerationJobId);
        Assert.Equal(second.Job.Id, secondClip.GenerationJobId);
        Assert.Single(await db.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{first.Job.Id:N}").ToListAsync());
        Assert.Single(await db.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{second.Job.Id:N}").ToListAsync());
    }

    [Fact]
    public async Task Same_user_key_cannot_be_reassigned_to_another_movie()
    {
        // An idempotency key belongs to one user-scoped generation request,
        // not merely to a movie. Reusing it on another private project must
        // neither reveal the old clip nor persist a losing speculative clip.
        using var client = factory.CreateClient();
        var firstMovie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "First movie for shared user");
        var secondMovie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Second movie for shared user",
            existingOwner: firstMovie.Owner);
        const string key = "movie-cross-project-idempotency-boundary";
        var canonical = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, $"/api/movie-studio/shots/{firstMovie.Shot.Id}/generate",
            new { title = "Canonical project clip" }, idempotencyKey: key);

        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{secondMovie.Shot.Id}/generate",
            new { title = "Different project clip" }, idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = Assert.Single(await db.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync());
        Assert.Equal(canonical.Job.Id, job.Id);
        Assert.Equal(firstMovie.Owner.User.Id, job.CreatedByUserId);
        var firstClip = await db.MovieClips.AsNoTracking()
            .SingleAsync(item => item.MovieProjectId == firstMovie.Project.Project.Id);
        Assert.Equal(canonical.ClipId, firstClip.Id);
        Assert.Equal(job.Id, firstClip.GenerationJobId);
        Assert.False(await db.MovieClips.AsNoTracking()
            .AnyAsync(item => item.MovieProjectId == secondMovie.Project.Project.Id));
        Assert.Single(await db.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{job.Id:N}").ToListAsync());
    }

    [Fact]
    public async Task Reused_movie_job_reconciles_to_canonical_clip_without_persisting_a_duplicate()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie idempotency reconciliation");
        const string idempotencyKey = "movie-clip-reconcile-1";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Canonical clip" },
            idempotencyKey: idempotencyKey);

        // Reproduce the small race window after GenerationJobService has committed
        // the canonical job but before the winning request links its clip row.
        // The next request must repair that link and discard its speculative clip.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.MovieClips
                .Where(item => item.Id == first.ClipId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, (Guid?)null));
        }

        var replay = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Canonical clip" },
            idempotencyKey: idempotencyKey);

        Assert.Equal(first.Job.Id, replay.Job.Id);
        Assert.Equal(first.ClipId, replay.ClipId);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await verifyDb.GenerationJobs.CountAsync(item => item.IdempotencyKey == idempotencyKey));
        var clips = await verifyDb.MovieClips
            .AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id)
            .ToListAsync();
        var clip = Assert.Single(clips);
        Assert.Equal(first.ClipId, clip.Id);
        Assert.Equal(first.Job.Id, clip.GenerationJobId);
    }

    [Fact]
    public async Task Job_first_replay_rejects_a_canonical_clip_linked_to_another_generation_job()
    {
        // A delayed job-first replay may legitimately repair a null clip link.
        // It must not overwrite, accept or return a clip owned by a different
        // generation job: those jobs have separate cost and usage identities.
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie idempotency foreign job link");
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        const string canonicalKey = "movie-replay-foreign-link-original";
        const string otherKey = "movie-replay-foreign-link-other";

        var canonical = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Canonical clip" }, idempotencyKey: canonicalKey);
        var other = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Other clip" }, idempotencyKey: otherKey);
        Assert.NotEqual(canonical.Job.Id, other.Job.Id);

        // Simulate persisted inconsistent linkage without deleting either job
        // or usage reservation. This forces the replay through the job-first
        // lookup instead of the linked-clip fast path.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.MovieClips.Where(item => item.Id == other.ClipId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, (Guid?)null));
            await db.MovieClips.Where(item => item.Id == canonical.ClipId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, (Guid?)other.Job.Id));
        }

        using var replay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { title = "Canonical clip" },
            idempotencyKey: canonicalKey);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(other.Job.Id, (await verifyDb.MovieClips.AsNoTracking()
            .SingleAsync(item => item.Id == canonical.ClipId)).GenerationJobId);
        Assert.Null((await verifyDb.MovieClips.AsNoTracking()
            .SingleAsync(item => item.Id == other.ClipId)).GenerationJobId);
        Assert.Equal(2, await verifyDb.GenerationJobs.CountAsync(item =>
            item.Id == canonical.Job.Id || item.Id == other.Job.Id));
        Assert.Single(await verifyDb.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{canonical.Job.Id:N}").ToListAsync());
        Assert.Single(await verifyDb.UsageTransactions.AsNoTracking()
            .Where(item => item.RequestId == $"generation:{other.Job.Id:N}").ToListAsync());
    }

    [Fact]
    public async Task Reusing_a_clip_key_with_a_different_title_is_rejected_without_an_extra_clip()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie idempotency mismatch");
        const string key = "movie-clip-title-mismatch-1";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Original clip" }, idempotencyKey: key);

        // Exercise the already-linked fast path. The old behavior silently
        // returned the original clip despite a materially different request.
        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { title = "Changed clip" }, idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains(
            "IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.CountAsync(job => job.IdempotencyKey == key));
        var clip = Assert.Single(await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(first.ClipId, clip.Id);
        Assert.Equal(first.Job.Id, clip.GenerationJobId);
    }

    [Fact]
    public async Task Reusing_a_clip_key_after_shot_duration_changes_rejects_the_stale_cost_intent()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie duration-sensitive replay");
        const string key = "movie-shot-duration-edit-replay-1";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Canonical duration" }, idempotencyKey: key);

        int initialDuration;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == first.Job.Id);
            var input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson);
            Assert.NotNull(input);
            initialDuration = input!.DurationSeconds;

            // Simulate editing a planned shot after a job was queued. The
            // original idempotency key must not silently serve a clip whose
            // priced duration no longer matches the requested shot.
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            shot.DurationSeconds = initialDuration == 6 ? 7 : 6;
            await db.SaveChangesAsync();
        }

        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { title = "Canonical duration" },
            idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scopeVerify = factory.Services.CreateScope();
        var verifyDb = scopeVerify.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var canonicalJob = Assert.Single(await verifyDb.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync());
        Assert.Equal(first.Job.Id, canonicalJob.Id);
        Assert.Equal(initialDuration, JsonSerializer.Deserialize<MovieGenerationInput>(canonicalJob.InputJson)!.DurationSeconds);
        var canonicalClip = Assert.Single(await verifyDb.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(first.ClipId, canonicalClip.Id);
        Assert.Equal(first.Job.Id, canonicalClip.GenerationJobId);
    }

    [Theory]
    [InlineData("shot-description")]
    [InlineData("shot-framing")]
    [InlineData("shot-narration")]
    [InlineData("scene-title")]
    [InlineData("scene-dialogue")]
    [InlineData("movie-style")]
    [InlineData("movie-language")]
    [InlineData("movie-instructions")]
    [InlineData("movie-aspect-ratio")]
    public async Task Replaying_after_creative_direction_changes_rejects_the_stale_generation_intent(string editedField)
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Creative direction idempotency replay");
        var key = $"movie-creative-{editedField}-{Guid.NewGuid():N}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Canonical creative direction" }, idempotencyKey: key);

        using (var editScope = factory.Services.CreateScope())
        {
            var db = editScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
            switch (editedField)
            {
                case "shot-description":
                    shot.Description = "Newly edited scene action that must not reuse the previous video.";
                    break;
                case "shot-framing":
                    shot.CameraAndFraming = "Newly edited shot framing must request another clip.";
                    break;
                case "shot-narration":
                    shot.Narration = "Rewritten narration for the shot.";
                    break;
                case "scene-title":
                    (await db.MovieScenes.SingleAsync(item => item.Id == fixture.Scene.Id)).Title = "Rewritten scene heading";
                    break;
                case "scene-dialogue":
                    (await db.MovieScenes.SingleAsync(item => item.Id == fixture.Scene.Id)).Dialogue = "New scene dialogue";
                    break;
                case "movie-style":
                    movie.Style = "distinct-new-visual-style";
                    break;
                case "movie-language":
                    movie.Language = "eo";
                    break;
                case "movie-instructions":
                    movie.AdditionalInstructions = "Fresh creative direction for subsequent clips.";
                    break;
                case "movie-aspect-ratio":
                    movie.AspectRatio = "9:16";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(editedField));
            }
            await db.SaveChangesAsync();
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usageCount = await verifyDb.UsageTransactions.CountAsync();

        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path,
            new { title = "Canonical creative direction" }, idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var canonicalJob = Assert.Single(await verifyDb.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync());
        Assert.Equal(first.Job.Id, canonicalJob.Id);
        var canonicalClip = Assert.Single(await verifyDb.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(first.ClipId, canonicalClip.Id);
        Assert.Equal(first.Job.Id, canonicalClip.GenerationJobId);
        Assert.Equal(usageCount, await verifyDb.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Conflicting_title_in_job_first_window_preserves_the_canonical_job_and_clip()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie job-first conflicting replay");
        const string key = "movie-job-first-conflicting-title-1";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Canonical clip" }, idempotencyKey: key);

        // Model the job-committed/clip-link-pending interleaving. A conflicting
        // replay must reject the key, not adopt or alter the canonical clip.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.MovieClips
                .Where(item => item.Id == first.ClipId)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(item => item.GenerationJobId, (Guid?)null));
        }

        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { title = "Different request" },
            idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = Assert.Single(await verifyDb.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync());
        Assert.Equal(first.Job.Id, job.Id);
        Assert.Equal("Canonical clip", job.Title);
        var clip = Assert.Single(await verifyDb.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(first.ClipId, clip.Id);
        Assert.Null(clip.GenerationJobId);
    }

    [Theory]
    [InlineData("targetResolution", "4k")]
    [InlineData("sourceResolution", "720p")]
    [InlineData("qualityTier", "alternate-quality")]
    [InlineData("processingPath", "alternate-path")]
    [InlineData("upscalingRequested", "true")]
    public async Task Changed_priced_intent_during_job_first_window_cannot_adopt_or_strand_the_canonical_clip(
        string option, string value)
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie job-first priced replay");
        var key = $"movie-job-first-priced-{option}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";
        var originalRequest = new { title = "Canonical priced job" };
        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, originalRequest, idempotencyKey: key);

        // The canonical job has committed but the winning request has not yet
        // published the clip link. A different cost/output intent must not be
        // allowed to claim this key even if its title and target are unchanged.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.MovieClips.Where(item => item.Id == first.ClipId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.GenerationJobId, (Guid?)null));
        }

        var conflictingRequest = new Dictionary<string, object>
        {
            ["title"] = originalRequest.title,
            [option] = option == "upscalingRequested" ? true : value,
        };
        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, conflictingRequest, idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Replaying the *original* intent may repair the missing link; the
        // conflicting request must not consume or mutate the canonical job.
        var replay = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, originalRequest, idempotencyKey: key);
        Assert.Equal(first.Job.Id, replay.Job.Id);
        Assert.Equal(first.ClipId, replay.ClipId);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = Assert.Single(await verifyDb.GenerationJobs.AsNoTracking()
            .Where(item => item.IdempotencyKey == key).ToListAsync());
        Assert.Equal(first.Job.Id, job.Id);
        Assert.Equal(originalRequest.title, job.Title);
        var clip = Assert.Single(await verifyDb.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(first.ClipId, clip.Id);
        Assert.Equal(first.Job.Id, clip.GenerationJobId);
    }

    [Fact]
    public async Task Rejected_overlong_key_does_not_leave_a_speculative_movie_clip()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie rejected key cleanup");
        var key = new string('x', 81);

        // GenerationJobService rejects keys longer than 80 chars *after*
        // QueueClipAsync persists its speculative clip. Rejection must not
        // strand a queued clip without an associated job.
        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Never queued" }, idempotencyKey: key);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_INVALID",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
    }

    [Fact]
    public async Task Eighty_character_key_with_surrounding_spaces_replays_the_same_job()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie key boundary");
        var key = new string('k', 80);
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        // Normalization must happen before the length limit. A legitimate
        // 80-character key remains valid even with surrounding whitespace.
        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Boundary clip" },
            idempotencyKey: $"  {key}  ");
        var replay = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Boundary clip" }, idempotencyKey: key);

        Assert.Equal(first.Job.Id, replay.Job.Id);
        Assert.Equal(first.ClipId, replay.ClipId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
        Assert.Equal(1, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
    }

    [Fact]
    public void Invalid_or_non_movie_job_input_is_not_treated_as_a_clip_target()
    {
        Assert.False(MovieStudioIdempotency.TryGetMovieClipId(null, out _));
        Assert.False(MovieStudioIdempotency.TryGetMovieClipId("{not-json}", out _));
        Assert.False(MovieStudioIdempotency.TryGetMovieClipId("{}", out _));
    }

    [Theory]
    [InlineData("targetResolution", "4k")]
    [InlineData("sourceResolution", "720p")]
    [InlineData("qualityTier", "not-the-current-tier")]
    [InlineData("processingPath", "not-native")]
    [InlineData("upscalingRequested", "true")]
    public async Task Replaying_a_priced_option_change_rejects_the_key_without_changing_the_canonical_job(string option, string value)
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie priced option replay");
        var key = $"movie-pricing-option-collision-{option}";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Canonical priced intent" }, idempotencyKey: key);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == first.Job.Id);
            var input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson);
            Assert.NotNull(input);
            Assert.Equal("1080p", input!.SourceResolution);
            Assert.Equal("1080p", input.TargetResolution);
            Assert.Equal("native", input.ProcessingPath);
            Assert.False(input.UpscalingRequested);
        }

        // Same target/title do not make a changed quality or cost intent idempotent.
        // Exercise every field used in the fast-path request fingerprint.
        var conflictingRequest = new Dictionary<string, object>
        {
            ["title"] = "Canonical priced intent",
            [option] = option == "upscalingRequested" ? true : value,
        };
        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, conflictingRequest,
            idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", await conflicting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await verifyDb.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
        var canonical = Assert.Single(await verifyDb.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(first.ClipId, canonical.Id);
        Assert.Equal(first.Job.Id, canonical.GenerationJobId);
    }

    [Fact]
    public async Task Clip_idempotency_never_adopts_another_movie_operation()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie idempotency operation boundary");
        var response = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Scene clip" }, idempotencyKey: "movie-operation-boundary-1");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var inputJson = await db.GenerationJobs.AsNoTracking()
            .Where(job => job.Id == response.Job.Id)
            .Select(job => job.InputJson).SingleAsync();
        Assert.True(MovieStudioIdempotency.TryGetMovieClipId(inputJson, out var clipId));
        Assert.Equal(response.ClipId, clipId);

        // A valid clip id alone must not allow an unrelated Movie operation to
        // be mistaken for the canonical scene/shot-generation clip.
        var otherOperation = JsonNode.Parse(inputJson)!.AsObject();
        otherOperation[nameof(MovieGenerationInput.Operation)] = "another-movie-operation";
        Assert.False(MovieStudioIdempotency.TryGetMovieClipId(otherOperation.ToJsonString(), out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task Unsupported_priced_retry_or_upscale_passes_fail_before_persisting_a_clip(
        int retryAttempts, int upscalePasses)
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: $"Movie unsupported attempt {retryAttempts}, pass {upscalePasses}");
        var key = $"movie-unsupported-{retryAttempts}-{upscalePasses}";
        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Unsupported priced settings", retryAttempts, upscalePasses },
            idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_GENERATION_OPTIONS_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "upscale")]
    [InlineData(false, "adaptive")]
    public async Task Unsupported_upscale_or_processing_path_cannot_queue_or_reserve_usage(
        bool upscalingRequested, string? processingPath)
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie unsupported processing path");
        var key = $"movie-unsupported-path-{Guid.NewGuid():N}";
        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Unsupported processing intent", upscalingRequested, processingPath },
            idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_GENERATION_OPTIONS_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
    }

    [Fact]
    public async Task Completing_the_first_job_before_clip_link_publishes_the_canonical_private_asset()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie early completion");
        var queued = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Early completed clip" },
            idempotencyKey: "movie-first-completion-before-link");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.SingleAsync(item => item.Id == queued.Job.Id);
        var now = DateTime.UtcNow;
        job.Status = GenerationJobStatus.Succeeded;
        job.ConcurrencyToken = Guid.NewGuid();
        await db.MovieClips.Where(item => item.Id == queued.ClipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, (Guid?)null));

        // Deterministically model worker completion inside the first-request
        // job-save/clip-link gap, with no paid provider or file-store call.
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = job.WorkspaceId,
            ProjectId = job.ProjectId,
            UserId = job.CreatedByUserId,
            OriginalFileName = "private-movie.mp4",
            StoredFileName = "private-movie.mp4",
            ContentType = "video/mp4",
            Extension = ".mp4",
            StorageKey = "tests/movie-first-completion.mp4",
            CreatedAt = now,
        };
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            WorkspaceId = job.WorkspaceId,
            ProjectId = job.ProjectId,
            CreatedByUserId = job.CreatedByUserId,
            SourceGenerationJobId = job.Id,
            StoredFileId = file.Id,
            Name = "Private completed movie clip",
            AssetType = AssetTypes.Video,
            MimeType = "video/mp4",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.StoredFiles.Add(file);
        db.Assets.Add(asset);
        db.MovieVideoProviderExecutions.Add(new MovieVideoProviderExecution
        {
            Id = Guid.NewGuid(),
            GenerationJobId = job.Id,
            MovieClipId = queued.ClipId,
            ProviderKey = "fake-movie",
            Status = MovieVideoExecutionStatuses.Running,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var executions = scope.ServiceProvider.GetRequiredService<MovieVideoExecutionStore>();
        await executions.MarkReadyAsync(job.Id, job.ConcurrencyToken, asset.Id, file.Id, 5, null, CancellationToken.None);

        var completedClip = await db.MovieClips.AsNoTracking().SingleAsync(item => item.Id == queued.ClipId);
        Assert.Equal(job.Id, completedClip.GenerationJobId);
        Assert.Equal(MovieClipStatuses.Ready, completedClip.Status);
        Assert.Equal(asset.Id, completedClip.AssetId);
        Assert.Equal(file.Id, completedClip.StoredFileId);
        Assert.Equal(1, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(job.WorkspaceId, await db.Assets.AsNoTracking()
            .Where(item => item.Id == asset.Id).Select(item => item.WorkspaceId).SingleAsync());
    }
}
