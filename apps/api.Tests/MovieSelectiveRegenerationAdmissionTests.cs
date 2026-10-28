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
