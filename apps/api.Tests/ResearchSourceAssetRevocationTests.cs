using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Privacy-focused synthetic acceptance for Research search-result source assets in a
/// shared workspace. Research persists, per succeeded job, a source ledger
/// (<c>ResearchSource</c>/<c>ResearchEvidence</c> rows plus a CSV export), a report
/// <c>Asset</c> with DOCX and PDF representations, and, for uploaded sources, the
/// backing <c>StoredFile</c>. After a workspace membership revoke every one of those
/// channels must fail closed and must not echo source content.
///
/// Fixtures use the deterministic, provider-independent <see cref="ResearchApiFactory"/>
/// fakes: zero external provider calls, zero charge, synthetic marker strings only, and
/// no real user data. Revocation follows the repository's semantics (the
/// <c>WorkspaceMember</c> row is hard-removed; there is no soft flag).
/// </summary>
public sealed class ResearchSourceAssetRevocationTests : IClassFixture<ResearchApiFactory>
{
    // Markers emitted by FakeResearchSearchProvider / FakeResearchReportProvider.
    private const string ReportTitle = "Solar energy research";
    private const string SourceUrlMarker = "example.gov/energy";
    private const string SourceTitleMarker = "Energy ministry report";
    private const string SourceTextMarker = "Official energy evidence";
    private const string UploadedSourceMarker = "REVOKED-SOURCE-FILE-MARKER";

    private readonly ResearchApiFactory factory;

    public ResearchSourceAssetRevocationTests(ResearchApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Project_scoped_research_source_ledger_and_report_assets_fail_closed_after_membership_revoke()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Research Revocation Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "Evidence Program (synthetic)");

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Research Revocation Member");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: true);

