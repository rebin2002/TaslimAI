using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// End-to-end, provider-free regression for failed measured-duration publication.
// The real worker, private-file publisher and usage ledger execute normally;
// only the external Movie video provider is replaced with a deterministic fake.
public sealed class MissingMeasuredDurationMovieFactory : MovieOperationalApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider, MissingMeasuredDurationMovieProvider>();
        });
    }
}

public sealed class MovieDurationFailureAccountingTests : IClassFixture<MissingMeasuredDurationMovieFactory>
{
    private readonly MissingMeasuredDurationMovieFactory factory;

    public MovieDurationFailureAccountingTests(MissingMeasuredDurationMovieFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Missing_provider_duration_fails_before_clip_publication_or_usage_finalization()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie measured duration failure fixture");
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Synthetic output without measured duration" },
            idempotencyKey: $"missing-measured-duration-{Guid.NewGuid():N}");

        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>(
                $"/api/generation/jobs/{generated.Job.Id}");
            Assert.NotNull(terminal);
            if (terminal.Status is "Succeeded" or "Failed" or "Cancelled") break;
            await Task.Delay(50);
        }
        Assert.NotNull(terminal);
        Assert.Equal("Failed", terminal.Status);
        Assert.Empty(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.GenerationJobId == generated.Job.Id);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);

        var clip = await db.MovieClips.AsNoTracking().SingleAsync(item => item.Id == generated.ClipId);
        Assert.Equal(MovieClipStatuses.Failed, clip.Status);
        Assert.Null(clip.AssetId);
        Assert.False(await db.Assets.AsNoTracking()
            .AnyAsync(item => item.SourceGenerationJobId == generated.Job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking()
            .AnyAsync(item => item.GenerationJobId == generated.Job.Id));
    }
}

public sealed class MissingMeasuredDurationMovieProvider : IMovieVideoProvider
{
    public string Key => "fake-movie";
    public bool IsAvailable => true;
    public IReadOnlyCollection<string> SupportedOperations { get; } =
        [MovieStudioOperations.QuickMovie, MovieStudioOperations.SceneClip];

    public Task<MovieVideoSubmission> SubmitAsync(
        MovieVideoGenerationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new MovieVideoSubmission("missing-duration-provider-job"));

    public Task<MovieVideoProviderStatus> GetStatusAsync(
        string providerJobId, CancellationToken cancellationToken) =>
        Task.FromResult(new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Succeeded, 100));

    public Task<MovieVideoProviderOutput> RetrieveAsync(
        string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken)
    {
        // The validation failure is the absent measured duration, not MIME or size.
        var bytes = new byte[] {
            0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D,
            0, 0, 0, 0, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0
        };
        return Task.FromResult(new MovieVideoProviderOutput(
            "video/mp4",
            "synthetic.mp4",
            bytes.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
            null, null, null, null, null, null, null));
    }

    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
