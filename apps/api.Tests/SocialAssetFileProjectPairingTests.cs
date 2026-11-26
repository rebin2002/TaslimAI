using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Assets;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #667 acceptance criterion 2, corrected against the shipped storage design.
///
/// Row #1128 admitted an asset into a Social generation only when
/// <c>asset.ProjectId == asset.StoredFile.ProjectId</c>. That equality is unsatisfiable for two
/// supported write flows and it guarded a channel that does not exist:
/// - <c>AssetService.UpdateAsync</c> moves <c>Asset.ProjectId</c> without moving the backing
///   file's scope, and <c>FileProcessingService.FinalizeGeneratedFileAsync</c> adopts the
///   pre-existing same-hash row when it dedups generated output, so a legitimately created asset
///   can point at a file whose scope belongs to another project or another member. The unique
///   index on <c>(WorkspaceId, ContentHashSha256)</c> (<c>TaslimDbContext</c>) forbids a
///   per-project copy of the same content, so no write can repair the columns; every such asset
///   stayed permanently unusable behind a generic "unavailable" error while the same member could
///   still download that very file through the asset route.
/// - An asset's backing-file extracted text never reaches the provider: SocialPromptBuilder
///   renders only name, type and content type for kind "asset" (pinned by
///   SocialGenerationUnitTests.Prompt_builder_keeps_asset_metadata_but_excludes_private_extracted_text),
///   and SocialGenerationJobHandler no longer passes the text to the builder at all.
///
/// Admission therefore follows the platform's own decisions - <c>AssetVisibility</c> for the
/// asset, <c>AssetService.CanReadStoredFile</c> (the single source of truth behind the download
/// route and the read model's canDownload flag) for the pair. Lanes kept strict, and one of them
/// is stricter than before the correction: a personal or conversation-backed file that belongs to
/// another member is refused even when the scopes line up, and an asset whose project leaves the
/// workspace is refused at all.
///
/// Synthetic, provider-free, two-project / two-workspace / two-user API tests. These lanes do NOT
/// duplicate <c>SocialAssetSelectionTests</c> (unsupported asset type, creator-private asset
/// selected by another member) or <c>SocialGenerationAttachmentAdmissionTests</c> (attachment id
/// admission). Draft PR #766 and #1065 still carry companion lanes of their own; neither is
/// merged, so the shapes named here are covered in this file rather than assumed from them.
///
/// No production data, no object storage, no provider calls, no migrations, no charges.
/// </summary>
public sealed class SocialAssetFileProjectPairingTests : IClassFixture<SocialGenerationApiFactory>
{
    private const string PrivateExtractedText = "PRIVATE-BACKING-FILE-TEXT-5e91c0";

    private readonly SocialGenerationApiFactory factory;

    public SocialAssetFileProjectPairingTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // ---------------------------------------------------------------------------------------
    // The defect being fixed: legal (asset, backing file) pairs must stay usable.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Asset_admission_and_download_availability_ask_the_same_question_of_the_backing_file()
    {
        // The invariant that replaces row #1128's project-scope equality: Social Studio admits an
        // asset exactly when the same member can already download its backing file through the
        // asset route. AssetService.CanReadStoredFile is that route's decision and the read
        // model's canDownload flag, so pinning the verdicts here means the two paths cannot drift
        // apart again: if this decision ever changes shape, the matching Social lane above (or
        // this table) fails. Same six shapes as the API lanes, same workspace W.
        var workspaceId = Guid.NewGuid();
        var otherWorkspaceId = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var otherMember = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        static StoredFile File(Guid fileWorkspaceId, Guid? projectId, Guid ownerId, Guid projectWorkspaceId, Guid? conversationId = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = fileWorkspaceId,
            ProjectId = projectId,
            ConversationId = conversationId,
            UserId = ownerId,
            Status = StoredFileStatus.Ready,
            Project = projectId.HasValue
                ? new Project { Id = projectId.Value, WorkspaceId = projectWorkspaceId, Name = "parity", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
                : null,
        };

        // Project file in this workspace: readable by every member, whatever project the asset
        // itself carries - reassignment and dedup adoption both produce these pairs, which is the
        // whole point of following this decision instead of comparing the two scope columns.
        Assert.True(AssetService.CanReadStoredFile(File(workspaceId, projectA, otherMember, workspaceId), requester, workspaceId));
        Assert.True(AssetService.CanReadStoredFile(File(workspaceId, projectB, requester, workspaceId), requester, workspaceId));
        // Own personal file: readable.
        Assert.True(AssetService.CanReadStoredFile(File(workspaceId, null, requester, workspaceId), requester, workspaceId));
        // Another member's personal file, and any conversation-backed file the caller does not
        // own: refused. That is the disclosure decision that actually matters here.
        Assert.False(AssetService.CanReadStoredFile(File(workspaceId, null, otherMember, workspaceId), requester, workspaceId));
        Assert.False(AssetService.CanReadStoredFile(File(workspaceId, projectA, otherMember, workspaceId, conversationId), requester, workspaceId));
        // Cross-workspace file, and a project file whose project does not resolve inside this
        // workspace: refused.
        Assert.False(AssetService.CanReadStoredFile(File(otherWorkspaceId, null, requester, otherWorkspaceId), requester, workspaceId));
        Assert.False(AssetService.CanReadStoredFile(File(workspaceId, projectA, otherMember, otherWorkspaceId), requester, workspaceId));
    }

    [Fact]
    public async Task Social_job_accepts_asset_whose_backing_file_kept_the_earlier_project_scope_after_reassignment()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reassignment Requester");
        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, auth.PersonalWorkspace.Id, "Reassignment project A");
            AddProject(db, projectBId, auth.PersonalWorkspace.Id, "Reassignment project B");
            // Reassignment shape: the asset moved to project B, the file behind it stayed in A.
            AddPair(db, assetId, auth.PersonalWorkspace.Id, auth.User.Id, assetProjectId: projectBId, fileProjectId: projectAId, fileOwnerUserId: auth.User.Id, name: "Reassigned launch image");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(client, auth, assetId);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);

        // Contributed as a media reference only: identity in the prompt, never the file's text.
        var prompt = RecordedPrompt();
        Assert.Contains("Reassigned launch image", prompt.UserInstruction, StringComparison.Ordinal);
        Assert.Contains(prompt.Sources, source => source.Kind == "asset" && source.Name == "Reassigned launch image" && source.Content is null);
        Assert.DoesNotContain(PrivateExtractedText, prompt.UserInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Content:", prompt.UserInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Social_job_accepts_generated_asset_whose_backing_file_was_adopted_from_another_project()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Adoption Requester");
        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();
        var adoptedIntoBId = Guid.NewGuid();
        var adoptedIntoAId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, auth.PersonalWorkspace.Id, "Adoption project A");
            AddProject(db, projectBId, auth.PersonalWorkspace.Id, "Adoption project B");
            // Dedup-adoption shape: each ingestion adopted the row the other project created
            // first, so both assets point at a file carrying the opposite project scope.
            AddPair(db, adoptedIntoBId, auth.PersonalWorkspace.Id, auth.User.Id, assetProjectId: projectBId, fileProjectId: projectAId, fileOwnerUserId: auth.User.Id, name: "Adopted into B");
            AddPair(db, adoptedIntoAId, auth.PersonalWorkspace.Id, auth.User.Id, assetProjectId: projectAId, fileProjectId: projectBId, fileOwnerUserId: auth.User.Id, name: "Adopted into A");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(client, auth, adoptedIntoBId, adoptedIntoAId);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);
        var prompt = RecordedPrompt();
        Assert.Equal(2, prompt.Sources.Count(source => source.Kind == "asset"));
        Assert.DoesNotContain(PrivateExtractedText, prompt.UserInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Social_job_accepts_personal_asset_whose_backing_file_is_project_scoped()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Personal Asset Requester");
        var projectAId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, auth.PersonalWorkspace.Id, "Personal asset project A");
            // Unscoped asset over a project-scoped file, selected by its own creator: the file is
            // already readable by every member of this workspace, so nothing new is disclosed.
            AddPair(db, assetId, auth.PersonalWorkspace.Id, auth.User.Id, assetProjectId: null, fileProjectId: projectAId, fileOwnerUserId: auth.User.Id, name: "Personal asset over project file");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(client, auth, assetId);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.DoesNotContain(PrivateExtractedText, RecordedPrompt().UserInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Social_job_accepts_creator_selected_asset_with_matching_project_scoped_pair()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pairing Creator Requester");
        var projectAId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, auth.PersonalWorkspace.Id, "Pairing project A");
            AddPair(db, assetId, auth.PersonalWorkspace.Id, auth.User.Id, assetProjectId: projectAId, fileProjectId: projectAId, fileOwnerUserId: auth.User.Id, name: "Creator project image");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(client, auth, assetId);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);

        using var check = factory.Services.CreateScope();
        var verifyDb = check.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await verifyDb.Assets.FindAsync(assetId);
        Assert.NotNull(asset);
        Assert.Equal(projectAId, asset!.ProjectId);
    }

    // ---------------------------------------------------------------------------------------
    // Fail-closed property preserved: one inadmissible asset sinks the whole selection.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Social_job_fails_closed_when_one_of_several_selected_assets_is_inadmissible()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Mixed Selection Owner");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Mixed Selection Member");
        var projectAId = Guid.NewGuid();
        var usableAssetId = Guid.NewGuid();
        var privateFileAssetId = Guid.NewGuid();
        var foreignPersonalFileId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, owner.PersonalWorkspace.Id, "Mixed selection project A");
            AddMembership(db, owner.PersonalWorkspace.Id, member.User.Id);
            // Both assets belong to the same member, so asset visibility is not what separates
            // them: only the second one's backing file is another member's personal upload.
            AddPair(db, usableAssetId, owner.PersonalWorkspace.Id, member.User.Id, assetProjectId: projectAId, fileProjectId: projectAId, fileOwnerUserId: member.User.Id, name: "Usable project image");
            AddPair(db, privateFileAssetId, owner.PersonalWorkspace.Id, member.User.Id, assetProjectId: projectAId, fileProjectId: null, fileOwnerUserId: owner.User.Id, storedFileId: foreignPersonalFileId, name: "Project image over foreign personal file");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(memberClient, member, usableAssetId, privateFileAssetId);

        // The admissible asset must not be quietly dropped and the job must not proceed on the
        // remainder of the selection.
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    // ---------------------------------------------------------------------------------------
    // Lanes that stay strict - and the two the correction makes stricter.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Social_job_refuses_asset_backed_by_another_members_personal_file_even_for_its_creator()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Foreign Personal File Owner");
        using var creatorClient = factory.CreateClient();
        var creator = await Register(creatorClient, "Foreign Personal File Creator");
        var projectAId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, owner.PersonalWorkspace.Id, "Foreign personal file project A");
            AddMembership(db, owner.PersonalWorkspace.Id, creator.User.Id);
            // Creator's own asset, backed by a personal file another member uploaded.
            AddPair(db, assetId, owner.PersonalWorkspace.Id, creator.User.Id, assetProjectId: projectAId, fileProjectId: null, fileOwnerUserId: owner.User.Id, name: "Project image over foreign personal file");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(creatorClient, creator, assetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_refuses_personal_asset_backed_by_another_members_personal_file()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Personal Leak Owner");
        using var creatorClient = factory.CreateClient();
        var creator = await Register(creatorClient, "Personal Leak Creator");
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddMembership(db, owner.PersonalWorkspace.Id, creator.User.Id);
            // Both sides unscoped, so the old project-scope equality matched and admitted this
            // pair: an unscoped asset whose content is another member's personal upload.
            AddPair(db, assetId, owner.PersonalWorkspace.Id, creator.User.Id, assetProjectId: null, fileProjectId: null, fileOwnerUserId: owner.User.Id, name: "Personal asset over foreign personal file");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(creatorClient, creator, assetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_refuses_asset_whose_project_is_outside_the_workspace()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Foreign Project Requester");
        var foreignWorkspaceId = Guid.NewGuid();
        var foreignProjectId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddWorkspace(db, foreignWorkspaceId, "Foreign pairing workspace");
            AddProject(db, foreignProjectId, foreignWorkspaceId, "Foreign pairing project");
            // Scopes line up perfectly, which is why the old equality admitted this pair: the
            // asset's project - and the file's - belong to a different workspace entirely.
            AddPair(db, assetId, auth.PersonalWorkspace.Id, auth.User.Id, assetProjectId: foreignProjectId, fileProjectId: foreignProjectId, fileOwnerUserId: auth.User.Id, name: "Cross-workspace project image");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(client, auth, assetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_refuses_asset_backed_by_a_file_in_another_workspace()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Cross Workspace File Requester");
        var foreignWorkspaceId = Guid.NewGuid();
        var projectAId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddWorkspace(db, foreignWorkspaceId, "Foreign file workspace");
            AddProject(db, projectAId, auth.PersonalWorkspace.Id, "Local pairing project A");
            var storedFileId = Guid.NewGuid();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = foreignWorkspaceId,
                UserId = auth.User.Id,
                OriginalFileName = "foreign-file.png",
                StoredFileName = "foreign-file.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = 128,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"tests/{storedFileId:N}",
                Status = StoredFileStatus.Ready,
                TextExtractionStatus = FileExtractionStatus.Ready,
                ExtractedText = PrivateExtractedText,
                CreatedAt = DateTime.UtcNow,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                ProjectId = projectAId,
                CreatedByUserId = auth.User.Id,
                StoredFileId = storedFileId,
                Name = "Local asset over foreign-workspace file",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(client, auth, assetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_refuses_non_creator_selection_of_project_asset_over_a_conversation_backed_file()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Conversation File Owner");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Conversation File Member");
        var projectAId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, owner.PersonalWorkspace.Id, "Conversation file project A");
            AddMembership(db, owner.PersonalWorkspace.Id, member.User.Id);
            // Project-scoped on both sides, but the file is pinned to one conversation, so it is
            // not shareable with the rest of the workspace.
            AddPair(db, assetId, owner.PersonalWorkspace.Id, owner.User.Id, assetProjectId: projectAId, fileProjectId: projectAId, fileOwnerUserId: owner.User.Id, conversationId: conversationId, name: "Project image over conversation file");
            await db.SaveChangesAsync();
        }

        var terminal = await RunSocial(memberClient, member, assetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private DeterministicSocialProvider Provider() =>
        (DeterministicSocialProvider)factory.Services.GetRequiredService<ISocialGenerationProvider>();

    private SocialGenerationPrompt RecordedPrompt()
    {
        var prompt = Provider().LastPrompt;
        Assert.NotNull(prompt);
        return prompt;
    }

    private async Task<GenerationJobDto> RunSocial(HttpClient client, AuthResponse auth, params Guid[] assetIds)
    {
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce the workshop to local business owners using the selected references.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds,
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        return await WaitForTerminal(client, created.Job.Id);
    }

    private static void AddWorkspace(TaslimDbContext db, Guid workspaceId, string name) =>
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{workspaceId:N}",
            Type = WorkspaceType.Business,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void AddProject(TaslimDbContext db, Guid projectId, Guid workspaceId, string name) =>
        db.Projects.Add(new Project
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void AddMembership(TaslimDbContext db, Guid workspaceId, Guid userId) =>
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });

    private static void AddConversation(TaslimDbContext db, Guid conversationId, Guid workspaceId, Guid userId)
    {
        db.Conversations.Add(new Conversation
        {
            Id = conversationId,
            WorkspaceId = workspaceId,
            UserId = userId,
            Title = "Pairing conversation",
            Status = ConversationStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Seeds an asset and its backing file, letting the two scopes, the two owners and the
    /// conversation link diverge independently, which is exactly what the write flows do.
    /// </summary>
    private static void AddPair(
        TaslimDbContext db,
        Guid assetId,
        Guid workspaceId,
        Guid assetCreatorUserId,
        Guid? assetProjectId,
        Guid? fileProjectId,
        Guid fileOwnerUserId,
        string name,
        Guid? storedFileId = null,
        Guid? conversationId = null)
    {
        var fileId = storedFileId ?? Guid.NewGuid();
        var now = DateTime.UtcNow;
        if (conversationId.HasValue) AddConversation(db, conversationId.Value, workspaceId, fileOwnerUserId);
        db.StoredFiles.Add(new StoredFile
        {
            Id = fileId,
            WorkspaceId = workspaceId,
            UserId = fileOwnerUserId,
            ProjectId = fileProjectId,
            ConversationId = conversationId,
            OriginalFileName = "pairing-reference.png",
            StoredFileName = $"pairing-reference-{fileId:N}.png",
            ContentType = "image/png",
            Extension = ".png",
            SizeBytes = 128,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"tests/{fileId:N}",
            Status = StoredFileStatus.Ready,
            TextExtractionStatus = FileExtractionStatus.Ready,
            ExtractedText = PrivateExtractedText,
            CreatedAt = now,
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            ProjectId = assetProjectId,
            CreatedByUserId = assetCreatorUserId,
            StoredFileId = fileId,
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
