using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Images;
using Taslim.Api.Movies;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProviderGenerationApiFactory : GenerationJobsApiFactory
{
    public FakeProviderScenarioCatalog Scenarios { get; } = new();
    public FakeProviderCallLog Calls { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ImageGeneration:Enabled", "true");
        builder.UseSetting("ImageGeneration:ProviderKey", "fake-image");
        builder.UseSetting("ImageGeneration:Model", "fake-image-model");
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "fake-music");
        builder.UseSetting("MusicGeneration:Model", "fake-music-model");
        builder.UseSetting("VoiceGeneration:Enabled", "true");
        builder.UseSetting("VoiceGeneration:ProviderKey", "fake-voice");
        builder.UseSetting("VoiceGeneration:Model", "fake-voice-model");
        builder.UseSetting("MovieVideo:MaxStatusPolls", "3");
        builder.UseSetting("MovieVideo:StatusPollIntervalSeconds", "1");
        builder.UseSetting("MovieVideo:MaxTransientRetries", "1");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Scenarios);
            services.AddSingleton(Calls);
            services.RemoveAll<IImageGenerationProvider>();
            services.AddSingleton<IImageGenerationProvider>(sp => new FakeImageGenerationProvider(sp.GetRequiredService<FakeProviderScenarioCatalog>(), sp.GetRequiredService<FakeProviderCallLog>()));
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => new FakeMusicGenerationProvider(sp.GetRequiredService<FakeProviderScenarioCatalog>(), sp.GetRequiredService<FakeProviderCallLog>()));
            services.RemoveAll<IVoiceGenerationProvider>();
            services.AddSingleton<IVoiceGenerationProvider>(sp => new FakeVoiceGenerationProvider(sp.GetRequiredService<FakeProviderScenarioCatalog>(), sp.GetRequiredService<FakeProviderCallLog>()));
            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton<IMovieVideoProvider>(sp => new FakeMovieVideoProvider(sp.GetRequiredService<FakeProviderScenarioCatalog>(), sp.GetRequiredService<FakeProviderCallLog>()));
        });
    }
}

[CollectionDefinition("ProviderGeneration", DisableParallelization = true)]
public sealed class ProviderGenerationCollectionDefinition { }

[Collection("ProviderGeneration")]
public sealed class ProviderGenerationE2ETests : IClassFixture<ProviderGenerationApiFactory>
{
    private readonly ProviderGenerationApiFactory factory;

    public ProviderGenerationE2ETests(ProviderGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Four_modalities_complete_through_request_job_provider_output_validation_storage_asset_and_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Provider E2E Owner");

        factory.Scenarios[FakeProviderKind.Image] = FakeProviderScenario.ImmediateSuccess;
        var imageResponse = await SendWithCsrf<CreateImageGenerationResponse>(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A deterministic test image",
            style = "illustration",
            aspectRatio = "square",
            quality = "standard",
            title = "Fake image",
        });
        var image = await WaitForTerminal(client, imageResponse.Job.Id);
        await AssertSuccessfulLifecycle(image, auth.PersonalWorkspace.Id, UsageFeature.Image, AssetTypes.Image);

