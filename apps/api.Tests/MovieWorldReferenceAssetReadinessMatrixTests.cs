using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Completes the readiness matrix that <c>MovieWorldReferenceAssetPrunedDownloadAvailabilityTests</c>
/// (#1176) opened for the Movie Studio world read model, and is the additional QA lane for the fix in
/// PR #1177. Nothing here repeats that file's three facts; it covers the rows and the boundary that
/// file never reaches.
///
/// #1177 replaces the identity-derived <c>item.StoredFileId.HasValue</c> in
/// <c>MovieStudioService.GetWorldAsync</c> with the positive predicate
/// <c>item.StoredFile != null &amp;&amp; item.StoredFile.Status == StoredFileStatus.Ready</c>. #1176 proves
/// that change against exactly one non-Ready status - <c>Deleted</c>, produced by a retention prune -
/// and against exactly one media family, <c>image/*</c>. Five rows are therefore still unpinned:
///
///  1. The <em>pending</em> statuses (<c>Uploading</c>, <c>Processing</c>) and <c>Failed</c> on the READ
///     side. #1176 touches <c>Processing</c> only on the WRITE side (its attach-side mechanism pin), so
///     nothing pins that the new gate is readiness-positive rather than merely "not pruned". A gate
///     written as <c>Status != Deleted</c> would satisfy every #1176 assertion and still advertise a
///     half-written or failed file. These rows are what distinguish those two predicates.
///  2. The <c>item.StoredFile != null</c> clause: an attached asset whose identity carries no stored
///     file at all. <c>EnsureMovieAssetAsync</c> permits that row (<c>if (asset.StoredFileId.HasValue
///     &amp;&amp; ...)</c>), so the null-navigation leg is reachable through the public API and is exercised
///     by no other world-read assertion.
///  3. <c>Ready</c> plus the media-specific half of <c>canPreview</c>. #1176 pins <c>image/*</c> true and
///     a non-media row never enters the world projection. <c>audio/*</c> and <c>video/*</c> are asserted
///     by the projection and by <c>ProductionKitWorkspace</c> but never tested there, and no test pins
///     that a <c>Ready</c> non-media reference keeps <c>hasFile: true</c> while losing
///     <c>canPreview</c> - the exact over-tightening risk the #1177 rewrite introduces, since one
///     predicate now feeds both flags.
///  4. Per-movie scoping of the availability verdict. #1176 gives each fact its own movie, so nothing
///     establishes that a withheld reference in one movie cannot bleed into a sibling movie that shares
///     the same workspace - the projection's own filter is workspace-wide
///     (<c>item.WorkspaceId == movie.WorkspaceId &amp;&amp; assetIds.Contains(item.Id)</c>), so the pairing of
///     "readiness gate" and "per-movie asset set" is unproven.
///  5. Cross-workspace isolation of that asset set: <c>MovieWorldTests</c> isolates world <em>records</em>
///     for another user's movie but never reads the <c>assets</c> array, and #1176 never has a second
///     workspace in the fixture.
///
/// Each expectation is checked against the authority #1030 defines it by - the real
/// <c>GET /api/assets/{id}/download</c> route - rather than against another flag: an unready row must
/// answer <c>409 ASSET_FILE_UNAVAILABLE</c> exactly when the world withholds it, and a <c>Ready</c> row
/// must serve bytes with the world's <c>canPreview</c> agreeing with whether that route can render the
/// asset inline (the private inline URL is the only thing
/// <c>ProductionKitWorkspace.tsx</c>'s <c>&lt;img src={assetFileUrl(id, true)}&gt;</c> can consume).
///
/// Synthetic rows in the shared in-memory SQLite fixture only. Stored bytes are written through the
/// app's own configured local storage service so the download route reaches its real serving path
/// without any external provider: no network, no credential, no S3, no real user data, no paid call,
/// no migration, and no production source file is touched by this PR.
/// </summary>
public sealed class MovieWorldReferenceAssetReadinessMatrixTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieWorldReferenceAssetReadinessMatrixTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// Row 1, the withhold side of the matrix. A reference that was attached while healthy and whose
    /// stored file then regressed to a pending or failed status - the lifecycle a re-transcode, a
    /// re-ingest or a failed virus scan produces - must disappear from the world's availability claim at
    /// the same moment the download route starts refusing it. <c>Deleted</c> is deliberately absent:
    /// #1176 already owns that row, and repeating it here would duplicate a fact instead of extending it.
    /// The per-movie leg below reuses these statuses, so the same transition is scored twice: once for
    /// the verdict, once for which movie the verdict belongs to.
    /// </summary>
    [Theory]
    [InlineData(StoredFileStatus.Uploading)]
    [InlineData(StoredFileStatus.Processing)]
    [InlineData(StoredFileStatus.Failed)]
    public async Task Unready_reference_asset_is_withheld_by_the_world_read_model_and_refused_by_the_download_route(StoredFileStatus status)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Unready Matrix Owner");
        var movieId = await CreateMovie(client, auth.PersonalWorkspace.Id, $"Unready {status} Movie");
        var asset = await CreateReferenceAsset(client, auth, $"Plate {status}", AssetTypes.Image, "image/png", "plate.png", StoredFileStatus.Ready);
        await AttachLocation(client, movieId, $"Pier {status}", asset.AssetId);

        // Attach requires Ready, so the row is advertised and served before it regresses. Without this
        // the assertions below could pass on a fixture that never reached the projection at all.
        var before = await ReadWorldAsset(client, movieId, asset.AssetId);
        Assert.True(before.HasFile, $"A Ready reference asset must start advertised (status {status}).");
        Assert.True(before.CanPreview, $"A Ready image reference asset must start previewable (status {status}).");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/assets/{asset.AssetId}/download")).StatusCode);

        await SetStoredFileStatus(asset.StoredFileId!.Value, status);

        var after = await ReadWorldAsset(client, movieId, asset.AssetId);
        Assert.False(after.HasFile,
            $"#1030 invariant, world projection: status {status} is not {StoredFileStatus.Ready}, so the " +
            $"world read model may not advertise the reference as present. Pinning the non-Deleted rows is " +
            $"what forces the gate to be readiness-positive rather than a mere 'not pruned' test.");
        Assert.False(after.CanPreview,
            $"Status {status} means no bytes to render, so canPreview must be false for a {asset.AssetType} reference.");

        // The authority for that verdict: the route the flag would have pointed the user at.
        var download = await client.GetAsync($"/api/assets/{asset.AssetId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());

        // And the already-settled library read model agrees row-for-row (#1004).
        var library = await client.GetFromJsonAsync<AssetDto>($"/api/assets/{asset.AssetId}");
        Assert.NotNull(library);
        Assert.False(library!.HasFile);
        Assert.False(library!.CanPreview);
    }

    /// <summary>
    /// Row 2. An asset with no stored-file identity is attachable - <c>EnsureMovieAssetAsync</c> only
    /// guards the association when <c>StoredFileId</c> is set - so the world read model can be handed a
    /// reference whose navigation is absent rather than merely unready. This is the only case that
    /// exercises the first clause of #1177's predicate, and it holds independently of that fix, which is
    /// why it belongs beside the status matrix instead of inside it.
    /// </summary>
    [Fact]
    public async Task Reference_asset_without_any_stored_file_row_is_withheld_and_refused()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Fileless Reference Owner");
        var movieId = await CreateMovie(client, auth.PersonalWorkspace.Id, "Fileless Reference Movie");
        var asset = await CreateReferenceAsset(client, auth, "Nameplate only", AssetTypes.Image, "image/png", "nameplate.png", status: null);
        await AttachLocation(client, movieId, "Nameplate Pier", asset.AssetId);

        Assert.Null(asset.StoredFileId);
        var world = await ReadWorldAsset(client, movieId, asset.AssetId);
        Assert.False(world.HasFile, "An asset whose identity carries no stored file cannot be advertised as having one.");
        Assert.False(world.CanPreview, "A fileless reference has nothing to preview.");

        var download = await client.GetAsync($"/api/assets/{asset.AssetId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Row 3, the Ready side of the boundary. Readiness opens the availability gate; the media
    /// family alone decides whether the preview flag follows, and the route's own inline-serving rule is
    /// the fact <c>canPreview</c> must track. Together these pin that #1177's single readiness predicate
    /// did not collapse <c>hasFile</c> into <c>canPreview</c>: a Ready spreadsheet reference is still
    /// downloadable, it simply cannot be previewed, and the route proves the difference by answering with
    /// an attachment rather than an inline body.
    /// </summary>
    [Theory]
    [InlineData(AssetTypes.Image, "image/png", "plate.png", true)]
    [InlineData(AssetTypes.Audio, "audio/mpeg", "room-tone.mp3", true)]
    [InlineData(AssetTypes.Video, "video/mp4", "establishing.mp4", true)]
    [InlineData(AssetTypes.Document, "application/pdf", "location-brief.pdf", false)]
    [InlineData(AssetTypes.File, "text/csv", "continuity-sheet.csv", false)]
    public async Task Ready_reference_asset_advertises_exactly_what_the_route_can_serve(string assetType, string contentType, string fileName, bool expectInlinePreview)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Media Eligibility Owner");
        var movieId = await CreateMovie(client, auth.PersonalWorkspace.Id, $"Media Eligibility {assetType} Movie");
        var asset = await CreateReferenceAsset(client, auth, $"Reference {assetType}", assetType, contentType, fileName, StoredFileStatus.Ready);
        await AttachLocation(client, movieId, $"{assetType} Pier", asset.AssetId);

        var world = await ReadWorldAsset(client, movieId, asset.AssetId);
        Assert.True(world.HasFile, $"A Ready {assetType} reference has bytes and must stay advertised even though it may not be previewable.");
        Assert.Equal(expectInlinePreview, world.CanPreview);

        // The readiness half is open in both directions: the route serves a Ready row, media or not.
        var download = await client.GetAsync($"/api/assets/{asset.AssetId}/download?inline=true");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(contentType, download.Content.Headers.ContentType?.MediaType);

        // ...and only the media half may render inline, which is what canPreview promises the consumer.
        var disposition = download.Content.Headers.ContentDisposition;
        if (expectInlinePreview)
        {
            Assert.NotEqual("attachment", disposition?.DispositionType);
            Assert.Null(disposition?.FileName);
        }
        else
        {
            Assert.Equal("attachment", disposition?.DispositionType);
            Assert.False(string.IsNullOrWhiteSpace(disposition?.FileName), $"A non-media Ready reference must be served as an attachment, so canPreview=false is the honest claim for {assetType}.");
        }
    }

    /// <summary>
    /// Row 4. Two movies in one workspace, one reference dead and one alive. #1176 never places two
    /// movies in the same fixture, so it cannot show that the readiness gate is evaluated inside the
    /// per-movie asset set. The projection filters by workspace and then by the movie's own reference ids,
    /// so a sibling's withheld row must neither leak into this movie nor suppress this movie's healthy
    /// one.
    /// </summary>
    [Fact]
    public async Task Reference_availability_verdict_is_scoped_to_the_movie_that_owns_the_reference()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Project Scoping Owner");
        var deadMovieId = await CreateMovie(client, auth.PersonalWorkspace.Id, "Dead Reference Movie");
        var liveMovieId = await CreateMovie(client, auth.PersonalWorkspace.Id, "Live Reference Movie");

        var deadAsset = await CreateReferenceAsset(client, auth, "Pier plate", AssetTypes.Image, "image/png", "pier.png", StoredFileStatus.Ready);
        var liveAsset = await CreateReferenceAsset(client, auth, "Depot plate", AssetTypes.Image, "image/png", "depot.png", StoredFileStatus.Ready);
        await AttachLocation(client, deadMovieId, "Pier", deadAsset.AssetId);
        await AttachLocation(client, liveMovieId, "Depot", liveAsset.AssetId);
        await SetStoredFileStatus(deadAsset.StoredFileId!.Value, StoredFileStatus.Failed);

        var deadWorld = await ReadWorldAssets(client, deadMovieId);
        var liveWorld = await ReadWorldAssets(client, liveMovieId);

        Assert.Equal(new[] { deadAsset.AssetId }, deadWorld.Select(item => item.Id).ToArray());
        Assert.Equal(new[] { liveAsset.AssetId }, liveWorld.Select(item => item.Id).ToArray());

        var withheld = Assert.Single(deadWorld).HasFile;
        Assert.False(withheld, "The failed reference must be withheld in the movie that references it.");
        Assert.True(Assert.Single(liveWorld).HasFile,
            "A sibling movie's dead reference must not suppress this movie's healthy one: the readiness gate " +
            "belongs to the row, inside the movie's own asset set, not to the shared workspace.");
        Assert.True(Assert.Single(liveWorld).CanPreview);

        // Per-movie, not per-workspace, on the route side too.
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/assets/{deadAsset.AssetId}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/assets/{liveAsset.AssetId}/download")).StatusCode);
    }

    /// <summary>
    /// Row 5. The world read model's asset set is filtered by <c>movie.WorkspaceId</c>, and this pins
    /// every part of that boundary at the availability surface:
    /// another user cannot read the payload at all, cannot attach into it, and - with the attach guard
    /// deliberately bypassed by a forged row, the state a data bug or a migrated legacy project produces -
    /// a foreign-workspace reference earns no advertised preview and no readable bytes. Absence is the
    /// safe outcome here, so the assertion is that the foreign asset never appears, not that it appears
    /// with <c>hasFile: false</c>.
    /// </summary>
    [Fact]
    public async Task Cross_workspace_reference_asset_is_neither_advertised_nor_readable()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "World Isolation Owner");
        var ownerMovieId = await CreateMovie(owner, ownerAuth.PersonalWorkspace.Id, "Isolation Owner Movie");
        var ownerAsset = await CreateReferenceAsset(owner, ownerAuth, "Owner plate", AssetTypes.Image, "image/png", "owner.png", StoredFileStatus.Ready);
        await AttachLocation(owner, ownerMovieId, "Owner Pier", ownerAsset.AssetId);

        using var intruder = factory.CreateClient();
        var intruderAuth = await Register(intruder, "World Isolation Intruder");
        var intruderMovieId = await CreateMovie(intruder, intruderAuth.PersonalWorkspace.Id, "Isolation Intruder Movie");
        var foreignAsset = await CreateReferenceAsset(intruder, intruderAuth, "Foreign plate", AssetTypes.Image, "image/png", "foreign.png", StoredFileStatus.Ready);

        // 1. The world payload is not readable across the workspace boundary.
        var stolenWorld = await intruder.GetAsync($"/api/movie-studio/projects/{ownerMovieId}/world");
        Assert.Equal(HttpStatusCode.NotFound, stolenWorld.StatusCode);
        Assert.Contains("MOVIE_PROJECT_NOT_FOUND", await stolenWorld.Content.ReadAsStringAsync());

        // 2. Nor is a foreign asset attachable through the public route.
        var rejectedAttach = await SendWithCsrf(intruder, HttpMethod.Post,
            $"/api/movie-studio/projects/{intruderMovieId}/locations",
            new { name = "Stolen Pier", description = "A reference owned elsewhere.", visualContinuityNotes = (string?)null, referenceAssetId = ownerAsset.AssetId });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedAttach.StatusCode);
        var error = await rejectedAttach.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_LOCATION_INVALID", error.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(ownerAsset.AssetId, (await ReadWorldAssets(intruder, intruderMovieId)).Select(item => item.Id));

        // 3. With the guard bypassed, the projection's workspace filter is the last line: the forged
        //    foreign reference is simply not part of the owner's asset set, while the owner's own
        //    healthy reference keeps its full claim.
        await ForgeLocationReference(ownerMovieId, foreignAsset.AssetId, "Forged Pier");

        var ownerWorld = await ReadWorldAssets(owner, ownerMovieId);
        Assert.Contains(ownerAsset.AssetId, ownerWorld.Select(item => item.Id));
        Assert.DoesNotContain(foreignAsset.AssetId, ownerWorld.Select(item => item.Id));
        Assert.True(Assert.Single(ownerWorld, item => item.Id == ownerAsset.AssetId).HasFile);

        var foreignDownload = await owner.GetAsync($"/api/assets/{foreignAsset.AssetId}/download");
        Assert.Equal(HttpStatusCode.NotFound, foreignDownload.StatusCode);
        Assert.Contains("ASSET_NOT_FOUND", await foreignDownload.Content.ReadAsStringAsync());
    }

    private record Fixture(Guid AssetId, Guid? StoredFileId, string AssetType, string StorageKey);

    private record WorldAssetFacts(Guid Id, string AssetType, bool HasFile, bool CanPreview);

    /// <summary>
    /// Creates the asset row directly, as #1176 does, so the stored-file status is exactly the one under
    /// test rather than whatever the upload pipeline happens to leave behind. A non-null status also
    /// materialises the object through the app's own local storage service, which is what lets the Ready
    /// rows be checked against a real 200 from the download route instead of only against the gate.
    /// </summary>
    private async Task<Fixture> CreateReferenceAsset(HttpClient client, AuthResponse auth, string name, string assetType, string contentType, string fileName, StoredFileStatus? status)
    {
        var assetId = Guid.NewGuid();
        var storedFileId = status.HasValue ? Guid.NewGuid() : (Guid?)null;
        var extension = Path.GetExtension(fileName);
        var storageKey = storedFileId.HasValue ? $"movie-world-readiness/{storedFileId.Value:N}{extension}" : string.Empty;
        var now = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        if (status.HasValue)
        {
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId!.Value,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                OriginalFileName = fileName,
                StoredFileName = $"{storedFileId.Value:N}{extension}",
                ContentType = contentType,
                Extension = extension,
                SizeBytes = 16,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = storageKey,
                Status = status.Value,
                CreatedAt = now,
                ProcessedAt = status.Value == StoredFileStatus.Ready ? now : null,
            });
        }

        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            StoredFileId = storedFileId,
            Name = name,
            AssetType = assetType,
            MimeType = contentType,
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        if (status == StoredFileStatus.Ready)
        {
            using var storageScope = factory.Services.CreateScope();
            var storage = storageScope.ServiceProvider.GetRequiredService<IFileStorageService>();
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes($"synthetic world reference payload for {name}"));
            await storage.StoreAsync(storageKey, bytes);
        }

        return new Fixture(assetId, storedFileId, assetType, storageKey);
    }

    /// <summary>Applies the lifecycle transition the attach guard forbids at creation time.</summary>
    private async Task SetStoredFileStatus(Guid storedFileId, StoredFileStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var storedFile = await db.StoredFiles.SingleAsync(item => item.Id == storedFileId);
        storedFile.Status = status;
        storedFile.ProcessedAt = status == StoredFileStatus.Ready ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
    }

    /// <summary>Bypasses <c>EnsureMovieAssetAsync</c> to model a reference row that predates the guard.</summary>
    private async Task ForgeLocationReference(Guid movieProjectId, Guid referenceAssetId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var now = DateTime.UtcNow;
        db.MovieLocations.Add(new MovieLocation
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movieProjectId,
            Name = name,
            Description = "A location whose reference belongs to another workspace.",
            ReferenceAssetId = referenceAssetId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateMovie(HttpClient client, Guid workspaceId, string title)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects",
            new { workspaceId, mode = "Full", title, description = "A synthetic readiness-matrix fixture movie.", durationSeconds = 30, aspectRatio = "16:9", style = "cinematic", language = "en" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project").GetProperty("id").GetGuid();
    }

    private async Task AttachLocation(HttpClient client, Guid movieId, string name, Guid referenceAssetId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/locations",
            new { name, description = "A repeatable location.", visualContinuityNotes = (string?)null, referenceAssetId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<WorldAssetFacts> ReadWorldAsset(HttpClient client, Guid movieId, Guid assetId)
    {
        var assets = await ReadWorldAssets(client, movieId);
        var match = assets.FirstOrDefault(item => item.Id == assetId);
        if (match is null)
        {
            throw new InvalidOperationException(
                $"The world read model did not return reference asset {assetId} for movie {movieId}; the " +
                $"fixture never reached the projection under test. Assets advertised: " +
                $"{string.Join(", ", assets.Select(item => item.Id))}.");
        }

        return match;
    }

    private static async Task<List<WorldAssetFacts>> ReadWorldAssets(HttpClient client, Guid movieId)
    {
        var world = await client.GetFromJsonAsync<JsonElement>($"/api/movie-studio/projects/{movieId}/world");
        return world.GetProperty("assets").EnumerateArray()
            .Select(item => new WorldAssetFacts(
                item.GetProperty("id").GetGuid(),
                item.GetProperty("assetType").GetString()!,
                item.GetProperty("hasFile").GetBoolean(),
                item.GetProperty("canPreview").GetBoolean()))
            .ToList();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register",
            new { displayName, email = $"movie-world-readiness-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
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
