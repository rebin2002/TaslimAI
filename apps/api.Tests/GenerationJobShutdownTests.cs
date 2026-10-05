using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationJobShutdownFactory : GenerationJobsApiFactory
{
    public string DatabasePath => databasePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<IGenerationJobHandler, ShutdownBlockingSystemTestHandler>();
        });
    }
}

public sealed class ShutdownBlockingSystemTestHandler : IGenerationJobHandler
{
    public static readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The shutdown test handler should only finish through cancellation.");
    }
}

public sealed class GenerationJobShutdownTests : IClassFixture<GenerationJobShutdownFactory>
{
    private readonly GenerationJobShutdownFactory factory;

    public GenerationJobShutdownTests(GenerationJobShutdownFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Host_shutdown_finalizes_in_flight_job_and_usage_without_waiting_for_lease_recovery()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var created = await CreateJobAsync(client, auth.PersonalWorkspace.Id);

        await ShutdownBlockingSystemTestHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await factory.DisposeAsync();

        await using var connection = new SqliteConnection($"Data Source={factory.DatabasePath};Cache=Shared;Default Timeout=30");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == created.Id);
        Assert.Equal(GenerationJobStatus.Cancelled, job.Status);
        Assert.Equal(GenerationJobErrorCodes.Cancelled, job.ErrorCode);
        Assert.Null(job.ClaimExpiresAt);

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Id);
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Shutdown Tester",
            email = $"shutdown-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> CreateJobAsync(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{}",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GenerationJobDto>())!;
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
