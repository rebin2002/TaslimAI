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
/// Issue #708 QA lane (test-only): the cross-workspace boundary of the read-only reference
/// authorization/eligibility HTTP route as joined on #815 (built on the #805 authorizer and the
/// #776 policy). Existing coverage stops short of four things, all asserted here through the real
/// pipeline (authentication, workspace fence, shared asset visibility rules, authorizer, policy):
/// <list type="bullet">
/// <item>Every cross-workspace HTTP case so far addresses an AUDIO asset, and no multi-asset
/// selection ever reaches the authorizer (the oversized case is refused at the transport edge).
/// Here a VIDEO asset sits in the foreign workspace, and one request mixes an owned synthetic
/// audio with that foreign-workspace video: the owned half must still resolve, the foreign half
/// must deny with the boundary's own fence, and neither denial may disclose the video's
/// identifier, stored name, container or duration.</item>
/// <item>Missing and retention-purged (Deleted) backing files are exercised only against the
/// #805 unit fakes. These are the first cases where an Active asset in the requesting workspace
/// loses its file row at the HTTP level, where the refusal must arrive as the stable
/// <c>MUSIC_REFERENCE_AUTH_FILE_UNAVAILABLE</c> code with no sentinel leakage and no mutation.</item>
/// <item>No-mutation snapshots currently guard only the happy audio path; these tests snapshot
/// jobs, usage, credits, assets and stored-file rows around every refusal path they cover.</item>
/// </list>
/// Everything is synthetic and provider-free: no network, no real storage, no provider adapter,
/// no generation, no billing movement. Nothing here enables sung generation or reaches any
/// paid call; it only checks what the read-only boundary says about references it may never use.
/// </summary>
public sealed class MusicReferenceEligibilityCrossWorkspaceBoundaryApiTests : IClassFixture<GenerationJobsApiFactory>
{
    private const string SentinelStorageKey = "SENTINELXBOUNDARYSTORAGEKEY";
    private const string SentinelAudioFileName = "sentinelx-boundary-audio.mp3";
    private const string SentinelVideoFileName = "sentinelx-boundary-video.mp4";
    private const string SentinelAudioAssetName = "SENTINELX BOUNDARY AUDIO ASSET";
    private const string SentinelVideoAssetName = "SENTINELX BOUNDARY VIDEO ASSET";
    private const string SentinelTeamWorkspaceName = "SENTINELX BOUNDARY TEAM WORKSPACE";

    private const string AuthFileUnavailable = "MUSIC_REFERENCE_AUTH_FILE_UNAVAILABLE";
    private const string AuthWorkspaceMismatch = "MUSIC_REFERENCE_AUTH_WORKSPACE_MISMATCH";
    private const string AuthAssetNotAvailable = "MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE";

    private readonly GenerationJobsApiFactory factory;

    public MusicReferenceEligibilityCrossWorkspaceBoundaryApiTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Active_audio_reference_with_no_backing_file_row_denies_at_http_level_without_leak_or_mutation()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Boundary Missing File Owner");
        // The asset row is Active in the requester's own workspace, but the server holds no
        // stored-file row behind it: the refusal must be loud, coded, and silent about everything else.
        var assetId = await SeedAudioAssetAsync(auth.PersonalWorkspace.Id, auth.User.Id, withBackingFile: false);
        var before = await SnapshotSideEffectCountsAsync();

        var (raw, body) = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.False(body.GetProperty("isEligible").GetBoolean());
        // Every selection refused at the access layer: only the access refusal is reported, with no
        // synthetic eligibility noise invented on top of it.
        Assert.Equal(new[] { AuthFileUnavailable }, BlockerCodes(body));
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
        var blocker = body.GetProperty("blockers")[0];
        // An access-layer refusal names no asset: the blocker carries neither the identifier nor a path.
        AssertRefusalNamesNoAsset(blocker);
        var message = blocker.GetProperty("message").GetString()!;
        Assert.DoesNotContain(assetId.ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain("/", message, StringComparison.Ordinal);

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    [Fact]
    public async Task Retention_purged_backing_file_denies_as_unavailable_without_exposing_storage_detail()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Boundary Purged File Owner");
        // A Deleted stored-file row is what a retention purge leaves behind: the asset still points
        // at it, so the boundary must refuse instead of reporting the purged bytes as usable.
        var assetId = await SeedAudioAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, withBackingFile: true, fileStatus: StoredFileStatus.Deleted);
        var before = await SnapshotSideEffectCountsAsync();

