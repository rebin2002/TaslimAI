using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieApprovedQuoteConfirmationTests
{
    [Fact]
    public async Task Fresh_render_with_tampered_maximum_usd_is_rejected_before_any_generation_or_override_write()
    {
        using var factory = new MovieOperationalNoWorkerFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Tampered approved quote", sceneDurationSeconds: 5);
        var source = await MovieOperationalFixtures.CreateApprovedMotionPreviewAsync(client, movie.Shot.Id);

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TaslimDbContext>();
        var provider = Assert.IsType<FakeMovieVideoProvider>(services.GetRequiredService<IMovieVideoProvider>());
        var modelKey = Assert.IsType<string>(provider.SceneClipPricingModelKey);
        await MovieOperationalFixtures.AddMoviePricingRouteAsync(db, provider.Key, modelKey);

        var preflightPath = $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight"
            + $"?sourceVersionId={source.Id}&targetResolution=1080p&qualityTier=Standard";
        using var preflightResponse = await client.GetAsync(preflightPath);
        Assert.Equal(HttpStatusCode.OK, preflightResponse.StatusCode);
        var preflight = await preflightResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>();
        Assert.NotNull(preflight);
        var priceIntent = Assert.IsType<MovieProductionPriceIntentDto>(preflight!.PriceIntent);

        var projectId = movie.Project.Project.Id;
        var shotId = movie.Shot.Id;
        var snapshotsBefore = await db.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == projectId);
        var clipsBefore = await db.MovieClips.CountAsync(item => item.MovieProjectId == projectId);
        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId);
        var transitionsBefore = await db.Set<MovieProductionStageTransition>().CountAsync(item => item.MovieShotId == shotId);
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();
        var overrideAuditsBefore = await db.MovieReferenceReadinessOverrideAudits.CountAsync();
        var providerCallsBefore = services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie);

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/shots/{shotId}/production/render",
            new
            {
                sourceVersionId = source.Id,
                label = "Tampered quote",
                targetResolution = "1080p",
                qualityTier = DirectorQualityLevels.Standard,
                confirmationAccepted = true,
                expectedMaximumAmountUsd = priceIntent.ExpectedMaximumAmountUsd + 0.01m,
                expectedQuoteFingerprint = priceIntent.QuoteFingerprint,
                allowReferenceReadinessOverride = true,
                referenceReadinessOverrideReason = "quote mismatch must not leave an audit",
            },
            idempotencyKey: "approved-quote-amount-tampered");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_PRODUCTION_QUOTE_STALE",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(snapshotsBefore,
            await db.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == projectId));
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync(item => item.MovieProjectId == projectId));
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId));
        Assert.Equal(transitionsBefore,
            await db.Set<MovieProductionStageTransition>().CountAsync(item => item.MovieShotId == shotId));
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(overrideAuditsBefore, await db.MovieReferenceReadinessOverrideAudits.CountAsync());
        Assert.Equal(providerCallsBefore,
            services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Replay_with_missing_canonical_version_fails_closed_without_duplicate_reservation_or_clip()
    {
        using var factory = new MovieOperationalNoWorkerFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Incomplete approved linkage replay", sceneDurationSeconds: 5);
        var source = await MovieOperationalFixtures.CreateApprovedMotionPreviewAsync(client, movie.Shot.Id);

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TaslimDbContext>();
        var provider = Assert.IsType<FakeMovieVideoProvider>(services.GetRequiredService<IMovieVideoProvider>());
        var modelKey = Assert.IsType<string>(provider.SceneClipPricingModelKey);
        await MovieOperationalFixtures.AddMoviePricingRouteAsync(db, provider.Key, modelKey);

        var preflightPath = $"/api/movie-studio/shots/{movie.Shot.Id}/production/preflight"
            + $"?sourceVersionId={source.Id}&targetResolution=1080p&qualityTier=Standard";
        using var preflightResponse = await client.GetAsync(preflightPath);
        Assert.Equal(HttpStatusCode.OK, preflightResponse.StatusCode);
        var preflight = await preflightResponse.Content.ReadFromJsonAsync<MovieProductionPreflightDto>();
        Assert.NotNull(preflight);
        var priceIntent = Assert.IsType<MovieProductionPriceIntentDto>(preflight!.PriceIntent);
        const string idempotencyKey = "approved-incomplete-link-replay";
        var renderPath = $"/api/movie-studio/shots/{movie.Shot.Id}/production/render";
        var payload = new
        {
            sourceVersionId = source.Id,
            label = "Recover canonical linkage",
            targetResolution = "1080p",
            qualityTier = DirectorQualityLevels.Standard,
            confirmationAccepted = true,
            expectedMaximumAmountUsd = priceIntent.ExpectedMaximumAmountUsd,
            expectedQuoteFingerprint = priceIntent.QuoteFingerprint,
        };

        using var accepted = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, payload, idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var canonical = await accepted.Content.ReadFromJsonAsync<MovieProductionRenderResponse>();
        Assert.NotNull(canonical);

        var canonicalVersion = await db.MovieProductionVersions.SingleAsync(
            item => item.GenerationJobId == canonical!.Job.Id);
        var transitions = await db.Set<MovieProductionStageTransition>()
            .Where(item => item.MovieProductionVersionId == canonicalVersion.Id)
            .ToListAsync();
        db.Set<MovieProductionStageTransition>().RemoveRange(transitions);
        db.MovieProductionVersions.Remove(canonicalVersion);
        await db.SaveChangesAsync();

        var snapshotsBeforeReplay = await db.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == movie.Project.Project.Id);
        var clipsBeforeReplay = await db.MovieClips.CountAsync(item => item.MovieProjectId == movie.Project.Project.Id);
        var versionsBeforeReplay = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == movie.Shot.Id);
        var transitionsBeforeReplay = await db.Set<MovieProductionStageTransition>().CountAsync(item => item.MovieShotId == movie.Shot.Id);
        var jobsBeforeReplay = await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == idempotencyKey);
        var usageBeforeReplay = await db.UsageTransactions.CountAsync();
        var providerCallsBeforeReplay = services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie);

        using var replay = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, renderPath, payload, idempotencyKey: idempotencyKey);

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("IDEMPOTENCY_INCOMPLETE",
            await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(snapshotsBeforeReplay,
            await db.MovieCharacterContinuitySnapshots.CountAsync(item => item.MovieProjectId == movie.Project.Project.Id));
        Assert.Equal(clipsBeforeReplay, await db.MovieClips.CountAsync(item => item.MovieProjectId == movie.Project.Project.Id));
        Assert.Equal(versionsBeforeReplay, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == movie.Shot.Id));
        Assert.Equal(transitionsBeforeReplay,
            await db.Set<MovieProductionStageTransition>().CountAsync(item => item.MovieShotId == movie.Shot.Id));
        Assert.Equal(jobsBeforeReplay, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == idempotencyKey));
        Assert.Equal(usageBeforeReplay, await db.UsageTransactions.CountAsync());
        Assert.Equal(providerCallsBeforeReplay,
            services.GetRequiredService<FakeProviderCallLog>().Calls(FakeProviderKind.Movie));
    }
}
