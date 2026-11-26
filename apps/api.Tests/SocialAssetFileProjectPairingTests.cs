using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #667 acceptance coverage for Social asset selection. Project IDs on an Asset and its
/// StoredFile may differ when both resources remain visible in the same workspace; visibility
/// and ownership, not a duplicated equality rule, decide whether the selection is admissible.
/// All rows and users are synthetic, the provider is deterministic, and no live storage or
/// production service is contacted.
/// </summary>
public sealed class SocialAssetFileProjectPairingTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;

    public SocialAssetFileProjectPairingTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_job_allows_a_workspace_member_to_use_a_project_asset_backed_by_another_project_file()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Pairing Asset Owner");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Pairing Workspace Member");
        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, owner.PersonalWorkspace.Id, "Pairing project A");
            AddProject(db, projectBId, owner.PersonalWorkspace.Id, "Pairing project B");
            AddWorkspaceMember(db, owner.PersonalWorkspace.Id, member.User.Id);
            // Same workspace and canonical visibility, different project columns: a legal
            // reassignment/dedup shape, not an authorization mismatch.
            AddAssetAndFile(db, owner.PersonalWorkspace.Id, owner.User.Id, assetId,
                assetProjectId: projectBId, fileProjectId: projectAId, fileOwnerId: owner.User.Id,
                name: "Shared launch image");
            await db.SaveChangesAsync();
        }

        var response = await Submit(memberClient, owner.PersonalWorkspace.Id, projectBId, assetId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(memberClient, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_rejects_a_noncreator_members_project_asset_backed_by_another_members_personal_file()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Private File Owner");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Private File Requester");
        var projectId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var privateText = $"private-file-text-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectId, owner.PersonalWorkspace.Id, "Private asset project");
            AddWorkspaceMember(db, owner.PersonalWorkspace.Id, member.User.Id);
            AddAssetAndFile(db, owner.PersonalWorkspace.Id, owner.User.Id, assetId,
                assetProjectId: projectId, fileProjectId: null, fileOwnerId: owner.User.Id,
                name: "Private reference image", extractedText: privateText);
            await db.SaveChangesAsync();
        }

        var response = await Submit(memberClient, owner.PersonalWorkspace.Id, projectId, assetId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(memberClient, created.Job.Id);
        var rawTerminal = await memberClient.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
        Assert.DoesNotContain("Private reference image", rawTerminal, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, rawTerminal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Social_job_rejects_a_file_from_another_workspace_even_when_the_requester_owns_it()
    {
        using var workspaceOwnerClient = factory.CreateClient();
        var workspaceOwner = await Register(workspaceOwnerClient, "Asset Workspace Owner");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Cross Workspace Asset Creator");
        var projectId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectId, workspaceOwner.PersonalWorkspace.Id, "Workspace project");
            AddWorkspaceMember(db, workspaceOwner.PersonalWorkspace.Id, member.User.Id);
            // The requester owns both the asset and file, but the file belongs to a different
            // workspace; creator ownership must not bypass the workspace boundary.
            AddAssetAndFile(db, workspaceOwner.PersonalWorkspace.Id, member.User.Id, assetId,
                assetProjectId: projectId, fileProjectId: null, fileOwnerId: member.User.Id,
                name: "Cross-workspace image", fileWorkspaceId: member.PersonalWorkspace.Id);
            await db.SaveChangesAsync();
        }

        var response = await Submit(memberClient, workspaceOwner.PersonalWorkspace.Id, projectId, assetId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(memberClient, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_rejects_an_asset_whose_project_belongs_to_another_workspace()
    {
        using var workspaceOwnerClient = factory.CreateClient();
        var workspaceOwner = await Register(workspaceOwnerClient, "Project Workspace Owner");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Foreign Project Creator");
        var foreignProjectId = Guid.NewGuid();
        var validFileProjectId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, foreignProjectId, member.PersonalWorkspace.Id, "Foreign project");
            AddProject(db, validFileProjectId, workspaceOwner.PersonalWorkspace.Id, "Local file project");
            AddWorkspaceMember(db, workspaceOwner.PersonalWorkspace.Id, member.User.Id);
            AddAssetAndFile(db, workspaceOwner.PersonalWorkspace.Id, member.User.Id, assetId,
                assetProjectId: foreignProjectId, fileProjectId: validFileProjectId, fileOwnerId: member.User.Id,
                name: "Foreign-project image");
            await db.SaveChangesAsync();
        }

        var response = await Submit(memberClient, workspaceOwner.PersonalWorkspace.Id, null, assetId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(memberClient, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_keeps_a_creator_owned_personal_asset_and_personal_file_usable()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Personal Asset Creator");
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddAssetAndFile(db, auth.PersonalWorkspace.Id, auth.User.Id, assetId,
                assetProjectId: null, fileProjectId: null, fileOwnerId: auth.User.Id,
                name: "Personal reference image");
            await db.SaveChangesAsync();
        }

        var response = await Submit(client, auth.PersonalWorkspace.Id, null, assetId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_keeps_a_creator_owned_personal_asset_over_a_project_file_usable()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Personal Asset With Shared File Creator");
        var projectId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectId, auth.PersonalWorkspace.Id, "Shared file project");
            AddAssetAndFile(db, auth.PersonalWorkspace.Id, auth.User.Id, assetId,
                assetProjectId: null, fileProjectId: projectId, fileOwnerId: auth.User.Id,
                name: "Personal asset over project file");
            await db.SaveChangesAsync();
        }

        var response = await Submit(client, auth.PersonalWorkspace.Id, null, assetId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_fails_the_entire_selection_when_one_asset_is_not_visible_to_the_member()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Mixed Selection Owner");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Mixed Selection Member");
        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();
        var visibleAssetId = Guid.NewGuid();
        var privateAssetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, owner.PersonalWorkspace.Id, "Mixed project A");
            AddProject(db, projectBId, owner.PersonalWorkspace.Id, "Mixed project B");
            AddWorkspaceMember(db, owner.PersonalWorkspace.Id, member.User.Id);
            AddAssetAndFile(db, owner.PersonalWorkspace.Id, owner.User.Id, visibleAssetId,
                assetProjectId: projectBId, fileProjectId: projectAId, fileOwnerId: owner.User.Id,
                name: "Visible project image");
            AddAssetAndFile(db, owner.PersonalWorkspace.Id, owner.User.Id, privateAssetId,
                assetProjectId: projectBId, fileProjectId: null, fileOwnerId: owner.User.Id,
                name: "Private personal-file image");
            await db.SaveChangesAsync();
        }

        var response = await Submit(memberClient, owner.PersonalWorkspace.Id, projectBId, visibleAssetId, privateAssetId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(memberClient, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    private static void AddProject(TaslimDbContext db, Guid projectId, Guid workspaceId, string name) =>
        db.Projects.Add(new Project
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void AddWorkspaceMember(TaslimDbContext db, Guid workspaceId, Guid userId) =>
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });

    private static void AddAssetAndFile(
        TaslimDbContext db,
        Guid assetWorkspaceId,
        Guid assetCreatorId,
        Guid assetId,
        Guid? assetProjectId,
        Guid? fileProjectId,
        Guid fileOwnerId,
        string name,
        string? extractedText = null,
        Guid? fileWorkspaceId = null)
    {
        var storedFileId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = fileWorkspaceId ?? assetWorkspaceId,
            UserId = fileOwnerId,
            ProjectId = fileProjectId,
            OriginalFileName = "pairing-reference.png",
            StoredFileName = "pairing-reference.png",
            ContentType = "image/png",
            Extension = ".png",
            SizeBytes = 128,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"tests/{storedFileId:N}",
            Status = StoredFileStatus.Ready,
            TextExtractionStatus = extractedText is null ? FileExtractionStatus.NotApplicable : FileExtractionStatus.Ready,
            ExtractedText = extractedText,
            ExtractedTextLength = extractedText?.Length,
            CreatedAt = now,
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = assetWorkspaceId,
            ProjectId = assetProjectId,
            CreatedByUserId = assetCreatorId,
            StoredFileId = storedFileId,
            Name = name,
            AssetType = AssetTypes.Image,
            MimeType = "image/png",
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, new
        {
            displayName,
            email = $"social-asset-pairing-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<HttpResponseMessage> Submit(HttpClient client, Guid workspaceId, Guid? projectId, params Guid[] assetIds) =>
        SendWithCsrf(client, new
        {
            workspaceId,
            projectId,
            prompt = "Announce the product workshop using the selected reference images.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds,
            attachmentIds = Array.Empty<Guid>(),
        });

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Headers.Add("Idempotency-Key", $"social-asset-pairing-test-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Social job did not reach a terminal state.");
    }
}
