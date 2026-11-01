using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// Provider-free P0 acceptance contract for the server-side beta generation boundary.
/// This intentionally fails until the source owner adds the shared allowlist.
public sealed class BetaLaunchGenerationAllowlistNegativeAcceptanceTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public BetaLaunchGenerationAllowlistNegativeAcceptanceTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData(GenerationJobTypes.ResearchGenerate)]
    [InlineData(GenerationJobTypes.SocialGenerate)]
    [InlineData(GenerationJobTypes.VoiceGenerate)]
    [InlineData(GenerationJobTypes.MusicGenerate)]
    public async Task Excluded_studio_types_are_denied_before_enqueue_and_usage(string jobType)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"beta-negative-{jobType}-{Guid.NewGuid():N}@example.com");
        var idempotencyKey = $"beta-negative-{jobType}-{Guid.NewGuid():N}";

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType,
            inputJson = "{}",
        }, idempotencyKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BETA_MODULE_NOT_AVAILABLE", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.CreatedByUserId == auth.User.Id && item.IdempotencyKey == idempotencyKey));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(item => item.UserId == auth.User.Id && item.RequestId.StartsWith("generation:") && item.Feature != UsageFeature.Chat));
    }

    [Fact]
    public async Task Document_and_presentation_controls_remain_available()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"beta-controls-{Guid.NewGuid():N}@example.com");

        foreach (var jobType in new[] { GenerationJobTypes.DocumentGenerate, GenerationJobTypes.PresentationGenerate })
        {
            var response = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                jobType,
                inputJson = "{}",
            }, $"beta-control-{jobType}-{Guid.NewGuid():N}");

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Synthetic Beta Acceptance",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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
}

public sealed class BetaLaunchReplayFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<BetaLaunchReplayProbe>();
            services.AddSingleton<IGenerationJobHandler, BetaLaunchReplayHandler>();
            services.RemoveAll<IGenerationBudgetService>();
            services.AddSingleton<IGenerationBudgetService, BetaLaunchReplayBudget>();
        });
    }
}

public sealed class BetaLaunchReplayProbe
{
    public int HandlerCalls => Volatile.Read(ref handlerCalls);
    public int BudgetBeginCalls => Volatile.Read(ref budgetBeginCalls);
    public int ProviderCalls => Volatile.Read(ref providerCalls);

    private int handlerCalls;
    private int budgetBeginCalls;
    private int providerCalls;

    public void RecordHandler() => Interlocked.Increment(ref handlerCalls);
    public void RecordBudgetBegin() => Interlocked.Increment(ref budgetBeginCalls);
    public void RecordProvider() => Interlocked.Increment(ref providerCalls);
}

public sealed class BetaLaunchReplayHandler(BetaLaunchReplayProbe probe) : IGenerationJobHandler
{
    public bool CanHandle(string jobType) =>
        string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase)
        || string.Equals(jobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase);

    public Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        probe.RecordHandler();
        probe.RecordProvider();
        progress.Report(100);
        return Task.FromResult(new GenerationHandlerResult(
            "{\"message\":\"synthetic provider-free replay\"}",
            [],
            new AiUsageMetadata("synthetic", "beta-replay", null, null, null, 0m, 0m, 0, "completed", true)));
    }
}

public sealed class BetaLaunchReplayBudget(BetaLaunchReplayProbe probe) : IGenerationBudgetService
{
    public Task<GenerationProviderAttempt> BeginAttemptAsync(GenerationJob job, string provider, string? model, GenerationCostEstimate estimate, CancellationToken cancellationToken = default)
    {
        probe.RecordBudgetBegin();
        return Task.FromResult(new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(),
            GenerationJobId = job.Id,
            JobConcurrencyToken = job.ConcurrencyToken,
            IdempotencyKey = $"synthetic:{job.Id:N}",
            Capability = job.JobType,
            AttemptNumber = 1,
            Provider = "synthetic",
            Status = GenerationProviderAttemptStatus.Started,
            ResultClassification = "Started",
            StartedAt = DateTime.UtcNow,
        });
    }

    public Task CompleteAttemptAsync(GenerationProviderAttempt attempt, decimal? actualProviderCostUsd, bool actualCostKnown, GenerationProviderAttemptStatus status, string? failureCode = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class BetaLaunchWorkerReplayTests : IClassFixture<BetaLaunchReplayFactory>
{
    private readonly BetaLaunchReplayFactory factory;

    public BetaLaunchWorkerReplayTests(BetaLaunchReplayFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Queued_excluded_job_is_rejected_before_attempt_handler_provider_or_billable_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"beta-replay-{Guid.NewGuid():N}@example.com");
        var excludedId = Guid.NewGuid();
        var controlId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.GenerationJobs.AddRange(
                NewQueuedJob(excludedId, auth, GenerationJobTypes.ResearchGenerate, now),
                NewQueuedJob(controlId, auth, GenerationJobTypes.SystemTest, now.AddMilliseconds(1)));
            await db.SaveChangesAsync();
        }

        var excluded = await WaitForTerminal(client, excludedId);
        var control = await WaitForTerminal(client, controlId);

        Assert.Equal("Failed", excluded.Status);
        Assert.Equal("BETA_MODULE_NOT_AVAILABLE", excluded.ErrorCode);
        Assert.Equal("Failed", control.Status);
        Assert.Equal(GenerationJobErrorCodes.NoBillableAsset, control.ErrorCode);

        var probe = factory.Services.GetRequiredService<BetaLaunchReplayProbe>();
        Assert.Equal(1, probe.HandlerCalls);
        Assert.Equal(1, probe.BudgetBeginCalls);
        Assert.Equal(1, probe.ProviderCalls);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await verifyDb.UsageTransactions.AsNoTracking().AnyAsync(item => item.GenerationJobId == excludedId));
        Assert.False(await verifyDb.GenerationProviderAttempts.AsNoTracking().AnyAsync(item => item.GenerationJobId == excludedId));
    }

    private static GenerationJob NewQueuedJob(Guid id, AuthResponse auth, string jobType, DateTime queuedAt) => new()
    {
        Id = id,
        WorkspaceId = auth.PersonalWorkspace.Id,
        CreatedByUserId = auth.User.Id,
        JobType = jobType,
        Status = GenerationJobStatus.Queued,
        InputJson = "{}",
        CreatedAt = queuedAt,
        QueuedAt = queuedAt,
        RequestId = $"generation:{id:N}",
    };

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Synthetic Beta Replay",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {id} did not reach a terminal state.");
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
