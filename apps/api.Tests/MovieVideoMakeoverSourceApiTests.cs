using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Explicit test-only opt-in for the default-off production admission of the
/// Video Makeover source read model (#698 release gate,
/// <see cref="MovieVideoMakeoverSourceFeatureGate"/>). No committed appsettings
/// file sets this key, so production stays off; only this fixture admits the
/// endpoints, and the established authorised-fixture contract below keeps its
/// exact behaviour with zero test-body changes.
/// </summary>
public sealed class MovieVideoMakeoverSourceEnabledTestFactory : GenerationJobsApiFactory
{
    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(MovieVideoMakeoverSourceFeatureGate.EnabledConfigKey, "true");
    }
}

/// <summary>
/// Provider-free HTTP contract for the AI Video Makeover source-metadata read
/// model (#698 slice, the server-side descriptor #811 identified as missing from
/// the browser read models). Covers: authenticated owner access, cross-user and
/// cross-workspace non-disclosure, fail-closed handling of absent or unbounded
/// persisted facts, and the honest frame-rate/audio gaps that #775 admission
/// keeps gating. No provider, no cost, no storage reads of file bytes.
/// Reached only through <see cref="MovieVideoMakeoverSourceEnabledTestFactory"/>'s
/// explicit opt-in; see <see cref="MovieVideoMakeoverSourceDisabledApiTests"/>
/// for the default-off contract this opt-in is opting out of.
/// </summary>
public sealed class MovieVideoMakeoverSourceApiTests : IClassFixture<MovieVideoMakeoverSourceEnabledTestFactory>
{
    private readonly MovieVideoMakeoverSourceEnabledTestFactory factory;