        factory.Scenarios[FakeProviderKind.Music] = FakeProviderScenario.AsynchronousSuccess;
        var musicResponse = await SendWithCsrf<CreateMusicGenerationResponse>(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A deterministic ambient music bed",
            purpose = "Provider contract testing",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
            title = "Fake music",
        });
        var music = await WaitForTerminal(client, musicResponse.Job.Id);
        await AssertSuccessfulLifecycle(music, auth.PersonalWorkspace.Id, UsageFeature.Music, AssetTypes.Music);

        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.QueuedRunningCompleted;
        var voiceResponse = await SendWithCsrf<CreateVoiceGenerationResponse>(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "A deterministic provider contract voice output.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
            title = "Fake voice",
        });
        var voice = await WaitForTerminal(client, voiceResponse.Job.Id);
        await AssertSuccessfulLifecycle(voice, auth.PersonalWorkspace.Id, UsageFeature.Voice, AssetTypes.Audio);

        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.QueuedRunningCompleted;
        Guid movieJobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            var movieProject = new MovieProject
            {
                Id = Guid.NewGuid(),
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                Mode = MovieProjectModes.Quick,
                Status = MovieProjectStatuses.Draft,
                Title = "Fake movie",
                Description = "A deterministic movie provider contract output.",
                DurationSeconds = 1,
                AspectRatio = "16:9",
                Style = "cinematic",
                Language = "en",
                CreatedAt = now,
                UpdatedAt = now,
                Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            };
            var clip = new MovieClip
            {
                Id = Guid.NewGuid(),
                MovieProjectId = movieProject.Id,
                Status = MovieClipStatuses.Queued,
                ContinuitySnapshotJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.MovieProjects.Add(movieProject);
            db.MovieClips.Add(clip);
            await db.SaveChangesAsync();
            var jobs = scope.ServiceProvider.GetRequiredService<IGenerationJobService>();
            var job = await jobs.CreateAsync(auth.User.Id, new CreateGenerationJobRequest
            {
                WorkspaceId = auth.PersonalWorkspace.Id,
                JobType = GenerationJobTypes.MovieQuickGenerate,
                Title = movieProject.Title,
                InputJson = JsonSerializer.Serialize(new MovieGenerationInput(
                    MovieStudioOperations.QuickMovie,
                    movieProject.Id,
                    clip.Id,
                    null,
                    null,
                    movieProject.Description,
                    movieProject.DurationSeconds,
                    movieProject.AspectRatio,
                    movieProject.Style,
                    movieProject.Language,
                    null,
                    "{}",
                    null,
                    null)),
            });
            await db.MovieClips
                .Where(item => item.Id == clip.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, job.Id));
            movieJobId = job.Id;
        }
        var movie = await WaitForTerminal(client, movieJobId);
        await AssertSuccessfulLifecycle(movie, auth.PersonalWorkspace.Id, UsageFeature.Movie, AssetTypes.Video);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Image) > 0);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Music) > 0);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Voice) > 0);
        Assert.True(factory.Calls.Calls(FakeProviderKind.Movie) > 0);
    }

    [Fact]
    public async Task Malformed_output_and_rate_limit_fail_without_false_success_assets_or_usage_completion()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Provider Failure Owner");

        factory.Scenarios[FakeProviderKind.Image] = FakeProviderScenario.MalformedOutput;
        var imageResponse = await SendWithCsrf<CreateImageGenerationResponse>(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "This output is intentionally malformed",
            style = "illustration",
            aspectRatio = "square",
            quality = "standard",
        });
        var image = await WaitForTerminal(client, imageResponse.Job.Id);
        await AssertFailedLifecycle(image, auth.PersonalWorkspace.Id, GenerationJobErrorCodes.ImageOutputInvalid);

        factory.Scenarios[FakeProviderKind.Music] = FakeProviderScenario.RateLimit;
        var musicResponse = await SendWithCsrf<CreateMusicGenerationResponse>(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "This request is rate limited",
            purpose = "Failure contract testing",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
        });
        var music = await WaitForTerminal(client, musicResponse.Job.Id);
        await AssertFailedLifecycle(music, auth.PersonalWorkspace.Id, GenerationJobErrorCodes.MusicGenerationFailed);
    }

    [Fact]
    public async Task Delayed_provider_cancellation_finalizes_cancelled_usage_without_an_asset()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Provider Cancellation Owner");
        factory.Scenarios[FakeProviderKind.Voice] = FakeProviderScenario.DelayedCompletion;
        var response = await SendWithCsrf<CreateVoiceGenerationResponse>(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "This delayed provider call will be cancelled.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
        });

        GenerationJobDto? running = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            running = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{response.Job.Id}");
            if (running?.Status == "Running") break;
            await Task.Delay(10);
        }
        Assert.Equal("Running", running?.Status);
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{response.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var cancelled = await WaitForTerminal(client, response.Job.Id);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Empty(cancelled.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == response.Job.Id);
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == response.Job.Id));
        Assert.True(factory.Calls.Cancellations(FakeProviderKind.Voice) > 0);
    }

    private async Task AssertSuccessfulLifecycle(GenerationJobDto job, Guid workspaceId, UsageFeature feature, string assetType)
    {
        Assert.Equal("Succeeded", job.Status);
        Assert.Single(job.Outputs);
        Assert.NotNull(job.Outputs[0].StoredFileId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
        Assert.Equal(feature, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == job.Id);
        Assert.Equal(assetType, asset.AssetType);
        Assert.Equal(StoredFileStatus.Ready, asset.StoredFile!.Status);
        Assert.Equal(workspaceId, asset.WorkspaceId);
    }

    private async Task AssertFailedLifecycle(GenerationJobDto job, Guid workspaceId, string expectedCode)
    {
        Assert.Equal("Failed", job.Status);
        Assert.Equal(expectedCode, job.ErrorCode);
        Assert.Empty(job.Outputs);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == job.Id && item.WorkspaceId == workspaceId));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == job.Id));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"provider-e2e-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            if (job?.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Generation job {id} did not reach a terminal state.");
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
