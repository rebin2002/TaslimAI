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
/// Issue #667, acceptance criterion 2 - companion regressions for the selection-side
/// correction in <c>SocialGenerationJobHandler.LoadAssetsAsync</c>.
///
/// Synthetic, provider-free, two-project/two-user API tests for the pairing invariant
/// <c>asset.ProjectId == asset.StoredFile.ProjectId</c>. These lanes were previously
/// unclaimed and deliberately do NOT duplicate:
/// - <c>SocialAssetSelectionTests</c> (main): unsupported asset type, creator-private
///   asset selected by another workspace member.
/// - <c>SocialAssetProjectScopeMismatchTests</c> (draft PR #766): mismatched and
///   matching pairs requested by a non-creator member.
/// - <c>SocialAssetStoredFileProjectScopePolicyTests</c> (draft PR #1065): creator
///   selected mismatch, project asset with a personal or conversation-backed file,
///   non-creator and creator-private matching positives.
///
/// Covered here instead:
/// 1. mixed selections fail closed as a whole rather than silently dropping the
///    mismatched asset and continuing with the valid one;
/// 2. the inverse asymmetry, where the asset is unscoped (personal) but its backing
///    file carries a project scope, which exercises the nullable comparison;
/// 3. the authorized creator path over a correctly paired project asset, which must
///    keep succeeding after the predicate is tightened.
///
/// No production data, no object storage, no provider calls, no migrations, no charges.
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
    public async Task Social_job_fails_closed_when_one_of_several_selected_assets_has_a_mismatched_project_pair()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pairing Mixed Requester");
        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();
        var validAssetId = Guid.NewGuid();
        var mismatchedAssetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, auth.PersonalWorkspace.Id, "Pairing project A");
            AddProject(db, projectBId, auth.PersonalWorkspace.Id, "Pairing project B");

            // Correctly paired selection: asset and backing file both in project A.
            AddPairedAsset(db, validAssetId, auth, projectAId, projectAId, "Paired launch image");
            // Reassignment-shaped defect: the asset moved to project B while the file
            // behind it remained in project A.
            AddPairedAsset(db, mismatchedAssetId, auth, projectBId, projectAId, "Reassigned launch image");
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce the launch using both of the project images selected here.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = new[] { validAssetId, mismatchedAssetId },
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);

        // Fail closed: the valid asset must not be quietly dropped and the job must not
        // proceed on the remainder of the selection.
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    [Fact]
    public async Task Social_job_rejects_personal_asset_whose_backing_file_carries_a_project_scope()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pairing Personal Requester");
        var projectAId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            AddProject(db, projectAId, auth.PersonalWorkspace.Id, "Pairing project A");
            // The opposite asymmetry from a project asset over a personal file: an
            // unscoped (personal) asset whose backing file is project-scoped.
            AddPairedAsset(db, assetId, auth, assetProjectId: null, fileProjectId: projectAId, name: "Personal asset over project file");
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Reuse the personal reference image for this announcement post.",
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
            AddPairedAsset(db, assetId, auth, projectAId, projectAId, "Creator project image");
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce the workshop to local business owners using the project image.",
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

        Assert.Equal("Succeeded", terminal.Status);
        Assert.Null(terminal.ErrorCode);
        Assert.Single(terminal.Outputs);

        using var check = factory.Services.CreateScope();
        var verifyDb = check.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await verifyDb.Assets.FindAsync(assetId);
        Assert.NotNull(asset);
        Assert.Equal(projectAId, asset!.ProjectId);
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

    private static void AddPairedAsset(
        TaslimDbContext db,
        Guid assetId,
        AuthResponse auth,
        Guid? assetProjectId,
        Guid? fileProjectId,
        string name)
    {
        var storedFileId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = auth.User.Id,
            ProjectId = fileProjectId,
            OriginalFileName = "pairing-reference.png",
            StoredFileName = "pairing-reference.png",
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
            WorkspaceId = auth.PersonalWorkspace.Id,
            ProjectId = assetProjectId,
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
