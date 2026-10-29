using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionLabelAdmissionTests
{
    [Fact]
    public async Task Oversized_render_label_rejects_before_clip_job_or_usage_reservation()
    {
        // The same provider-free approval workflow can render a valid request.
        // Only an oversized label must fail before any billable work exists.
        using var factory = new MovieOperationalNoWorkerFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Production render label admission", sceneDurationSeconds: 5);
        var shotId = movie.Shot.Id;
        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe, sourceVersionId = approvedStoryboard.Id,
                compositionJson = "{\"frame\":\"approved\"}" });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{keyframe.Id}/review",
            new { approve = true });
        var motion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/motion-preview",
            new { sourceVersionId = approvedKeyframe.Id });
        var approvedMotion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{motion.Id}/review",
            new { approve = true });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId);
        var ledgerBefore = await db.UsageTransactions.CountAsync();
        var path = $"/api/movie-studio/shots/{shotId}/production/render";

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path,
            new { sourceVersionId = approvedMotion.Id, label = new string('x', 161),
                confirmationAccepted = true }, idempotencyKey: "render-long-label-rejected");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("PRODUCTION_LABEL_TOO_LARGE",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == movie.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "render-long-label-rejected"));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());

        // The boundary length remains legal, and the same approved source can
        // still queue once with a fresh idempotency key.
        var valid = await MovieOperationalFixtures.PostAsync<MovieProductionRenderResponse>(
            client, path,
            new { sourceVersionId = approvedMotion.Id, label = new string('x', 160),
                confirmationAccepted = true }, idempotencyKey: "render-label-160-valid");
        Assert.NotEqual(Guid.Empty, valid.ClipId);
        Assert.Equal(valid.Job.Id, valid.Version.GenerationJobId);
        Assert.Single(await db.MovieClips.Where(item => item.MovieShotId == shotId).ToListAsync());
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == "render-label-160-valid"));
    }

    [Fact]
    public async Task Oversized_production_replay_key_rejects_before_any_render_persistence()
    {
        // The idempotency key must be rejected before reference validation,
        // continuity snapshots, speculative clips, jobs, or usage reservations.
        using var factory = new MovieOperationalNoWorkerFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Production replay-key boundary", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var versionsBefore = await db.MovieProductionVersions.CountAsync();
        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var ledgerBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{movie.Shot.Id}/production/render",
            new { sourceVersionId = Guid.NewGuid(), label = "Replay-key validation",
                confirmationAccepted = true }, idempotencyKey: new string('k', 81));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_INVALID",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync());
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }


    [Fact]
    public async Task Oversized_render_title_rejects_before_clip_job_or_usage_reservation()
    {
        // Title validation precedes even source-version validation; an invalid
        // input must not persist speculative generation state or reserve usage.
        // The worker is disabled, so no provider calls are made.
        using var factory = new MovieOperationalNoWorkerFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Approved render title boundary", sceneDurationSeconds: 5);
        var path = $"/api/movie-studio/shots/{movie.Shot.Id}/production/render";
        var nonexistentSource = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var versionsBefore = await db.MovieProductionVersions.CountAsync();
        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var oversized = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path,
            new { sourceVersionId = nonexistentSource, title = new string('t', 161),
                confirmationAccepted = true }, idempotencyKey: "render-title-over-160");
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Contains("PRODUCTION_TITLE_TOO_LARGE",
            await oversized.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The title's maximum is measured after trimming. The valid boundary
        // now reaches the independent source-version gate (source intentionally
        // does not exist), rather than being rejected as an oversized title.
        using var atBoundary = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path,
            new { sourceVersionId = nonexistentSource, title = $"  {new string('t', 160)}  ",
                confirmationAccepted = true }, idempotencyKey: "render-title-exact-160");
        Assert.Equal(HttpStatusCode.BadRequest, atBoundary.StatusCode);
        var boundaryBody = await atBoundary.Content.ReadAsStringAsync();
        Assert.Contains("PRODUCTION_RENDER_INVALID", boundaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PRODUCTION_TITLE_TOO_LARGE", boundaryBody, StringComparison.Ordinal);

        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync());
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Approved_render_replay_rejects_an_oversized_edited_shot_without_touching_canonical_usage()
    {
        // One five-second render has already acquired a canonical job.
        // Editing a shot to 120-minute timeline metadata cannot reuse that
        // priced job. No generation worker or paid provider runs in this test.
        using var factory = new MovieOperationalNoWorkerFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Feature timeline replay boundary", sceneDurationSeconds: 5);
        var shotId = movie.Shot.Id;

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, "/api/movie-studio/shots/" + shotId + "/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate,
                compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, "/api/movie-studio/production/versions/" + storyboard.Id + "/review",
            new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, "/api/movie-studio/shots/" + shotId + "/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id,
                compositionJson = "{\"frame\":\"approved\"}" });
        var approvedKeyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, "/api/movie-studio/production/versions/" + keyframe.Id + "/review",
            new { approve = true });
        var motion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, "/api/movie-studio/shots/" + shotId + "/production/motion-preview",
            new { sourceVersionId = approvedKeyframe.Id });
        var approvedMotion = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, "/api/movie-studio/production/versions/" + motion.Id + "/review",
            new { approve = true });

        const string replayKey = "approved-replay-edited-feature-duration";
        var path = "/api/movie-studio/shots/" + shotId + "/production/render";
        var request = new { sourceVersionId = approvedMotion.Id, label = "Canonical five-second render" };
        using var first = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, request, idempotencyKey: replayKey);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var canonicalJob = await db.GenerationJobs.AsNoTracking()
            .SingleAsync(item => item.IdempotencyKey == replayKey);
        var canonicalClip = await db.MovieClips.AsNoTracking()
            .SingleAsync(item => item.GenerationJobId == canonicalJob.Id);
        Assert.Equal(5, canonicalClip.DurationSeconds);
        var versionsBefore = await db.MovieProductionVersions.CountAsync();
        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync();

        var shot = await db.MovieShots.SingleAsync(item => item.Id == shotId);
        shot.DurationSeconds = 7_200;
        await db.SaveChangesAsync();

        using var replay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, request, idempotencyKey: replayKey);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync());
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(snapshotsBefore, await db.MovieCharacterContinuitySnapshots.CountAsync());
        var preserved = await db.MovieClips.AsNoTracking()
            .SingleAsync(item => item.Id == canonicalClip.Id);
        Assert.Equal(canonicalJob.Id, preserved.GenerationJobId);
        Assert.Equal(5, preserved.DurationSeconds);
    }

}
