using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public class GenerationJobsApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-generation-{Guid.NewGuid():N}.db");

    protected virtual bool WorkerEnabled => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("GenerationJobs:WorkerEnabled", WorkerEnabled.ToString());
        builder.UseSetting("GenerationJobs:PollIntervalMilliseconds", "50");
        builder.UseSetting("GenerationJobs:CancellationPollMilliseconds", "5");
        builder.UseSetting("GenerationJobs:ClaimRecoveryIntervalMilliseconds", "1000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(databasePath)) File.Delete(databasePath);
    }
}

public sealed class GenerationJobsNoWorkerFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;
}

public sealed class GenerationJobsTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public GenerationJobsTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        Task.Delay(150).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task System_test_job_reaches_success_with_progress_result_output_and_zero_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"jobs-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = "system.test",
            title = "Foundation check",
            inputJson = "{}",
        });

        var completed = await WaitForTerminal(client, created.Id);
        Assert.True(completed.Status == "Succeeded", $"Status={completed.Status}; Code={completed.ErrorCode}; Message={completed.ErrorMessage}");
        Assert.Equal(100, completed.ProgressPercent);
        Assert.Contains("system.test", completed.ResultJson);
        Assert.Single(completed.Outputs);
        Assert.NotNull(completed.Outputs[0].StoredFileId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Id:N}");
        Assert.Equal(UsageFeature.Generation, usage.Feature);
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == created.Id);
        Assert.Equal(auth.PersonalWorkspace.Id, asset.WorkspaceId);
        Assert.Null(asset.ProjectId);
        Assert.Equal(AssetTypes.File, asset.AssetType);
        Assert.Equal(completed.Outputs[0].StoredFileId, asset.StoredFileId);
        Assert.Equal("application/json", asset.StoredFile!.ContentType);
        Assert.Equal(StoredFileStatus.Ready, asset.StoredFile.Status);
    }

    [Fact]
    public async Task Jobs_are_workspace_isolated_and_project_must_belong_to_workspace()
    {
        using var first = factory.CreateClient();
        var firstAuth = await Register(first, $"jobs-first-{Guid.NewGuid():N}@example.com");
        using var second = factory.CreateClient();
        var secondAuth = await Register(second, $"jobs-second-{Guid.NewGuid():N}@example.com");

        var crossWorkspace = await SendWithCsrf(second, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = firstAuth.PersonalWorkspace.Id,
            jobType = "system.test",
            inputJson = "{}",
        });
        Assert.Equal(HttpStatusCode.Forbidden, crossWorkspace.StatusCode);

        var invalidProject = await SendWithCsrf(first, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = firstAuth.PersonalWorkspace.Id,
            projectId = secondAuth.PersonalWorkspace.Id,
            jobType = "system.test",
            inputJson = "{}",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidProject.StatusCode);
        var body = await invalidProject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PROJECT_NOT_IN_WORKSPACE", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Jobs_support_list_filter_pagination_and_terminal_conflict_cancellation()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"jobs-list-{Guid.NewGuid():N}@example.com");
        var first = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, jobType = "system.test", inputJson = "{}" });
        var second = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, jobType = "system.test", inputJson = "{}" });
        var list = await client.GetFromJsonAsync<GenerationJobListDto>($"/api/generation/jobs?workspaceId={auth.PersonalWorkspace.Id}&page=1&pageSize=1&jobType=system.test");
        Assert.NotNull(list);
        Assert.Equal(1, list.PageSize);
        Assert.True(list.TotalCount >= 2);

        var completed = await WaitForTerminal(client, first.Id);
        if (completed.Status != "Cancelled")
        {
            var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{first.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        }
        var nextJobTimer = Stopwatch.StartNew();
        await WaitForTerminal(client, second.Id);
        Assert.True(nextJobTimer.Elapsed < TimeSpan.FromSeconds(1), $"The next queued job was delayed for {nextJobTimer.Elapsed}.");
    }

    [Fact]
    public async Task Running_test_job_can_receive_cooperative_cancellation_request()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"jobs-cancel-running-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, jobType = "system.test", inputJson = "{}" });
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Id}");
            if (current?.Status == "Running") break;
            await Task.Delay(10);
        }
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Id}/cancel", null);
        Assert.True(cancel.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, await cancel.Content.ReadAsStringAsync());
        var terminal = await WaitForTerminal(client, created.Id);
        Assert.Equal("Cancelled", terminal.Status);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Id));
    }

    [Fact]
    public async Task Expired_running_claim_is_requeued_and_processed()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"jobs-recovery-{Guid.NewGuid():N}@example.com");
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = id,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.SystemTest,
                Status = GenerationJobStatus.Running,
                InputJson = "{}",
                ProgressPercent = 40,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                StartedAt = DateTime.UtcNow.AddMinutes(-1),
                ClaimExpiresAt = DateTime.UtcNow.AddSeconds(-1),
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        var terminal = await WaitForTerminal(client, id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Equal(100, terminal.ProgressPercent);
    }

    [Fact]
    public async Task Unsupported_job_type_returns_safe_error()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"jobs-type-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, jobType = "unsupported.generate", inputJson = "{}" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("JOB_TYPE_NOT_SUPPORTED", body.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("Provider", await response.Content.ReadAsStringAsync());
    }

    private async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Job Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {id} did not reach a terminal state.");
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
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

public sealed class GenerationJobQueuedCancellationTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public GenerationJobQueuedCancellationTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Queued_job_can_be_cancelled_immediately()
    {
        using var client = factory.CreateClient();
        var authResponse = await Register(client);
        var auth = (await authResponse.Content.ReadFromJsonAsync<AuthResponse>())!;
        var created = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, jobType = "system.test", inputJson = "{}" });
        var cancelled = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Id}");
        Assert.Equal("Cancelled", job!.Status);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Id));
    }

    private static async Task<HttpResponseMessage> Register(HttpClient client)
    {
        return await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Queued Cancel", email = $"jobs-queued-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
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
