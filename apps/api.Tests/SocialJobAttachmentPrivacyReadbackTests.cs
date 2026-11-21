using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Issue #349, readback complement to SocialJobPrivacyBoundaryTests (PR #1130).
//
// PR #1130 fences the *execution record* itself: a no-project `social.generate` job is hidden
// from a workspace co-member on the job GET, the workspace job list and its pagination totals,
// and on retry/cancel. Every test there creates its job with `attachmentIds = Array.Empty<Guid>()`,
// so the one thing the #349 record still carries that PR does not exercise is the **attachment
// surface** - the project-less personal stored file the creator handed to the job, plus the
// output asset the job produced from it.
//
// Those live behind three separate predicates that could each drift independently of
// `IsPrivateExecutionRecord`: `FilesController.CanAccessAsync` (direct file detail), the
// `StoredFileVisibility`-mirroring filter in `FilesController.List` (workspace file list), and
// the `generation`/`file`/`asset` groups in `GlobalSearchController`. This file pins that a
// second workspace member gets the *same boundary* across all of them for the *same* #349
// record - denial on every seam, no content disclosure on any seam, and no presence in any
// list or search page - while the creator keeps every seam, and while a co-member's OWN
// no-project social job stays visible so the fence cannot be satisfied by over-hiding.
//
// Synthetic and isolated: two freshly registered throwaway principals, a deterministic
// in-process `DeterministicSocialProvider` via `SocialGenerationApiFactory`, and a fabricated
// marker string. No paid inference, no provider call, no production secret, no real user data.
public sealed class SocialJobAttachmentPrivacyReadbackTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;

    public SocialJobAttachmentPrivacyReadbackTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Member_is_denied_no_project_social_job_attachment_on_detail_list_and_search_creator_keeps_all_three()
    {
        using var creator = factory.CreateClient();
        var creatorAuth = await Register(creator, "Attachment Privacy Creator");

        // One marker, reachable through every seam at once: it is the attachment's file name
        // (the `file` search group matches OriginalFileName), part of the job prompt (the
        // `generation` search group matches InputJson), and the attachment's body text.
        var marker = $"zq-attach-{Guid.NewGuid():N}";

        var attachment = await Upload(creator, creatorAuth.PersonalWorkspace.Id, $"{marker}.txt", $"Synthetic source body {marker}.", projectId: null);
        var job = await CreateSocialJob(creator, creatorAuth, projectId: null, attachmentIds: [attachment.Id], prompt: $"Summarise {marker} for the launch.");
        var terminal = await WaitForTerminal(creator, job.Id);
        Assert.Equal("Succeeded", terminal.Status);

        // The no-project job produced its creator-private output asset from that attachment.
        Guid outputAssetId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets.AsNoTracking().SingleAsync(item => item.SourceGenerationJobId == job.Id);
            outputAssetId = asset.Id;
        }

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Attachment Privacy Member");
        await AddMember(creatorAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        // --- direct GET / detail -------------------------------------------------------
        // Two different denial codes are the established policy for the two entities, so the
        // invariant that matters is that neither seam discloses content for the same record.
        using var memberJob = await member.GetAsync($"/api/generation/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.NotFound, memberJob.StatusCode);
        var memberJobBody = await memberJob.Content.ReadAsStringAsync();
        Assert.DoesNotContain(marker, memberJobBody, StringComparison.Ordinal);
        Assert.DoesNotContain(terminal.ResultJson!, memberJobBody, StringComparison.Ordinal);

        using var memberFile = await member.GetAsync($"/api/files/{attachment.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, memberFile.StatusCode);
        var memberFileBody = await memberFile.Content.ReadAsStringAsync();
        Assert.DoesNotContain(marker, memberFileBody, StringComparison.Ordinal);

        using var memberAsset = await member.GetAsync($"/api/assets/{outputAssetId}");
        Assert.Equal(HttpStatusCode.NotFound, memberAsset.StatusCode);
        using var memberDownload = await member.GetAsync($"/api/assets/{outputAssetId}/download");
        Assert.Equal(HttpStatusCode.NotFound, memberDownload.StatusCode);

        // --- list ----------------------------------------------------------------------
        var memberFiles = await member.GetFromJsonAsync<List<StoredFileDto>>($"/api/workspaces/{creatorAuth.PersonalWorkspace.Id}/files?pageSize=100");
        Assert.NotNull(memberFiles);
        Assert.DoesNotContain(memberFiles!, item => item.Id == attachment.Id);

        var memberJobs = await member.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={creatorAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.SocialGenerate}&pageSize=100");
        Assert.NotNull(memberJobs);
        Assert.Equal(0, memberJobs!.TotalCount);
        Assert.Equal(0, memberJobs.TotalPages);
        Assert.Empty(memberJobs.Items);

        // --- search --------------------------------------------------------------------
        var memberSearch = await member.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={marker}");
        Assert.NotNull(memberSearch);
        var memberHits = memberSearch!.Groups.SelectMany(group => group.Items).ToArray();
        Assert.DoesNotContain(memberHits, item => item.Id == job.Id || item.AssetId == job.Id);
        Assert.DoesNotContain(memberHits, item => item.Id == attachment.Id || item.AssetId == attachment.Id);
        Assert.DoesNotContain(memberHits, item => item.Id == outputAssetId || item.AssetId == outputAssetId);
        Assert.DoesNotContain(memberHits, item => string.Equals(item.Type, GlobalSearchResultTypes.Generation, StringComparison.Ordinal)
            || string.Equals(item.Type, GlobalSearchResultTypes.File, StringComparison.Ordinal)
            || string.Equals(item.Type, GlobalSearchResultTypes.Asset, StringComparison.Ordinal));
        // The response echoes the caller's own search term in its `Query` field, so the
        // anti-leak check is scoped to the result groups: the creator's marker must not
        // surface in any item the member receives (title, description or metadata).
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(memberSearch!.Groups), StringComparison.Ordinal);

        // --- own-record positive control -----------------------------------------------
        // The same three seams stay fully open to the creator, so the denials above are the
        // privacy fence and not an unrelated fixture or storage failure.
        using var creatorFile = await creator.GetAsync($"/api/files/{attachment.Id}");
        Assert.Equal(HttpStatusCode.OK, creatorFile.StatusCode);
        var creatorFileDto = await creatorFile.Content.ReadFromJsonAsync<StoredFileDto>();
        Assert.NotNull(creatorFileDto);
        Assert.Equal($"{marker}.txt", creatorFileDto!.OriginalFileName);
        Assert.Null(creatorFileDto.ProjectId);

        using var creatorJob = await creator.GetAsync($"/api/generation/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.OK, creatorJob.StatusCode);
        Assert.Contains("posts", await creatorJob.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var creatorAsset = await creator.GetAsync($"/api/assets/{outputAssetId}");
        Assert.Equal(HttpStatusCode.OK, creatorAsset.StatusCode);

        var creatorFiles = await creator.GetFromJsonAsync<List<StoredFileDto>>($"/api/workspaces/{creatorAuth.PersonalWorkspace.Id}/files?pageSize=100");
        Assert.NotNull(creatorFiles);
        Assert.Contains(creatorFiles!, item => item.Id == attachment.Id);

        var creatorSearch = await creator.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={marker}");
        Assert.NotNull(creatorSearch);
        var creatorHits = creatorSearch!.Groups.SelectMany(group => group.Items).ToArray();
        Assert.Contains(creatorHits, item => item.Id == attachment.Id && string.Equals(item.Type, GlobalSearchResultTypes.File, StringComparison.Ordinal));
        Assert.Contains(creatorHits, item => item.Id == job.Id && string.Equals(item.Type, GlobalSearchResultTypes.Generation, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Member_own_no_project_social_job_and_attachment_stay_visible_while_the_creators_stay_hidden_from_page_totals()
    {
        using var creator = factory.CreateClient();
        var creatorAuth = await Register(creator, "Mixed Social Privacy Owner");
        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Mixed Social Privacy Member");
        await AddMember(creatorAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        // Both principals create a no-project social job with its own attachment in the SAME
        // workspace. The fence is per-record and per-principal, so each keeps exactly its own.
        var creatorMarker = $"zq-own-{Guid.NewGuid():N}";
        var memberMarker = $"zq-peer-{Guid.NewGuid():N}";

        var creatorAttachment = await Upload(creator, creatorAuth.PersonalWorkspace.Id, $"{creatorMarker}.txt", $"Owner body {creatorMarker}.", projectId: null);
        var creatorJob = await CreateSocialJob(creator, creatorAuth, projectId: null, attachmentIds: [creatorAttachment.Id], prompt: $"Summarise {creatorMarker} for launch.");
        Assert.Equal("Succeeded", (await WaitForTerminal(creator, creatorJob.Id)).Status);

        var memberAttachment = await Upload(member, creatorAuth.PersonalWorkspace.Id, $"{memberMarker}.txt", $"Member body {memberMarker}.", projectId: null);
        var memberJob = await CreateSocialJob(member, creatorAuth, workspaceId: creatorAuth.PersonalWorkspace.Id, projectId: null, attachmentIds: [memberAttachment.Id], prompt: $"Summarise {memberMarker} for launch.");
        Assert.Equal("Succeeded", (await WaitForTerminal(member, memberJob.Id)).Status);

        // A single page must account for exactly the member's own record: the hidden creator
        // record may neither appear nor inflate TotalCount/TotalPages.
        var memberJobs = await member.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={creatorAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.SocialGenerate}&pageSize=100");
        Assert.NotNull(memberJobs);
        Assert.Equal(1, memberJobs!.TotalCount);
        Assert.Equal(1, memberJobs.TotalPages);
        Assert.Equal(memberJob.Id, Assert.Single(memberJobs.Items).Id);

        var creatorJobs = await creator.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={creatorAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.SocialGenerate}&pageSize=100");
        Assert.NotNull(creatorJobs);
        Assert.Equal(1, creatorJobs!.TotalCount);
        Assert.Equal(creatorJob.Id, Assert.Single(creatorJobs.Items).Id);

        var memberFiles = await member.GetFromJsonAsync<List<StoredFileDto>>($"/api/workspaces/{creatorAuth.PersonalWorkspace.Id}/files?pageSize=100");
        Assert.NotNull(memberFiles);
        Assert.Contains(memberFiles!, item => item.Id == memberAttachment.Id);
        Assert.DoesNotContain(memberFiles!, item => item.Id == creatorAttachment.Id);

        var creatorFiles = await creator.GetFromJsonAsync<List<StoredFileDto>>($"/api/workspaces/{creatorAuth.PersonalWorkspace.Id}/files?pageSize=100");
        Assert.NotNull(creatorFiles);
        Assert.Contains(creatorFiles!, item => item.Id == creatorAttachment.Id);
        Assert.DoesNotContain(creatorFiles!, item => item.Id == memberAttachment.Id);

        // Search agrees with the lists: each principal finds its own record and never the other's.
        var memberSearch = await member.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={memberMarker}");
        var memberCreatorSearch = await member.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={creatorMarker}");
        var creatorSearch = await creator.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={creatorMarker}");
        var creatorMemberSearch = await creator.GetFromJsonAsync<GlobalSearchResponseDto>($"/api/search?q={memberMarker}");
        Assert.NotNull(memberSearch);
        Assert.NotNull(memberCreatorSearch);
        Assert.NotNull(creatorSearch);
        Assert.NotNull(creatorMemberSearch);

        Assert.Contains(memberSearch!.Groups.SelectMany(group => group.Items), item => item.Id == memberJob.Id);
        Assert.DoesNotContain(memberCreatorSearch!.Groups.SelectMany(group => group.Items), item => item.Id == creatorJob.Id || item.Id == creatorAttachment.Id);
        Assert.Contains(creatorSearch!.Groups.SelectMany(group => group.Items), item => item.Id == creatorJob.Id);
        Assert.DoesNotContain(creatorMemberSearch!.Groups.SelectMany(group => group.Items), item => item.Id == memberJob.Id || item.Id == memberAttachment.Id);
    }

    private async Task<StoredFileDto> Upload(HttpClient client, Guid workspaceId, string name, string content, Guid? projectId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        if (projectId is not null) form.Add(new StringContent(projectId.Value.ToString()), "projectId");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new Exception($"Attachment upload returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        var stored = (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
        // Social job admission requires a Ready file with extracted text, and extraction runs
        // inline during upload; assert it rather than letting a fixture race turn into a
        // confusing SocialContextUnavailable further down.
        Assert.Equal("Ready", stored.Status);
        Assert.Equal("Ready", stored.TextExtractionStatus);
        return stored;
    }

    private async Task<GenerationJobDto> CreateSocialJob(HttpClient client, AuthResponse auth, Guid? projectId, IReadOnlyList<Guid> attachmentIds, string prompt, Guid? workspaceId = null)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/social-generation/jobs", new
        {
            workspaceId = workspaceId ?? auth.PersonalWorkspace.Id,
            projectId,
            prompt,
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds,
        });
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new Exception($"Social job creation returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        return (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!.Job;
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"social-attach-privacy-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var request = new HttpRequestMessage(method, path);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (path == "/api/social-generation/jobs")
        {
            request.Headers.Add("Idempotency-Key", $"social-attach-privacy-{Guid.NewGuid():N}");
        }
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private async Task AddMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Social job did not reach a terminal state.");
    }
}
