using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieFinalAssemblyCancellationApiFactory : MovieOperationalApiFactory
{
    public FinalAssemblyMaterializationGate MaterializationGate { get; } = new();
    public CancellationGuardMovieFinalAssemblyExecutor AssemblyExecutor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MovieFinalAssembly:Enabled", "true");
        builder.UseSetting("GenerationJobs:PollIntervalMilliseconds", "10");
        builder.UseSetting("GenerationJobs:CancellationPollMilliseconds", "10");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(MaterializationGate);
            services.AddSingleton(AssemblyExecutor);
            services.RemoveAll<IMovieFinalAssemblyExecutor>();
            services.AddSingleton<IMovieFinalAssemblyExecutor>(sp => sp.GetRequiredService<CancellationGuardMovieFinalAssemblyExecutor>());
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(sp => new GateFileStorageService(
                new LocalFileStorageService(
                    sp.GetRequiredService<IOptions<Taslim.Api.Files.FileOptions>>(),
                    sp.GetRequiredService<ILogger<LocalFileStorageService>>()),
                sp.GetRequiredService<FinalAssemblyMaterializationGate>()));
        });
    }
}

public sealed class FinalAssemblyMaterializationGate
{
    private int armed;
    private int claimed;

    public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Arm() => Interlocked.Exchange(ref armed, 1);

    public bool TryClaim() => Volatile.Read(ref armed) == 1 && Interlocked.Exchange(ref claimed, 1) == 0;
}

internal sealed class GateFileStorageService(IFileStorageService inner, FinalAssemblyMaterializationGate gate) : IFileStorageService
{
    public string ProviderKey => inner.ProviderKey;

    public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => inner.StoreAsync(storageKey, content, cancellationToken);

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var stream = await inner.OpenReadAsync(storageKey, cancellationToken);
        return stream is null || !gate.TryClaim() ? stream : new ReleaseOnDisposeStream(stream, gate, cancellationToken);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => inner.DeleteAsync(storageKey, cancellationToken);

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => inner.ExistsAsync(storageKey, cancellationToken);

    private sealed class ReleaseOnDisposeStream(Stream inner, FinalAssemblyMaterializationGate gate, CancellationToken cancellationToken) : Stream
    {
        private int releaseWaitStarted;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await WaitForReleaseAsync();
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WaitForReleaseAsync().GetAwaiter().GetResult();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task WaitForReleaseAsync()
        {
            if (Interlocked.Exchange(ref releaseWaitStarted, 1) == 0)
            {
                gate.Reached.TrySetResult(true);
                await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
        }
    }
}

public sealed class CancellationGuardMovieFinalAssemblyExecutor : IMovieFinalAssemblyExecutor
{
    public string Key => "cancellation-cleanup-test";
    public bool IsAvailable => true;
    public int InvocationCount { get; private set; }

    public Task<MovieFinalAssemblyExecutionResult> ExecuteAsync(
        MovieFinalAssemblyExecutionRequest request,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        InvocationCount++;
        throw new InvalidOperationException("The executor must not run after source materialization cancellation.");
    }
}

[CollectionDefinition("MovieFinalAssemblyCancellation", DisableParallelization = true)]
public sealed class MovieFinalAssemblyCancellationCollectionDefinition { }

[Collection("MovieFinalAssemblyCancellation")]
public sealed class MovieFinalAssemblyCancellationCleanupTests : IClassFixture<MovieFinalAssemblyCancellationApiFactory>
{
    private readonly MovieFinalAssemblyCancellationApiFactory factory;

    public MovieFinalAssemblyCancellationCleanupTests(MovieFinalAssemblyCancellationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Cancellation_after_source_materialization_deletes_temporary_inputs_before_job_settlement()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Assembly cancellation cleanup");
        var (takeId, sourceAssetId) = await CreateFinalizedTakeAsync(client, fixture.Project.Project.Id, fixture.Shot.Id);

        factory.MaterializationGate.Arm();
        try
        {
            var assembly = await MovieOperationalFixtures.PostAsync<MovieFinalAssemblyDto>(
                client,
                $"/api/movie-studio/projects/{fixture.Project.Project.Id}/final-assembly",
                new
                {
                    resolutionProfile = MovieFinalAssemblyProfiles.Hd1080p,
                    timeline = new[] { new { takeId, inPointSeconds = 0m } },
                },
                idempotencyKey: "assembly-cancellation-cleanup-1");

            await factory.MaterializationGate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var cancel = await MovieOperationalFixtures.SendWithCsrfAsync(
                client,
                HttpMethod.Post,
                $"/api/generation/jobs/{assembly.GenerationJobId}/cancel",
                null);
            Assert.True(cancel.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
                $"Cancellation request returned {(int)cancel.StatusCode}: {await cancel.Content.ReadAsStringAsync()}");

            factory.MaterializationGate.Release.TrySetResult(true);
            var terminal = await WaitForJobAsync(client, assembly.GenerationJobId!.Value);
            Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);
            Assert.Equal(0, factory.AssemblyExecutor.InvocationCount);

            var inputRoot = Path.Combine(Path.GetTempPath(), "taslim-final-assembly-inputs");
            var leftovers = Directory.Exists(inputRoot)
                ? Directory.EnumerateFiles(inputRoot, $"{sourceAssetId:N}.*", SearchOption.AllDirectories).ToArray()
                : [];
            Assert.Empty(leftovers);
        }
        finally
        {
            factory.MaterializationGate.Release.TrySetResult(true);
        }
    }

    private async Task<(Guid TakeId, Guid AssetId)> CreateFinalizedTakeAsync(HttpClient client, Guid projectId, Guid shotId)
    {
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{shotId}/generate",
            new { title = "Cancellation cleanup source take" },
            idempotencyKey: "assembly-cancellation-source-1");
        var terminal = await WaitForJobAsync(client, generated.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), terminal.Status);
        var assetId = await WaitForClipAssetAsync(generated.ClipId);
        var take = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/shots/{shotId}/takes",
            new
            {
                label = "Cancellation cleanup source take",
                qualityLevel = MovieQualityLevels.Standard,
                autoDirectorEnabled = false,
                movieClipId = generated.ClipId,
                generationJobId = generated.Job.Id,
                assetId,
            });
        _ = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/takes/{take.Id}/approvals",
            new { decision = MovieApprovalDecisions.Approved, comment = "Ready for cancellation cleanup coverage." });
        using (var select = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{take.Id}/select", null))
            Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);
        using (var finalize = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/takes/{take.Id}/finalize", null))
            Assert.Equal(HttpStatusCode.NoContent, finalize.StatusCode);
        return (take.Id, assetId);
    }

    private async Task<Guid> WaitForClipAssetAsync(Guid clipId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        for (var attempt = 0; attempt < 160; attempt++)
        {
            var assetId = await db.MovieClips.AsNoTracking().Where(item => item.Id == clipId).Select(item => item.AssetId).FirstOrDefaultAsync();
            if (assetId.HasValue) return assetId.Value;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Movie clip {clipId} never received a private asset.");
    }

    private static async Task<GenerationJobDto> WaitForJobAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (current?.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }
}
