using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Second post-billing duplicate-submission lane: lease recovery. When a worker's claim expires, the
/// recovery sweep re-queues the job up to MaxAutomaticRetries times and the requeued attempt runs the
/// handler from the beginning. For music that means a fresh billable POST to the provider, because
/// neither Stable Audio nor Mubert accepts an idempotency key on creation and the provider operation
/// id is only known once the call returns. A worker that dies after submitting (crash loop, OOM kill,
/// a rollout mid-generation) would therefore pay for up to four generations for one customer request
/// while the first artifact is orphaned. Music is terminalized instead of re-dispatched, and the
/// customer keeps a deliberate single retry; every other job type keeps the existing recovery.
/// </summary>
public sealed class MusicLeaseRecoveryDuplicateSubmissionTests : IClassFixture<MusicLeaseRecoveryApiFactory>
{
    private readonly MusicLeaseRecoveryApiFactory factory;

    public MusicLeaseRecoveryDuplicateSubmissionTests(MusicLeaseRecoveryApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Expired_music_lease_terminates_without_re_dispatching_the_billable_provider_request()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-lease-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Lease recovery isolation",
            description = "A calm instrumental bed used to pin lease-recovery rebill prevention.",
            purpose = "Integration test for automatic duplicate paid submission prevention.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(JsonOptions))!.Job;

        // The application worker is disabled in this factory, so nothing has been submitted yet.
        Assert.Equal(0, Provider().SubmittedGenerations);

        var claimStart = DateTime.UtcNow;
        Guid staleToken;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = await db.GenerationJobs.FirstAsync(item => item.Id == job.Id);
            row.Status = GenerationJobStatus.Running;
            row.StartedAt = claimStart.AddMinutes(-5);
            row.ProgressPercent = 55;
            row.ClaimExpiresAt = claimStart.AddMinutes(-1); // expired lease: stuck Running
            staleToken = row.ConcurrencyToken;
            await db.SaveChangesAsync();
        }

        using var stopping = new CancellationTokenSource();
        var worker = CreateWorker(maxAutomaticRetries: 3);
        await ((IHostedService)worker).StartAsync(stopping.Token);

        var failed = await WaitForStatusAsync(client, job.Id, "Failed");
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, failed.ErrorCode);
        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed), failed.ErrorMessage);
        // The dead attempt's progress is retained, exactly as the poison terminal does.
        Assert.Equal(55, failed.ProgressPercent);
        Assert.Equal(0, failed.RetryCount);

        stopping.Cancel();
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        // No re-dispatch means no second billable provider request, now or later.
        Assert.Equal(0, Provider().SubmittedGenerations);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var durable = await finalDb.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == job.Id);
        Assert.NotEqual(staleToken, durable.ConcurrencyToken);
        Assert.Null(durable.ClaimExpiresAt);
        Assert.Equal(0, durable.RetryCount);

        // The reservation is finalized as failed with zero customer charge, like every other failure.
        var usage = await finalDb.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);

        // Unlike the retrieval-failure lane, a finished track is not known to exist here, so the
        // customer's deliberate single retry must stay available.
        var retry = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{job.Id}/retry", null, $"music-lease-retry-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var child = (await retry.Content.ReadFromJsonAsync<GenerationJobDto>(JsonOptions))!;
        Assert.Equal(job.Id, child.RetryOfJobId);
    }

    [Fact]
    public async Task Non_music_lease_recovery_still_requeues_unchanged()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"generic-lease-{Guid.NewGuid():N}@example.com");
        var stalled = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = "system.test",
            inputJson = "{}",
            title = "Generic recovery control",
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var row = await db.GenerationJobs.FirstAsync(item => item.Id == stalled.Id);
            row.Status = GenerationJobStatus.Running;
            row.StartedAt = DateTime.UtcNow.AddMinutes(-5);
            row.ClaimExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var stopping = new CancellationTokenSource();
        var worker = CreateWorker(maxAutomaticRetries: 3);
        await ((IHostedService)worker).StartAsync(stopping.Token);

        GenerationJobDto? resumed = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            resumed = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{stalled.Id}");
            if (resumed is not null && resumed.RetryCount >= 1) break;
            await Task.Delay(50);
        }

        stopping.Cancel();
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        Assert.NotNull(resumed);
        // Recovery re-dispatched it once, which is the pre-existing behaviour for every non-music lane.
        Assert.True(resumed!.RetryCount >= 1, $"expected the generic job to be requeued, status={resumed.Status} retryCount={resumed.RetryCount}");
        Assert.NotEqual(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed, resumed.ErrorCode);
        Assert.Equal(0, Provider().SubmittedGenerations);
    }

    [Fact]
    public void Unconfirmed_submission_copy_keeps_the_customer_retry_and_leaks_nothing()
    {
        var message = GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed);

        Assert.NotNull(message);
        Assert.DoesNotContain("http", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider key", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", message, StringComparison.OrdinalIgnoreCase);
        // Deliberate contrast with the retrieval lane, whose copy must not invite a retry.
        Assert.DoesNotContain("try again", GenerationJobErrorMessages.For(GenerationJobErrorCodes.MusicOutputRetrievalFailed)!, StringComparison.OrdinalIgnoreCase);
    }

    private CountingMusicProvider Provider() => factory.Services.GetRequiredService<CountingMusicProvider>();

    private GenerationJobWorker CreateWorker(int maxAutomaticRetries)
        => new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GenerationJobOptions
            {
                WorkerEnabled = true,
                WorkerConcurrency = 1,
                PollIntervalMilliseconds = 60_000,
                CancellationPollMilliseconds = 10,
                ClaimRecoveryIntervalMilliseconds = 60_000,
                ClaimLeaseMinutes = 2,
                MaxAutomaticRetries = maxAutomaticRetries,
            }),
            NullLogger<GenerationJobWorker>.Instance);

    private static async Task<GenerationJobDto> WaitForStatusAsync(HttpClient client, Guid jobId, string status)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job!.Status == status) return job;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Job {jobId} never reached {status}.");
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Lease Isolation Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class MusicLeaseRecoveryApiFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<CountingMusicProvider>();
            services.AddSingleton<IMusicGenerationProvider>(sp => sp.GetRequiredService<CountingMusicProvider>());
        });
    }
}

/// <summary>
/// Counts how many times a provider request was actually submitted, which is the test-side stand-in
/// for "a second billable generation was created". It produces the same minimal MP3 the deterministic
/// provider uses so that a deliberate customer retry behaves like a normal successful generation.
/// </summary>
internal sealed class CountingMusicProvider : IMusicGenerationProvider
{
    private static readonly byte[] Mp3 = CreateMinimalMp3();

    public string Key => "test";

    private int submittedGenerations;

    public int SubmittedGenerations => Volatile.Read(ref submittedGenerations);

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref submittedGenerations);
        return Task.FromResult(new MusicProviderResult(
            Mp3,
            "audio/mpeg",
            "mp3",
            request.DurationSeconds,
            new MusicProviderUsage(100, 200, 0.004m, 0.004m, 12, CostBasis: UsageCostBasis.Actual)));
    }

    private static byte[] CreateMinimalMp3()
    {
        // Byte-identical to the payload the existing music HTTP pipeline accepts, so the only thing
        // this provider contributes is the submission counter.
        var bytes = new byte[417];
        bytes[0] = 0xFF;
        bytes[1] = 0xFB;
        bytes[2] = 0x90;
        bytes[3] = 0x64;
        return bytes;
    }
}
