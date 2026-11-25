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
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Focused regressions for the shared ("provider-free") generation-job resume path:
// when a worker's lease expires, the recovery sweep resumes the stalled job by
// requeueing it. The requeued attempt executes the handler from the beginning,
// so the dead attempt's progress must not survive the Running -> Queued
// transition. The poison terminal (Running -> Failed) keeps the last attempt's
// progress so failure displays retain the point where the job died.

public sealed class GenerationJobLeaseRecoveryStateFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<IGenerationJobHandler, BlockingGenerationJobHandler>();
        });
    }
}

public sealed class BlockingGenerationJobHandler : IGenerationJobHandler
{
    public bool CanHandle(string jobType) => true;

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("The blocking recovery-test handler must be cancelled before it completes.");
    }
}

public sealed class GenerationJobLeaseRecoveryStateTests : IClassFixture<GenerationJobLeaseRecoveryStateFactory>
{
    private readonly GenerationJobLeaseRecoveryStateFactory factory;

    public GenerationJobLeaseRecoveryStateTests(GenerationJobLeaseRecoveryStateFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Lease_recovery_requeues_the_stalled_job_with_reset_attempt_progress()
    {
        using var client = factory.CreateClient();
        var workspaceId = await RegisterAsync(client);
        var stalled = await CreateJobAsync(client, workspaceId, "recovery-progress-stalled");
        var blocker = await CreateJobAsync(client, workspaceId, "recovery-progress-blocker");

        Guid staleToken;
        var claimStart = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.FirstAsync(item => item.Id == stalled.Id);
            job.Status = GenerationJobStatus.Running;
            job.StartedAt = claimStart.AddMinutes(-5);
            job.ProgressPercent = 40;
            job.ClaimExpiresAt = claimStart.AddMinutes(-1); // expired lease: stuck Running
            staleToken = job.ConcurrencyToken;
            await db.SaveChangesAsync();
        }

        // A single manually owned worker generation performs the expired-claim
        // recovery sweep on its first iteration, then claims the oldest queued
        // job. The blocker's handler never returns, so the recovered job stays
        // Queued while it is observed.
        using var stopping = new CancellationTokenSource();
        var worker = CreateWorker(maxAutomaticRetries: 3);
        await StartWorkerAsync(worker, stopping.Token);

        var resumed = await WaitForStatusAsync(client, stalled.Id, "Queued");
        Assert.Equal("Queued", resumed.Status);
        Assert.Equal(0, resumed.ProgressPercent);
        Assert.Equal(1, resumed.RetryCount);
        Assert.False(resumed.CancellationRequested);
        Assert.Null(resumed.ErrorCode);
        Assert.Null(resumed.ErrorMessage);
        Assert.NotNull(resumed.QueuedAt);
        Assert.True(resumed.QueuedAt > claimStart);
        Assert.True(resumed.CreatedAt < resumed.QueuedAt);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var durable = await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == stalled.Id);
            Assert.Equal(GenerationJobStatus.Queued, durable.Status);
            Assert.Equal(0, durable.ProgressPercent);
            Assert.Null(durable.StartedAt);
            Assert.Null(durable.ClaimExpiresAt);
            Assert.False(durable.CancellationRequested);
            Assert.NotEqual(staleToken, durable.ConcurrencyToken);
        }

        // The worker is parked on the blocker; the recovered job was not claimed.
        var blockerState = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{blocker.Id}");
        Assert.Equal("Running", blockerState!.Status);

        stopping.Cancel();
        await StopWorkerAsync(worker);
    }

    [Fact]
    public async Task Lease_poison_terminalizes_the_stalled_job_and_keeps_the_dead_attempt_progress()
    {
        using var client = factory.CreateClient();
        var workspaceId = await RegisterAsync(client);
        var stalled = await CreateJobAsync(client, workspaceId, "recovery-poison-progress");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.FirstAsync(item => item.Id == stalled.Id);
            job.Status = GenerationJobStatus.Running;
            job.StartedAt = DateTime.UtcNow.AddMinutes(-5);
            job.ProgressPercent = 77;
            job.RetryCount = 3; // at the default bounded automatic-retry limit
            job.ClaimExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var stopping = new CancellationTokenSource();
        var worker = CreateWorker(maxAutomaticRetries: 3);
        await StartWorkerAsync(worker, stopping.Token);

        var poisoned = await WaitForStatusAsync(client, stalled.Id, "Failed");
        Assert.Equal("Failed", poisoned.Status);
        Assert.Equal(77, poisoned.ProgressPercent);
        Assert.Equal(GenerationJobErrorCodes.Poisoned, poisoned.ErrorCode);
        Assert.NotNull(poisoned.FailedAt);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var durable = await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == stalled.Id);
            Assert.Null(durable.ClaimExpiresAt);

            // The reservation is finalized as failed with zero customer charge.
            var usage = await db.UsageTransactions.AsNoTracking()
                .SingleAsync(item => item.RequestId == $"generation:{stalled.Id:N}");
            Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
        }

        stopping.Cancel();
        await StopWorkerAsync(worker);
    }

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

    private static Task StartWorkerAsync(GenerationJobWorker worker, CancellationToken token)
        => ((IHostedService)worker).StartAsync(token);

    private static Task StopWorkerAsync(GenerationJobWorker worker)
        => ((IHostedService)worker).StopAsync(CancellationToken.None);

    private static async Task<GenerationJobDto> WaitForStatusAsync(HttpClient client, Guid jobId, string status)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job.Status == status) return job;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Job {jobId} never reached {status}.");
    }

    private static async Task<Guid> RegisterAsync(HttpClient client)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Lease Recovery Tester",
            email = $"lease-recovery-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        return auth.PersonalWorkspace.Id;
    }

    private static async Task<GenerationJobDto> CreateJobAsync(HttpClient client, Guid workspaceId, string title)
    {
        var job = await SendWithCsrfAsync<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId,
            jobType = "system.test",
            inputJson = "{}",
            title,
        });
        return job;
    }

    private static async Task<T> SendWithCsrfAsync<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrfAsync(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }
}
