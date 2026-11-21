using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Persistence;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-only regression lane for security issue #936: a project-scoped Research job publishes
/// its report <see cref="Asset"/> with <c>ProjectId = job.ProjectId</c>
/// (<c>apps/api/Assets/GeneratedAssetPublisher.cs</c>), so <c>AssetVisibility.ForWorkspaceMember</c>
/// treats the report like an ordinary shared project deliverable and <c>AssetService</c> grants
/// every workspace member both the metadata and the real DOCX/PDF bytes through the normal
/// <c>/api/assets</c> read and download API. At the same time
/// <c>GenerationJobService.ListAsync</c> and the research ledger keep the *execution record*
/// creator-private, asserting a rationale ("their result JSON references assets that other
/// workspace members cannot read") that is false on this path.
///
/// The primary fact reproduces the leak over the normal asset channels as a non-creator
/// co-member of a synthetic two-member workspace and requires denial on BOTH dimensions:
/// API status (the repository fail-closed convention for creator-private reads is 404) and
/// bytes/metadata isolation (no document bytes, no report identity in bodies or listings).
/// It simultaneously pins the behavior a correct fix must preserve:
///   * full creator access to the report metadata, DOCX download and PDF representation;
///   * the existing creator privacy of the research execution record (404 for co-members);
///   * ordinary project-shared deliverables (here an Image Studio asset published through the
///     same GeneratedAssetPublisher with no Research provenance) stay readable by co-members.
///
/// A second fact guards the shared-deliverable axis in isolation so an over-tightened fix is
/// caught separately. Fixtures use the deterministic, provider-free fakes already in the
/// assembly: zero external provider calls, zero real charges, synthetic marker strings only,
/// no production data. This file touches no production code and is not merge authorization.
/// </summary>
public sealed class ResearchProjectReportAssetComemberPrivacyTests : IClassFixture<ResearchProjectReportAssetComemberPrivacyTests.ResearchReportAssetPrivacyRegressionApiFactory>
{
    // Markers emitted by FakeResearchSearchProvider / FakeResearchReportProvider
    // (apps/api.Tests/ResearchGenerationTests.cs).
    private const string ReportTitle = "Solar energy research";
    private const string SourceUrlMarker = "example.gov/energy";
    private const string SourceTextMarker = "Official energy evidence.";

    private readonly ResearchReportAssetPrivacyRegressionApiFactory factory;