        var response = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}&assetId={assetId}&mediaType=audio");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var json = body.ToString();

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        Assert.Equal(new[] { AuthFileUnavailable }, BlockerCodes(body));
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
        // The purged file's storage key and recorded names must never surface in the refusal.
        AssertNoSentinelLeak(json, forbiddenAssetId: null);

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    [Fact]
    public async Task Mixed_owned_audio_and_foreign_workspace_video_selection_resolves_only_the_owned_reference()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Boundary Mixed Selection Owner");
        var (teamWorkspaceId, _) = await SeedTeamWorkspaceAsync(auth.User.Id);

        var ownedAudioId = await SeedAudioAssetAsync(auth.PersonalWorkspace.Id, auth.User.Id, withBackingFile: true);
        // A synthetic owned video that lives in the user's OTHER workspace. The user can see it, so
        // the fence that must answer is the boundary's own workspace comparison - not visibility.
        var foreignVideoId = await SeedVideoAssetAsync(teamWorkspaceId, auth.User.Id);

        var before = await SnapshotSideEffectCountsAsync();
        var response = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}"
            + $"&assetId={ownedAudioId},{foreignVideoId}&mediaType=audio,video");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var json = body.ToString();

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        var codes = BlockerCodes(body);
        // The foreign video is refused by the workspace fence...
        Assert.Contains(AuthWorkspaceMismatch, codes);
        // ...while the owned audio still passes authorization and is judged on its own evidence,
        // proving one refusal does not blanket or silently drop the other selection.
        Assert.Contains("MUSIC_REFERENCE_RIGHTS_MISSING", codes);
        Assert.Contains("MUSIC_REFERENCE_PROVIDER_CAPABILITY_MISSING", codes);
        Assert.DoesNotContain(AuthAssetNotAvailable, codes);

        var fact = Assert.Single(body.GetProperty("verifiedFacts").EnumerateArray());
        Assert.Equal(ownedAudioId, Guid.Parse(fact.GetProperty("assetId").GetString()!));
        Assert.Equal("audio", fact.GetProperty("mediaType").GetString());

        // Privacy: the refusal of the foreign video confirms nothing about it - not its identifier,
        // not its stored names, not its container or recorded duration.
        AssertNoSentinelLeak(json, forbiddenAssetId: foreignVideoId);
        Assert.DoesNotContain("mp4", json, StringComparison.OrdinalIgnoreCase);
        var mismatchBlocker = body.GetProperty("blockers").EnumerateArray()
            .First(item => item.GetProperty("code").GetString() == AuthWorkspaceMismatch);
        AssertRefusalNamesNoAsset(mismatchBlocker);

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    [Fact]
    public async Task Foreign_workspace_video_is_indistinguishable_from_an_unknown_id_for_a_stranger_account()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Boundary Stranger Owner");
        var foreignVideoId = await SeedVideoAssetAsync(owner.PersonalWorkspace.Id, owner.User.Id);

        using var strangerClient = factory.CreateClient();
        var stranger = await Register(strangerClient, "Boundary Stranger Intruder");
        var before = await SnapshotSideEffectCountsAsync();

        var (_, body) = await EvaluateAsync(
            strangerClient, stranger.PersonalWorkspace.Id, foreignVideoId.ToString(), "video");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        // One uniform refusal: the stranger cannot tell a foreign video from a never-existing id,
        // and learns nothing about its media kind, file state or workspace.
        Assert.Equal(new[] { AuthAssetNotAvailable }, BlockerCodes(body));
        Assert.DoesNotContain(AuthWorkspaceMismatch, BlockerCodes(body));
        Assert.DoesNotContain("MUSIC_REFERENCE_AUTH_MEDIA_KIND_MISMATCH", BlockerCodes(body));
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
        AssertNoSentinelLeak(body.ToString(), forbiddenAssetId: foreignVideoId);

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    private async Task<(HttpResponseMessage Raw, JsonElement Body)> EvaluateAsync(
        HttpClient client, Guid workspaceId, string assetIds, string mediaTypes)
    {
        var response = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={workspaceId}&assetId={assetIds}&mediaType={mediaTypes}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>A named-asset-free refusal: the field is absent or null, and no identifier leaks into the message.</summary>
    private static void AssertRefusalNamesNoAsset(JsonElement blocker)
    {
        if (blocker.TryGetProperty("assetId", out var assetId))
        {
            Assert.Equal(JsonValueKind.Null, assetId.ValueKind);
        }
    }

    private static IReadOnlyList<string> BlockerCodes(JsonElement body) =>
        body.GetProperty("blockers").EnumerateArray().Select(item => item.GetProperty("code").GetString()!).ToList();

    private static void AssertNoSentinelLeak(string json, Guid? forbiddenAssetId)
    {
        foreach (var sentinel in new[]
                 {
                     SentinelStorageKey, SentinelAudioFileName, SentinelVideoFileName,
                     SentinelAudioAssetName, SentinelVideoAssetName, SentinelTeamWorkspaceName,
                 })
        {
            Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("storagekey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("originalfilename", json, StringComparison.OrdinalIgnoreCase);
        if (forbiddenAssetId is not null)
        {
            Assert.DoesNotContain(forbiddenAssetId.Value.ToString(), json, StringComparison.Ordinal);
        }
    }

    private static void AssertNoBoundaryMutation(
        (int Jobs, int Usage, int Credits, int Assets, int StoredFiles) before,
        (int Jobs, int Usage, int Credits, int Assets, int StoredFiles) after)
    {
        Assert.Equal(before.Jobs, after.Jobs);
        Assert.Equal(before.Usage, after.Usage);
        Assert.Equal(before.Credits, after.Credits);
        Assert.Equal(before.Assets, after.Assets);
        Assert.Equal(before.StoredFiles, after.StoredFiles);
    }

    private async Task<Guid> SeedAudioAssetAsync(
        Guid workspaceId,
        Guid userId,
        bool withBackingFile,
        StoredFileStatus fileStatus = StoredFileStatus.Ready)
    {
        Guid? storedFileId = null;
        if (withBackingFile)
        {
            storedFileId = await SeedStoredFileAsync(
                workspaceId, userId, "audio/mpeg", ".mp3", SentinelAudioFileName,
                containerFormat: "mp3", durationSeconds: 42, status: fileStatus);
        }

        return await SeedAssetAsync(
            workspaceId, userId, storedFileId, AssetTypes.Audio, "audio/mpeg",
            SentinelAudioAssetName, AssetStatus.Active);
    }

    private async Task<Guid> SeedVideoAssetAsync(Guid workspaceId, Guid userId)
    {
        var storedFileId = await SeedStoredFileAsync(
            workspaceId, userId, "video/mp4", ".mp4", SentinelVideoFileName,
            containerFormat: "mp4", durationSeconds: 77.5, status: StoredFileStatus.Ready);
        return await SeedAssetAsync(
            workspaceId, userId, storedFileId, AssetTypes.Video, "video/mp4",
            SentinelVideoAssetName, AssetStatus.Active);
    }

    private async Task<Guid> SeedStoredFileAsync(
        Guid workspaceId,
        Guid userId,
        string contentType,
        string extension,
        string fileName,
        string containerFormat,
        double durationSeconds,
        StoredFileStatus status)
    {
        var now = DateTime.UtcNow;
        var storedFileId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = workspaceId,
            UserId = userId,
            OriginalFileName = fileName,
            StoredFileName = fileName,
            ContentType = contentType,
            Extension = extension,
            SizeBytes = 4_000_000,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = workspaceId.ToString("N") + "/" + storedFileId.ToString("N") + "/" + SentinelStorageKey,
            Status = status,
            CreatedAt = now,
            ProcessedAt = now,
            TextExtractionStatus = FileExtractionStatus.NotApplicable,
            ContentHashSha256 = new string('B', 64),
            ContainerFormat = containerFormat,
            DurationSeconds = durationSeconds,
        });
        await db.SaveChangesAsync();
        return storedFileId;
    }

    private async Task<Guid> SeedAssetAsync(
        Guid workspaceId,
        Guid userId,
        Guid? storedFileId,
        string assetType,
        string mimeType,
        string name,
        AssetStatus status)
    {
        var now = DateTime.UtcNow;
        var assetId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            StoredFileId = storedFileId,
            Name = name,
            AssetType = assetType,
            MimeType = mimeType,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return assetId;
    }

    private async Task<(Guid WorkspaceId, Guid ProjectId)> SeedTeamWorkspaceAsync(Guid ownerId)
    {
        var now = DateTime.UtcNow;
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = SentinelTeamWorkspaceName,
            Slug = workspaceId.ToString("N"),
            Type = WorkspaceType.Business,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = ownerId,
            Role = WorkspaceRole.Owner,
            JoinedAt = now,
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            Name = SentinelTeamWorkspaceName,
            Type = ProjectTypes.General,
            Status = ProjectStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (workspaceId, projectId);
    }

    private async Task<(int Jobs, int Usage, int Credits, int Assets, int StoredFiles)> SnapshotSideEffectCountsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return (await db.GenerationJobs.CountAsync(), await db.UsageTransactions.CountAsync(),
            await db.CreditLedgerEntries.CountAsync(), await db.Assets.CountAsync(),
            await db.StoredFiles.CountAsync());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = "boundary-xws-" + Guid.NewGuid().ToString("N") + "@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
