using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

// Synthetic provider: no worker or paid call. A deliberately small declared
// prompt limit distinguishes ordinary creative text from the JSON direction
// that the actual video adapters also include in their assembled prompts.
public sealed class MovieStructuredPromptBudgetFactory : MovieOperationalApiFactory
{
    public bool IncludeWorldContextInPrompt { get; init; }
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>())
            {
                SceneClipPromptMaxCharacters = 220,
                SceneClipWorldContextInPrompt = IncludeWorldContextInPrompt,
                SceneClipDurationRange = new MovieClipDurationRange(2, 10),
            });
        });
    }
}

public sealed class MovieProductionStructuredPromptAdmissionTests
{
    [Fact]
    public async Task Legacy_shot_generation_rejects_oversized_structured_direction_before_reservation()
    {
        using var factory = new MovieStructuredPromptBudgetFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Short Movie", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject)
            .SingleAsync(item => item.Id == fixture.Shot.Id);
        var movie = shot.Scene.MovieProject;

        // The readable description and style fit the declared prompt budget.
        // Required serialized shot direction does not; legacy admission must
        // reject that overflow before estimating or reserving paid usage.
        var readableLength = (long)shot.Description.Trim().Length
            + (movie.Style?.Trim().Length ?? 0)
            + (movie.AdditionalInstructions?.Trim().Length ?? 0);
        Assert.True(readableLength < 220, $"Fixture must fit before structured direction: {readableLength}");
        shot.CameraAndFraming = new string('C', 240);
        await db.SaveChangesAsync();

        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/generate",
            new { title = "Short clip", sourceResolution = "720p",
                targetResolution = "720p", qualityTier = "Standard",
                confirmationAccepted = true },
            idempotencyKey: "legacy-structured-prompt-overflow");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MOVIE_CLIP_PROMPT_UNSUPPORTED",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Legacy_shot_generation_counts_world_context_before_cost_reservation_when_provider_embeds_it()
    {
        // The synthetic adapter opts into using the world snapshot, as Manus
        // does. The other creative sections fit exactly within the configured
        // capacity; even the smallest world snapshot ("{}") exceeds it.
        // Provider calls and generation workers are disabled.
        using var factory = new MovieStructuredPromptBudgetFactory
        {
            IncludeWorldContextInPrompt = true,
        };
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "World Prompt Film", sceneDurationSeconds: 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject)
            .SingleAsync(item => item.Id == movie.Shot.Id);
        var project = shot.Scene.MovieProject;
        var withoutWorld = (long)shot.Description.Trim().Length
            + (project.Style?.Trim().Length ?? 0)
            + (project.AdditionalInstructions?.Trim().Length ?? 0)
            + MovieStudioService.SceneSnapshot(shot.Scene).Length
            + MovieStudioService.ShotSnapshot(shot).Length;
        Assert.InRange(withoutWorld, 1, int.MaxValue - 2);
        var provider = Assert.IsType<FakeMovieVideoProvider>(
            scope.ServiceProvider.GetRequiredService<IMovieVideoProvider>());
        provider.SceneClipPromptMaxCharacters = checked((int)withoutWorld + 1);

        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shot.Id}/generate",
            new { title = "Five-second world shot", sourceResolution = "720p",
                targetResolution = "720p", qualityTier = "Standard",
                confirmationAccepted = true },
            idempotencyKey: "legacy-world-context-overflow");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MOVIE_CLIP_PROMPT_UNSUPPORTED",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie));
    }

    [Fact]
    public async Task Approved_render_rejects_oversized_structured_direction_before_job_or_usage_reservation()
    {
        using var factory = new MovieStructuredPromptBudgetFactory();
        using (var setup = factory.Services.CreateScope())
            setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

        using var client = factory.CreateClient();
        var movie = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Short Movie", sceneDurationSeconds: 5);
        var shotId = movie.Shot.Id;

        var storyboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.StoryboardCandidate, compositionJson = "{\"frame\":\"wide\"}" });
        var approvedStoryboard = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/production/versions/{storyboard.Id}/review",
            new { approve = true });
        var keyframe = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(
            client, $"/api/movie-studio/shots/{shotId}/production/versions",
            new { stage = MovieProductionStages.ProductionKeyframe,
                sourceVersionId = approvedStoryboard.Id,
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
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject)
            .SingleAsync(item => item.Id == shotId);
        var plainCreativeLength = "Short Movie".Length
            + (shot.Scene.MovieProject.Style?.Trim().Length ?? 0)
            + (shot.Scene.MovieProject.AdditionalInstructions?.Trim().Length ?? 0)
            + shot.Description.Trim().Length;
        // The legacy check counted these readable fields only. The scene,
        // shot and guide JSON cause the *actual* provider prompt to overflow.
        Assert.True(plainCreativeLength < 220,
            $"Fixture must be short before structured JSON: {plainCreativeLength}");

        var versionsBefore = await db.MovieProductionVersions.CountAsync();
        var clipsBefore = await db.MovieClips.CountAsync();
        var jobsBefore = await db.GenerationJobs.CountAsync();
        var usageBefore = await db.UsageTransactions.CountAsync();

        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/shots/{shotId}/production/render",
            new { sourceVersionId = approvedMotion.Id, title = "Short Movie",
                targetResolution = "720p", qualityTier = "Standard",
                confirmationAccepted = true }, idempotencyKey: "structured-prompt-too-long");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MOVIE_CLIP_PROMPT_UNSUPPORTED",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(versionsBefore, await db.MovieProductionVersions.CountAsync());
        Assert.Equal(clipsBefore, await db.MovieClips.CountAsync());
        Assert.Equal(jobsBefore, await db.GenerationJobs.CountAsync());
        Assert.Equal(usageBefore, await db.UsageTransactions.CountAsync());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<FakeProviderCallLog>()
            .Calls(FakeProviderKind.Movie));
    }
}
