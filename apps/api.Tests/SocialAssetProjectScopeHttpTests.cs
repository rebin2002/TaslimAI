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
/// Issue #667 — cross-project admission evidence for Social asset selection, driven through the
/// real in-process HTTP pipeline: register, CSRF, POST /api/social-generation/jobs, the queue,
/// the job handler, then GET /api/generation/jobs/{id}. Provider-free and storage-free.
///
/// This is deliberately disjoint from <c>SocialAssetFileProjectPairingTests</c> (PR #1128), which
/// proves the pairing predicate with a single creator-side requester, and from
/// <c>SocialAssetSelectionTests</c>, which covers unsupported types and private-asset selection.
/// The gap closed here is exactly what issue #667 records as not yet confirmed by an API
/// integration test: the two-user / two-project boundary, where the requester is a workspace
/// member rather than <c>Asset.CreatedByUserId</c>.
///
/// Four claims are pinned:
/// 1. Mismatched pair (asset in project B over a backing file in project A), requested by a
///    non-creator member: refused, fails closed, no outputs, and nothing about either scope or the
///    backing file's extracted text is observable in the HTTP response.
/// 2. Correctly paired, explicitly authorized project-shared asset, requested by that member:
///    still usable. Tightening the invariant must not break legitimate project sharing.
/// 3. Personal asset over personal backing file (unscoped over unscoped): still usable, so the
///    lifted <c>Guid?</c> comparison cannot read symmetry-by-absence as a mismatch.
/// 4. Mirrored asymmetry (asset in project B over a personal backing file), requested by a member:
///    also fails closed, with creator privacy enforced ahead of any pairing shortcut.
///
/// Synthetic rows only: no billing, no paid inference, no real customer data or object storage,
/// no migrations, and no modification of any source file.
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
    public async Task Social_job_rejects_cross_project_pair_for_non_creator_member_and_leaks_nothing()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        var assetName = $"scope-asset-{Guid.NewGuid():N}";
        var extractedText = $"cross-project-secret-{Guid.NewGuid():N}";

        // The shape from issue #667: the asset's project moved to Beta, the backing file stayed in Alpha.
        var assetId = await SeedAssetAsync(world, assetName, extractedText,
            assetProjectId: world.ProjectBId, fileProjectId: world.ProjectAId);

        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: null,
            prompt: "Announce the workshop using the image selected here.", assetId: assetId);

        // Admission is asynchronous: enqueueing succeeds and the invariant is enforced by the
        // handler, so the refusal must surface as a terminal failure rather than a 4xx.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var rawCreate = await response.Content.ReadAsStringAsync();
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);
        var rawTerminal = await world.Member.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
        Assert.True(string.IsNullOrEmpty(terminal.ResultJson));

        // Nothing from either scope may come back over HTTP: not the sibling project names, not
        // the asset's identity, and not the backing file's extracted text — the value that
        // SocialGenerationJobHandler actually feeds into the prompt.
        foreach (var secret in new[] { assetName, extractedText, ProjectAName, ProjectBName })
        {
            Assert.DoesNotContain(secret, rawCreate, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, rawTerminal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Social_job_admits_authorized_project_shared_asset_with_matching_pair_for_non_creator_member()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        var assetName = $"scope-asset-{Guid.NewGuid():N}";
        var extractedText = $"shared-project-text-{Guid.NewGuid():N}";

        // Correctly paired and explicitly shared with the project the member is acting in.
        var assetId = await SeedAssetAsync(world, assetName, extractedText,
            assetProjectId: world.ProjectAId, fileProjectId: world.ProjectAId);

        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: world.ProjectAId,
            prompt: "Announce the workshop with the shared project image.", assetId: assetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);

        // The invariant must not over-restrict: an authorized project-shared asset stays usable
        // for a member who did not create it.
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
        Assert.Contains("posts", terminal.ResultJson);

        // Usable is not the same as readable: the selection's private content stays out of the response.
        var rawTerminal = await world.Member.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");
        Assert.DoesNotContain(extractedText, rawTerminal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Social_job_admits_unscoped_personal_asset_with_unscoped_backing_file_for_its_creator()
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

        // Absence on both sides is symmetry, not mismatch: personal assets must keep working.
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_rejects_project_scoped_asset_over_personal_backing_file_for_non_creator_member()
    {
        using var world = await SeedTwoUsersTwoProjectsAsync();
        var assetName = $"scope-asset-{Guid.NewGuid():N}";
        var extractedText = $"personal-file-secret-{Guid.NewGuid():N}";

        // The mirrored asymmetry: the asset carries Beta while its backing file never left personal scope.
        var assetId = await SeedAssetAsync(world, assetName, extractedText,
            assetProjectId: world.ProjectBId, fileProjectId: null);

        var response = await SubmitAsync(world.Member, workspaceId: world.WorkspaceId, projectId: world.ProjectBId,
            prompt: "Use the Beta image whose backing file is still personal.", assetId: assetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminalAsync(world.Member, created.Job.Id);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);

        // Creator privacy is enforced ahead of any pairing shortcut, and the refusal stays generic.
        var rawTerminal = await world.Member.GetStringAsync($"/api/generation/jobs/{created.Job.Id}");
        foreach (var secret in new[] { assetName, extractedText, ProjectAName, ProjectBName })
            Assert.DoesNotContain(secret, rawTerminal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two distinct users in one workspace with two projects inside it. Membership is seeded
    /// directly so the boundary under test is the Social selection predicate itself rather than
    /// workspace access, which the pipeline already grants to the member.
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
