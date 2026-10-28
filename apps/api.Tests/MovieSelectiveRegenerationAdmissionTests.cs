using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSelectiveRegenerationAdmissionTests
{
    [Fact]
    public async Task Unsupported_priced_profile_rejects_preview_before_any_regeneration_or_usage_write()
    {
        using var factory = new MovieClipIntentDeniedApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration unsupported profile", sceneDurationSeconds: 5);
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests",
            Request(sourceId));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_EXECUTION_INTENT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieRegenerationRequests.AnyAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Out_of_range_single_clip_duration_rejects_regeneration_before_preview_or_ledger_write()
    {
        using var factory = new MovieClipDurationApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration unsupported duration", sceneDurationSeconds: 11);
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests",
            Request(sourceId));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieRegenerationRequests.AnyAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Overlong_confirmation_idempotency_key_rejects_before_speculative_clip_persistence()
    {
        using var factory = new MovieClipDurationApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration invalid replay key", sceneDurationSeconds: 5);
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        var preview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests", Request(sourceId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await db.UsageTransactions.CountAsync();
        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id);

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm",
            new { confirm = true },
            idempotencyKey: new string('x', 81));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_INVALID",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation,
            await db.MovieRegenerationRequests.Where(item => item.Id == preview.Request.Id)
                .Select(item => item.Status).SingleAsync());
        Assert.Equal(versionsBefore,
            await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Shot_duration_increased_after_preview_rejects_confirmation_without_creating_billable_work()
    {
        using var factory = new MovieClipDurationApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration stale duration preview", sceneDurationSeconds: 5);
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        var preview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests", Request(sourceId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await db.UsageTransactions.CountAsync();
        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id);

        // The priced preview was for five seconds, but the shot has since
        // grown beyond this test provider's verified ten-second clip limit.
        // Confirmation must revalidate the current shot, not trust the preview.
        var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
        shot.DurationSeconds = 11;
        await db.SaveChangesAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm",
            new { confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation,
            await db.MovieRegenerationRequests.Where(item => item.Id == preview.Request.Id)
                .Select(item => item.Status).SingleAsync());
        Assert.Equal(versionsBefore,
            await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Provider_becoming_unavailable_after_preview_rejects_confirmation_without_clip_job_or_debit()
    {
        using var factory = new MovieClipDurationApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration provider unavailable at confirmation", sceneDurationSeconds: 5);
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        var preview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests", Request(sourceId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await db.UsageTransactions.CountAsync();
        scope.ServiceProvider.GetRequiredService<FakeProviderScenarioCatalog>()[FakeProviderKind.Movie] =
            FakeProviderScenario.AllProvidersUnavailable;

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm",
            new { confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_PROVIDER_UNAVAILABLE",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation,
            await db.MovieRegenerationRequests.Where(item => item.Id == preview.Request.Id)
                .Select(item => item.Status).SingleAsync());
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Reusing_another_regeneration_job_key_rejects_without_a_speculative_clip_or_job()
    {
        using var factory = new MovieClipDurationApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var first = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "First independent regeneration", sceneDurationSeconds: 5);
        var second = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Second independent regeneration", sceneDurationSeconds: 5);
        var firstSource = await ApprovedStoryboardAsync(client, first.Shot.Id);
        var secondSource = await ApprovedStoryboardAsync(client, second.Shot.Id);
        var firstPreview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client, $"/api/movie-studio/shots/{first.Shot.Id}/regeneration-requests", Request(firstSource));
        var secondPreview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client, $"/api/movie-studio/shots/{second.Shot.Id}/regeneration-requests", Request(secondSource));

        const string sharedKey = "selective-regeneration-cross-request-key";
        using (var confirmed = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{firstPreview.Request.Id}/confirm",
            new { confirm = true }, idempotencyKey: sharedKey))
            Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == second.Project.Project.Id);
        var jobsBefore = await db.GenerationJobs.CountAsync(item => item.ProjectId == second.Project.Project.ProjectId);

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{secondPreview.Request.Id}/confirm",
            new { confirm = true }, idempotencyKey: sharedKey);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == second.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync(item => item.ProjectId == second.Project.Project.ProjectId));
        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation,
            await db.MovieRegenerationRequests.Where(item => item.Id == secondPreview.Request.Id)
                .Select(item => item.Status).SingleAsync());
    }

    private static object Request(Guid sourceId) => new
    {
        actionType = MovieRegenerationActionTypes.ProductionKeyframeCandidate,
        requestedStage = MovieProductionStages.ProductionKeyframe,
        reason = "Rebuild the keyframe while preserving verified continuity.",
        sourceVersionId = sourceId,
        changedInputsJson = "{\"camera\":\"new framing\"}",
        compositionJson = "{\"frame\":\"reframed\"}",
    };

    private static async Task<Guid> ApprovedStoryboardAsync(HttpClient client, Guid shotId)
    {
        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"layout\":\"left\"}" });
        await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true, reason = "Storyboard approved." });
        return storyboard.Id;
    }
}
