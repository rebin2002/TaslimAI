using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class NoAssetGenerationJobFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<IGenerationJobHandler, NoAssetSystemTestHandler>();
        });
    }
}

public sealed class NoAssetSystemTestHandler : IGenerationJobHandler
{
    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase);

    public Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        progress.Report(100);
        return Task.FromResult(new GenerationHandlerResult(
            "{\"message\":\"provider returned no asset\"}",
            [new GenerationHandlerOutput(GenerationJobOutputTypes.Json, null, "{}")],
            new AiUsageMetadata("system", "system.test", null, null, null, 0.01m, 0.01m, 2, "completed", true)));
    }
}

public sealed class GenerationNoAssetAccountingTests : IClassFixture<NoAssetGenerationJobFactory>
{
    private readonly NoAssetGenerationJobFactory factory;

    public GenerationNoAssetAccountingTests(NoAssetGenerationJobFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Generation_without_published_asset_fails_and_usage_is_not_billable()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{}",
        });

        var terminal = await WaitForTerminal(client, created.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.NoBillableAsset, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Id);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.False(usage.IsBillable);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Id));
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "No Asset Tester", email = $"no-asset-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
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