    public MovieVideoMakeoverSourceApiTests(MovieVideoMakeoverSourceEnabledTestFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Owner_gets_verified_facts_for_a_ready_video_clip_with_honest_gaps()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Makeover source facts");
        var seed = await SeedSourceAsync(projectId, owner,
            Healthy(width: 1920, height: 1080, durationSeconds: 30.5, sizeBytes: 4_000_000));

        var response = await client.GetAsync(ListPath(projectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MovieVideoMakeoverSourceCodes.ReadModel, response.Headers.GetValues("X-Movie-Read-Model").Single());
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(projectId, json.RootElement.GetProperty("movieProjectId").GetGuid());
        Assert.Equal(owner.PersonalWorkspace.Id, json.RootElement.GetProperty("workspaceId").GetGuid());
        Assert.Equal(1, json.RootElement.GetProperty("totalClipCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("disclosedCount").GetInt32());
        var source = json.RootElement.GetProperty("sources")[0];
        Assert.Equal(seed.ClipId, source.GetProperty("clipId").GetGuid());
        Assert.True(source.GetProperty("attachPermitted").GetBoolean());
        Assert.Empty(source.GetProperty("blockers").EnumerateArray());

        // Real persisted facts are now carried — the subset #811 said was absent.
        Assert.Equal(seed.AssetId, source.GetProperty("assetId").GetGuid());
        Assert.Equal(seed.FileId, source.GetProperty("storedFileId").GetGuid());
        Assert.Equal(1920, source.GetProperty("width").GetInt32());
        Assert.Equal(1080, source.GetProperty("height").GetInt32());
        Assert.Equal(30.5, source.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(4_000_000, source.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("video/mp4", source.GetProperty("contentType").GetString());
        Assert.Equal("video", source.GetProperty("assetType").GetString());
        Assert.Equal("active", source.GetProperty("assetStatus").GetString());
        Assert.True(source.GetProperty("ownershipVerified").GetBoolean());
        Assert.True(source.GetProperty("ownerIsRequester").GetBoolean());
        Assert.Equal("owner_private", source.GetProperty("visibilityScope").GetString());
        Assert.True(source.GetProperty("visibilityPrivate").GetBoolean());
        Assert.True(source.GetProperty("retentionVerified").GetBoolean());
        Assert.True(source.GetProperty("storageAvailable").GetBoolean());

        // Frame rate and audio state are not persisted anywhere: they are reported
        // as gaps with null values, never invented.
        Assert.Equal(JsonValueKind.Null, source.GetProperty("frameRate").ValueKind);
        Assert.False(source.GetProperty("frameRateVerified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("audioState").ValueKind);
        Assert.False(source.GetProperty("audioStateVerified").GetBoolean());
        Assert.Contains("frame_rate", source.GetProperty("pendingFacts").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("audio_state", source.GetProperty("pendingFacts").EnumerateArray().Select(item => item.GetString()));

        // No raw account identifiers are disclosed — ownership is a boolean, not an id.
        Assert.DoesNotContain("userId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.User.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);

        // Single-source lookup returns the same descriptor.
        var single = await client.GetAsync($"{ListPath(projectId)}/{seed.ClipId}");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        var singleJson = await single.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(singleJson.GetProperty("attachPermitted").GetBoolean());
        Assert.Equal(seed.FileId, singleJson.GetProperty("storedFileId").GetGuid());
    }

    [Fact]
    public async Task Cross_user_cannot_enumerate_or_read_makeover_sources()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await RegisterAsync(ownerClient);
        var projectId = await CreateProjectAsync(ownerClient, owner, "Private source project");
        var seed = await SeedSourceAsync(projectId, owner, Healthy());

        using var outsiderClient = factory.CreateClient();
        await RegisterAsync(outsiderClient);

        var list = await outsiderClient.GetAsync(ListPath(projectId));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.DoesNotContain(seed.ClipId.ToString("D"), await list.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var single = await outsiderClient.GetAsync($"{ListPath(projectId)}/{seed.ClipId}");
        Assert.Equal(HttpStatusCode.NotFound, single.StatusCode);
        var singleBody = await single.Content.ReadAsStringAsync();
        Assert.DoesNotContain(seed.AssetId.ToString("D"), singleBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seed.FileId.ToString("D"), singleBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cross_workspace_and_requester_invisible_links_are_withheld_without_disclosure()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Withholding project");

        // (a) The clip's asset points into a foreign workspace.
        var foreignWorkspaceId = await CreateWorkspaceAsync();
        var crossWorkspace = await SeedSourceAsync(projectId, owner, Healthy(),
            configureAsset: asset => asset.WorkspaceId = foreignWorkspaceId);

        // (b) A second real user's file (same workspace, but personal and not shared
        // through a project) is referenced by another clip: invisible under the
        // authoritative StoredFileVisibility boundary.
        using var otherClient = factory.CreateClient();
        var otherUser = await RegisterAsync(otherClient);
        var invisible = await SeedSourceAsync(projectId, owner, Healthy(),
            configureFile: file => file.UserId = otherUser.User.Id);

        // One accessible source is interleaved with the two withheld links.
        // Response totals, not just the list body, must disclose only this row.
        var accessible = await SeedSourceAsync(projectId, owner, Healthy());

        var response = await client.GetAsync(ListPath(projectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(1, json.RootElement.GetProperty("totalClipCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("disclosedCount").GetInt32());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
        var onlyVisibleSource = Assert.Single(json.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Equal(accessible.ClipId, onlyVisibleSource.GetProperty("clipId").GetGuid());

        // The withheld rows do not affect totals/truncation and are not described:
        // ids of the foreign asset/file
        // and the other user's file never appear in the payload.
        Assert.DoesNotContain(crossWorkspace.AssetId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(crossWorkspace.FileId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invisible.FileId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invisible.ClipId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);

        // Direct lookups also fail closed rather than leaking the link.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{ListPath(projectId)}/{crossWorkspace.ClipId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{ListPath(projectId)}/{invisible.ClipId}")).StatusCode);
    }

    [Fact]
    public async Task Absent_unverified_or_out_of_bounds_facts_fail_closed_without_invention()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Fail-closed project");

        var missingDimensions = await SeedSourceAsync(projectId, owner, Healthy(width: null, height: null));
        var nullRetention = await SeedSourceAsync(projectId, owner, Healthy(retentionDays: null));
        var expiredRetention = await SeedSourceAsync(projectId, owner, Healthy(retentionDays: -0.01));
        var outOfBounds = await SeedSourceAsync(projectId, owner, Healthy(width: 20_000, durationSeconds: 7200));
        var orphanedFile = await SeedSourceAsync(projectId, owner, Healthy(), noFileEvidence: true);
        using var otherClient = factory.CreateClient();
        var anotherCreator = await RegisterAsync(otherClient);
        // The file belongs to the requester, but the asset row names another
        // creator: nonempty IDs alone must not prove ownership of the link.
        var mismatchedOwner = await SeedSourceAsync(projectId, owner, Healthy(),
            configureAsset: asset => asset.CreatedByUserId = anotherCreator.User.Id);

        var cases = new (SourceSeed Seed, string ExpectedCode)[]
        {
            (missingDimensions, MovieVideoMakeoverSourceCodes.MetadataMissing),
            (nullRetention, MovieVideoMakeoverSourceCodes.RetentionUnverified),
            (expiredRetention, MovieVideoMakeoverSourceCodes.RetentionUnverified),
            (outOfBounds, MovieVideoMakeoverSourceCodes.MetadataOutOfBounds),
            (orphanedFile, MovieVideoMakeoverSourceCodes.OwnershipUnverified),
            (mismatchedOwner, MovieVideoMakeoverSourceCodes.OwnershipUnverified),
        };

        foreach (var (seed, expectedCode) in cases)
        {
            var response = await client.GetAsync($"{ListPath(projectId)}/{seed.ClipId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var source = await response.Content.ReadFromJsonAsync<JsonElement>();
            var blockers = source.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains(expectedCode, blockers);
            Assert.False(source.GetProperty("attachPermitted").GetBoolean());
        }

        // Values that were never persisted stay null: fail closed means no fallback
        // 1920x1080 is ever reported, and the verdict fails closed only on the
        // facts that actually lack evidence.
        var missing = await (await client.GetAsync($"{ListPath(projectId)}/{missingDimensions.ClipId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("width").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("height").ValueKind);
        // This clip's retention genuinely is persisted and in force: it is reported
        // verified while the missing dimensions are not invented.
        Assert.True(missing.GetProperty("retentionVerified").GetBoolean());
        Assert.Contains(MovieVideoMakeoverSourceCodes.MetadataMissing,
            missing.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()));

        var ownerMismatch = await (await client.GetAsync($"{ListPath(projectId)}/{mismatchedOwner.ClipId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(ownerMismatch.GetProperty("ownershipVerified").GetBoolean());
        Assert.True(ownerMismatch.GetProperty("ownerIsRequester").GetBoolean());
        Assert.False(ownerMismatch.GetProperty("attachPermitted").GetBoolean());

        // The orphaned-file row proves visibility/retention/metadata all fail closed
        // together when the backing file evidence is gone.
        var orphan = await (await client.GetAsync($"{ListPath(projectId)}/{orphanedFile.ClipId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(orphan.GetProperty("visibilityPrivate").GetBoolean());
        Assert.False(orphan.GetProperty("retentionVerified").GetBoolean());
        Assert.Contains(MovieVideoMakeoverSourceCodes.FileMissing,
            orphan.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Conflicting_file_links_and_spoofed_video_mime_never_become_attachable()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Source link and MIME integrity");

        // The asset and clip each point to an existing, requester-owned file,
        // but the two persisted IDs disagree. Merely finding a valid file is
        // not proof that it belongs to this clip's Asset.
        var alternate = await SeedSourceAsync(projectId, owner, Healthy());
        var mismatchedLink = await SeedSourceAsync(projectId, owner, Healthy(),
            configureAsset: asset => asset.StoredFileId = alternate.FileId);

        // The asset's type label can be "video" even when the stored bytes were
        // classified as an image, or its own MIME claim contradicts the file.
        var nonVideoStoredFile = await SeedSourceAsync(projectId, owner, Healthy(),
            configureFile: file => file.ContentType = "image/png");
        var nonVideoAssetMime = await SeedSourceAsync(projectId, owner, Healthy(),
            configureAsset: asset => asset.MimeType = "image/jpeg");

        await AssertBlockedAsync(client, projectId, mismatchedLink,
            MovieVideoMakeoverSourceCodes.FileLinkMismatch);
        await AssertBlockedAsync(client, projectId, nonVideoStoredFile,
            MovieVideoMakeoverSourceCodes.NotVideo);
        await AssertBlockedAsync(client, projectId, nonVideoAssetMime,
            MovieVideoMakeoverSourceCodes.NotVideo);

        // A healthy independent clip remains available; this is a targeted
        // fail-closed admission check, not a blanket disablement.
        var healthy = await client.GetAsync($"{ListPath(projectId)}/{alternate.ClipId}");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        var dto = await healthy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("attachPermitted").GetBoolean());
        Assert.Empty(dto.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task Structural_blockers_are_reported_and_the_audio_and_frame_rate_gaps_stay_listed()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Structural project");

        var imageAsset = await SeedSourceAsync(projectId, owner, Healthy(assetType: AssetTypes.Image));
        var plannedClip = await SeedSourceAsync(projectId, owner, Healthy(clipStatus: MovieClipStatuses.Planned));
        var deletedFile = await SeedSourceAsync(projectId, owner, Healthy(
            fileStatus: StoredFileStatus.Deleted));
        var archivedAsset = await SeedSourceAsync(projectId, owner, Healthy(assetStatus: AssetStatus.Archived));

        await AssertBlockedAsync(client, projectId, imageAsset, MovieVideoMakeoverSourceCodes.NotVideo);
        await AssertBlockedAsync(client, projectId, plannedClip, MovieVideoMakeoverSourceCodes.ClipNotReady);
        await AssertBlockedAsync(client, projectId, archivedAsset, MovieVideoMakeoverSourceCodes.AssetUnavailable);

        var deleted = await AssertBlockedAsync(client, projectId, deletedFile, MovieVideoMakeoverSourceCodes.AssetUnavailable);
        Assert.Contains(MovieVideoMakeoverSourceCodes.RetentionUnverified,
            deleted.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()));

        // Even when blocked, the descriptor keeps naming both unverifiable facts.
        Assert.Contains("audio_state",
            deleted.GetProperty("pendingFacts").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("frame_rate",
            deleted.GetProperty("pendingFacts").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Nonfinite_persisted_video_duration_fails_closed_and_serializes_safely(double invalidDuration)
    {
        // Exercise the pure persisted-row evaluator: SQLite may normalize NaN
        // to NULL before an HTTP test ever reaches the JSON serialization path.
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieProjectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId,
            AssetId = assetId, StoredFileId = fileId,
            Status = MovieClipStatuses.Ready, DurationSeconds = 12,
        };
        var asset = new Asset
        {
            Id = assetId, WorkspaceId = workspaceId, CreatedByUserId = userId,
            StoredFileId = fileId, AssetType = AssetTypes.Video,
            MimeType = "video/mp4", Status = AssetStatus.Active,
        };
        var file = new StoredFile
        {
            Id = fileId, WorkspaceId = workspaceId, UserId = userId,
            ContentType = "video/mp4", Status = StoredFileStatus.Ready,
            DurationSeconds = invalidDuration, Width = 1280, Height = 720,
            SizeBytes = 1_500_000, RetainUntil = DateTime.UtcNow.AddDays(1),
        };
        var result = MovieVideoMakeoverSourceEvaluator.Evaluate(
            clip, movieProjectId, asset, file, userId, DateTime.UtcNow);

        Assert.False(result.AttachPermitted);
        Assert.Null(result.DurationSeconds);
        Assert.Contains(MovieVideoMakeoverSourceCodes.MetadataOutOfBounds, result.Blockers);
        var json = JsonSerializer.Serialize(result);
        using var serialized = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, serialized.RootElement.GetProperty("DurationSeconds").ValueKind);
    }

    [Fact]
    public async Task Anonymous_request_is_rejected_without_reading_anything()
    {
        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/movie-studio/projects/{Guid.NewGuid()}/video-makeover-sources");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- helpers ---------------------------------------------------------------

    private static string ListPath(Guid projectId) =>
        $"/api/movie-studio/projects/{projectId}/video-makeover-sources";

    private static SourceVariant Healthy(
        string clipStatus = MovieClipStatuses.Ready,
        string assetType = AssetTypes.Video,
        AssetStatus assetStatus = AssetStatus.Active,
        StoredFileStatus fileStatus = StoredFileStatus.Ready,
        int? width = 1280,
        int? height = 720,
        double? durationSeconds = 12,
        long sizeBytes = 1_500_000,
        double? retentionDays = 30) =>
        new(clipStatus, assetType, assetStatus, fileStatus, width, height, durationSeconds, sizeBytes,
            retentionDays is null ? null : DateTime.UtcNow.AddDays(retentionDays.Value));

    private async Task<JsonElement> AssertBlockedAsync(HttpClient client, Guid projectId, SourceSeed seed, string code)
    {
        var response = await client.GetAsync($"{ListPath(projectId)}/{seed.ClipId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var source = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(source.GetProperty("attachPermitted").GetBoolean());
        Assert.Contains(code, source.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()));
        return source;
    }

    private sealed record SourceVariant(
        string ClipStatus,
        string AssetType,
        AssetStatus AssetStatus,
        StoredFileStatus FileStatus,
        int? Width,
        int? Height,
        double? DurationSeconds,
        long SizeBytes,
        DateTime? RetainUntilUtc);

    private sealed record SourceSeed(Guid ClipId, Guid AssetId, Guid FileId);

    private async Task<SourceSeed> SeedSourceAsync(
        Guid projectId,
        AuthResponse owner,
        SourceVariant variant,
        Action<Asset>? configureAsset = null,
        Action<StoredFile>? configureFile = null,
        bool noFileEvidence = false)
    {
        var clipId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var userId = owner.User.Id;
        var workspaceId = owner.PersonalWorkspace.Id;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            var file = new StoredFile
            {
                Id = fileId,
                WorkspaceId = workspaceId,
                UserId = userId,
                OriginalFileName = "source.mp4",
                StoredFileName = $"{fileId:N}.mp4",
                Extension = ".mp4",
                ContentType = "video/mp4",
                SizeBytes = variant.SizeBytes,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"tests/makeover-source/{fileId:N}.mp4",
                Status = variant.FileStatus,
                CreatedAt = now,
                ProcessedAt = now,
                ContainerFormat = "mp4",
                Width = variant.Width,
                Height = variant.Height,
                DurationSeconds = variant.DurationSeconds,
                RetainUntil = variant.RetainUntilUtc,
                DeletedAt = variant.FileStatus == StoredFileStatus.Deleted ? now : null,
            };
            if (!noFileEvidence)
            {
                configureFile?.Invoke(file);
                db.StoredFiles.Add(file);
            }

            var asset = new Asset
            {
                Id = assetId,
                WorkspaceId = workspaceId,
                CreatedByUserId = userId,
                StoredFileId = noFileEvidence ? null : fileId,
                Name = "Makeover source clip",
                AssetType = variant.AssetType,
                MimeType = "video/mp4",
                Status = variant.AssetStatus,
                CreatedAt = now,
                UpdatedAt = now,
                ArchivedAt = variant.AssetStatus == AssetStatus.Archived ? now : null,
            };
            configureAsset?.Invoke(asset);
            db.Assets.Add(asset);

            db.MovieClips.Add(new MovieClip
            {
                Id = clipId,
                MovieProjectId = projectId,
                GenerationJobId = null,
                AssetId = assetId,
                StoredFileId = noFileEvidence ? null : fileId,
                Status = variant.ClipStatus,
                DurationSeconds = variant.DurationSeconds is null ? null : (int?)Math.Round(variant.DurationSeconds.Value),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        return new SourceSeed(clipId, assetId, fileId);
    }

    private async Task<Guid> CreateWorkspaceAsync()
    {
        var workspaceId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = $"Foreign {workspaceId:N}",
            Slug = $"foreign-{workspaceId:N}",
            Type = WorkspaceType.Business,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return workspaceId;
    }

    private async Task<AuthResponse> RegisterAsync(HttpClient client) =>
        await SendWithCsrfAsync<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Makeover Source Tester",
            email = $"makeover-source-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });

    private async Task<Guid> CreateProjectAsync(HttpClient client, AuthResponse owner, string title)
    {
        var response = await SendWithCsrfAsync<JsonElement>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = owner.PersonalWorkspace.Id,
            mode = "Full",
            title,
            description = "A deterministic provider-free Makeover source test.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        return response.GetProperty("project").GetProperty("id").GetGuid();
    }

    private static async Task<T> SendWithCsrfAsync<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}

/// <summary>
/// Default-off, fail-closed production admission for the two source GET
/// endpoints (#698 release preparation). This fixture uses the shared
/// <see cref="GenerationJobsNoWorkerFactory"/> exactly as an unconfigured
/// deployment runs today - the opt-in key is never set - and pins the contract
/// that makes shipping the controller safe: every request receives the same
/// opaque project 404 as an inaccessible project, with no read-model marker
/// header and no source metadata, before any database read. A paid provider is
/// never involved; no generation path is enabled by anything here.
/// </summary>
public sealed class MovieVideoMakeoverSourceDisabledApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieVideoMakeoverSourceDisabledApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Unconfigured_deployment_opaques_both_source_gets_behind_the_project_404()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Disabled gate project");

        // A genuinely attachable, fully healthy source exists in the database:
        // the gate must hide it at the admission layer, not rely on data absence.
        var clipId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.StoredFiles.Add(new StoredFile
            {
                Id = fileId,
                WorkspaceId = owner.PersonalWorkspace.Id,
                UserId = owner.User.Id,
                OriginalFileName = "source.mp4",
                StoredFileName = $"{fileId:N}.mp4",
                Extension = ".mp4",
                ContentType = "video/mp4",
                SizeBytes = 1_500_000,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"tests/makeover-source-disabled/{fileId:N}.mp4",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
                ContainerFormat = "mp4",
                Width = 1280,
                Height = 720,
                DurationSeconds = 12,
                RetainUntil = now.AddDays(30),
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = owner.PersonalWorkspace.Id,
                CreatedByUserId = owner.User.Id,
                StoredFileId = fileId,
                Name = "Disabled gate source clip",
                AssetType = AssetTypes.Video,
                MimeType = "video/mp4",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.MovieClips.Add(new MovieClip
            {
                Id = clipId,
                MovieProjectId = projectId,
                GenerationJobId = null,
                AssetId = assetId,
                StoredFileId = fileId,
                Status = MovieClipStatuses.Ready,
                DurationSeconds = 12,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // The project's own owner - the exact caller the enabled contract serves -
        // sees an opaque 404 on both endpoints, no marker header, no metadata.
        var list = await client.GetAsync(ListPath(projectId));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        var listBody = await list.Content.ReadAsStringAsync();
        AssertOpaqueProjectNotFound(list, listBody);
        Assert.DoesNotContain(clipId.ToString("D"), listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(assetId.ToString("D"), listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileId.ToString("D"), listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attachPermitted", listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("video-makeover-sources", listBody, StringComparison.OrdinalIgnoreCase);

        var single = await client.GetAsync($"{ListPath(projectId)}/{clipId}");
        Assert.Equal(HttpStatusCode.NotFound, single.StatusCode);
        var singleBody = await single.Content.ReadAsStringAsync();
        AssertOpaqueProjectNotFound(single, singleBody);
        Assert.DoesNotContain(clipId.ToString("D"), singleBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blockers", singleBody, StringComparison.OrdinalIgnoreCase);

        // Non-disclosure across identities: an outsider and the owner receive the
        // byte-identical envelope, so the response cannot confirm the project,
        // the read model, or the seeded source exists.
        using var outsiderClient = factory.CreateClient();
        await RegisterAsync(outsiderClient);
        var outsiderList = await outsiderClient.GetAsync(ListPath(projectId));
        Assert.Equal(list.StatusCode, outsiderList.StatusCode);
        Assert.Equal(listBody, await outsiderList.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_request_is_rejected_before_the_gate_without_disclosing_it()
    {
        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(ListPath(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Movie-Read-Model"));
    }

    private static void AssertOpaqueProjectNotFound(HttpResponseMessage response, string body)
    {
        Assert.False(response.Headers.Contains("X-Movie-Read-Model"));
        using var json = JsonDocument.Parse(body);
        Assert.Equal("MOVIE_PROJECT_NOT_FOUND", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static string ListPath(Guid projectId) =>
        $"/api/movie-studio/projects/{projectId}/video-makeover-sources";

    private async Task<AuthResponse> RegisterAsync(HttpClient client) =>
        await SendWithCsrfAsync<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Makeover Gate Tester",
            email = $"makeover-gate-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });

    private async Task<Guid> CreateProjectAsync(HttpClient client, AuthResponse owner, string title)
    {
        var response = await SendWithCsrfAsync<JsonElement>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = owner.PersonalWorkspace.Id,
            mode = "Full",
            title,
            description = "A deterministic provider-free gate test.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        return response.GetProperty("project").GetProperty("id").GetGuid();
    }

    private static async Task<T> SendWithCsrfAsync<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}

/// <summary>
/// Pure parsing contract of the admission key: only the boolean literal true
/// (whitespace-tolerant, case-insensitive) admits the read model. Everything
/// ambiguous - absent, empty, "1", "yes", "on", junk - fails closed. No web
/// host, no database, no provider.
/// </summary>
public sealed class MovieVideoMakeoverSourceFeatureGateTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    [InlineData("enabled", false)]
    [InlineData("true1", false)]
    [InlineData("0", false)]
    public void Gate_admits_only_the_boolean_true_literal(string raw, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [MovieVideoMakeoverSourceFeatureGate.EnabledConfigKey] = raw,
            })
            .Build();
        Assert.Equal(expected, MovieVideoMakeoverSourceFeatureGate.IsEnabled(configuration));
    }

    [Fact]
    public void Gate_fails_closed_without_the_key_or_without_configuration()
    {
        Assert.False(MovieVideoMakeoverSourceFeatureGate.IsEnabled(null));
        Assert.False(MovieVideoMakeoverSourceFeatureGate.IsEnabled(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void No_committed_appsettings_enables_the_gate()
    {
        // The default-off guarantee is a repository property, not only a runtime
        // one: neither appsettings.json nor appsettings.Production.json may set
        // the admission key. A future edit adding it must be a reviewed release
        // decision, and this test fails first.
        var apiDirectory = LocateApiProjectDirectory();
        foreach (var file in new[] { "appsettings.json", "appsettings.Production.json" })
        {
            var path = Path.Combine(apiDirectory, file);
            Assert.True(File.Exists(path), $"Missing {file} next to the API project.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(
                document.RootElement.TryGetProperty("MovieVideoMakeoverSource", out _),
                $"{file} must not carry the MovieVideoMakeoverSource admission section while #698 stays unlaunched.");
        }
    }

    private static string LocateApiProjectDirectory()
    {
        // Test output lives under apps/api.Tests/bin; the appsettings files are
        // linked into the API project. Walk up to the repository apps/ folder.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !string.Equals(directory.Name, "apps", StringComparison.Ordinal))
            directory = directory.Parent;
        var apiDirectory = directory is null ? null : Path.Combine(directory.FullName, "api");
        Assert.NotNull(apiDirectory);
        Assert.True(Directory.Exists(apiDirectory));
        return apiDirectory!;
    }
}
