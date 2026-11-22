using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free denial coverage for the document export read boundary. AssetsController serves
/// every document export read path: asset metadata, the primary file, and each rendered DOCX/PDF
/// representation. An authenticated caller whose security principal carries no usable identifier
/// must be denied before any asset or storage lookup happens, and an export that belongs to a
/// different workspace must never be reachable through an identifier pair.
/// </summary>
public sealed class DocumentExportAccessDenialTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public DocumentExportAccessDenialTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Every_document_export_read_action_fails_closed_on_an_unusable_principal(string? identifier)
    {
        IEnumerable<Claim> claims = identifier is null
            ? Array.Empty<Claim>()
            : [new Claim(ClaimTypes.NameIdentifier, identifier)];
        var controller = new AssetsController(null!, null!, NullLogger<AssetsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")),
                },
            },
        };

        var assetId = Guid.NewGuid();
        var representationId = Guid.NewGuid();
        var actions = new Func<Task<IActionResult>>[]
        {
            () => controller.List(Guid.NewGuid(), null, AssetTypes.Document, AssetStatus.Active, null, null, 1, 24, default),
            () => controller.Get(assetId, default),
            () => controller.Update(assetId, new UpdateAssetRequest { Name = "Renamed export" }, default),
            () => controller.Archive(assetId, default),
            () => controller.Restore(assetId, default),
            () => controller.Download(assetId, false, default),
            () => controller.DownloadRepresentation(assetId, representationId, default),
        };

        foreach (var action in actions)
            Assert.IsType<UnauthorizedResult>(await action());
    }

    [Fact]
    public async Task Document_export_representation_metadata_and_download_are_denied_to_another_workspace()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Export Owner");

        var assetId = Guid.NewGuid();
        var primaryFileId = Guid.NewGuid();
        var exportFileId = Guid.NewGuid();
        var representationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.AddRange(
                new StoredFile
                {
                    Id = primaryFileId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    UserId = ownerAuth.User.Id,
                    OriginalFileName = "private-export-source.txt",
                    StoredFileName = "private-export-source.txt",
                    ContentType = "text/plain",
                    Extension = ".txt",
                    SizeBytes = 7,
                    StorageProvider = FileStorageProviders.Local,
                    StorageKey = $"document-export-denial/{primaryFileId:N}.txt",
                    Status = StoredFileStatus.Ready,
                    CreatedAt = now,
                    ProcessedAt = now,
                },
                new StoredFile
                {
                    Id = exportFileId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    UserId = ownerAuth.User.Id,
                    OriginalFileName = "private-rendered-export.pdf",
                    StoredFileName = "private-rendered-export.pdf",
                    ContentType = "application/pdf",
                    Extension = ".pdf",
                    SizeBytes = 7,
                    StorageProvider = FileStorageProviders.Local,
                    StorageKey = $"document-export-denial/{exportFileId:N}.pdf",
                    Status = StoredFileStatus.Ready,
                    CreatedAt = now,
                    ProcessedAt = now,
                });
            var asset = new Asset
            {
                Id = assetId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = ownerAuth.User.Id,
                StoredFileId = primaryFileId,
                Name = "Private rendered export",
                AssetType = AssetTypes.Document,
                MimeType = "text/plain",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            asset.Representations.Add(new AssetRepresentation
            {
                Id = representationId,
                AssetId = assetId,
                StoredFileId = exportFileId,
                RepresentationType = AssetRepresentationTypes.Pdf,
                FileName = "private-rendered-export.pdf",
                ContentType = "application/pdf",
                SizeBytes = 7,
                CreatedAt = now,
            });
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
        }

        // Positive control: the owning workspace member sees the rendered export representation.
        var ownerRead = await owner.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        var ownerDto = await ownerRead.Content.ReadFromJsonAsync<AssetDto>();
        Assert.NotNull(ownerDto);
        Assert.Contains(representationId, ownerDto!.Representations.Select(item => item.Id));

        using var outsider = factory.CreateClient();
        await Register(outsider, "Export Outsider");

        // A user from another workspace cannot enumerate the export through asset metadata.
        var foreignRead = await outsider.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal("ASSET_NOT_FOUND", await ErrorCode(foreignRead));

        // The rendered document export itself stays unreadable outside the owning workspace.
        var foreignExport = await outsider.GetAsync($"/api/assets/{assetId}/representations/{representationId}/download");
        Assert.Equal(HttpStatusCode.NotFound, foreignExport.StatusCode);
        Assert.Equal("ASSET_NOT_FOUND", await ErrorCode(foreignExport));
        Assert.DoesNotContain("document-export-denial", await foreignExport.Content.ReadAsStringAsync());

        // An outsider cannot pair a nonexistent asset identifier with a foreign export identifier.
        var mismatchedExport = await outsider.GetAsync($"/api/assets/{Guid.NewGuid()}/representations/{representationId}/download");
        Assert.Equal(HttpStatusCode.NotFound, mismatchedExport.StatusCode);
        Assert.Equal("ASSET_NOT_FOUND", await ErrorCode(mismatchedExport));

        // Unauthenticated callers are denied before any storage lookup.
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/assets/{assetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/assets/{assetId}/representations/{representationId}/download")).StatusCode);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString();

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                email = $"document-export-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
