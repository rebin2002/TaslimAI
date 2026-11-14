using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Product-ad preview asset-privacy contract, provider-free.
///
/// Covers the case not exercised elsewhere: a brand logo attached to a product-ad
/// campaign project is previewed inline, the browser session is logged out, a second
/// account signs in on that same cookie jar (a real account switch), and the preview
/// URL must then expose nothing from the other account's project — neither while the
/// session is anonymous nor after the switch. PR #736 adds the authenticated preview
/// route, PR #785 pins logo optionality, and PR #725 covers foreign-user image
/// readback from a separate client; none of them pins the logout leg or the
/// project-attached-logo-after-account-switch leg.
/// </summary>
public sealed class ProductAdLogoPreviewAccountSwitchIsolationTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private static readonly byte[] LogoBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly GenerationJobsNoWorkerFactory factory;

    public ProductAdLogoPreviewAccountSwitchIsolationTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task ProductAdLogoInlinePreviewIsNotExposedAcrossLogoutAndAccountSwitch()
    {
        // One HttpClient for the whole journey, so the cookie jar carries the real
        // A -> signed-out -> B account switch instead of two isolated clients.
        using var browser = factory.CreateClient();

        var accountA = await Register(browser, "Product Ad Owner A");
        var workspaceA = accountA.PersonalWorkspace.Id;
        var campaignProject = await CreateProject(browser, workspaceA, "Product Ad Campaign A");

        var storedFileId = Guid.NewGuid();
        var logoAssetId = Guid.NewGuid();
        var storageKey = $"product-ad-logo/{Guid.NewGuid():N}.png";
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
            await using var logoStream = new MemoryStream(LogoBytes, writable: false);
            await storage.StoreAsync(storageKey, logoStream);

            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = workspaceA,
                UserId = accountA.User.Id,
                ProjectId = campaignProject.Id,
                OriginalFileName = "brand-logo.png",
                StoredFileName = "brand-logo.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = LogoBytes.Length,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = storageKey,
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
            });
            db.Assets.Add(new Asset
            {
                Id = logoAssetId,
                WorkspaceId = workspaceA,
                ProjectId = campaignProject.Id,
                CreatedByUserId = accountA.User.Id,
                StoredFileId = storedFileId,
                Name = "Campaign A brand logo",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var previewUrl = $"/api/assets/{logoAssetId}/download?inline=true";

        try
        {
            // Leg 1 - the owning account previews its own attached logo: bytes, not a denial.
            using (var ownedPreview = await browser.GetAsync(previewUrl))
            {
                Assert.Equal(HttpStatusCode.OK, ownedPreview.StatusCode);
                Assert.Equal("image/png", ownedPreview.Content.Headers.ContentType?.MediaType);
                Assert.Equal(LogoBytes, await ownedPreview.Content.ReadAsByteArrayAsync());
            }

            // Leg 2 - same cookie jar, now signed out. A stale preview URL must not keep
            // serving the logo to an anonymous session, and the denial must not leak the
            // image payload or its content type.
            await Logout(browser);
            using (var anonymousPreview = await browser.GetAsync(previewUrl))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousPreview.StatusCode);
                Assert.NotEqual("image/png", anonymousPreview.Content.Headers.ContentType?.MediaType);
                Assert.False(ArrayEqual(await anonymousPreview.Content.ReadAsByteArrayAsync(), LogoBytes));
            }

            // Leg 3 - account B signs in through that same browser session. B is not a
            // member of campaign A's workspace, so the logo stays unresolvable: no bytes,
            // no image media type, no asset metadata.
            var accountB = await Register(browser, "Product Ad Visitor B");
            Assert.NotEqual(accountB.PersonalWorkspace.Id, workspaceA);
            using (var foreignPreview = await browser.GetAsync(previewUrl))
            {
                Assert.Equal(HttpStatusCode.NotFound, foreignPreview.StatusCode);
                Assert.NotEqual("image/png", foreignPreview.Content.Headers.ContentType?.MediaType);
                Assert.False(ArrayEqual(await foreignPreview.Content.ReadAsByteArrayAsync(), LogoBytes));
                var error = await foreignPreview.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("ASSET_NOT_FOUND", error.GetProperty("error").GetProperty("code").GetString());
            }

            using (var foreignMetadata = await browser.GetAsync($"/api/assets/{logoAssetId}"))
                Assert.Equal(HttpStatusCode.NotFound, foreignMetadata.StatusCode);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets.SingleOrDefaultAsync(item => item.Id == logoAssetId);
            if (asset is not null) db.Assets.Remove(asset);
            var storedFile = await db.StoredFiles.SingleOrDefaultAsync(item => item.Id == storedFileId);
            if (storedFile is not null) db.StoredFiles.Remove(storedFile);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IFileStorageService>().DeleteAsync(storageKey);
        }
    }

    private static bool ArrayEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index += 1)
            if (left[index] != right[index]) return false;
        return true;
    }

    private static async Task Logout(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/logout", payload: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"product-ad-logo-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"unexpected {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!;
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
