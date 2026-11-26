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
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #667 — Social Asset/backing-file visibility through the real in-process HTTP pipeline:
/// register, CSRF, POST /api/social-generation/jobs, queue/handler, then GET the terminal job.
/// The fixture replaces the provider with a deterministic in-process implementation.
///
/// Project IDs on a visible Asset and its backing StoredFile do not have to match. The documented
/// boundary shares a project Asset and a project file with workspace members when each belongs to
/// that workspace and neither object is private or conversation-scoped. These cases pin that
/// legitimate cross-project use remains available while private and foreign-workspace selections
/// fail closed after ordinary job admission.
///
/// Synthetic rows only: no production data, live storage, external provider, billing action,
/// migration, or production source change.
/// </summary>
public sealed class SocialAssetProjectScopeHttpTests : IClassFixture<SocialGenerationApiFactory>
{
    private const string ProjectAName = "Scope project Alpha";
    private const string ProjectBName = "Scope project Beta";

    private readonly SocialGenerationApiFactory factory;

    public SocialAssetProjectScopeHttpTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_job_admits_visible_same_workspace_cross_project_asset_and_backing_file()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        var assetName = $"scope-asset-{Guid.NewGuid():N}";
        var extractedText = $"cross-project-visible-text-{Guid.NewGuid():N}";

