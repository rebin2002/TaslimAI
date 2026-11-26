using System.Diagnostics;
using System.Globalization;
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
// Real FFmpeg-created media is returned by a deterministic adapter; the real worker,
// private-file publisher, SQLite store, and usage ledger execute normally.
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
    public async Task Missing_provider_duration_fails_before_clip_publication_and_keeps_known_provider_cost_nonbillable()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie measured duration failure fixture");
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Synthetic FFmpeg output without provider duration" },
            idempotencyKey: $"missing-measured-duration-{Guid.NewGuid():N}");
        var terminal = await MovieDurationTestHelpers.WaitForTerminalAsync(client, generated.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Empty(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.GenerationJobId == generated.Job.Id);
        Assert.Equal(1, await db.UsageTransactions.CountAsync(item => item.GenerationJobId == generated.Job.Id));
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal($"generation:{generated.Job.Id:N}", usage.RequestId);
        Assert.Equal(0.037m, usage.ProviderCostUsd);
        Assert.True(usage.ProviderCostKnown);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(usage.IsBillable);
        Assert.NotNull(usage.ActualRecordedAt);

        var clip = await db.MovieClips.AsNoTracking().SingleAsync(item => item.Id == generated.ClipId);
        Assert.Equal(MovieClipStatuses.Failed, clip.Status);
        Assert.Null(clip.AssetId);
        Assert.False(await db.Assets.AsNoTracking()
            .AnyAsync(item => item.SourceGenerationJobId == generated.Job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking()
            .AnyAsync(item => item.GenerationJobId == generated.Job.Id));
        Assert.Empty(await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.Type == CreditLedgerEntryType.Debit).ToListAsync());
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

    public async Task<MovieVideoProviderOutput> RetrieveAsync(
        string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken)
    {
        var media = await SyntheticMovieMedia.GetAsync();
        // The fixture is a decodable one-second MP4; the only missing evidence is
        // the provider-reported measured duration needed to mark the clip ready.
        return new MovieVideoProviderOutput(
            "video/mp4",
            "synthetic-ffmpeg.mp4",
            media.Bytes.LongLength,
            _ => Task.FromResult<Stream>(new MemoryStream(media.Bytes, writable: false)),
            null,
            null,
            0.05m,
            0.037m,
            "USD",
            UsageCostBasis.Actual,
            "{\"mediaFixture\":\"local-ffmpeg\",\"providerUsage\":\"known-actual\"}");
    }

    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class MeasuredDurationMovieFactory : MovieOperationalApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider, MeasuredDurationMovieProvider>();
        });
    }
}

public sealed class MovieMeasuredDurationSuccessAccountingTests : IClassFixture<MeasuredDurationMovieFactory>
{
    private readonly MeasuredDurationMovieFactory factory;

    public MovieMeasuredDurationSuccessAccountingTests(MeasuredDurationMovieFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Ready_clip_and_single_usage_finalization_follow_the_ffprobe_measured_runtime()
    {
        var media = await SyntheticMovieMedia.GetAsync();
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(
            client, title: "Movie measured duration success fixture");
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Synthetic FFmpeg output with measured duration" },
            idempotencyKey: $"measured-duration-success-{Guid.NewGuid():N}");
        var terminal = await MovieDurationTestHelpers.WaitForTerminalAsync(client, generated.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Single(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var clip = await db.MovieClips.AsNoTracking().SingleAsync(item => item.Id == generated.ClipId);
        Assert.Equal(MovieClipStatuses.Ready, clip.Status);
        Assert.Equal(media.MeasuredDurationSeconds, clip.DurationSeconds);
        Assert.NotNull(clip.AssetId);

        var asset = await db.Assets.AsNoTracking()
            .SingleAsync(item => item.Id == clip.AssetId && item.SourceGenerationJobId == generated.Job.Id);
        var output = await db.GenerationJobOutputs.AsNoTracking()
            .SingleAsync(item => item.GenerationJobId == generated.Job.Id);
        Assert.Equal(1, await db.GenerationJobOutputs.CountAsync(item => item.GenerationJobId == generated.Job.Id));
        Assert.NotNull(output.StoredFileId);
        var storedFileId = output.StoredFileId!.Value;
        var storedFile = await db.StoredFiles.AsNoTracking()
            .SingleAsync(item => item.Id == storedFileId);
        Assert.Equal(StoredFileStatus.Ready, storedFile.Status);
        Assert.Equal(asset.Id, clip.AssetId);

        var usage = await db.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.GenerationJobId == generated.Job.Id);
        Assert.Equal(1, await db.UsageTransactions.CountAsync(item => item.GenerationJobId == generated.Job.Id));
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal($"generation:{generated.Job.Id:N}", usage.RequestId);
        Assert.Equal(0.037m, usage.ProviderCostUsd);
        Assert.True(usage.ProviderCostKnown);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Empty(await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.UsageTransactionId == usage.Id && item.Type == CreditLedgerEntryType.Debit).ToListAsync());
    }
}

