using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Taslim.Api.Controllers;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #667 acceptance criterion 2, independent companion lane.
///
/// Synthetic, provider-free, two-user/two-project API regressions for the Social
/// asset-selection scope invariant <c>asset.ProjectId == asset.StoredFile.ProjectId</c>.
///
/// This file deliberately does NOT duplicate the scenarios already pinned elsewhere:
/// - <c>SocialAssetSelectionTests</c> (main) pins unsupported type and creator-private
///   asset selected by another workspace member.
/// - <c>SocialAssetProjectScopeMismatchTests</c> (draft PR #766) pins the mismatched pair
///   and the matching pair requested by a NON-creator workspace member.
///
/// The lanes here cover the remaining, previously unclaimed policy surface:
/// 1. the same project mismatch requested BY THE ASSET CREATOR, which shows the invariant
///    is a data-integrity requirement and not merely a cross-user authorization fence;
/// 2. the project-scoped asset whose backing file is personal (no project scope);
/// 3. the project-scoped asset whose backing file is conversation-backed;
/// 4. same-project pairs for both the project-shared and creator-private paths, which must
///    keep working after any fix.
///
/// Test-only: no production source is modified, no real records, no provider calls, no
/// object storage, no migrations.
/// </summary>
public sealed class SocialAssetStoredFileProjectScopePolicyTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;

    public SocialAssetStoredFileProjectScopePolicyTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_job_rejects_creator_selected_asset_whose_backing_file_is_in_a_sibling_project()
    {
        // Requester IS the asset creator. Two sibling projects in one workspace:
        // Asset -> Project B, backing StoredFile -> Project A.
        // Selection-side rejection and any reassignment reconciliation must both enforce
        // the pairing regardless of who is requesting, so this must not be admitted.
        var fixture = await SeedAsync(assetInProject: ProjectSlot.B, fileInProject: ProjectSlot.A, fileConversationBacked: false);

        var terminal = await RunJobAsync(fixture.CreatorClient, fixture.WorkspaceId, fixture.AssetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_rejects_project_shared_asset_whose_backing_file_has_no_project_scope()
    {
        // Asset is project-scoped, the backing StoredFile stayed personal (ProjectId null).
        // The existing non-creator fence already rejects this class; pinned so a fix that
        // only compares non-null project ids cannot silently widen it.
        var fixture = await SeedAsync(
            assetInProject: ProjectSlot.A,
            fileInProject: ProjectSlot.None,
            fileConversationBacked: false,
            requester: RequesterKind.NonCreatorMember);

        var terminal = await RunJobAsync(fixture.MemberClient, fixture.WorkspaceId, fixture.AssetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_rejects_project_shared_asset_whose_backing_file_is_conversation_backed()
    {
        // Same project id on both rows, but the backing file is conversation-backed:
        // documented policy keeps conversation content private to its creator, so sharing
        // it through a project-scoped asset must stay rejected for another member.
        var fixture = await SeedAsync(
            assetInProject: ProjectSlot.A,
            fileInProject: ProjectSlot.A,
            fileConversationBacked: true,
            requester: RequesterKind.NonCreatorMember);

        var terminal = await RunJobAsync(fixture.MemberClient, fixture.WorkspaceId, fixture.AssetId);

        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_allows_project_shared_asset_with_matching_backing_file_for_non_creator_member()
    {
        // Positive control for the sharing policy: a correctly paired project asset must
        // remain selectable by another member of the same workspace after any fix.
        var fixture = await SeedAsync(
            assetInProject: ProjectSlot.A,
            fileInProject: ProjectSlot.A,
            fileConversationBacked: false,
            requester: RequesterKind.NonCreatorMember);

        var terminal = await RunJobAsync(fixture.MemberClient, fixture.WorkspaceId, fixture.AssetId);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.NotEmpty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_allows_creator_private_asset_with_matching_personal_backing_file()
    {
        // Positive control for the creator-private policy: no project scope anywhere.
        // The creator's own unscoped asset must keep working untouched by any fix.
        var fixture = await SeedAsync(
            assetInProject: ProjectSlot.None,
            fileInProject: ProjectSlot.None,
            fileConversationBacked: false);

        var terminal = await RunJobAsync(fixture.CreatorClient, fixture.WorkspaceId, fixture.AssetId);

        Assert.Equal("Succeeded", terminal.Status);
        Assert.NotEmpty(terminal.Outputs);
    }

    private enum ProjectSlot
    {
        None,
        A,
        B,
    }

    private enum RequesterKind
    {
        Creator,
        NonCreatorMember,
    }

    private sealed record Fixture(
        HttpClient CreatorClient,
        HttpClient MemberClient,
        Guid WorkspaceId,
        Guid AssetId);

    private async Task<Fixture> SeedAsync(
        ProjectSlot assetInProject,
        ProjectSlot fileInProject,
        bool fileConversationBacked,
        RequesterKind requester = RequesterKind.Creator)
    {
        var creator = factory.CreateClient();
        var creatorAuth = await RegisterAsync(creator, "Scope Policy Creator");
        var workspaceId = creatorAuth.PersonalWorkspace.Id;

        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var conversationId = fileConversationBacked ? Guid.NewGuid() : (Guid?)null;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;

            db.Projects.Add(new Project
            {
                Id = projectAId,
                WorkspaceId = workspaceId,
                Name = "Policy project A",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Projects.Add(new Project
            {
                Id = projectBId,
                WorkspaceId = workspaceId,
                Name = "Policy project B",
                CreatedAt = now,
                UpdatedAt = now,
            });

            if (conversationId is Guid conversationRowId)
            {
                db.Conversations.Add(new Conversation
                {
                    Id = conversationRowId,
                    WorkspaceId = workspaceId,
                    UserId = creatorAuth.User.Id,
                    ProjectId = ResolveSlot(fileInProject, projectAId, projectBId),
                    Title = "Policy conversation",
                    Status = ConversationStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastMessageAt = now,
                    NextMessageSequence = 1,
                });
            }

            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = workspaceId,
                UserId = creatorAuth.User.Id,
                ProjectId = ResolveSlot(fileInProject, projectAId, projectBId),
                ConversationId = conversationId,
                OriginalFileName = "policy-reference.png",
                StoredFileName = "policy-reference.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = 128,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"tests/{storedFileId:N}",
                Status = StoredFileStatus.Ready,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                CreatedAt = now,
            });

            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = workspaceId,
                ProjectId = ResolveSlot(assetInProject, projectAId, projectBId),
                CreatedByUserId = creatorAuth.User.Id,
                StoredFileId = storedFileId,
                Name = "Policy scoped reference",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });

            await db.SaveChangesAsync();
        }

        HttpClient member = creator;
        if (requester == RequesterKind.NonCreatorMember)
        {
            member = factory.CreateClient();
            var memberAuth = await RegisterAsync(member, "Scope Policy Member");
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = memberAuth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new Fixture(creator, member, workspaceId, assetId);
    }

    private static Guid? ResolveSlot(ProjectSlot slot, Guid projectAId, Guid projectBId) => slot switch
    {
        ProjectSlot.A => projectAId,
        ProjectSlot.B => projectBId,
        _ => null,
    };

    private async Task<GenerationJobDto> RunJobAsync(HttpClient client, Guid workspaceId, Guid assetId)
    {
        var response = await SendWithCsrfAsync(client, new
        {
            workspaceId,
            prompt = "Announce the quarterly workshop using the scoped reference asset.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = new[] { assetId },
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        return await WaitForTerminalAsync(client, created.Job.Id);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string displayName)
    {
        var response = await SendWithCsrfAsync(client, new
        {
            displayName,
            email = $"social-scope-policy-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, object payload, string path = "/api/social-generation/jobs")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Headers.Add("Idempotency-Key", $"social-scope-policy-{Guid.NewGuid():N}");
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
