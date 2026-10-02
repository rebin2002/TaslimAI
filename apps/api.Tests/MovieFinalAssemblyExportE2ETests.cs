using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-safe acceptance for the final movie export path. The real controllers,
/// authorization, persistence, generation worker, output validation, private asset
/// publication and download path are exercised; only the video provider and the
/// FFmpeg assembly executor are replaced with deterministic in-memory doubles.
/// </summary>
public sealed class MovieFinalAssemblyApiFactory : MovieOperationalApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MovieFinalAssembly:Enabled", "true");
        builder.UseSetting("MovieFinalAssembly:FfmpegPath", "unused-deterministic-executor");
        builder.UseSetting("GenerationJobs:PollIntervalMilliseconds", "25");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieFinalAssemblyExecutor>();
            services.AddSingleton<IMovieFinalAssemblyExecutor, DeterministicMovieFinalAssemblyExecutor>();
        });
    }
}

/// <summary>
/// Deterministic replacement for the local FFmpeg executor. It never spawns a
/// process and never touches the network, but it produces a real byte stream that
/// the normal StoredFile/Asset publication path persists and serves.
/// </summary>
internal sealed class DeterministicMovieFinalAssemblyExecutor : IMovieFinalAssemblyExecutor
{
    public string Key => "deterministic-test";
    public bool IsAvailable => true;

    public Task<MovieFinalAssemblyExecutionResult> ExecuteAsync(
        MovieFinalAssemblyExecutionRequest request,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(50);
        // A real, minimal ISO base media file header so the production
        // deterministic output quality control accepts the artifact.
        var payload = new byte[4096];
        payload[3] = 0x18;                                  // ftyp box size (24)
        "ftyp"u8.CopyTo(payload.AsSpan(4));                  // box type
        "isom"u8.CopyTo(payload.AsSpan(8));                  // major brand
        payload[15] = 0x02;                                  // minor version
        "isom"u8.CopyTo(payload.AsSpan(16));                 // compatible brand
        "mp42"u8.CopyTo(payload.AsSpan(20));                 // compatible brand
        var duration = Math.Max(1, request.Contract.ExpectedDurationSeconds);
        return Task.FromResult(new MovieFinalAssemblyExecutionResult(
            "master.mp4",
            "video/mp4",
            payload.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            new MovieResolution(request.Contract.Profile.Width, request.Contract.Profile.Height),
            duration,
            "deterministic-final-assembly-sha256",
            "{\"assetType\":\"video\",\"contentType\":\"video/mp4\",\"executor\":\"deterministic-test\"}"));
    }
}

[CollectionDefinition("MovieFinalAssemblyAcceptance", DisableParallelization = true)]
public sealed class MovieFinalAssemblyAcceptanceCollectionDefinition { }

[Collection("MovieFinalAssemblyAcceptance")]
public sealed class MovieFinalAssemblyExportE2ETests : IClassFixture<MovieFinalAssemblyApiFactory>
{
    private readonly MovieFinalAssemblyApiFactory factory;

    public MovieFinalAssemblyExportE2ETests(MovieFinalAssemblyApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Approved_selected_take_exports_a_durable_private_master_that_downloads()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Final assembly export acceptance");
        var projectId = fixture.Project.Project.Id;
        var shotId = fixture.Shot.Id;

        var takeId = await CreateFinalizedTakeAsync(client, projectId, shotId, "final-assembly-export");

        // A finalized take with a ready private video asset is the only accepted source.
        var assembly = await MovieOperationalFixtures.PostAsync<MovieFinalAssemblyDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/final-assembly",
            new
            {
                resolutionProfile = MovieFinalAssemblyProfiles.Hd1080p,
                timeline = new[] { new { takeId, inPointSeconds = 0m } },
            },
            idempotencyKey: "final-assembly-export-1");

        Assert.Equal(MovieAssemblyStatuses.Queued, assembly.Status);
        Assert.NotNull(assembly.GenerationJobId);
        Assert.Equal(MovieFinalAssemblyProfiles.Hd1080p, assembly.ResolutionProfile);
        Assert.Equal(1_920, assembly.OutputWidth);
        Assert.Equal(1_080, assembly.OutputHeight);
        Assert.Contains(takeId, assembly.SourceTakeIds);

