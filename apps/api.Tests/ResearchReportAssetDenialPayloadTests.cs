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
/// One focused negative synthetic authorization probe for the creator-private
/// project-scoped Research report asset (#936, reviewed on PR #1042): what the
/// fail-closed denial response itself carries.
///
/// The sibling lanes pin the denial STATUS and content markers -
/// <c>ResearchSourceAssetRevocationTests</c> (#934/#1042) asserts 404 fail-closed
/// with no source markers echoed, and <c>ResearchProjectReportAssetComemberPrivacyTests</c>
/// (#1026) enumerates the non-creator co-member leak. Neither asserts the payload
/// MINIMALITY of the denial: that the error response returns no signed/download URL
/// of any form (no scheme, no storage key, no GUID identifiers) and no content
/// metadata (asset name, file name, MIME type, byte size, representation listing) -
/// only the repository's generic <c>ASSET_NOT_FOUND</c> error envelope.
///
/// This fact pins exactly that contract for the two subjects that must NEVER read
/// the report asset over the normal API:
///   1. a revoked workspace member (the #934 revoke axis on the research lane), and
///   2. a cross-workspace stranger who was never a member of the asset workspace.
/// Across all four asset read channels - metadata, DOCX download, inline read and
/// PDF representation download - every response must be the 404 JSON error envelope
/// with no URL-like content and none of the live asset's metadata values.
///
/// Fixtures use the deterministic, provider-independent <see cref="ResearchApiFactory"/>
/// fakes: zero external provider calls, zero charge, synthetic <c>@example.com</c>
/// identities only, and no real user data. QA evidence only - not a security sign-off.
/// </summary>
public sealed class ResearchReportAssetDenialPayloadTests : IClassFixture<ResearchApiFactory>
{
    // Marker emitted by FakeResearchReportProvider (apps/api.Tests/ResearchGenerationTests.cs).
    private const string ReportTitle = "Solar energy research";

    // Field names a content-metadata body would carry but an ASSET_NOT_FOUND
    // error envelope must never contain.
    private static readonly string[] ForbiddenMetadataFields =
    {
        "\"name\":", "\"mimeType\":", "\"sizeBytes\":", "\"fileName\":",
        "\"storageKey\":", "\"contentType\":", "\"representations\":",
        "\"downloadUrl\":", "\"url\":", "\"projectId\":", "\"workspaceId\":",
    };

    private readonly ResearchApiFactory factory;

    public ResearchReportAssetDenialPayloadTests(ResearchApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Revoked_member_and_cross_workspace_stranger_receive_no_url_or_metadata_on_research_report_asset_reads()
    {
        // Workspace A: owner + researcher member + shared project; researcher runs Research.
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Denial Payload Workspace Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "Denial Payload Program (synthetic)");

        using var researcher = factory.CreateClient();
        var researcherAuth = await Register(researcher, "Denial Payload Researcher");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, researcherAuth.User.Id, attach: true);

        var created = await SendWithCsrf<CreateResearchGenerationResponse>(researcher, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            question = "Summarize the synthetic evidence for the denial payload probe.",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var job = await WaitForTerminal(researcher, created.Job.Id);
        Assert.True(job.Status == "Succeeded", $"Status={job.Status}; Code={job.ErrorCode}; Message={job.ErrorMessage}");

        // Live asset values that a leaky denial body could echo.
        var probe = ReadAssetProbe(created.Job.Id);
        var assetId = probe.Id;

        // Baseline while attached: the researcher (creator) reads metadata as 200 with the
        // very tokens the denial must withhold, so the forbidden-token set is discriminative
        // and the access path demonstrably worked before the denials below are asserted.
        var baseline = await researcher.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
        var baselineBody = await baseline.Content.ReadAsStringAsync();
        Assert.Contains(probe.Name, baselineBody, StringComparison.Ordinal);
        Assert.Contains("\"mimeType\":", baselineBody, StringComparison.Ordinal);

        // Channel paths exercised for every denied subject.
        var channels = new (string Name, string Path)[]
        {
            ("metadata", $"/api/assets/{assetId}"),
            ("download", $"/api/assets/{assetId}/download"),
            ("inline download", $"/api/assets/{assetId}/download?inline=true"),
            ("pdf representation download", $"/api/assets/{assetId}/representations/{probe.PdfRepresentationId}/download"),
        };

        // Subject 1: cross-workspace stranger - its own workspace, never a member of A.
        using var stranger = factory.CreateClient();
        var strangerAuth = await Register(stranger, "Denial Payload Foreign Workspace User");
        Assert.NotEqual(strangerAuth.PersonalWorkspace.Id, ownerAuth.PersonalWorkspace.Id);
        foreach (var (name, path) in channels)
        {
            AssertMinimalDenial(await stranger.GetAsync(path), name, "cross-workspace stranger", probe);
        }

        // Subject 2: the researcher after membership revoke (#934 axis on the research lane).
        await SetMembership(ownerAuth.PersonalWorkspace.Id, researcherAuth.User.Id, attach: false);
        foreach (var (name, path) in channels)
        {
            AssertMinimalDenial(await researcher.GetAsync(path), name, "revoked member", probe);
        }
    }

