using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionProviderUnavailableTests
{
    [Fact]
    public async Task Approved_render_rejects_provider_unavailable_after_preview_without_clip_job_or_usage_write()
    {
        // Prepare a fully approved source while the synthetic adapter is ready.
        // Flipping availability afterwards exercises admission at queue time.
        using var factory = new MovieClipDurationApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Approved render provider unavailable", sceneDurationSeconds: 5);
        var shotId = fixture.Shot.Id;

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
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
        var calls = scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>();
        var versionsBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId);
        var transactionsBefore = await db.UsageTransactions.CountAsync();

        scope.ServiceProvider.GetRequiredService<FakeProviderScenarioCatalog>()[FakeProviderKind.Movie] =
            FakeProviderScenario.AllProvidersUnavailable;

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/render",
            new { sourceVersionId = approvedMotion.Id, label = "Must not queue",
                confirmationAccepted = true }, idempotencyKey: "approved-provider-unavailable");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_PROVIDER_UNAVAILABLE",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "approved-provider-unavailable"));
        Assert.Equal(transactionsBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Approved_render_rejects_unsupported_single_clip_duration_without_persisting_or_reserving_usage()
    {
        // The production route must enforce the selected adapter's per-clip
        // duration range even when an approved source already exists.
        // This does not call a paid provider or execute a generation worker.
        using var factory = new MovieClipDurationApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Approved render unsupported duration", sceneDurationSeconds: 11);
        var shotId = fixture.Shot.Id;

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
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
        var calls = scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>();
        var versionCountBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId);
        var transactionCountBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/render",
            new { sourceVersionId = approvedMotion.Id, label = "Do not queue unsupported clip",
                confirmationAccepted = true }, idempotencyKey: "approved-duration-unsupported-11");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionCountBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "approved-duration-unsupported-11"));
        Assert.Equal(transactionCountBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }
    [Fact]
    public async Task Approved_render_rejects_unsupported_priced_execution_intent_without_persisting_or_reserving_usage()
    {
        // The production route must reject a priced output profile that its selected
        // adapter cannot honor, even with an approved source and cost confirmation.
        // This does not call a paid provider or execute a generation worker.
        using var factory = new MovieClipIntentDeniedApiFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Approved render unsupported priced intent", sceneDurationSeconds: 5);
        var shotId = fixture.Shot.Id;

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id, compositionJson = "{\"frame\":\"approved\"}" });
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
        var calls = scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>();
        var versionCountBefore = await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId);
        var transactionCountBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/render",
            new { sourceVersionId = approvedMotion.Id, label = "Do not queue unsupported output",
                targetResolution = "720p", qualityTier = "Standard",
                confirmationAccepted = true }, idempotencyKey: "approved-cost-intent-unsupported");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionCountBefore, await db.MovieProductionVersions.CountAsync(item => item.MovieShotId == shotId));
        Assert.False(await db.MovieClips.AnyAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.IdempotencyKey == "approved-cost-intent-unsupported"));
        Assert.Equal(transactionCountBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, calls.Calls(FakeProviderKind.Movie));
    }

}
