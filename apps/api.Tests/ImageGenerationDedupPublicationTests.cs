using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationDedupPublicationTests : IClassFixture<ImageGenerationApiFactory>
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly ImageGenerationApiFactory factory;

    public ImageGenerationDedupPublicationTests(ImageGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Image_job_fails_closed_when_deduplication_finds_an_unreferenced_ready_file()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"image-dedup-{Guid.NewGuid():N}@example.com");
        var existingFileId = Guid.NewGuid();
        var contentHash = Convert.ToHexString(SHA256.HashData(Png));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = existingFileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                OriginalFileName = "uncommitted.png",
                StoredFileName = "uncommitted.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = Png.Length,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"{auth.PersonalWorkspace.Id:N}/{existingFileId:N}/uncommitted.png",
                Status = StoredFileStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                ProcessedAt = DateTime.UtcNow,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                ContentHashSha256 = contentHash,
                ContainerFormat = "png",
                Width = 1,
                Height = 1,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A deterministic image whose storage reference must remain safe.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateImageGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.ImageOutputStorageFailed, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var existingFile = await verifyDb.StoredFiles.AsNoTracking().SingleAsync(file => file.Id == existingFileId);
        Assert.Equal(StoredFileStatus.Ready, existingFile.Status);
        Assert.False(await verifyDb.Assets.AsNoTracking().AnyAsync(asset => asset.SourceGenerationJobId == created.Job.Id));
        Assert.False(await verifyDb.GenerationJobOutputs.AsNoTracking().AnyAsync(output => output.GenerationJobId == created.Job.Id));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Image Deduplication Tester",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/image-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Image job {id} did not reach a terminal state.");
    }
}