    /// <summary>
    /// A fail-closed asset denial must be exactly the generic 404 ASSET_NOT_FOUND error
    /// envelope: JSON only, no document disposition/location headers, no URL of any form,
    /// and none of the live asset's identifiers or content-metadata values or field names.
    /// </summary>
    private static void AssertMinimalDenial(HttpResponseMessage response, string channel, string subject, AssetProbe probe)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        Assert.True(HttpStatusCode.NotFound == response.StatusCode,
            $"{subject} on {channel} unexpectedly returned {(int)response.StatusCode}: {Truncate(body)}");

        // Headers carry neither a document body nor a location to fetch one from.
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.False(response.Headers.TryGetValues("Content-Location", out _));
        Assert.StartsWith("application/json", contentType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wordprocessingml", contentType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("application/pdf", contentType, StringComparison.OrdinalIgnoreCase);

        // No signed/download URL of any form in the body.
        Assert.DoesNotContain("://", body, StringComparison.OrdinalIgnoreCase);

        // No live identifiers echoed - these would seed signed-URL or enumeration guesses.
        Assert.DoesNotContain(probe.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(probe.Id.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(probe.StorageKey, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(probe.StoredFileName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(probe.PdfRepresentationId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(probe.PdfRepresentationId.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(probe.PdfFileName, body, StringComparison.OrdinalIgnoreCase);

        // No content metadata - neither the values nor the DTO field names that carry them.
        Assert.DoesNotContain(probe.Name, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(probe.OriginalFileName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReportTitle, body, StringComparison.OrdinalIgnoreCase);
        foreach (var field in ForbiddenMetadataFields)
        {
            Assert.DoesNotContain(field, body, StringComparison.OrdinalIgnoreCase);
        }

        // Positive shape check: the denial is the repository's generic envelope.
        Assert.Contains("ASSET_NOT_FOUND", body, StringComparison.Ordinal);
    }

    private sealed record AssetProbe(
        Guid Id,
        string Name,
        Guid PdfRepresentationId,
        string PdfFileName,
        string StorageKey,
        string StoredFileName,
        string OriginalFileName);

    private AssetProbe ReadAssetProbe(Guid generationJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = db.Assets.AsNoTracking()
            .Include(item => item.Representations)
            .Include(item => item.StoredFile)
            .Single(item => item.SourceGenerationJobId == generationJobId);
        Assert.Equal(AssetTypes.Research, asset.AssetType);
        var pdf = asset.Representations.Single(item => item.RepresentationType == AssetRepresentationTypes.Pdf);
        // The forbidden-token set must carry real discriminative values, never an empty
        // string that DoesNotContain would treat as trivially leaked.
        Assert.False(string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(pdf.FileName)
            || string.IsNullOrWhiteSpace(asset.StoredFile!.StorageKey) || asset.StoredFile.StorageKey.Length <= 8
            || string.IsNullOrWhiteSpace(asset.StoredFile.StoredFileName) || asset.StoredFile.StoredFileName.Length <= 8);
        return new AssetProbe(
            asset.Id,
            asset.Name,
            pdf.Id,
            pdf.FileName,
            asset.StoredFile!.StorageKey,
            asset.StoredFile.StoredFileName,
            asset.StoredFile.OriginalFileName);
    }

    private static string Truncate(string value) => value.Length <= 400 ? value : value[..400];

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
            email = $"rpdenial-{Guid.NewGuid():N}@example.com",
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
