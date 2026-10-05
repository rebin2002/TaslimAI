using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialAssetSelectionTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;

    public SocialAssetSelectionTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_job_rejects_selected_asset_with_unsupported_type()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                OriginalFileName = "audio-reference.mp3",
                StoredFileName = "audio-reference.mp3",
                ContentType = "audio/mpeg",
                Extension = ".mp3",
                SizeBytes = 128,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"tests/{storedFileId:N}",
                Status = StoredFileStatus.Ready,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                CreatedAt = DateTime.UtcNow,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                StoredFileId = storedFileId,
                Name = "Unsupported audio reference",
                AssetType = AssetTypes.Audio,
                MimeType = "audio/mpeg",
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Use only supported Social Studio assets.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = new[] { assetId },
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_rejects_private_asset_selected_by_workspace_member()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Private Social Asset Owner");
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = ownerAuth.User.Id,
                OriginalFileName = "private-reference.png",
                StoredFileName = "private-reference.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = 128,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"tests/{storedFileId:N}",
                Status = StoredFileStatus.Ready,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                CreatedAt = DateTime.UtcNow,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = ownerAuth.User.Id,
                StoredFileId = storedFileId,
                Name = "Private image reference",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Private Social Asset Member");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(member, new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            prompt = "This member must not use a private asset.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = new[] { assetId },
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(member, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, new
        {
            displayName = "Social Asset Tester",
            email = $"social-asset-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Headers.Add("Idempotency-Key", $"social-asset-test-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Social job did not reach a terminal state.");
    }
}
