using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Privacy-focused synthetic acceptance for Health Care report assets in shared
/// workspaces: after a workspace membership revoke, every read and download path
/// must fail closed and no report content may appear in any response body.
/// All fixtures use the deterministic, provider-independent <c>system.test</c>
/// job and synthetic marker strings only; no real user data and no external
/// storage provider is involved.
/// </summary>
public sealed class HealthCareReportAssetRevocationTests : IClassFixture<GenerationJobsApiFactory>
{
    private const string ReportTitle = "Member wellness report (synthetic)";
    private const string ProjectName = "Care Coordination (synthetic)";
    private const string SyntheticContentMarker = "system.test";

    private readonly GenerationJobsApiFactory factory;

    public HealthCareReportAssetRevocationTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Shared_workspace_report_asset_read_and_download_fail_closed_after_membership_revoke()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Report Workspace Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, ProjectName);

        // Deterministic, provider-independent publication of the report asset.
        var job = await CreateJob(owner, ownerAuth.PersonalWorkspace.Id, project.Id, ReportTitle);
        await WaitForTerminal(owner, job.Id);
        var ownerAssets = await owner.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=20");
        var report = Assert.Single(ownerAssets!.Items, item => item.Name == ReportTitle);

        // Grant workspace membership, then prove the pre-revoke baseline: reads and
        // downloads are allowed while the member is attached to the workspace.
        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Report Workspace Member");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: true);

        var listed = await member.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=20");
        Assert.Contains(listed!.Items, item => item.Id == report.Id);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/assets/{report.Id}")).StatusCode);
        var baseline = await member.GetAsync($"/api/assets/{report.Id}/download");
        Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
        Assert.Contains(SyntheticContentMarker, await baseline.Content.ReadAsStringAsync());

        // Revoke the workspace membership (hard removal of the member row).
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: false);

        // Every read path fails closed and never echoes report content.
        var listAfterRevoke = await member.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=20");
        Assert.Equal(HttpStatusCode.Forbidden, listAfterRevoke.StatusCode);
        AssertNoReportContent(await member.GetAsync($"/api/assets/{report.Id}"), "metadata read");
        AssertNoReportContent(await member.GetAsync($"/api/assets/{report.Id}/download"), "download");
        AssertNoReportContent(await member.GetAsync($"/api/assets/{report.Id}/download?inline=true"), "inline read");
        AssertNoReportContent(await member.GetAsync($"/api/assets/{report.Id}/representations/{Guid.NewGuid():N}/download"), "representation download");

        // Mutation paths fail closed too: a revoked member cannot rename or archive.
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrf(member, HttpMethod.Patch, $"/api/assets/{report.Id}", new { name = "Post-revoke rename" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrf(member, HttpMethod.Post, $"/api/assets/{report.Id}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrf(member, HttpMethod.Post, $"/api/assets/{report.Id}/restore", null)).StatusCode);

        // Revoke is non-destructive: the owner keeps full access to the report.
        var ownerDownload = await owner.GetAsync($"/api/assets/{report.Id}/download");
        Assert.Equal(HttpStatusCode.OK, ownerDownload.StatusCode);
        Assert.Contains(SyntheticContentMarker, await ownerDownload.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revoked_creator_loses_access_to_own_workspace_report_assets_until_rejoined()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Care Program Owner");

        // The member authors a personal (project-less) synthetic health report in
        // the shared workspace; visibility keeps it private to its creator.
        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Care Program Member");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: true);

        var job = await CreateJob(member, ownerAuth.PersonalWorkspace.Id, null, ReportTitle);
        await WaitForTerminal(member, job.Id);
        var memberAssets = await member.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=20");
        var report = Assert.Single(memberAssets!.Items, item => item.Name == ReportTitle);

        var sharedListing = await owner.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=20");
        Assert.DoesNotContain(sharedListing!.Items, item => item.Id == report.Id);

        var beforeRevoke = await member.GetAsync($"/api/assets/{report.Id}/download");
        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);

        // Revoke the creator: the report bytes stay workspace-scoped, so even the
        // creator may not read or download them from outside the workspace.
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: false);
        AssertNoReportContent(await member.GetAsync($"/api/assets/{report.Id}"), "creator metadata read");
        AssertNoReportContent(await member.GetAsync($"/api/assets/{report.Id}/download"), "creator download");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}&status=Active&pageSize=20")).StatusCode);

        // Re-admission restores access; the asset itself was never destroyed.
        await SetMembership(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id, attach: true);
        var afterRejoin = await member.GetAsync($"/api/assets/{report.Id}/download");
        Assert.Equal(HttpStatusCode.OK, afterRejoin.StatusCode);
        Assert.Contains(SyntheticContentMarker, await afterRejoin.Content.ReadAsStringAsync());
    }

    private void AssertNoReportContent(HttpResponseMessage response, string channel)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(SyntheticContentMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportTitle, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".json", body, StringComparison.OrdinalIgnoreCase);
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
            var member = await db.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == workspaceId && item.UserId == userId);
            db.WorkspaceMembers.Remove(member);
        }
        await db.SaveChangesAsync();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"hcrev-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name) =>
        SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });

    private static Task<GenerationJobDto> CreateJob(HttpClient client, Guid workspaceId, Guid? projectId, string title) =>
        SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId, projectId, jobType = "system.test", title, inputJson = "{}" });

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 200; attempt += 1)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {id} did not reach a terminal state.");
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
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