    public ResearchProjectReportAssetComemberPrivacyTests(ResearchReportAssetPrivacyRegressionApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// Provider-free host that publishes both Research reports and Image Studio deliverables
    /// through the normal generation pipeline with deterministic in-process fakes.
    /// </summary>
    public sealed class ResearchReportAssetPrivacyRegressionApiFactory : GenerationJobsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ResearchGeneration:Enabled", "true");
            builder.UseSetting("ResearchGeneration:MaxSourceCount", "8");
            builder.UseSetting("ImageGeneration:Enabled", "true");
            builder.UseSetting("ImageGeneration:ProviderKey", "openai");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IResearchSearchProvider>();
                services.RemoveAll<IResearchReportProvider>();
                services.AddSingleton<IResearchSearchProvider, FakeResearchSearchProvider>();
                services.AddSingleton<IResearchReportProvider, FakeResearchReportProvider>();
                services.RemoveAll<IImageGenerationProvider>();
                services.AddSingleton<IImageGenerationProvider, DeterministicImageProvider>();
            });
        }
    }

    [Fact]
    public async Task Project_scoped_research_report_asset_denies_noncreator_comember_on_status_bytes_and_metadata()
    {
        // Owner runs the workspace; a second member performs research inside a shared project.
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Report Asset Privacy Workspace Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "Quarterly Research Program (synthetic)");

        using var researcher = factory.CreateClient();
        var researcherAuth = await Register(researcher, "Report Asset Privacy Researcher");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, researcherAuth.User.Id, attach: true);

        var created = await SendWithCsrf<CreateResearchGenerationResponse>(researcher, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            question = "Summarize the synthetic solar evidence.",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var job = await WaitForTerminal(researcher, created.Job.Id);
        Assert.True(job.Status == "Succeeded", $"Status={job.Status}; Code={job.ErrorCode}; Message={job.ErrorMessage}");

        var (assetId, pdfRepresentationId, assetName) = await ReadResearchPublicationAsync(created.Job.Id);

        // --- Preserved behavior 1: the creator keeps full access through every channel. ---
        var creatorMeta = await researcher.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, creatorMeta.StatusCode);
        Assert.Contains(assetName, await creatorMeta.Content.ReadAsStringAsync());
        var creatorDocx = await researcher.GetAsync($"/api/assets/{assetId}/download");
        Assert.Equal(HttpStatusCode.OK, creatorDocx.StatusCode);
        var creatorDocxBytes = await creatorDocx.Content.ReadAsByteArrayAsync();
        Assert.True(creatorDocxBytes.Length > 0, "The creator must receive the report bytes.");
        Assert.Contains("wordprocessingml", creatorDocx.Content.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var creatorPdf = await researcher.GetAsync($"/api/assets/{assetId}/representations/{pdfRepresentationId}/download");
        Assert.Equal(HttpStatusCode.OK, creatorPdf.StatusCode);
        var creatorPdfBytes = await creatorPdf.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, (await researcher.GetAsync($"/api/generation/jobs/{created.Job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await researcher.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources")).StatusCode);

        // --- Preserved behavior 2: the execution record is already creator-private. Pinned
        // so this lane only repairs the asset axis and a fix cannot silently widen or
        // collapse the record channels. ---
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/generation/jobs/{created.Job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/research-generation/jobs/{created.Job.Id}/sources")).StatusCode);

        // --- The defect under issue #936: on main every probe below currently returns 200
        // with real report bytes or the report identity, through the normal asset API, as an
        // attached non-creator co-member. Failures are collected so one run enumerates the
        // complete leaking surface. ---
        var leaks = new List<string>();
        RecordMetadataLeak(leaks, await owner.GetAsync($"/api/assets/{assetId}"), "asset metadata read", assetName);
        await RecordDocumentLeak(leaks, await owner.GetAsync($"/api/assets/{assetId}/download"), creatorDocxBytes, "asset download");
        await RecordDocumentLeak(leaks, await owner.GetAsync($"/api/assets/{assetId}/download?inline=true"), creatorDocxBytes, "asset inline read");
        await RecordDocumentLeak(leaks, await owner.GetAsync($"/api/assets/{assetId}/representations/{pdfRepresentationId}/download"), creatorPdfBytes, "pdf representation download");
        RecordListingLeak(leaks, await owner.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&projectId={project.Id}&status=Active&pageSize=24"), assetId, "project asset listing");
        RecordListingLeak(leaks, await owner.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=24"), assetId, "workspace asset listing");

        Assert.True(leaks.Count == 0,
            $"Non-creator workspace member reaches the creator-private project-scoped research report through the normal asset API (#936):\n{string.Join("\n", leaks)}");
    }

    [Fact]
    public async Task Ordinary_project_shared_image_asset_stays_readable_by_noncreator_comember()
    {
        // The complementary guarantee: an Image Studio deliverable published through the same
        // GeneratedAssetPublisher into the same project (no Research provenance) must remain
        // workspace-shared. This fact is green on main; a fix that hides shared deliverables
        // turns it red without disturbing the leak fact above.
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Shared Deliverable Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "Brand Program (synthetic)");

        using var colleague = factory.CreateClient();
        var colleagueAuth = await Register(colleague, "Shared Deliverable Colleague");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, colleagueAuth.User.Id, attach: true);

        var created = await SendWithCsrf<CreateImageGenerationResponse>(owner, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            title = "Product hero",
            description = "A refined ceramic cup on a pale stone table.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });
        var job = await WaitForTerminal(owner, created.Job.Id);
        Assert.True(job.Status == "Succeeded", $"Status={job.Status}; Code={job.ErrorCode}; Message={job.ErrorMessage}");

        var imageAssetId = await ReadPublishedImageAssetAsync(created.Job.Id);

        // The non-creator colleague reads the shared deliverable through the normal API...
        var meta = await colleague.GetAsync($"/api/assets/{imageAssetId}");
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        var sharedBytes = await DownloadBytesAsync(colleague, $"/api/assets/{imageAssetId}/download");
        var ownerBytes = await DownloadBytesAsync(owner, $"/api/assets/{imageAssetId}/download");
        Assert.True(sharedBytes.Length > 0, "The shared deliverable must stream real bytes to a co-member.");
        Assert.True(sharedBytes.SequenceEqual(ownerBytes), "The co-member download must carry the identical published bytes.");

        // ...and discovers it in the ordinary project listing.
        var listing = await colleague.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&projectId={project.Id}&status=Active&pageSize=24");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        var listingBody = await listing.Content.ReadAsStringAsync();
        Assert.True(ContainsId(listingBody, imageAssetId), $"Project asset listing must keep listing shared deliverables. Body: {Truncate(listingBody)}");
    }

    // ---------------------------------------------------------------------------------------
    // Leak recorders: each probe must FAIL CLOSED (non-2xx status) and must carry neither the
    // document bytes nor the report identity. Successful status, byte equality with the
    // creator's document, or an identity leak each add a line to the collected surface.
    // ---------------------------------------------------------------------------------------

    private static void RecordMetadataLeak(List<string> leaks, HttpResponseMessage response, string channel, string assetName)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var identityLeaked = body.Contains(assetName, StringComparison.OrdinalIgnoreCase)
            || body.Contains(ReportTitle, StringComparison.OrdinalIgnoreCase);
        if (response.IsSuccessStatusCode || identityLeaked)
        {
            leaks.Add($"- {channel}: status {(int)response.StatusCode} ({contentType}), identity leaked: {(identityLeaked ? "yes" : "no")}, body: {Truncate(body)}");
        }
    }

    private static async Task RecordDocumentLeak(List<string> leaks, HttpResponseMessage response, byte[] creatorBytes, string channel)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var body = System.Text.Encoding.UTF8.GetString(bytes);
        var documentDelivered = bytes.SequenceEqual(creatorBytes)
            || contentType.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("application/pdf", StringComparison.OrdinalIgnoreCase);
        var textLeaked = body.Contains(ReportTitle, StringComparison.OrdinalIgnoreCase)
            || body.Contains(SourceUrlMarker, StringComparison.OrdinalIgnoreCase)
            || body.Contains(SourceTextMarker, StringComparison.OrdinalIgnoreCase);
        if (response.IsSuccessStatusCode || documentDelivered || textLeaked)
        {
            leaks.Add($"- {channel}: status {(int)response.StatusCode} ({contentType}), bytes identical to the creator's document: {(bytes.SequenceEqual(creatorBytes) ? "yes" : "no")}, byte length {bytes.Length}, body: {Truncate(body)}");
        }
    }

    private static void RecordListingLeak(List<string> leaks, HttpResponseMessage response, Guid assetId, string channel)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (ContainsId(body, assetId) || body.Contains(ReportTitle, StringComparison.OrdinalIgnoreCase))
        {
            leaks.Add($"- {channel}: status {(int)response.StatusCode} exposes the research report asset {assetId:D} to a non-creator co-member, body: {Truncate(body)}");
        }
    }

    private static bool ContainsId(string body, Guid id) =>
        body.Contains(id.ToString("D")) || body.Contains(id.ToString("N"));

    private async Task<(Guid AssetId, Guid PdfRepresentationId, string AssetName)> ReadResearchPublicationAsync(Guid generationJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.AsNoTracking()
            .Include(item => item.Representations)
            .SingleAsync(item => item.SourceGenerationJobId == generationJobId);
        var pdf = asset.Representations.Single(item => item.RepresentationType == AssetRepresentationTypes.Pdf);
        return (asset.Id, pdf.Id, asset.Name);
    }

    private async Task<Guid> ReadPublishedImageAssetAsync(Guid generationJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.AsNoTracking()
            .SingleAsync(item => item.SourceGenerationJobId == generationJobId && item.AssetType == AssetTypes.Image);
        return asset.Id;
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
            email = $"r936-{Guid.NewGuid():N}@example.com",
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
        throw new TimeoutException($"Job {id} did not reach a terminal state. Last: {current?.Status ?? "null"}/{current?.ErrorCode}");
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

    private static string Truncate(string value) => value.Length <= 400 ? value : value[..400];

    private static async Task<byte[]> DownloadBytesAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsByteArrayAsync();
    }
}
