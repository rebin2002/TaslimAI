using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

// Use the real HTTP admission, pricing, persistence and reservation path with
// workers disabled and an explicitly range-limited in-memory video provider.
public sealed class MovieClipDurationApiFactory : MovieOperationalApiFactory
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

public sealed class MovieClipDurationAdmissionTests : IClassFixture<MovieClipDurationApiFactory>
{
    private readonly MovieClipDurationApiFactory factory;

    public MovieClipDurationAdmissionTests(MovieClipDurationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task Supported_boundary_duration_queues_one_clip_without_invoking_provider(int duration)
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: $"Movie supported duration {duration}", sceneDurationSeconds: duration);
        var response = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Supported clip" },
            idempotencyKey: $"movie-duration-supported-{duration}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(duration, await db.MovieClips.Where(item => item.Id == response.ClipId)
            .Select(item => item.DurationSeconds).SingleAsync());
        Assert.Single(await db.GenerationJobs.Where(item => item.Id == response.Job.Id).ToListAsync());
    }

    [Fact]
    public async Task Unsupported_clip_duration_rejects_before_job_clip_or_usage_reservation()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie duration outside adapter range", sceneDurationSeconds: 11);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var transactionCountBefore = await db.UsageTransactions.CountAsync();

        using var rejected = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Unsupported 11-second clip" },
            idempotencyKey: "movie-duration-unsupported-11");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("MOVIE_CLIP_DURATION_UNSUPPORTED",
            await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == "movie-duration-unsupported-11"));
        Assert.Equal(transactionCountBefore, await db.UsageTransactions.CountAsync());
    }

    [Theory]
    [InlineData("720p", "720p", "Standard", "native", "16:9", true)]
    [InlineData("720p", "720p", "Standard", "native", "9:16", true)]
    [InlineData("1080p", "720p", "Standard", "native", "16:9", false)]
    [InlineData("720p", "1080p", "Standard", "native", "16:9", false)]
    [InlineData("720p", "4k", "Standard", "native", "16:9", false)]
    [InlineData("720p", "720p", "Cinematic", "native", "16:9", false)]
    [InlineData("720p", "720p", "Standard", "upscale", "16:9", false)]
    [InlineData("720p", "720p", "Standard", "native", "1:1", false)]
    public void Runway_scene_clip_only_advertises_the_execution_profile_it_submits(
        string source, string target, string quality, string path, string aspect, bool accepted)
    {
        using var httpClient = new HttpClient();
        IMovieVideoProvider provider = new RunwayMovieVideoProvider(
            httpClient, Options.Create(new MovieVideoOptions()),
            NullLogger<RunwayMovieVideoProvider>.Instance, new ProviderUrlPolicy());
        Assert.Equal(accepted, provider.SupportsSceneClipCostIntent(source, target, quality, path, aspect));
    }

    [Fact]
    public void Manus_scene_clip_output_resolution_and_quality_are_not_verified()
    {
        using var httpClient = new HttpClient();
        IMovieVideoProvider provider = new ManusMovieVideoProvider(
            httpClient, Options.Create(new MovieVideoOptions()),
            NullLogger<ManusMovieVideoProvider>.Instance);
        Assert.False(provider.SupportsSceneClipCostIntent("720p", "720p", "Standard", "native", "16:9"));
    }

    [Theory]
    [InlineData("runway", 1, false)]
    [InlineData("runway", 2, true)]
    [InlineData("runway", 10, true)]
    [InlineData("runway", 11, false)]
    [InlineData("manus", 0, false)]
    [InlineData("manus", 1, true)]
    [InlineData("manus", 30, true)]
    [InlineData("manus", 31, false)]
    [InlineData("manus", 60, false)]
    public void Real_adapter_declares_the_same_single_clip_duration_bounds_it_enforces(
        string adapter, int duration, bool supported)
    {
        using var httpClient = new HttpClient();
        IMovieVideoProvider provider = adapter == "runway"
            ? new RunwayMovieVideoProvider(httpClient, Options.Create(new MovieVideoOptions()),
                NullLogger<RunwayMovieVideoProvider>.Instance, new ProviderUrlPolicy())
            : new ManusMovieVideoProvider(httpClient, Options.Create(new MovieVideoOptions()),
                NullLogger<ManusMovieVideoProvider>.Instance);
        Assert.Equal(supported, provider.SceneClipDurationRange?.Supports(duration) == true);
    }
}
