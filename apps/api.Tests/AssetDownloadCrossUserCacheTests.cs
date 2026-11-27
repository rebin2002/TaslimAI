using System.Net;
using System.Net.Http.Headers;
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
/// Cross-user cache behaviour of authenticated asset downloads over the real pipeline.
///
/// The download URL is scoped to the asset id only. Two principals therefore request the
/// identical cache key, and the response additionally carries the owner's original file name
/// in Content-Disposition. These tests pin the fence that keeps an intermediary from storing
/// or replaying one principal's answer to another.
/// </summary>
public sealed class AssetDownloadCrossUserCacheTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public AssetDownloadCrossUserCacheTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Owner_and_workspace_member_downloads_of_the_same_url_are_both_non_storable()
    {
        var (owner, assetId) = await CreatePublishedAssetAsync("Cache Fence Owner", "Cross User Cache Asset");

        var member = factory.CreateClient();
        var memberAuth = await Register(member, "Cache Fence Member");
        await AddMemberAsync(memberAuth.User.Id, owner.WorkspaceId);

        var ownerDownload = await owner.Client.GetAsync($"/api/assets/{assetId}/download");
        var memberDownload = await member.GetAsync($"/api/assets/{assetId}/download");

        Assert.Equal(HttpStatusCode.OK, ownerDownload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, memberDownload.StatusCode);

        AssertNonStorable(ownerDownload);
        AssertNonStorable(memberDownload);

        // Both principals share one URL, so a storeable response would be replayed across them.
        Assert.Equal(
            ownerDownload.Headers.CacheControl?.ToString(),
            memberDownload.Headers.CacheControl?.ToString());

        // The private original file name travels in Content-Disposition, so it must never land
        // in a response an intermediary is allowed to keep.
        Assert.Equal("attachment", ownerDownload.Content.Headers.ContentDisposition?.DispositionType);
        Assert.False(string.IsNullOrWhiteSpace(ownerDownload.Content.Headers.ContentDisposition?.FileName));
    }

    [Fact]
    public async Task Range_requests_for_inline_media_keep_the_private_fence()
    {
        var (owner, assetId) = await CreatePublishedAssetAsync("Range Fence Owner", "Range Cache Asset");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{assetId}/download?inline=true");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var response = await owner.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentRange);
        AssertNonStorable(response);
    }

    [Fact]
    public async Task Inline_request_for_a_non_previewable_asset_stays_an_attachment_and_stays_private()
    {
        var (owner, assetId) = await CreatePublishedAssetAsync("Inline Fallthrough Owner", "Inline Fallthrough Asset");

        var response = await owner.Client.GetAsync($"/api/assets/{assetId}/download?inline=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        AssertNonStorable(response);
    }

    private sealed record OwnerClient(HttpClient Client, Guid WorkspaceId);

    private async Task<(OwnerClient Owner, Guid AssetId)> CreatePublishedAssetAsync(string ownerName, string assetTitle)
    {
        var client = factory.CreateClient();
        var auth = await Register(client, ownerName);
        var project = await CreateProject(client, auth.PersonalWorkspace.Id, $"{assetTitle} Project");
        var job = await CreateJob(client, auth.PersonalWorkspace.Id, project.Id, assetTitle);
        await WaitForTerminal(client, job.Id);

        var listed = await client.GetFromJsonAsync<AssetListDto>(
            $"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&projectId={project.Id}&status=Active&page=1&pageSize=10");
        var asset = Assert.Single(listed!.Items, item => item.Name == assetTitle);
        return (new OwnerClient(client, auth.PersonalWorkspace.Id), asset.Id);
    }

    private async Task AddMemberAsync(Guid userId, Guid workspaceId)
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

    private static void AssertNonStorable(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(cacheControl),
            "Authenticated asset downloads answered without any Cache-Control directive, leaving them open to heuristic caching by shared intermediaries.");
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("public", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s-maxage", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cacheControl, StringComparison.OrdinalIgnoreCase);
        // An Expires header is acceptable only when it is already stale. A future-dated Expires
        // would hand a shared cache a freshness lifetime for private asset bytes.
        if (response.Headers.TryGetValues("Expires", out var expiresValues))
        {
            var expires = DateTimeOffset.Parse(string.Join(",", expiresValues)!);
            Assert.True(expires <= DateTimeOffset.UtcNow,
                $"Authenticated asset downloads must not carry a future-dated Expires; found {expires:O}.");
        }
        // The download URL is asset-scoped, so the credential is the only thing separating two
        // principals' answers. An intermediary configured to ignore Cache-Control still has to
        // honour the cache-key fence, which is what keeps one user's bytes out of another's hit.
        Assert.Contains("Cookie", string.Join(",", response.Headers.Vary), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register",
            new { displayName, email = $"asset-cache-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name) =>
        SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });

    private static Task<GenerationJobDto> CreateJob(HttpClient client, Guid workspaceId, Guid? projectId, string title) =>
        SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs",
            new { workspaceId, projectId, jobType = "system.test", title, inputJson = "{}" });

    private static async Task WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled")
            {
                Assert.Equal("Succeeded", job.Status);
                return;
            }
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