public sealed class MeasuredDurationMovieProvider : IMovieVideoProvider
{
    public string Key => "fake-movie";
    public bool IsAvailable => true;
    public IReadOnlyCollection<string> SupportedOperations { get; } =
        [MovieStudioOperations.QuickMovie, MovieStudioOperations.SceneClip];

    public Task<MovieVideoSubmission> SubmitAsync(
        MovieVideoGenerationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new MovieVideoSubmission("measured-duration-provider-job"));

    public Task<MovieVideoProviderStatus> GetStatusAsync(
        string providerJobId, CancellationToken cancellationToken) =>
        Task.FromResult(new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Succeeded, 100));

    public async Task<MovieVideoProviderOutput> RetrieveAsync(
        string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken)
    {
        var media = await SyntheticMovieMedia.GetAsync();
        return new MovieVideoProviderOutput(
            "video/mp4",
            "synthetic-ffmpeg.mp4",
            media.Bytes.LongLength,
            _ => Task.FromResult<Stream>(new MemoryStream(media.Bytes, writable: false)),
            media.MeasuredDurationSeconds,
            null,
            0.05m,
            0.037m,
            "USD",
            UsageCostBasis.Actual,
            "{\"mediaFixture\":\"local-ffmpeg\",\"durationSource\":\"ffprobe\",\"providerUsage\":\"known-actual\"}");
    }

    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class MovieDurationTestHelpers
{
    public static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(terminal);
            if (terminal!.Status is "Succeeded" or "Failed" or "Cancelled") return terminal;
            await Task.Delay(50);
        }

        Assert.NotNull(terminal);
        Assert.True(terminal!.Status is "Succeeded" or "Failed" or "Cancelled",
            $"Movie generation job did not reach a terminal state; last status was {terminal.Status}.");
        return terminal;
    }
}

internal sealed record SyntheticMovieMedia(byte[] Bytes, int MeasuredDurationSeconds)
{
    private static readonly Lazy<Task<SyntheticMovieMedia>> Cached = new(CreateAsync);

    public static Task<SyntheticMovieMedia> GetAsync() => Cached.Value;

    private static async Task<SyntheticMovieMedia> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"taslim-movie-duration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.mp4");
        try
        {
            await RunAsync("ffmpeg",
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=32x32:r=25",
                "-t", "1", "-an", "-c:v", "mpeg4", "-q:v", "5",
                "-movflags", "+faststart", "-y", path);
            var measured = await RunAsync("ffprobe",
                "-v", "error", "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1", path);
            if (!decimal.TryParse(measured, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                || seconds is <= 0 or > 3_600)
                throw new InvalidOperationException($"ffprobe returned an invalid synthetic media duration: '{measured}'.");
            var roundedDuration = checked((int)Math.Round(seconds, MidpointRounding.AwayFromZero));
            if (roundedDuration != 1 || Math.Abs(seconds - roundedDuration) > 0.001m)
                throw new InvalidOperationException($"Expected one second of synthetic media, but ffprobe measured {seconds.ToString(CultureInfo.InvariantCulture)} seconds.");
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0) throw new InvalidOperationException("FFmpeg generated an empty synthetic MP4.");
            return new SyntheticMovieMedia(bytes, roundedDuration);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {executable} for the synthetic Movie media fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} failed while building the synthetic Movie fixture (exit {process.ExitCode}): {error}");
        return output.Trim();
    }
}
