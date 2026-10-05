using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationOutputScopeFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddScoped<IGenerationJobHandler, ExistingStoredFileGenerationHandler>();
        });
    }
}

public sealed class ExistingStoredFileGenerationHandler(TaslimDbContext db) : IGenerationJobHandler
{
    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var file = await db.StoredFiles.AsNoTracking()
            .SingleAsync(item => item.OriginalFileName == "foreign-output.json" && item.WorkspaceId == job.WorkspaceId, cancellationToken);
        progress.Report(100);
        return new GenerationHandlerResult(
            "{\"generated\":true}",
            [new GenerationHandlerOutput(
                GenerationJobOutputTypes.StoredFile,
                file.Id,
                null,
                Asset: new GeneratedAssetDescriptor("Invalidly scoped output", "This output must not be published.", AssetTypes.File))]);
    }
}

public sealed class GenerationOutputScopeValidationTests : IClassFixture<GenerationOutputScopeFactory>
{
    private readonly GenerationOutputScopeFactory factory;

    public GenerationOutputScopeValidationTests(GenerationOutputScopeFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Existing_output_file_from_another_project_is_rejected_before_asset_publication()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var targetProjectId = Guid.NewGuid();
        var foreignProjectId = Guid.NewGuid();
        var foreignFileId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Projects.AddRange(
                new Project
                {
                    Id = targetProjectId,
                    WorkspaceId = auth.PersonalWorkspace.Id,
                    Name = "Target project",
                    Type = ProjectTypes.General,
                    Status = ProjectStatuses.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new Project
                {
                    Id = foreignProjectId,
                    WorkspaceId = auth.PersonalWorkspace.Id,
                    Name = "Foreign project",
                    Type = ProjectTypes.General,
                    Status = ProjectStatuses.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            db.StoredFiles.Add(new StoredFile
            {
                Id = foreignFileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                ProjectId = foreignProjectId,
                OriginalFileName = "foreign-output.json",
                StoredFileName = "foreign-output.json",
                ContentType = "application/json",
                Extension = ".json",
                SizeBytes = 2,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"{auth.PersonalWorkspace.Id:N}/{foreignFileId:N}/foreign-output.json",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                ContentHashSha256 = new string('A', 64),
                ContainerFormat = "json",
            });
            await db.SaveChangesAsync();
        }

        var created = await SendWithCsrfAsync<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            projectId = targetProjectId,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{}",
        });
        var terminal = await WaitForTerminalAsync(client, created.Id);

        Assert.Equal(GenerationJobStatus.Failed.ToString(), terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.ExecutionFailed, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await verificationDb.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Id));
        Assert.False(await verificationDb.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Id));
        var original = await verificationDb.StoredFiles.AsNoTracking().SingleAsync(item => item.Id == foreignFileId);
        Assert.Equal(StoredFileStatus.Ready, original.Status);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Generation output tester",
            email = $"generation-output-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrfAsync<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {jobId} did not reach a terminal state.");
    }
}
