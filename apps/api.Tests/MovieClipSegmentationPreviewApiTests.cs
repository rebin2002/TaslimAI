using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;
using Microsoft.AspNetCore.Hosting;

namespace Taslim.Api.Tests;

public sealed class MovieLongFormSceneSegmentationPreviewApiFactory : MovieOperationalApiFactory
{
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
                SceneClipDurationRange = new MovieClipDurationRange(2, 10),
            });
        });
    }
}

public sealed class MovieLongFormSceneSegmentationPreviewApiTests : IClassFixture<MovieLongFormSceneSegmentationPreviewApiFactory>
{
    private readonly MovieLongFormSceneSegmentationPreviewApiFactory factory;

    public MovieLongFormSceneSegmentationPreviewApiTests(MovieLongFormSceneSegmentationPreviewApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Two_hour_three_scene_preview_returns_720_bounded_segments_without_creating_work()
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Two Hour Segmentation Owner");
        var project = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Two Hour Three Scene Preview",
                description = "A provider-free boundary-preserving plan.",
                durationSeconds = 7_200,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        var opening = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/scenes",
            new { title = "Opening", summary = "Twenty minutes.", durationSeconds = 1_200 });
        var middle = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/scenes",
            new { title = "Middle", summary = "One hour.", durationSeconds = 3_600 });
        var ending = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/scenes",
            new { title = "Ending", summary = "Forty minutes.", durationSeconds = 2_400 });

        var preview = await MovieOperationalFixtures.GetAsync<MovieClipSegmentationPreviewDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/clip-segmentation-preview");

        Assert.Equal(7_200, preview.TotalDurationSeconds);
        Assert.Equal("fake-movie", preview.ProviderKey);
        Assert.True(preview.ProviderDeclaresSceneClipOperation);
        Assert.True(preview.CanSplitByDuration);
        Assert.True(preview.CanSplitByScene);
        Assert.Equal(720, preview.Segments.Count);
        var sceneSegments = Assert.IsAssignableFrom<IReadOnlyList<MoviePlannedSceneClipSegment>>(preview.SceneSegments);
        Assert.Equal(720, sceneSegments.Count);
        Assert.Equal(120, sceneSegments.Count(item => item.SceneId == opening.Id));
        Assert.Equal(360, sceneSegments.Count(item => item.SceneId == middle.Id));
        Assert.Equal(240, sceneSegments.Count(item => item.SceneId == ending.Id));
        Assert.Equal(7_200, sceneSegments.Sum(item => item.DurationSeconds));
        Assert.Equal(new MoviePlannedSceneClipSegment(opening.Id, 1, 1, 0, 0, 10), sceneSegments[0]);
        Assert.Equal(new MoviePlannedSceneClipSegment(middle.Id, 2, 121, 1_200, 0, 10), sceneSegments[120]);
        Assert.Equal(new MoviePlannedSceneClipSegment(ending.Id, 3, 481, 4_800, 0, 10), sceneSegments[480]);
        Assert.Equal(new MoviePlannedSceneClipSegment(ending.Id, 3, 720, 7_190, 2_390, 10), sceneSegments[^1]);
        Assert.All(sceneSegments, item => Assert.InRange(item.DurationSeconds, 2, 10));
        Assert.Equal(0, factory.Calls.Calls(FakeProviderKind.Movie));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.MovieClips.AsNoTracking().AnyAsync(item => item.MovieProjectId == project.Project.Id));
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(item => item.WorkspaceId == owner.PersonalWorkspace.Id));
    }
}

/// <summary>
/// Authenticated read-only duration planning against a deterministic fake adapter.
/// No background generation worker, paid provider or customer charge is enabled.
/// </summary>
public sealed class MovieClipSegmentationPreviewApiTests : IClassFixture<MovieOperationalNoWorkerFactory>
{
    private readonly MovieOperationalNoWorkerFactory factory;

    public MovieClipSegmentationPreviewApiTests(MovieOperationalNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Authenticated_two_hour_project_returns_duration_only_preview_without_creating_work()
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Long Form Preview Owner");
        var project = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Two Hour Preview Only",
                description = "Previewing boundaries must never schedule generation.",
                durationSeconds = 7_200,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        var preview = await MovieOperationalFixtures.GetAsync<MovieClipSegmentationPreviewDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/clip-segmentation-preview");

        Assert.Equal(project.Project.Id, preview.MovieProjectId);
        Assert.Equal(7_200, preview.TotalDurationSeconds);
        Assert.Equal("fake-movie", preview.ProviderKey);
        Assert.True(preview.ProviderDeclaresSceneClipOperation);
        Assert.True(preview.CanSplitByDuration);
        // The fake test adapter declares 1-3600 seconds. This is NOT a claim
        // that a real production video provider accepts 3600-second clips.
        Assert.Equal(2, preview.Segments.Count);
        Assert.Equal(7_200, preview.Segments.Sum(segment => segment.DurationSeconds));
        Assert.Equal(0, preview.Segments[0].StartSeconds);
        Assert.Equal(3_600, preview.Segments[1].StartSeconds);
        Assert.False(preview.CanSplitByScene);
        Assert.Empty(preview.SceneSegments ?? []);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.MovieClips.AsNoTracking()
            .AnyAsync(clip => clip.MovieProjectId == project.Project.Id));
        Assert.False(await db.GenerationJobs.AsNoTracking()
            .AnyAsync(job => job.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking()
            .AnyAsync(tx => tx.WorkspaceId == owner.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Explicit_scene_boundaries_are_preserved_without_generation_or_charges()
    {
        using var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, "Scene Preview Owner");
        var project = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Timed scenes",
                description = "Storyboard boundaries are not interchangeable with a clip slot.",
                durationSeconds = 21,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        var first = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/scenes",
            new { title = "Opening", summary = "First chapter", durationSeconds = 10 });
        var second = await MovieOperationalFixtures.PostAsync<MovieSceneDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/scenes",
            new { title = "Closing", summary = "Second chapter", durationSeconds = 11 });

        var preview = await MovieOperationalFixtures.GetAsync<MovieClipSegmentationPreviewDto>(
            client, $"/api/movie-studio/projects/{project.Project.Id}/clip-segmentation-preview");

        Assert.True(preview.CanSplitByDuration);
        Assert.True(preview.CanSplitByScene);
        var sceneSegments = Assert.IsAssignableFrom<IReadOnlyList<MoviePlannedSceneClipSegment>>(preview.SceneSegments);
        Assert.Equal(2, sceneSegments.Count);
        Assert.Equal(new MoviePlannedSceneClipSegment(first.Id, 1, 1, 0, 0, 10), sceneSegments[0]);
        Assert.Equal(new MoviePlannedSceneClipSegment(second.Id, 2, 2, 10, 0, 11), sceneSegments[1]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.MovieClips.AsNoTracking().AnyAsync(clip => clip.MovieProjectId == project.Project.Id));
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.WorkspaceId == owner.PersonalWorkspace.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(tx => tx.WorkspaceId == owner.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Foreign_user_cannot_preview_another_workspace_movie()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(ownerClient, "Private Plan Owner");
        var project = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            ownerClient, "/api/movie-studio/projects", new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title = "Private Long Form Plan",
                description = "Keep the timeline and its plan private.",
                durationSeconds = 7_200,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        using var otherClient = factory.CreateClient();
        await MovieOperationalFixtures.RegisterAsync(otherClient, "Foreign Preview User");
        using var result = await otherClient.GetAsync(
            $"/api/movie-studio/projects/{project.Project.Id}/clip-segmentation-preview");
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }
}