        // The member uploads a project source file and runs Research over it plus
        // hosted search results, so every source-asset channel has a backing row.
        var sourceFile = NewReadySourceFile(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, project.Id);
        await InsertSourceFile(sourceFile);
        var created = await SendWithCsrf<CreateResearchGenerationResponse>(member, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            question = "Summarize the selected source safely.",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = new[] { sourceFile.Id },
        });
        var job = await WaitForTerminal(member, created.Job.Id);
        Assert.True(job.Status == "Succeeded", $"Status={job.Status}; Code={job.ErrorCode}; Message={job.ErrorMessage}");

        var (assetId, pdfRepresentationId) = await ReadResearchPublicationAsync(created.Job.Id);

        // Baseline while attached: the ledger, the CSV export and the project report
        // asset are all readable, so the post-revoke denials below are proof of the
        // revoke taking effect rather than of an access path that never worked.
        var baselineLedger = await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources");
        Assert.Equal(HttpStatusCode.OK, baselineLedger.StatusCode);
        Assert.Contains(SourceUrlMarker, await baselineLedger.Content.ReadAsStringAsync());
        var baselineExport = await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export");
        Assert.Equal(HttpStatusCode.OK, baselineExport.StatusCode);
        Assert.Contains(SourceTitleMarker, await baselineExport.Content.ReadAsStringAsync());
        var baselineAsset = await member.GetAsync($"/api/assets/{assetId}/download");
        Assert.Equal(HttpStatusCode.OK, baselineAsset.StatusCode);
        var baselinePdf = await member.GetAsync($"/api/assets/{assetId}/representations/{pdfRepresentationId}/download");
        Assert.Equal(HttpStatusCode.OK, baselinePdf.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/files/{sourceFile.Id}")).StatusCode);

        // Revoke the researcher's workspace membership.
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: false);

        // Source ledger read and CSV export fail closed with no source content echoed.
        AssertNoSourceContent(await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources"), "source ledger");
        AssertNoSourceContent(await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export"), "source csv export");

        // The job record and the workspace listing that carry the asset/URL references.
        AssertNoSourceContent(await member.GetAsync($"/api/generation/jobs/{created.Job.Id}"), "job record");
        var jobList = await member.GetAsync($"/api/generation/jobs?workspaceId={ownerAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.ResearchGenerate}");
        Assert.Equal(HttpStatusCode.Forbidden, jobList.StatusCode);
        var assetList = await member.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=24");
        Assert.Equal(HttpStatusCode.Forbidden, assetList.StatusCode);

        // Report asset metadata and every published representation fail closed.
        AssertNoSourceContent(await member.GetAsync($"/api/assets/{assetId}"), "report asset metadata");
        AssertDocumentDenied(await member.GetAsync($"/api/assets/{assetId}/download"), "report asset download");
        AssertDocumentDenied(await member.GetAsync($"/api/assets/{assetId}/download?inline=true"), "report asset inline read");
        AssertDocumentDenied(await member.GetAsync($"/api/assets/{assetId}/representations/{pdfRepresentationId}/download"), "report pdf representation");

        // The uploaded source file itself is unreachable once membership is gone.
        AssertNoSourceContent(await member.GetAsync($"/api/files/{sourceFile.Id}"), "uploaded source file metadata");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrf(member, HttpMethod.Delete, $"/api/files/{sourceFile.Id}", null)).StatusCode);

        // Mutation and re-execution channels fail closed too.
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/research-generation/jobs/{Guid.NewGuid():N}/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrf(member, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrf(member, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null)).StatusCode);

        // Revoke is non-destructive: the project report asset and source file rows
        // survive, so the remaining member can still open the published report.
        var ownerAsset = await owner.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, ownerAsset.StatusCode);
        var ownerDownload = await owner.GetAsync($"/api/assets/{assetId}/download");
        Assert.Equal(HttpStatusCode.OK, ownerDownload.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            // Two hosted search results plus the explicitly selected uploaded source;
            // revocation removes only the membership row, never the source ledger.
            var ledger = await db.ResearchSources.AsNoTracking()
                .Where(item => item.GenerationJobId == created.Job.Id)
                .ToListAsync();
            Assert.Equal(3, ledger.Count);
            Assert.Equal(2, ledger.Count(item => item.SourceType == "web"));
            Assert.Contains(ledger, item => item.StoredFileId == sourceFile.Id);
            Assert.True(await db.StoredFiles.AsNoTracking().AnyAsync(item => item.Id == sourceFile.Id));
        }

        // Re-admission restores the revoked researcher's own access.
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: true);
        var afterRejoin = await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources");
        Assert.Equal(HttpStatusCode.OK, afterRejoin.StatusCode);
        Assert.Contains(SourceUrlMarker, await afterRejoin.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/assets/{assetId}/download")).StatusCode);
    }

    [Fact]
    public async Task Revoked_creator_loses_own_personal_research_sources_while_workspace_member_cannot_read_the_report_bytes()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Personal Research Workspace Owner");
        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Personal Research Workspace Member");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: true);

        // A project-less research job: the report asset stays private to its creator
        // under AssetVisibility, and research execution records stay creator-private.
        var created = await SendWithCsrf<CreateResearchGenerationResponse>(member, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            question = "What are the current opportunities and risks for solar energy in Iraq?",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var job = await WaitForTerminal(member, created.Job.Id);
        Assert.True(job.Status == "Succeeded", $"Status={job.Status}; Code={job.ErrorCode}; Message={job.ErrorMessage}");
        var (assetId, _) = await ReadResearchPublicationAsync(created.Job.Id);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/assets/{assetId}/download")).StatusCode);

        // The co-member never had the creator-private record, and asset visibility
        // keeps the personal report bytes out of reach as well.
        AssertNoSourceContent(await owner.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources"), "co-member source ledger");
        AssertNoSourceContent(await owner.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export"), "co-member source csv export");
        AssertDocumentDenied(await owner.GetAsync($"/api/assets/{assetId}/download"), "co-member report asset download");

        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: false);
        AssertNoSourceContent(await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources"), "revoked creator source ledger");
        AssertNoSourceContent(await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export"), "revoked creator source csv export");
        AssertDocumentDenied(await member.GetAsync($"/api/assets/{assetId}"), "revoked creator report asset metadata");
        AssertDocumentDenied(await member.GetAsync($"/api/assets/{assetId}/download"), "revoked creator report asset download");

        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: true);
        var restored = await member.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources/export");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Contains("citationId", await restored.Content.ReadAsStringAsync());
    }

    private void AssertNoSourceContent(HttpResponseMessage response, string channel)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(!response.IsSuccessStatusCode, $"{channel} unexpectedly returned {(int)response.StatusCode} with body: {Truncate(body)}");
        Assert.DoesNotContain(SourceUrlMarker, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SourceTitleMarker, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SourceTextMarker, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UploadedSourceMarker, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReportTitle, body, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertDocumentDenied(HttpResponseMessage response, string channel)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // A fail-closed denial is a JSON error envelope, never a document body.
        Assert.DoesNotContain("wordprocessingml", contentType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pdf", contentType, StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value) => value.Length <= 400 ? value : value[..400];

    private async Task<(Guid AssetId, Guid PdfRepresentationId)> ReadResearchPublicationAsync(Guid generationJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.AsNoTracking()
            .Include(item => item.Representations)
            .SingleAsync(item => item.SourceGenerationJobId == generationJobId);
        var pdf = asset.Representations.Single(item => item.RepresentationType == AssetRepresentationTypes.Pdf);
        return (asset.Id, pdf.Id);
    }

    private StoredFile NewReadySourceFile(Guid workspaceId, Guid userId, Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        ProjectId = projectId,
        OriginalFileName = "revoked-source.txt",
        StoredFileName = $"{Guid.NewGuid():N}.txt",
        ContentType = "text/plain",
        Extension = ".txt",
        SizeBytes = UploadedSourceMarker.Length,
        StorageProvider = FileStorageProviders.Local,
        StorageKey = $"research-revocation/{Guid.NewGuid():N}.txt",
        Status = StoredFileStatus.Ready,
        CreatedAt = DateTime.UtcNow,
        ProcessedAt = DateTime.UtcNow,
        TextExtractionStatus = FileExtractionStatus.Ready,
        ExtractedText = $"{UploadedSourceMarker} Hosted evidence for the research question.",
        ExtractedTextLength = UploadedSourceMarker.Length,
    };

    private async Task InsertSourceFile(StoredFile file)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.StoredFiles.Add(file);
        await db.SaveChangesAsync();
    }

    private async Task SetMembership(Guid workspaceId, Guid userId, bool attach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        if (attach)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
        }
        else
        {
            var membership = await db.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == workspaceId && item.UserId == userId);
            db.WorkspaceMembers.Remove(membership);
        }
        await db.SaveChangesAsync();
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"rsrev-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name) =>
        SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        GenerationJobDto? current = null;
        for (var attempt = 0; attempt < 320; attempt += 1)
        {
            using var response = await client.GetAsync($"/api/generation/jobs/{id}");
            if (response.IsSuccessStatusCode) current = await response.Content.ReadFromJsonAsync<GenerationJobDto>();
            if (current is not null && current.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Research job {id} did not reach a terminal state. Last: {current?.Status ?? "null"}/{current?.ErrorCode}");
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} -> {(int)response.StatusCode}: {Truncate(await response.Content.ReadAsStringAsync())}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
