using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

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
