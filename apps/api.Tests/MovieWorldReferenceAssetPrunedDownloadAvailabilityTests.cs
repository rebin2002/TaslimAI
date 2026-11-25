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
/// A read-model / actual-HTTP-availability consistency boundary that PR #1041 (issue #1030) does not
/// cover and that no open lane claims: the Movie Studio **world workspace** read model
/// <c>GET /api/movie-studio/projects/{id}/world</c>.
///
/// #1041 stops advertising 409-bound representation downloads inside the *Assets library* read model
/// (<c>AssetContractMapper.ToDto</c>), and #1004 did the same for the asset-level
/// <c>hasFile</c>/<c>canPreview</c> pair there. The world read model is a separate projection that
/// still derives both flags from the asset's identity alone - <c>item.StoredFileId.HasValue</c> in
/// <c>MovieStudioService.GetWorldAsync</c> - so it keeps promising a reference image after a
/// retention prune, while <c>GET /api/assets/{id}/download</c> answers that same row with
/// <c>409 ASSET_FILE_UNAVAILABLE</c>. The consumer that renders those flags
/// (<c>ProductionKitWorkspace.tsx</c>, gated on <c>asset.canPreview</c>) therefore shows a private
/// preview that can never load.
///
/// The asymmetry is provable from the server's own rules: <c>EnsureMovieAssetAsync</c> - the WRITE
/// side of the same relation - refuses to attach an asset whose stored file is not <c>Ready</c>. So
/// availability is required to enter the relation and ignored to report it.
///
/// Synthetic rows only: SQLite in-memory, no storage provider, no network, no real user data, no
/// paid calls. The download access policy itself is deliberately unchanged and only observed.
/// </summary>
public sealed class MovieWorldReferenceAssetPrunedDownloadAvailabilityTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieWorldReferenceAssetPrunedDownloadAvailabilityTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// Green baseline pin. Before any prune, the world read model advertises the reference image and
    /// the two availability facts agree with the route's own readiness rule. This is what makes the
    /// third fact below a divergence rather than a fixture that simply never resolves an asset.
    /// </summary>
    [Fact]
    public async Task Ready_reference_asset_is_advertised_by_the_world_read_model()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Ready Reference Owner");
        var movieId = await CreateMovie(client, auth.PersonalWorkspace.Id, "Ready Reference Movie");
        var asset = await CreateImageAsset(client, auth, "Ready harbor plate", StoredFileStatus.Ready);

        await AttachLocation(client, movieId, "Old Harbor", asset.AssetId);

        var worldAsset = await ReadWorldAsset(client, movieId, asset.AssetId);
        Assert.True(worldAsset.HasFile, "A Ready reference asset must be advertised as present by the world read model.");
        Assert.True(worldAsset.CanPreview, "A Ready image reference asset must be advertised as previewable.");
    }

    /// <summary>
    /// Green mechanism pin. The write side of the very same relation already refuses an asset whose
    /// stored file is not Ready, so "the file is Ready" is the availability fact this association is
    /// defined by - not "the asset row points at a file". This is the premise the read model drops.
    /// </summary>
    [Fact]
    public async Task Attach_side_refuses_a_reference_asset_whose_file_is_not_ready()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Attach Guard Owner");
        var movieId = await CreateMovie(client, auth.PersonalWorkspace.Id, "Attach Guard Movie");
        var processing = await CreateImageAsset(client, auth, "Still processing plate", StoredFileStatus.Processing);

        var response = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/projects/{movieId}/locations",
            new { name = "Guarded Pier", description = "A location whose reference is not ready.", visualContinuityNotes = (string?)null, referenceAssetId = processing.AssetId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_LOCATION_INVALID", error.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// Desired contract - currently RED, and the point of this handoff.
    ///
    /// A retention prune marks the backing <see cref="StoredFile"/> Deleted, clears its hashes and
    /// leaves <c>Asset.StoredFileId</c> in place (exactly <c>FileProcessingService.DeleteAsync</c>).
    /// The download route therefore refuses the row with <c>409 ASSET_FILE_UNAVAILABLE</c>, and the
    /// Assets library read model already reports <c>hasFile: false</c> for it (#1004, and #1041's
    /// sibling flag for representations). The world read model must agree: it may not advertise a
    /// preview or a download for a reference asset the route will not serve.
    /// </summary>
    [Fact]
    public async Task World_read_model_withholds_a_pruned_reference_asset_the_download_route_refuses()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Pruned Reference Owner");
        var movieId = await CreateMovie(client, auth.PersonalWorkspace.Id, "Pruned Reference Movie");
        var asset = await CreateImageAsset(client, auth, "Pruned harbor plate", StoredFileStatus.Ready);
        await AttachLocation(client, movieId, "Pruned Pier", asset.AssetId);

        // Baseline holds, so the assertion below cannot pass by never finding the asset.
        var before = await ReadWorldAsset(client, movieId, asset.AssetId);
        Assert.True(before.HasFile);
        Assert.True(before.CanPreview);

        await PruneStoredFile(asset.StoredFileId);

        // Source of truth, unchanged by this handoff: the route never serves the pruned row.
        var download = await client.GetAsync($"/api/assets/{asset.AssetId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());

        // The same row, through the Assets library read model, already reports unavailability.
        var library = await client.GetFromJsonAsync<AssetDto>($"/api/assets/{asset.AssetId}");
        Assert.NotNull(library);
        Assert.False(library!.HasFile);
        Assert.False(library!.CanPreview);

        // The world read model has to reach the same verdict for the same bytes.
        var after = await ReadWorldAsset(client, movieId, asset.AssetId);
        Assert.False(after.HasFile,
            $"#1030 invariant, world projection: a pruned reference asset keeps its StoredFileId, so " +
            $"hasFile must be read from the stored file's status (the fact the download route enforces) " +
            $"and not from the identity alone. MovieStudioService.GetWorldAsync still projects " +
            $"item.StoredFileId.HasValue.");
        Assert.False(after.CanPreview,
            "A withheld preview is the consumer's signal (ProductionKitWorkspace gates the reference " +
            "chooser and its <img> on canPreview); reporting true promises bytes the route refuses with 409.");
    }

    private record WorldAssetFacts(bool HasFile, bool CanPreview);

    private record Fixture(Guid AssetId, Guid StoredFileId);

    private async Task<Fixture> CreateImageAsset(HttpClient client, AuthResponse auth, string name, StoredFileStatus status)
    {
        var assetId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = auth.User.Id,
            OriginalFileName = $"{name}.png",
            StoredFileName = $"{storedFileId:N}.png",
            ContentType = "image/png",
            Extension = ".png",
            SizeBytes = 4,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"movie-world-reference/{storedFileId:N}.png",
            Status = status,
            CreatedAt = now,
            ProcessedAt = status == StoredFileStatus.Ready ? now : null,
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            StoredFileId = storedFileId,
            Name = name,
            AssetType = AssetTypes.Image,
            MimeType = "image/png",
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Fixture(assetId, storedFileId);
    }

    /// <summary>Mirrors FileProcessingService.DeleteAsync: the object goes, the identity stays.</summary>
    private async Task PruneStoredFile(Guid storedFileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var storedFile = await db.StoredFiles.SingleAsync(item => item.Id == storedFileId);
        storedFile.Status = StoredFileStatus.Deleted;
        storedFile.DeletedAt = DateTime.UtcNow;
        storedFile.ProcessedAt = DateTime.UtcNow;
        storedFile.ContentHashSha256 = null;
        storedFile.ExtractedText = null;
        storedFile.ExtractedTextLength = null;
        storedFile.RetainUntil = null;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateMovie(HttpClient client, Guid workspaceId, string title)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects",
            new { workspaceId, mode = "Full", title, description = "A synthetic world-availability fixture movie.", durationSeconds = 30, aspectRatio = "16:9", style = "cinematic", language = "en" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project").GetProperty("id").GetGuid();
    }

    private async Task AttachLocation(HttpClient client, Guid movieId, string name, Guid referenceAssetId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/movie-studio/projects/{movieId}/locations",
            new { name, description = "A repeatable waterfront location.", visualContinuityNotes = (string?)null, referenceAssetId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<WorldAssetFacts> ReadWorldAsset(HttpClient client, Guid movieId, Guid assetId)
    {
        var world = await client.GetFromJsonAsync<JsonElement>($"/api/movie-studio/projects/{movieId}/world");
        foreach (var item in world.GetProperty("assets").EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() != assetId) continue;
            return new WorldAssetFacts(item.GetProperty("hasFile").GetBoolean(), item.GetProperty("canPreview").GetBoolean());
        }
        throw new InvalidOperationException($"The world read model did not return reference asset {assetId}; the fixture never reached the projection under test.");
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register",
            new { displayName, email = $"movie-world-reference-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
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
