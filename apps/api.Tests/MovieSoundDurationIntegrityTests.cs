using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSoundDurationIntegrityTests
{
    private const int RequestedDurationMilliseconds = 750;

    [Theory]
    [InlineData(0, RequestedDurationMilliseconds)]
    [InlineData(-1, RequestedDurationMilliseconds)]
    [InlineData(RequestedDurationMilliseconds + 1, RequestedDurationMilliseconds)]
    public async Task Invalid_provider_reported_duration_is_rejected_before_publication(int reportedDurationMilliseconds, int maximumDurationMilliseconds)
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = reportedDurationMilliseconds });
        var handler = CreateHandler(provider, maximumDurationMilliseconds);

        await Assert.ThrowsAsync<MovieSoundOutputInvalidException>(() => handler.ExecuteAsync(
            CreateJob(request), new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task Provider_reported_duration_above_absolute_ceiling_is_rejected_before_publication()
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = 86_400_001 });
        var handler = CreateHandler(provider, int.MaxValue);

        await Assert.ThrowsAsync<MovieSoundOutputInvalidException>(() => handler.ExecuteAsync(
            CreateJob(request), new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task Provider_reported_duration_at_configured_maximum_is_accepted()
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = RequestedDurationMilliseconds });
        var handler = CreateHandler(provider, RequestedDurationMilliseconds);

        var result = await handler.ExecuteAsync(CreateJob(request), new Progress<int>(), CancellationToken.None);

        AssertPublishedDuration(result, RequestedDurationMilliseconds);
    }

    [Fact]
    public async Task Missing_provider_duration_uses_valid_requested_duration_before_publication()
    {
        var request = CreateRequest();
        var validOutput = await new FakeMovieSoundProvider().GenerateAsync(request);
        var provider = new FixedMovieSoundProvider(validOutput with { DurationMilliseconds = null });
        var handler = CreateHandler(provider, RequestedDurationMilliseconds);

        var result = await handler.ExecuteAsync(CreateJob(request), new Progress<int>(), CancellationToken.None);

        AssertPublishedDuration(result, request.DurationMilliseconds);
    }

    private static MovieSoundGenerationInput CreateRequest(int durationMilliseconds = RequestedDurationMilliseconds) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null,
        MovieSoundKinds.SoundEffect, MovieSoundLayers.Foreground,
        "A soft latch", durationMilliseconds, null);

    private static GenerationJob CreateJob(MovieSoundGenerationInput request) => new()
    {
        Id = Guid.NewGuid(),
        InputJson = JsonSerializer.Serialize(request),
    };

    private static MovieSoundGenerationJobHandler CreateHandler(FixedMovieSoundProvider provider, int maximumDurationMilliseconds) => new(
        [provider],
        Options.Create(new MovieSoundOptions
        {
            Enabled = true,
            ProviderKey = provider.Key,
            MaxDurationMilliseconds = maximumDurationMilliseconds,
        }),
        NullLogger<MovieSoundGenerationJobHandler>.Instance);

    private static void AssertPublishedDuration(GenerationHandlerResult result, int expectedDurationMilliseconds)
    {
        using var resultDocument = JsonDocument.Parse(result.ResultJson);
        Assert.Equal(expectedDurationMilliseconds, resultDocument.RootElement.GetProperty("durationMilliseconds").GetInt32());

        var output = Assert.Single(result.Outputs);
        Assert.NotNull(output.FileArtifact);
        using var metadataDocument = JsonDocument.Parse(output.MetadataJson!);
        Assert.Equal(expectedDurationMilliseconds, metadataDocument.RootElement.GetProperty("durationMilliseconds").GetInt32());
    }

    private sealed class FixedMovieSoundProvider(MovieSoundProviderResult result) : IMovieSoundProvider
    {
        public string Key => "fixed";

        public Task<MovieSoundProviderResult> GenerateAsync(MovieSoundGenerationInput request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}

public sealed class MovieSoundFailureSettlementTests : IClassFixture<MovieSoundFailureSettlementFactory>
{
    private readonly MovieSoundFailureSettlementFactory factory;

    public MovieSoundFailureSettlementTests(MovieSoundFailureSettlementFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Invalid_reported_duration_settles_actual_provider_cost_without_charge_or_publication()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var project = await PostAsync(client, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Synthetic usage settlement",
            description = "Integration coverage for a rejected provider result.",
            durationSeconds = 12,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        var projectId = project.GetProperty("project").GetProperty("id").GetGuid();
        var scene = await PostAsync(client, $"/api/movie-studio/projects/{projectId}/scenes", new
        {
            title = "Quiet room",
            summary = "A test scene for a synthetic sound cue.",
            durationSeconds = 12,
        });
        var sceneId = scene.GetProperty("id").GetGuid();
        var track = await PostAsync(client, $"/api/movie-sound/scenes/{sceneId}/tracks", new
        {
            kind = MovieSoundKinds.SoundEffect,
            layer = MovieSoundLayers.Foreground,
            name = "Synthetic rejected cue",
            description = "A short synthetic cue used only for accounting regression coverage.",
            startMilliseconds = 0,
            endMilliseconds = 750,
            generate = true,
        });
        var trackId = track.GetProperty("id").GetGuid();
        var jobId = track.GetProperty("generationJobId").GetGuid();

        var terminal = await WaitForTerminalAsync(client, jobId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieSoundOutputInvalid, terminal.ErrorCode);
        Assert.Equal(1, factory.Provider.CallCount);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var trackRecord = await db.MovieSoundTracks.AsNoTracking().SingleAsync(item => item.Id == trackId);
        Assert.Equal(MovieSoundStatuses.Failed, trackRecord.Status);
        Assert.Null(trackRecord.AssetId);
        Assert.Empty(await db.GenerationJobOutputs.AsNoTracking().Where(item => item.GenerationJobId == jobId).ToListAsync());
        Assert.Empty(await db.Assets.AsNoTracking().Where(item => item.SourceGenerationJobId == jobId).ToListAsync());

        Assert.Equal(1, await db.UsageTransactions.AsNoTracking().CountAsync(item => item.GenerationJobId == jobId));
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == jobId);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieSoundOutputInvalid, usage.FailureCode);
        Assert.Equal(BillableInvalidDurationMovieSoundProvider.ActualCostUsd, usage.ProviderCostUsd);
        Assert.True(usage.ProviderCostKnown);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
        Assert.Equal("synthetic-settlement-model", usage.Model);
        Assert.Equal(23, usage.LatencyMs);
        Assert.True(usage.ActualRecordedAt.HasValue);
        Assert.False(usage.IsBillable);
        Assert.Null(usage.BillableAt);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);
        Assert.Equal("{\"synthetic\":true}", usage.SafeMetadataJson);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var response = await PostAsync(client, "/api/auth/register", new
        {
            displayName = "Sound settlement test",
            email = $"sound-settlement-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        return response.Deserialize<AuthResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }
}

public sealed class MovieSoundFailureSettlementFactory : GenerationJobsApiFactory
{
    public BillableInvalidDurationMovieSoundProvider Provider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MovieSoundGeneration:Enabled", "true");
        builder.UseSetting("MovieSoundGeneration:ProviderKey", Provider.Key);
        builder.UseSetting("MovieSoundGeneration:Model", "synthetic-settlement-model");
        builder.UseSetting("MovieSoundGeneration:MaxDurationMilliseconds", "750");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMovieSoundProvider>();
            services.AddSingleton<IMovieSoundProvider>(Provider);
        });
    }
}

public sealed class BillableInvalidDurationMovieSoundProvider : IMovieSoundProvider
{
    private int calls;

    public const decimal ActualCostUsd = 0.037m;
    public string Key => "synthetic-settlement-provider";
    public int CallCount => Volatile.Read(ref calls);

    public Task<MovieSoundProviderResult> GenerateAsync(MovieSoundGenerationInput request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        var content = new byte[44];
        "RIFF"u8.CopyTo(content);
        "WAVE"u8.CopyTo(content.AsSpan(8));
        return Task.FromResult(new MovieSoundProviderResult(
            content,
            "audio/wav",
            "wav",
            request.DurationMilliseconds + 1,
            new MovieSoundProviderUsage(23, 0.05m, ActualCostUsd, "completed", UsageCostBasis.Actual, "{\"synthetic\":true}")));
    }
}
