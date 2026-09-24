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
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationJobFailureFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<IGenerationJobHandler, FailingSystemTestHandler>();
        });
    }
}

public sealed class FailingSystemTestHandler : IGenerationJobHandler
{
    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase);
    public Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken) =>
        Task.FromException<GenerationHandlerResult>(new InvalidOperationException("sensitive-provider-detail"));
}

public sealed class GenerationJobFailureTests : IClassFixture<GenerationJobFailureFactory>
{
    private readonly GenerationJobFailureFactory factory;

    public GenerationJobFailureTests(GenerationJobFailureFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Failed_job_has_safe_error_and_does_not_publish_asset_file_or_output()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var job = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, jobType = "system.test", inputJson = "{}" });
        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{job.Id}");
            if (terminal!.Status == "Failed") break;
            await Task.Delay(50);
        }

        Assert.NotNull(terminal);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.ExecutionFailed, terminal.ErrorCode);
        Assert.DoesNotContain("sensitive-provider-detail", terminal.ErrorMessage ?? string.Empty);
        Assert.Empty(terminal.Outputs);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == job.Id));
        Assert.False(await db.StoredFiles.AsNoTracking().AnyAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
        var notifications = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}");
        var notification = Assert.Single(notifications.GetProperty("items").EnumerateArray());
        Assert.Equal("generation.failed", notification.GetProperty("type").GetString());
        Assert.Contains("/activity", notification.GetProperty("destination").GetString());
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Failure Tester", email = $"failure-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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
