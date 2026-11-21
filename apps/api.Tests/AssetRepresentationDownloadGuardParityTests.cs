using System.Net;
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
/// PR #1041 made the advertised <c>canDownload</c> flag follow the representation's own stored-file
/// status, but the download route refuses a row on two further branches: the file must be readable
/// by the viewer (<c>AssetService.CanReadStoredFile</c>) and, for document assets, the representation
/// metadata must agree with the file (<c>AssetService.IsDocumentRepresentationConsistent</c>). A
/// <c>Ready</c> row failing either branch was advertised with a live link that dead-ended in
/// <c>409 ASSET_FILE_UNAVAILABLE</c> — the same #1030 invariant reached through an unmirrored branch
/// (QA observation A on #1041). These facts pin both drift cases on the read model *and* the route,
/// plus the controls that prove the flag was not over-tightened.
///
/// Synthetic and provider-free: users are registered fixtures, stored files are in-memory rows with
/// local provider markers, and no bytes are ever requested, so no storage provider, paid provider,
/// or real asset is touched.
/// </summary>
public sealed class AssetRepresentationDownloadGuardParityTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public AssetRepresentationDownloadGuardParityTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // ----- Mapper: each guard branch the route enforces must also clear the flag -----

    [Fact]
    public void Mapper_withholds_ready_representation_the_viewer_cannot_read()
    {
        // Drift case 1 (CanReadStoredFile): the file is Ready and in the asset's workspace, but it
        // belongs to another member with no project to share it through. The route 409s, so the
        // read model may not advertise it.
        var asset = BuildAsset(readableByViewer: false);
        var representation = MapRepresentation(asset);
        Assert.False(representation.GetProperty("canDownload").GetBoolean());
        // Identity and metadata are retained, so the format row is still visible.
        Assert.Equal("report.pdf", representation.GetProperty("fileName").GetString());
        Assert.Equal(AssetRepresentationTypes.Pdf, representation.GetProperty("representationType").GetString());
    }

    [Fact]
    public void Mapper_withholds_ready_document_representation_whose_metadata_disagrees_with_its_file()
    {
        // Drift case 2 (IsDocumentRepresentationConsistent): Ready and readable, but the
        // representation row's size disagrees with the file the route would serve.
        var asset = BuildAsset(representationSizeBytes: 99);
        Assert.False(MapRepresentation(asset).GetProperty("canDownload").GetBoolean());
    }

    [Fact]
    public void Mapper_withholds_ready_document_representation_whose_content_type_disagrees_with_its_file()
    {
        // The same branch through the other half of the consistency contract: the file carries a
        // content type that is not the representation type's canonical one.
        var asset = BuildAsset(representationContentType: "application/octet-stream");
        Assert.False(MapRepresentation(asset).GetProperty("canDownload").GetBoolean());
    }

    [Fact]
    public void Mapper_advertises_ready_readable_consistent_document_representation()
    {
        // Control: all three branches hold, so the healthy default must stay advertised.
        Assert.True(MapRepresentation(BuildAsset()).GetProperty("canDownload").GetBoolean());
    }

    [Fact]
    public void Mapper_does_not_apply_document_metadata_strictness_to_non_document_assets()
    {
        // Control on the other axis: the route only checks consistency for document assets, so the
        // flag must not refuse a mismatched image rendition the route would happily serve.
        var asset = BuildAsset(representationSizeBytes: 99, assetType: AssetTypes.Image);
        Assert.True(MapRepresentation(asset).GetProperty("canDownload").GetBoolean());
    }

    // ----- HTTP: the route and both read-model surfaces agree on the same two drift cases -----

    [Fact]
    public async Task Unreadable_ready_representation_is_refused_by_the_route_and_not_advertised()
    {
        using var client = factory.CreateClient();
        var owner = await Register(client, "Guard Parity Owner A");
        Guid foreignUserId;
        using (var otherClient = factory.CreateClient())
        {
            foreignUserId = (await Register(otherClient, "Guard Parity Owner B")).User.Id;
        }

        var fixture = await CreateDocumentAsset(client, owner, "Unreadable export", fileUserId: foreignUserId);

        var download = await client.GetAsync($"/api/assets/{fixture.AssetId}/representations/{fixture.RepresentationId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());

        // Before this lane the flag was true here: status alone decided it.
        Assert.False(await ReadRepresentationCanDownload(client, fixture.AssetId));
        Assert.False(await ReadListRepresentationCanDownload(client, owner, "Unreadable export"));
    }

    [Fact]
    public async Task Inconsistent_ready_document_representation_is_refused_by_the_route_and_not_advertised()
    {
        using var client = factory.CreateClient();
        var owner = await Register(client, "Guard Parity Owner C");
        var fixture = await CreateDocumentAsset(client, owner, "Drifted export", representationSizeBytes: 99);

        var download = await client.GetAsync($"/api/assets/{fixture.AssetId}/representations/{fixture.RepresentationId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());

        Assert.False(await ReadRepresentationCanDownload(client, fixture.AssetId));
        Assert.False(await ReadListRepresentationCanDownload(client, owner, "Drifted export"));
    }

    [Fact]
    public async Task Readable_consistent_document_representation_is_advertised_and_not_refused()
    {
        using var client = factory.CreateClient();
        var owner = await Register(client, "Guard Parity Owner D");
        var fixture = await CreateDocumentAsset(client, owner, "Live export");

        Assert.True(await ReadRepresentationCanDownload(client, fixture.AssetId));
        Assert.True(await ReadListRepresentationCanDownload(client, owner, "Live export"));

        // The fixture writes no bytes, so a served download ends in the storage-not-found branch;
        // the point is that it is not the availability refusal the flag promised to avoid.
        var download = await client.GetAsync($"/api/assets/{fixture.AssetId}/representations/{fixture.RepresentationId}/download");
        Assert.NotEqual(HttpStatusCode.Conflict, download.StatusCode);
        Assert.DoesNotContain("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());
    }

    // ----- Helpers -----

    private sealed record Fixture(Guid AssetId, Guid RepresentationId);

    private static Asset BuildAsset(
        bool readableByViewer = true,
        long? representationSizeBytes = null,
        string? representationContentType = null,
        string assetType = AssetTypes.Document)
    {
        var now = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        var assetId = Guid.Parse("71717171-1111-4111-8111-111111111111");
        var originalFileId = Guid.Parse("72727272-2222-4222-8222-222222222222");
        var representationFileId = Guid.Parse("73737373-3333-4333-8333-333333333333");
        var representationId = Guid.Parse("74747474-4444-4444-8444-444444444444");
        var workspaceId = Guid.Parse("75757575-5555-4555-8555-555555555555");
        var viewerId = Guid.Parse("76767676-6666-4666-8666-666666666666");
        StoredFile File(Guid id, string name, Guid ownerId) => new()
        {
            Id = id,
            WorkspaceId = workspaceId,
            UserId = ownerId,
            OriginalFileName = name,
            StoredFileName = $"{name}.bin",
            ContentType = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "application/pdf"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Extension = Path.GetExtension(name),
            SizeBytes = 10,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"guard-parity/{id:N}",
            Status = StoredFileStatus.Ready,
            CreatedAt = now,
            ProcessedAt = now,
        };
        var asset = new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = viewerId,
            StoredFileId = originalFileId,
            StoredFile = File(originalFileId, "report.docx", viewerId),
            Name = "Synthetic export",
            AssetType = assetType,
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Representations =
            {
                new AssetRepresentation
                {
                    Id = representationId,
                    AssetId = assetId,
                    StoredFileId = representationFileId,
                    StoredFile = File(representationFileId, "report.pdf", readableByViewer ? viewerId : Guid.Parse("77777777-7777-4777-8777-777777777777")),
                    RepresentationType = AssetRepresentationTypes.Pdf,
                    FileName = "report.pdf",
                    ContentType = representationContentType ?? "application/pdf",
                    SizeBytes = representationSizeBytes ?? 10,
                    CreatedAt = now,
                },
            },
        };
        return asset;
    }

    private static JsonElement MapRepresentation(Asset asset) =>
        JsonSerializer.SerializeToElement(
            AssetContractMapper.ToDto(asset, asset.CreatedByUserId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("representations")[0];

    private static async Task<JsonElement> ReadDetail(HttpClient client, Guid assetId) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/assets/{assetId}");

    private static async Task<bool> ReadRepresentationCanDownload(HttpClient client, Guid assetId) =>
        (await ReadDetail(client, assetId)).GetProperty("representations")[0].GetProperty("canDownload").GetBoolean();

    private static async Task<bool> ReadListRepresentationCanDownload(HttpClient client, AuthResponse auth, string name)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&search={Uri.EscapeDataString(name)}");
        return list.GetProperty("items")[0].GetProperty("representations")[0].GetProperty("canDownload").GetBoolean();
    }

    private async Task<Fixture> CreateDocumentAsset(
        HttpClient client,
        AuthResponse auth,
        string name,
        Guid? fileUserId = null,
        long representationSizeBytes = 6)
    {
        var assetId = Guid.NewGuid();
        var originalFileId = Guid.NewGuid();
        var representationId = Guid.NewGuid();
        var representationFileId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.StoredFiles.Add(new StoredFile
        {
            Id = originalFileId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = auth.User.Id,
            OriginalFileName = $"{name}.docx",
            StoredFileName = $"{originalFileId:N}.docx",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Extension = ".docx",
            SizeBytes = 4,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"guard-parity/{originalFileId:N}.docx",
            Status = StoredFileStatus.Ready,
            CreatedAt = now,
            ProcessedAt = now,
        });
        db.StoredFiles.Add(new StoredFile
        {
            Id = representationFileId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            // Owned by somebody else with no project to share it through: Ready, but the route's
            // readability guard refuses it.
            UserId = fileUserId ?? auth.User.Id,
            OriginalFileName = $"{name}.pdf",
            StoredFileName = $"{representationFileId:N}.pdf",
            ContentType = "application/pdf",
            Extension = ".pdf",
            SizeBytes = 6,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"guard-parity/{representationFileId:N}.pdf",
            Status = StoredFileStatus.Ready,
            CreatedAt = now,
            ProcessedAt = now,
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            StoredFileId = originalFileId,
            Name = name,
            AssetType = AssetTypes.Document,
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.AssetRepresentations.Add(new AssetRepresentation
        {
            Id = representationId,
            AssetId = assetId,
            StoredFileId = representationFileId,
            RepresentationType = AssetRepresentationTypes.Pdf,
            FileName = "report.pdf",
            ContentType = "application/pdf",
            // Deliberate disagreement with the 6-byte file proves the metadata branch too.
            SizeBytes = representationSizeBytes,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Fixture(assetId, representationId);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                email = $"asset-guard-parity-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