        // Both records are workspace-scoped and project-shared, but intentionally use distinct
        // projects. The member is not the creator and has no access to private creator content.
        var assetId = await SeedAssetAsync(world, assetName, extractedText,
            assetProjectId: world.ProjectBId, fileProjectId: world.ProjectAId);

        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: world.ProjectBId,
            prompt: "Announce the workshop using the selected project image.", assetId: assetId);

        // A non-empty opaque ID is admitted normally; visibility is resolved at job execution.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);
        var rawTerminal = await world.Member.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
        Assert.False(string.IsNullOrWhiteSpace(terminal.ResultJson));
        Assert.DoesNotContain(extractedText, rawTerminal, StringComparison.Ordinal);
        await AssertAdmittedJobSideEffectsAsync(world.WorkspaceId, created.Job.Id,
            GenerationJobStatus.Succeeded, UsageTransactionStatus.Completed, outputCount: 1, publishedAssetCount: 1);
    }

    [Fact]
    public async Task Social_job_still_admits_same_project_shared_asset_for_non_creator_member()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        var assetName = $"scope-asset-{Guid.NewGuid():N}";
        var extractedText = $"same-project-visible-text-{Guid.NewGuid():N}";
        var assetId = await SeedAssetAsync(world, assetName, extractedText,
            assetProjectId: world.ProjectAId, fileProjectId: world.ProjectAId);

        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: world.ProjectAId,
            prompt: "Announce the workshop with the shared project image.", assetId: assetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
        await AssertAdmittedJobSideEffectsAsync(world.WorkspaceId, created.Job.Id,
            GenerationJobStatus.Succeeded, UsageTransactionStatus.Completed, outputCount: 1, publishedAssetCount: 1);
    }

    [Fact]
    public async Task Social_job_admits_unscoped_personal_asset_and_file_for_its_creator()
    {
        using var creator = factory.CreateClient();
        var creatorAuth = await Register(creator, "Scope Personal Creator");
        var assetName = $"scope-personal-{Guid.NewGuid():N}";
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddStoredFile(db, creatorAuth, storedFileId, projectId: null, extractedText: $"personal-secret-{Guid.NewGuid():N}");
            AddAsset(db, creatorAuth, assetId, storedFileId, assetName, projectId: null);
            await db.SaveChangesAsync();
        }

        var response = await SubmitAsync(creator, workspaceId: creatorAuth.PersonalWorkspace.Id, projectId: null,
            prompt: "Reuse my own personal reference image for this announcement.", assetId: assetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminalAsync(creator, created.Job.Id);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
        await AssertAdmittedJobSideEffectsAsync(creatorAuth.PersonalWorkspace.Id, created.Job.Id,
            GenerationJobStatus.Succeeded, UsageTransactionStatus.Completed, outputCount: 1, publishedAssetCount: 1);
    }

    [Fact]
    public async Task Social_job_denies_project_asset_with_private_personal_backing_file_to_non_creator()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        var assetName = $"scope-private-file-asset-{Guid.NewGuid():N}";
        var extractedText = $"private-file-secret-{Guid.NewGuid():N}";
        var assetId = await SeedAssetAsync(world, assetName, extractedText,
            assetProjectId: world.ProjectBId, fileProjectId: null);

        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: world.ProjectBId,
            prompt: "Use the shared image and its source.", assetId: assetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var rawCreate = await response.Content.ReadAsStringAsync();
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);
        var rawTerminal = await world.Member.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
        Assert.True(string.IsNullOrEmpty(terminal.ResultJson));
        AssertNoPrivateContent(rawCreate, rawTerminal, assetName, extractedText, ProjectAName, ProjectBName);
        await AssertAdmittedJobSideEffectsAsync(world.WorkspaceId, created.Job.Id,
            GenerationJobStatus.Failed, UsageTransactionStatus.Failed, outputCount: 0, publishedAssetCount: 0);
    }

    [Fact]
    public async Task Social_job_denies_personal_asset_and_file_to_non_creator_workspace_member()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        var assetName = $"scope-private-asset-{Guid.NewGuid():N}";
        var extractedText = $"private-personal-pair-{Guid.NewGuid():N}";
        var assetId = await SeedAssetAsync(world, assetName, extractedText,
            assetProjectId: null, fileProjectId: null);

        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: null,
            prompt: "Use the personal image selected here.", assetId: assetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var rawCreate = await response.Content.ReadAsStringAsync();
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);
        var rawTerminal = await world.Member.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
        Assert.True(string.IsNullOrEmpty(terminal.ResultJson));
        AssertNoPrivateContent(rawCreate, rawTerminal, assetName, extractedText);
        await AssertAdmittedJobSideEffectsAsync(world.WorkspaceId, created.Job.Id,
            GenerationJobStatus.Failed, UsageTransactionStatus.Failed, outputCount: 0, publishedAssetCount: 0);
    }

    [Fact]
    public async Task Social_job_denies_project_asset_from_another_workspace_to_nonmember()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        using var foreignOwner = factory.CreateClient();
        var foreignAuth = await Register(foreignOwner, "Scope Foreign Workspace Owner");
        var foreignProjectId = Guid.NewGuid();
        var foreignFileId = Guid.NewGuid();
        var foreignAssetId = Guid.NewGuid();
        var foreignProjectName = $"foreign-project-{Guid.NewGuid():N}";
        var foreignAssetName = $"foreign-asset-{Guid.NewGuid():N}";
        var foreignText = $"foreign-workspace-secret-{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Projects.Add(new Project
            {
                Id = foreignProjectId,
                WorkspaceId = foreignAuth.PersonalWorkspace.Id,
                Name = foreignProjectName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            AddStoredFile(db, foreignAuth, foreignFileId, foreignProjectId, foreignAuth.User.Id, foreignText);
            AddAsset(db, foreignAuth, foreignAssetId, foreignFileId, foreignAssetName, foreignProjectId);
            await db.SaveChangesAsync();
        }

        // The foreign Asset and file are valid and project-shared inside their own workspace;
        // the requesting member is not a member of that workspace.
        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: world.ProjectBId,
            prompt: "Use the selected project image.", assetId: foreignAssetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var rawCreate = await response.Content.ReadAsStringAsync();
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);
        var rawTerminal = await world.Member.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
        Assert.True(string.IsNullOrEmpty(terminal.ResultJson));
        AssertNoPrivateContent(rawCreate, rawTerminal, foreignAssetName, foreignProjectName, foreignText);
        await AssertAdmittedJobSideEffectsAsync(world.WorkspaceId, created.Job.Id,
            GenerationJobStatus.Failed, UsageTransactionStatus.Failed, outputCount: 0, publishedAssetCount: 0);
    }

    /// <summary>
    /// Two distinct users in one workspace with two projects inside it. Membership is seeded
    /// directly so the boundary under test is the Social selection visibility policy itself.
    /// </summary>
    private async Task<SeededWorld> SeedTwoUsersTwoProjectsAsync()
    {
        var creator = factory.CreateClient();
        var creatorAuth = await Register(creator, "Scope Sharing Creator");
        var member = factory.CreateClient();
        var memberAuth = await Register(member, "Scope Sharing Member");
        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.Projects.AddRange(
            new Project { Id = projectAId, WorkspaceId = creatorAuth.PersonalWorkspace.Id, Name = ProjectAName, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new Project { Id = projectBId, WorkspaceId = creatorAuth.PersonalWorkspace.Id, Name = ProjectBName, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = creatorAuth.PersonalWorkspace.Id,
            UserId = memberAuth.User.Id,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new SeededWorld(creator, creatorAuth, member, memberAuth, creatorAuth.PersonalWorkspace.Id, projectAId, projectBId);
    }

    private async Task<Guid> SeedAssetAsync(SeededWorld world, string assetName, string extractedText, Guid? assetProjectId, Guid? fileProjectId)
    {
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        AddStoredFile(db, world.CreatorAuth, storedFileId, fileProjectId, world.CreatorAuth.User.Id, extractedText);
        AddAsset(db, world.CreatorAuth, assetId, storedFileId, assetName, assetProjectId);
        await db.SaveChangesAsync();
        return assetId;
    }

    private static void AddStoredFile(TaslimDbContext db, AuthResponse auth, Guid storedFileId, Guid? projectId, Guid? ownerId = null, string? extractedText = null)
    {
        var now = DateTime.UtcNow;
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = ownerId ?? auth.User.Id,
            ProjectId = projectId,
            OriginalFileName = "scope-reference.png",
            StoredFileName = "scope-reference.png",
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
    }

    private static void AddAsset(TaslimDbContext db, AuthResponse auth, Guid assetId, Guid storedFileId, string name, Guid? projectId)
    {
        var now = DateTime.UtcNow;
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            ProjectId = projectId,
            CreatedByUserId = auth.User.Id,
            StoredFileId = storedFileId,
            Name = name,
            AssetType = AssetTypes.Image,
            MimeType = "image/png",
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private sealed class SeededWorld(
        HttpClient creator,
        AuthResponse creatorAuth,
        HttpClient member,
        AuthResponse memberAuth,
        Guid workspaceId,
        Guid projectAId,
        Guid projectBId) : IDisposable
    {
        public HttpClient Creator => creator;
        public AuthResponse CreatorAuth => creatorAuth;
        public HttpClient Member => member;
        public AuthResponse MemberAuth => memberAuth;
        public Guid WorkspaceId => workspaceId;
        public Guid ProjectAId => projectAId;
        public Guid ProjectBId => projectBId;

        public void Dispose()
        {
            Creator.Dispose();
            Member.Dispose();
        }
    }

    private async Task AssertAdmittedJobSideEffectsAsync(
        Guid workspaceId,
        Guid jobId,
        GenerationJobStatus expectedJobStatus,
        UsageTransactionStatus expectedUsageStatus,
        int outputCount,
        int publishedAssetCount)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var jobs = await db.GenerationJobs.AsNoTracking().Where(job => job.WorkspaceId == workspaceId && job.Id == jobId).ToListAsync();
        var job = Assert.Single(jobs);
        Assert.Equal(expectedJobStatus, job.Status);
        Assert.Equal(outputCount, await db.GenerationJobOutputs.AsNoTracking().CountAsync(output => output.GenerationJobId == jobId));
        Assert.Equal(publishedAssetCount, await db.Assets.AsNoTracking().CountAsync(asset => asset.SourceGenerationJobId == jobId));

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == jobId);
        Assert.Equal(UsageFeature.Social, usage.Feature);
        Assert.Equal(expectedUsageStatus, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ReversedAmount);
    }

    private static void AssertNoPrivateContent(string rawCreate, string rawTerminal, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, rawCreate, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, rawTerminal, StringComparison.Ordinal);
        }
    }

    private async Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid workspaceId, Guid? projectId, string prompt, Guid assetId)
    {
        var payload = projectId.HasValue
            ? new
            {
                workspaceId,
                projectId,
                prompt,
                socialType = "announcement",
                platform = "linkedin",
                tone = "professional",
                language = "en",
                assetIds = new[] { assetId },
                attachmentIds = Array.Empty<Guid>(),
            }
            : (object)new
            {
                workspaceId,
                prompt,
                socialType = "announcement",
                platform = "linkedin",
                tone = "professional",
                language = "en",
                assetIds = new[] { assetId },
                attachmentIds = Array.Empty<Guid>(),
            };
        return await SendWithCsrf(client, payload);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, new
        {
            displayName,
            email = $"social-scope-{Guid.NewGuid():N}@example.com",
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
        request.Headers.Add("Idempotency-Key", $"social-scope-http-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid id)
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
