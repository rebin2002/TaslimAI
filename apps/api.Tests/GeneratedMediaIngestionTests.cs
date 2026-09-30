using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GeneratedMediaIngestionIntegrationTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public GeneratedMediaIngestionIntegrationTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Same_generated_bytes_reuse_one_private_file_and_keep_two_job_provenance_links()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var first = await CreateJobAsync(client, auth.PersonalWorkspace.Id, "same-output");
        var second = await CreateJobAsync(client, auth.PersonalWorkspace.Id, "same-output");
        var firstTerminal = await WaitForTerminalAsync(client, first.Id);
        var secondTerminal = await WaitForTerminalAsync(client, second.Id);

        Assert.Equal("Succeeded", firstTerminal.Status);
        Assert.Equal("Succeeded", secondTerminal.Status);
        Assert.NotNull(firstTerminal.Outputs.Single().StoredFileId);
        Assert.NotNull(secondTerminal.Outputs.Single().StoredFileId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var files = await db.StoredFiles.AsNoTracking()
            .Where(file => file.WorkspaceId == auth.PersonalWorkspace.Id && file.Status == StoredFileStatus.Ready)
            .ToListAsync();
        var file = Assert.Single(files);
        Assert.Equal(firstTerminal.Outputs.Single().StoredFileId, file.Id);
        Assert.Equal(secondTerminal.Outputs.Single().StoredFileId, file.Id);
        Assert.Equal(64, file.ContentHashSha256?.Length);
        Assert.Equal("json", file.ContainerFormat);
        Assert.Contains("contentHashSha256", file.MetadataJson, StringComparison.Ordinal);

        var provenance = await db.GeneratedMediaProvenance.AsNoTracking()
            .Where(item => item.WorkspaceId == auth.PersonalWorkspace.Id)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, provenance.Count);
        Assert.All(provenance, item =>
        {
            Assert.Equal(file.Id, item.StoredFileId);
            Assert.Equal(firstTerminal.Outputs.Single().StoredFileId, item.StoredFileId);
            Assert.Equal(64, item.ContentHashSha256.Length);
            Assert.Equal(64, item.ChainHashSha256.Length);
            Assert.Contains(item.GenerationJobId, new[] { first.Id, second.Id });
        });
        Assert.NotEqual(provenance[0].ChainHashSha256, provenance[1].ChainHashSha256);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Generated media owner",
            email = $"generated-media-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> CreateJobAsync(HttpClient client, Guid workspaceId, string title)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new { workspaceId, jobType = GenerationJobTypes.SystemTest, title, inputJson = "{}" });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GenerationJobDto>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {jobId} did not reach a terminal state.");
    }
}