        var ready = await WaitForAssemblyAsync(client, assembly.Id);
        if (ready.Status != MovieAssemblyStatuses.Ready)
        {
            using var diagScope = factory.Services.CreateScope();
            var diagDb = diagScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var stored = await diagDb.MovieAssemblies.AsNoTracking().FirstAsync(item => item.Id == assembly.Id);
            var jobCode = ready.GenerationJobId is Guid gj
                ? (await diagDb.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == gj))?.ErrorCode
                : null;
            Assert.Fail($"Final assembly ended '{ready.Status}'. Assembly error: {stored.LastErrorCode ?? "none"}. Job error: {jobCode ?? "none"}.");
        }
        Assert.True(ready.Status == MovieAssemblyStatuses.Ready,
            $"Final assembly ended '{ready.Status}' (job error: {ready.Job?.ErrorCode ?? "none"}).");
        Assert.Equal(100, ready.ProgressPercent);
        Assert.Equal(MovieFinalAssemblyQcStatuses.Passed, ready.QcStatus);
        Assert.NotNull(ready.OutputAssetId);
        Assert.NotNull(ready.CompletedAt);

        // The published master is a private asset served through the movie-scoped
        // download endpoint; no provider URL or storage key is exposed.
        using var download = await client.GetAsync($"/api/movie-studio/final-assemblies/{assembly.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("video/mp4", download.Content.Headers.ContentType?.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(4_096, bytes.Length);

        // The durable asset really exists in private storage and is workspace-scoped.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile)
                .FirstOrDefaultAsync(item => item.Id == ready.OutputAssetId!.Value);
            Assert.NotNull(asset);
            Assert.Equal(AssetTypes.Video, asset!.AssetType);
            Assert.Equal(fixture.Owner.PersonalWorkspace.Id, asset.WorkspaceId);
            Assert.NotNull(asset.StoredFile);
            Assert.Equal(StoredFileStatus.Ready, asset.StoredFile!.Status);
            // Full Movie assets are scoped to the movie's root Project record.
            Assert.Equal(fixture.Project.Project.ProjectId, asset.ProjectId);
        }

        // Idempotency: replaying the identical request returns the same assembly.
        var replay = await MovieOperationalFixtures.PostAsync<MovieFinalAssemblyDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/final-assembly",
            new
            {
                resolutionProfile = MovieFinalAssemblyProfiles.Hd1080p,
                timeline = new[] { new { takeId, inPointSeconds = 0m } },
            },
            idempotencyKey: "final-assembly-export-1");
        Assert.Equal(assembly.Id, replay.Id);
    }

    [Fact]
    public async Task Assembly_without_any_selected_take_is_rejected_before_any_job_is_created()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Final assembly empty project");
        var projectId = fixture.Project.Project.Id;

        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{projectId}/final-assembly",
            new { resolutionProfile = MovieFinalAssemblyProfiles.Hd1080p },
            idempotencyKey: "final-assembly-empty-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ASSEMBLY_TIMELINE_INVALID", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.MovieAssemblies.AsNoTracking().AnyAsync(item => item.MovieProjectId == projectId));
    }

    [Fact]
    public async Task Unfinished_assembly_has_no_downloadable_output()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Final assembly not ready");
        var projectId = fixture.Project.Project.Id;
        var shotId = fixture.Shot.Id;

        var takeId = await CreateFinalizedTakeAsync(client, projectId, shotId, "final-assembly-pending");
        var assembly = await MovieOperationalFixtures.PostAsync<MovieFinalAssemblyDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/final-assembly",
            new
            {
                resolutionProfile = MovieFinalAssemblyProfiles.Hd1080p,
                timeline = new[] { new { takeId, inPointSeconds = 0m } },
            },
            idempotencyKey: "final-assembly-pending-1");

        // Force the durable "not published yet" state by clearing the pointer that
        // only a successful worker completion may set.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.MovieAssemblies.Where(item => item.Id == assembly.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.AssetId, (Guid?)null));
        }

        using var download = await client.GetAsync($"/api/movie-studio/final-assemblies/{assembly.Id}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        var body = await download.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_FINAL_ASSEMBLY_NOT_READY", body.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<Guid> CreateFinalizedTakeAsync(HttpClient client, Guid projectId, Guid shotId, string keyPrefix)
    {
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{shotId}/generate",
            new { title = $"{keyPrefix} source take" },
            idempotencyKey: $"{keyPrefix}-generate");
        var terminal = await WaitForJobAsync(client, generated.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), terminal.Status);

        var assetId = await WaitForClipAssetAsync(generated.ClipId);
        var take = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/shots/{shotId}/takes",
            new
            {
                label = $"{keyPrefix} raw take",
                qualityLevel = MovieQualityLevels.Standard,
                autoDirectorEnabled = false,
                movieClipId = generated.ClipId,
                generationJobId = generated.Job.Id,
                assetId,
                notes = "Raw footage candidate retained for the export acceptance.",
            });
        var approved = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/takes/{take.Id}/approvals",
            new { decision = MovieApprovalDecisions.Approved, comment = "Reviewable take." });
        Assert.Equal(MovieTakeStatuses.Approved, approved.Status);

        using (var select = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{take.Id}/select", null))
            Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);
        using (var finalize = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{take.Id}/finalize", null))
            Assert.Equal(HttpStatusCode.NoContent, finalize.StatusCode);
        return take.Id;
    }

    private async Task<Guid> WaitForClipAssetAsync(Guid clipId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        for (var attempt = 0; attempt < 160; attempt++)
        {
            var assetId = await db.MovieClips.AsNoTracking()
                .Where(item => item.Id == clipId)
                .Select(item => item.AssetId)
                .FirstOrDefaultAsync();
            if (assetId.HasValue) return assetId.Value;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Movie clip {clipId} never received a private asset.");
    }

    private static async Task<GenerationJobDto> WaitForJobAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (current?.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }

    private static async Task<MovieFinalAssemblyDto> WaitForAssemblyAsync(HttpClient client, Guid assemblyId)
    {
        MovieFinalAssemblyDto? current = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            current = await client.GetFromJsonAsync<MovieFinalAssemblyDto>($"/api/movie-studio/final-assemblies/{assemblyId}");
            if (current?.Status is MovieAssemblyStatuses.Ready or MovieAssemblyStatuses.Failed or MovieAssemblyStatuses.Cancelled) return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Final assembly {assemblyId} did not reach a terminal state. Last status: {current?.Status}.");
    }
}
