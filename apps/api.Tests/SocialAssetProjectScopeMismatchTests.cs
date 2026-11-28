using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic, provider-free regressions for the invariant raised in issue #667:
/// a Social generation must not accept a project-scoped Asset whose backing
/// StoredFile belongs to a different project inside the same workspace when the
/// requesting member is not the asset creator, while a matching project-scoped
/// pair keeps the documented project-shared access.
/// Uses the shared <see cref="SocialGenerationApiFactory"/> (DeterministicSocialProvider
/// only; no paid provider, no live object storage, no cross-user private data).
/// </summary>
public sealed class SocialAssetProjectScopeMismatchTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;

    public SocialAssetProjectScopeMismatchTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_job_rejects_asset_whose_backing_file_is_in_a_different_project()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Project Mismatch Asset Owner");
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var fileProjectId = Guid.NewGuid();
        var assetProjectId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Projects.AddRange(
                new Project
                {
                    Id = fileProjectId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    Name = "Backing-file project",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                },
                new Project
                {
                    Id = assetProjectId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    Name = "Asset project",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = ownerAuth.User.Id,
                ProjectId = fileProjectId,
                OriginalFileName = "project-a-reference.png",
                StoredFileName = "project-a-reference.png",
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
                ProjectId = assetProjectId,
                CreatedByUserId = ownerAuth.User.Id,
                StoredFileId = storedFileId,
                Name = "Cross-project image reference",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Project Mismatch Asset Member");
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
            prompt = "A member must not select an asset across a project boundary.",
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

    [Fact]
    public async Task Social_job_accepts_asset_with_matching_project_scoped_backing_file()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Project Match Asset Owner");
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Projects.Add(new Project
            {
                Id = projectId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                Name = "Shared project",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = ownerAuth.User.Id,
                ProjectId = projectId,
                OriginalFileName = "project-shared-reference.png",
                StoredFileName = "project-shared-reference.png",
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
                ProjectId = projectId,
                CreatedByUserId = ownerAuth.User.Id,
                StoredFileId = storedFileId,
                Name = "Project-shared image reference",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Project Match Asset Member");
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
            prompt = "A member may select a project-shared asset whose backing file matches the project.",
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
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Single(terminal.Outputs);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, new
        {
            displayName,
            email = $"social-project-scope-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Headers.Add("Idempotency-Key", $"social-project-scope-test-{Guid.NewGuid():N}");
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
