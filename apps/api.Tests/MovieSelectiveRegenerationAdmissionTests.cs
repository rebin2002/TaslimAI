using System.Net;
using System.Text.Json;
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
    public async Task Oversized_shot_prompt_rejects_regeneration_preview_before_cost_or_usage_writes()
    {
        using var factory = new MovieClipPromptLimitedApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration over-budget prompt", sceneDurationSeconds: 5);
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests",
            Request(sourceId));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_PROMPT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieRegenerationRequests.AnyAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Combined_creative_direction_overflow_rejects_regeneration_preview_without_billable_work()
    {
        using var factory = new MovieClipPromptLimitedApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration combined prompt", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
        var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
        // Every section fits individually inside the fake adapter's 30-character
        // prompt limit, but the three sections cannot fit together.
        shot.Description = "A sunlit room";
        movie.Style = "cinematic";
        movie.AdditionalInstructions = new string('x', 20);
        await db.SaveChangesAsync();
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        var ledgerBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests",
            Request(sourceId));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_PROMPT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.MovieRegenerationRequests.AnyAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Creative_direction_expanded_after_regeneration_preview_rejects_confirmation_without_billable_work()
    {
        using var factory = new MovieClipPromptLimitedApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration changed creative direction", sceneDurationSeconds: 5);
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            var movie = await db.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
            shot.Description = "Brief shot.";
            movie.Style = string.Empty;
            movie.AdditionalInstructions = string.Empty;
            await db.SaveChangesAsync();
        }

        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        var preview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests", Request(sourceId));

        using var scope = factory.Services.CreateScope();
        var dbVerify = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await dbVerify.UsageTransactions.CountAsync();
        var versionsBefore = await dbVerify.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id);
        var editedMovie = await dbVerify.MovieProjects.SingleAsync(item => item.Id == fixture.Project.Project.Id);
        editedMovie.Style = "cinematic";
        editedMovie.AdditionalInstructions = new string('x', 20);
        await dbVerify.SaveChangesAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm",
            new { confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_PROMPT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation,
            await dbVerify.MovieRegenerationRequests.Where(item => item.Id == preview.Request.Id)
                .Select(item => item.Status).SingleAsync());
        Assert.Equal(versionsBefore,
            await dbVerify.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await dbVerify.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await dbVerify.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await dbVerify.UsageTransactions.CountAsync());
    }

    [Fact]
    public async Task Shot_prompt_expanded_after_preview_rejects_confirmation_without_creating_billable_work()
    {
        using var factory = new MovieClipPromptLimitedApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Selective regeneration stale prompt preview", sceneDurationSeconds: 5);

        // Use a small provider-supported description for the initial preview.
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var shot = await db.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
            shot.Description = "Brief shot.";
            await db.SaveChangesAsync();
        }
        var sourceId = await ApprovedStoryboardAsync(client, fixture.Shot.Id);
        var preview = await MovieOperationalFixtures.PostAsync<MovieSelectiveRegenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/regeneration-requests", Request(sourceId));

        using var scope = factory.Services.CreateScope();
        var verifyDb = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var ledgerBefore = await verifyDb.UsageTransactions.CountAsync();
        var versionsBefore = await verifyDb.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id);

        // The user edited the shot after pricing; confirmation must re-check
        // the current provider limit instead of trusting the earlier preview.
        var editedShot = await verifyDb.MovieShots.SingleAsync(item => item.Id == fixture.Shot.Id);
        editedShot.Description = new string('x', 50);
        await verifyDb.SaveChangesAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{preview.Request.Id}/confirm",
            new { confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("REGENERATION_PROMPT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(MovieRegenerationStatuses.PendingConfirmation,
            await verifyDb.MovieRegenerationRequests.Where(item => item.Id == preview.Request.Id)
                .Select(item => item.Status).SingleAsync());
        Assert.Equal(versionsBefore,
            await verifyDb.MovieProductionVersions.CountAsync(item => item.MovieShotId == fixture.Shot.Id));
        Assert.False(await verifyDb.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await verifyDb.GenerationJobs.AnyAsync(item => item.ProjectId == fixture.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await verifyDb.UsageTransactions.CountAsync());
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
        // Keep both movies under the same authenticated user. A second RegisterAsync
        // would replace the client's auth cookie and make first-movie actions 403.
        var second = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Second independent regeneration", sceneDurationSeconds: 5,
            existingOwner: first.Owner);
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
        var ledgerBefore = await db.UsageTransactions.CountAsync();
        var canonicalFirstJobCount = await db.GenerationJobs.CountAsync(item => item.ProjectId == first.Project.Project.ProjectId);

        // Confirmation must persist the admission-time video route so a future
        // worker cannot silently send this priced regeneration to a different
        // adapter after routing configuration changes.
        var canonicalJob = await db.GenerationJobs.AsNoTracking()
            .SingleAsync(item => item.IdempotencyKey == sharedKey);
        var canonicalInput = JsonSerializer.Deserialize<MovieGenerationInput>(canonicalJob.InputJson);
        Assert.NotNull(canonicalInput);
        Assert.Equal(scope.ServiceProvider.GetRequiredService<IMovieVideoProvider>().Key,
            canonicalInput.SelectedProviderKey);

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/regeneration-requests/{secondPreview.Request.Id}/confirm",
            new { confirm = true }, idempotencyKey: sharedKey);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == second.Project.Project.Id));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync(item => item.ProjectId == second.Project.Project.ProjectId));
        Assert.Equal(canonicalFirstJobCount,
            await db.GenerationJobs.CountAsync(item => item.ProjectId == first.Project.Project.ProjectId));
        Assert.Equal(ledgerBefore, await db.UsageTransactions.CountAsync());
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
