using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free HTTP contract for the AI Video Makeover source-metadata read
/// model (#698 slice, the server-side descriptor #811 identified as missing from
/// the browser read models). Covers: authenticated owner access, cross-user and
/// cross-workspace non-disclosure, fail-closed handling of absent or unbounded
/// persisted facts, and the honest frame-rate/audio gaps that #775 admission
/// keeps gating. No provider, no cost, no storage reads of file bytes.
/// </summary>
public sealed class MovieVideoMakeoverSourceApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieVideoMakeoverSourceApiTests(GenerationJobsNoWorkerFactory factory)
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

        var response = await client.GetAsync(ListPath(projectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(2, json.RootElement.GetProperty("totalClipCount").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("disclosedCount").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("sources").EnumerateArray());

        // The withheld rows are not even described: ids of the foreign asset/file
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

        var cases = new (SourceSeed Seed, string ExpectedCode)[]
        {
            (missingDimensions, MovieVideoMakeoverSourceCodes.MetadataMissing),
            (nullRetention, MovieVideoMakeoverSourceCodes.RetentionUnverified),
            (expiredRetention, MovieVideoMakeoverSourceCodes.RetentionUnverified),
            (outOfBounds, MovieVideoMakeoverSourceCodes.MetadataOutOfBounds),
            (orphanedFile, MovieVideoMakeoverSourceCodes.OwnershipUnverified),
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

        // The orphaned-file row proves visibility/retention/metadata all fail closed
        // together when the backing file evidence is gone.
        var orphan = await (await client.GetAsync($"{ListPath(projectId)}/{orphanedFile.ClipId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(orphan.GetProperty("visibilityPrivate").GetBoolean());
        Assert.False(orphan.GetProperty("retentionVerified").GetBoolean());
        Assert.Contains(MovieVideoMakeoverSourceCodes.FileMissing,
            orphan.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()));
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
