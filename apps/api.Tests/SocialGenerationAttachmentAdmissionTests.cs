using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Controllers;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Admission and execution-boundary safeguards for Social Studio job input
/// attachments. Social job requests carry two identifier lists (assetIds and
/// attachmentIds) that the handler resolves strictly inside the job's workspace
/// and strictly under the creator's file visibility (<see cref="StoredFileVisibility"/>).
/// Two properties are pinned here:
/// 1) Identifiers the server can prove are not member/workspace-scoped files -
///    the zero GUID - must be rejected at admission, before a GenerationJob row,
///    a usage reservation and a queue entry are created. The request validator
///    bounded only the list lengths, so a zero GUID was accepted with 202 and
///    only failed later inside SocialGenerationJobHandler (mirroring the defect
///    fixed for Research in #888).
/// 2) The scope itself: a foreign-workspace attachment id never reaches the
///    provider (the job fails closed with SOCIAL_CONTEXT_UNAVAILABLE), and a
///    revoked member cannot reuse a previously valid attachment id - admission
///    requires current membership, so the reuse attempt is a 403 with no new
///    job, and the job the user created while a member becomes unreadable.
/// All tests are deterministic and provider-free: rejected paths queue nothing,
/// and the one accepted run uses the in-process deterministic social provider.
/// </summary>
public sealed class SocialGenerationAttachmentAdmissionTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;

    public SocialGenerationAttachmentAdmissionTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_creation_rejects_zero_guid_attachment_before_queueing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "social-attachment-admission");

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce our new customer workshop to local business owners.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = new[] { Guid.Empty },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, error.GetProperty("error").GetProperty("code").GetString());

        // Nothing was queued and no usage was reserved for the rejected request.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Equal(0, await db.UsageTransactions.AsNoTracking().CountAsync(usage => usage.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Social_creation_rejects_zero_guid_asset_before_queueing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "social-asset-admission");

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce our new customer workshop to local business owners.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = new[] { Guid.Empty },
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, error.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Equal(0, await db.UsageTransactions.AsNoTracking().CountAsync(usage => usage.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Social_creation_still_accepts_explicitly_empty_identifier_lists()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "social-empty-lists");

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce our new customer workshop to local business owners.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Social_run_fails_closed_when_attachment_belongs_to_another_workspace()
    {
        // A well-formed identifier that is not scoped to the job's workspace
        // cannot be proven bad at admission, so it stays queueable, but the
        // execution boundary must contain it: the job fails closed and the
        // provider is never reached.
        using var foreign = factory.CreateClient();
        var foreignAuth = await Register(foreign, "social-foreign-owner");
        var foreignFileId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = foreignFileId,
                WorkspaceId = foreignAuth.PersonalWorkspace.Id,
                UserId = foreignAuth.User.Id,
                OriginalFileName = "foreign-notes.txt",
                StoredFileName = "foreign-notes.txt",
                ContentType = "text/plain",
                Extension = ".txt",
                SizeBytes = 17,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"social-foreign/{foreignFileId:N}.txt",
                Status = StoredFileStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                TextExtractionStatus = FileExtractionStatus.Ready,
                ExtractedText = "NOT FOR THE OTHER WORKSPACE",
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var auth = await Register(client, "social-foreign-attacker");
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce our new customer workshop to local business owners.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = new[] { foreignFileId },
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Null(terminal.ResultJson);

        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await checkDb.Assets.AsNoTracking().AnyAsync(asset => asset.SourceGenerationJobId == created.Job.Id));
    }

    [Fact]
    public async Task Social_revoked_member_cannot_reuse_attachment_id_and_loses_job_read_access()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "social-revoked-member");
        var fileId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = fileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                OriginalFileName = "workshop-notes.txt",
                StoredFileName = "workshop-notes.txt",
                ContentType = "text/plain",
                Extension = ".txt",
                SizeBytes = 21,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"social-member/{fileId:N}.txt",
                Status = StoredFileStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                TextExtractionStatus = FileExtractionStatus.Ready,
                ExtractedText = "WORKSHOP NOTES FOR LOCAL BUSINESSES",
            });
            await db.SaveChangesAsync();
        }

        // While a member, the same attachment id is accepted and the run is
        // scoped and succeeds with the deterministic provider.
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce our new customer workshop to local business owners.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = new[] { fileId },
        }, idempotencyKey: "social-member-first-request");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);

        // Revoke the membership, then reuse the very same attachment id.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.RemoveRange(db.WorkspaceMembers.Where(member => member.UserId == auth.User.Id));
            await db.SaveChangesAsync();
        }

        var reuse = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce our new customer workshop to local business owners.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = new[] { fileId },
        }, idempotencyKey: "social-revoked-reuse-request");
        Assert.Equal(HttpStatusCode.Forbidden, reuse.StatusCode);

        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await checkDb.GenerationJobs.AsNoTracking().CountAsync(job => job.WorkspaceId == auth.PersonalWorkspace.Id));

        // The revoked user also loses read access to the job created while a member.
        using var unread = await client.GetAsync($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal(HttpStatusCode.NotFound, unread.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string prefix)
    {
        var response = await SendWithCsrf(client, new
        {
            displayName = "Social Admission Tester",
            email = $"{prefix}-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, path: "/api/auth/register", idempotencyKey: null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs", string? idempotencyKey = "social-attachment-admission-request")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", $"{idempotencyKey}-{Guid.NewGuid():N}");
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
