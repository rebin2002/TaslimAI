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
/// PR #1004 pinned the asset-level rendition fallback: `hasFile`/`canPreview` now follow the
/// asset's own StoredFile status. The sibling gap it left open is per-representation availability:
/// a DOCX/PDF/PPTX row can keep a live download link after *its* stored file has been pruned, is
/// still processing, or has no file row at all, while the representation download route answers
/// those same rows with `409 ASSET_FILE_UNAVAILABLE` (AssetService.GetDownloadAsync). The read
/// model has to advertise a representation download from the same fact the route enforces.
/// Representations keep their identity — id, type, file name, content type, size — for consumers
/// that render the list for information; only the advertised download flips off.
///
/// Synthetic, provider-free fixtures: stored files are in-memory rows with local provider markers
/// and no bytes are ever requested, so no storage provider or network is touched.
/// </summary>
public sealed class AssetRepresentationDownloadAvailabilityTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public AssetRepresentationDownloadAvailabilityTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // ----- Synthetic in-memory stored-file fixtures through the mapper -----

    [Fact]
    public void Mapper_advertises_pdf_representation_while_its_stored_file_is_pruned()
    {
        // Scenario: original Ready + PDF representation pruned.
        var fixture = BuildFixture(original: StoredFileStatus.Ready, representation: StoredFileStatus.Deleted);
        var representation = MapRepresentation(fixture);

        Assert.False(representation.GetProperty("canDownload").GetBoolean());
        // Identity and metadata are retained: consumers keep showing the format row.
        Assert.Equal(fixture.RepresentationId, representation.GetProperty("id").GetGuid());
        Assert.Equal("pdf", representation.GetProperty("representationType").GetString());
        Assert.Equal("report.pdf", representation.GetProperty("fileName").GetString());
    }

    [Fact]
    public void Mapper_withholds_representation_while_its_stored_file_is_processing()
    {
        // Scenario: representation Processing.
        var fixture = BuildFixture(original: StoredFileStatus.Ready, representation: StoredFileStatus.Processing);
        var representation = MapRepresentation(fixture);

        Assert.False(MapRepresentation(fixture).GetProperty("canDownload").GetBoolean());
        Assert.Equal(fixture.RepresentationId, representation.GetProperty("id").GetGuid());
    }

    [Fact]
    public void Mapper_withholds_representation_whose_stored_file_row_is_missing()
    {
        // Scenario: missing representation file. The relation is DeleteBehavior.Restrict, so a
        // missing row surfaces as an unloaded navigation, never as a phantom Ready status.
        var fixture = BuildFixture(original: StoredFileStatus.Ready, representation: StoredFileStatus.Ready);
        fixture.Asset.Representations.Single().StoredFile = null!;

        var representation = MapRepresentation(fixture);

        Assert.False(representation.GetProperty("canDownload").GetBoolean());
        Assert.Equal("report.pdf", representation.GetProperty("fileName").GetString());
    }

    [Fact]
    public void Mapper_advertises_representation_when_original_and_representation_are_ready()
    {
        // Scenario: both Ready — the healthy default must stay advertised.
        var fixture = BuildFixture(original: StoredFileStatus.Ready, representation: StoredFileStatus.Ready);

        Assert.True(MapRepresentation(fixture).GetProperty("canDownload").GetBoolean());
    }

    [Fact]
    public void Mapper_advertises_ready_representation_even_when_original_is_pruned()
    {
        // Scenario: original pruned + representation Ready. The representation has its own stored
        // file and its own availability fact; the asset-level fallback must not bleed into it.
        var fixture = BuildFixture(original: StoredFileStatus.Deleted, representation: StoredFileStatus.Ready);
        var dto = Serialize(AssetContractMapper.ToDto(fixture.Asset, fixture.Asset.CreatedByUserId));

        Assert.False(dto.GetProperty("hasFile").GetBoolean());
        Assert.True(dto.GetProperty("representations")[0].GetProperty("canDownload").GetBoolean());
    }

    // ----- HTTP read model: list and detail agree with the route's 409 -----

    [Fact]
    public async Task Pruned_representation_stays_in_the_read_model_but_not_as_a_download_on_list_or_detail()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pruned Representation Owner");
        var fixture = await CreateDocumentAsset(client, auth, "Pruned pdf export",
            original: StoredFileStatus.Ready, representation: StoredFileStatus.Deleted);

        // Baseline: with the representation file Ready the download is advertised. (Created pruned
        // above; flip it Ready first, then prune, mirroring a retention pass.)
        await SetStoredFileStatus(fixture.RepresentationStoredFileId, StoredFileStatus.Ready);
        Assert.True(await ReadRepresentationCanDownload(client, fixture.AssetId));
        Assert.True(await ReadListRepresentationCanDownload(client, auth, "Pruned pdf export"));

        await SetStoredFileStatus(fixture.RepresentationStoredFileId, StoredFileStatus.Deleted);

        // The very request that dead-ends: still 409 by design, which is exactly why the read
        // model must stop advertising it.
        var download = await client.GetAsync($"/api/assets/{fixture.AssetId}/representations/{fixture.RepresentationId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());

        // Detail and list agree, and the unavailable format keeps its identity.
        Assert.False(await ReadRepresentationCanDownload(client, fixture.AssetId));
        Assert.False(await ReadListRepresentationCanDownload(client, auth, "Pruned pdf export"));
        var detail = await ReadDetail(client, fixture.AssetId);
        var representation = Assert.Single(detail.GetProperty("representations").EnumerateArray());
        Assert.Equal(fixture.RepresentationId, representation.GetProperty("id").GetGuid());
        Assert.Equal("report.pdf", representation.GetProperty("fileName").GetString());
        // The asset's own rendition is Ready, so the asset-level affordances are untouched.
        Assert.True(detail.GetProperty("hasFile").GetBoolean());
    }

    [Fact]
    public async Task Processing_representation_is_not_advertised_but_pruned_original_does_not_hide_ready_representation()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Mixed Representation Owner");

        var processing = await CreateDocumentAsset(client, auth, "Processing export",
            original: StoredFileStatus.Ready, representation: StoredFileStatus.Processing);
        Assert.False(await ReadRepresentationCanDownload(client, processing.AssetId));
        Assert.False(await ReadListRepresentationCanDownload(client, auth, "Processing export"));

        var originalPruned = await CreateDocumentAsset(client, auth, "Pruned original export",
            original: StoredFileStatus.Deleted, representation: StoredFileStatus.Ready);
        var detail = await ReadDetail(client, originalPruned.AssetId);
        // Asset-level fallback (PR #1004 lane) stays intact...
        Assert.False(detail.GetProperty("hasFile").GetBoolean());
        // ...while the independent, Ready representation remains honestly advertised.
        Assert.True(await ReadRepresentationCanDownload(client, originalPruned.AssetId));
        Assert.True(await ReadListRepresentationCanDownload(client, auth, "Pruned original export"));
    }

    [Fact]
    public async Task Both_ready_asset_advertises_the_representation_identically_on_list_and_detail()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Live Representation Owner");
        var fixture = await CreateDocumentAsset(client, auth, "Live export",
            original: StoredFileStatus.Ready, representation: StoredFileStatus.Ready);

        Assert.True(await ReadRepresentationCanDownload(client, fixture.AssetId));
        Assert.True(await ReadListRepresentationCanDownload(client, auth, "Live export"));
    }

    // ----- Helpers -----

    private sealed record Fixture(Guid AssetId, Guid RepresentationId, Guid RepresentationStoredFileId, Asset Asset);

    private static Fixture BuildFixture(StoredFileStatus original, StoredFileStatus representation)
    {
        var now = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        var assetId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var originalFileId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var representationFileId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var representationId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var workspaceId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var userId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        StoredFile File(Guid id, StoredFileStatus status, string name) => new()
        {
            Id = id,
            WorkspaceId = workspaceId,
            UserId = userId,
            OriginalFileName = name,
            StoredFileName = $"{name}.bin",
            ContentType = name.EndsWith(".pdf") ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Extension = Path.GetExtension(name),
            SizeBytes = 10,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"representation-availability/{id:N}",
            Status = status,
            CreatedAt = now,
            ProcessedAt = status == StoredFileStatus.Ready ? now : null,
        };
        var asset = new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            StoredFileId = originalFileId,
            StoredFile = File(originalFileId, original, "report.docx"),
            Name = "Synthetic export",
            AssetType = AssetTypes.Document,
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
                    StoredFile = File(representationFileId, representation, "report.pdf"),
                    RepresentationType = AssetRepresentationTypes.Pdf,
                    FileName = "report.pdf",
                    ContentType = "application/pdf",
                    // SizeBytes mirrors the representation file below, so this row is genuinely
                    // downloadable: #1041's follow-up parity gate (AssetService.CanDownloadRepresentation)
                    // also refuses a document row whose representation/file metadata disagrees.
                    SizeBytes = 10,
                    CreatedAt = now,
                },
            },
        };
        return new Fixture(assetId, representationId, representationFileId, asset);
    }

    private static JsonElement MapRepresentation(Fixture fixture) =>
        Serialize(AssetContractMapper.ToDto(fixture.Asset, fixture.Asset.CreatedByUserId)).GetProperty("representations")[0];

    private static JsonElement Serialize(AssetDto dto) =>
        JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<JsonElement> ReadDetail(HttpClient client, Guid assetId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/assets/{assetId}"));

    private static async Task<bool> ReadRepresentationCanDownload(HttpClient client, Guid assetId)
    {
        var detail = await ReadDetail(client, assetId);
        return detail.GetProperty("representations")[0].GetProperty("canDownload").GetBoolean();
    }

    private static async Task<bool> ReadListRepresentationCanDownload(HttpClient client, AuthResponse auth, string name)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&search={Uri.EscapeDataString(name)}");
        var item = list.GetProperty("items")[0];
        return item.GetProperty("representations")[0].GetProperty("canDownload").GetBoolean();
    }

    private async Task<(Guid AssetId, Guid RepresentationId, Guid RepresentationStoredFileId)> CreateDocumentAsset(
        HttpClient client, AuthResponse auth, string name, StoredFileStatus original, StoredFileStatus representation)
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
            StorageKey = $"representation-availability/{originalFileId:N}.docx",
            Status = original,
            CreatedAt = now,
            ProcessedAt = original == StoredFileStatus.Ready ? now : null,
        });
        db.StoredFiles.Add(new StoredFile
        {
            Id = representationFileId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = auth.User.Id,
            OriginalFileName = $"{name}.pdf",
            StoredFileName = $"{representationFileId:N}.pdf",
            ContentType = "application/pdf",
            Extension = ".pdf",
            SizeBytes = 6,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"representation-availability/{representationFileId:N}.pdf",
            Status = representation,
            CreatedAt = now,
            ProcessedAt = representation == StoredFileStatus.Ready ? now : null,
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
            SizeBytes = 6,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return (assetId, representationId, representationFileId);
    }

    private async Task SetStoredFileStatus(Guid storedFileId, StoredFileStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var storedFile = await db.StoredFiles.SingleAsync(item => item.Id == storedFileId);
        storedFile.Status = status;
        if (status == StoredFileStatus.Deleted)
        {
            storedFile.DeletedAt = DateTime.UtcNow;
            storedFile.ContentHashSha256 = null;
            storedFile.RetainUntil = null;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"asset-representation-availability-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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
