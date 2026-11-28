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
/// Provider-free HTTP contracts for the Music Studio reference-selection boundary added for issue
/// #708 (acceptance criteria 3 and 4): the backend must associate the authorized workspace Asset ID
/// and the server must validate access. These tests go through the real pipeline - authentication,
/// the workspace fence, the shared asset visibility rules, the asset authorizer and the fail-closed
/// eligibility policy - and assert the user-facing guarantees that unit tests of the two joined
/// components cannot:
/// - privacy: a foreign account or a foreign workspace learns nothing, not even existence, and no
///   storage key, file name or content-derived text ever appears in a response;
/// - fail-closed reason codes: missing rights, ownership refusal, unsupported media and absent
///   audio metadata each surface as a stable code instead of a silent allow;
/// - no enablement: the boundary creates no job, no usage row, no credit entry and no asset, and
///   never names a provider, so it cannot admit anything to a paid generation path.
/// Nothing here performs a provider call, reads production data, or touches billing configuration.
/// </summary>
public sealed class MusicReferenceEligibilityApiTests : IClassFixture<GenerationJobsApiFactory>
{
    private const string SentinelStorageKey = "SENTINELPRIVATESTORAGEKEY";
    private const string SentinelFileName = "sentinel-private-file-name.mp3";
    private const string SentinelAssetName = "SENTINEL PRIVATE ASSET NAME";
    private const string SentinelWorkspaceName = "SENTINEL TEAM WORKSPACE";

    private readonly GenerationJobsApiFactory factory;

    public MusicReferenceEligibilityApiTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Reference_eligibility_requires_an_authenticated_session()
    {
        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/music-references/eligibility?workspaceId={Guid.NewGuid()}&assetId={Guid.NewGuid()}&mediaType=audio");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authorized_audio_reference_reports_verified_facts_and_stays_blocked_on_rights_and_provider()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Eligibility Owner");
        var assetId = await SeedReferenceAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, contentType: "audio/mpeg", containerFormat: "mp3", durationSeconds: 42);
        // Snapshot after seeding so the only permitted delta is the reference asset itself.
        var before = await SnapshotSideEffectCountsAsync();

        var body = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.Single(body.GetProperty("verifiedFacts").EnumerateArray());
        var fact = body.GetProperty("verifiedFacts")[0];
        Assert.Equal(assetId, Guid.Parse(fact.GetProperty("assetId").GetString()!));
        Assert.Equal("audio", fact.GetProperty("mediaType").GetString());
        Assert.Equal("audio/mpeg", fact.GetProperty("contentType").GetString());
        Assert.Equal("mp3", fact.GetProperty("containerFormat").GetString());
        Assert.Equal(42d, fact.GetProperty("durationSeconds").GetDouble());
        Assert.True(fact.GetProperty("audioTrackDetected").GetBoolean());

        // No attestation service and no verified reference-conditioning contract exist yet, so the
        // boundary must refuse rather than imply that a sung generation would now be accepted.
        Assert.False(body.GetProperty("isEligible").GetBoolean());
        var codes = BlockerCodes(body);
        Assert.Contains("MUSIC_REFERENCE_RIGHTS_MISSING", codes);
        Assert.Contains("MUSIC_REFERENCE_PROVIDER_CAPABILITY_MISSING", codes);
        Assert.DoesNotContain("MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE", codes);

        // Read-only by construction: no job, usage, credit or asset was created by the check.
        var after = await SnapshotSideEffectCountsAsync();
        Assert.Equal(before.Jobs, after.Jobs);
        Assert.Equal(before.Usage, after.Usage);
        Assert.Equal(before.Credits, after.Credits);
        Assert.Equal(before.Assets, after.Assets);
    }

    [Fact]
    public async Task Archived_reference_denies_with_its_own_reason_code()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Archived Owner");
        var assetId = await SeedReferenceAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, "audio/mpeg", "mp3", 30, status: AssetStatus.Archived);

        var body = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        Assert.Contains("MUSIC_REFERENCE_AUTH_ASSET_ARCHIVED", BlockerCodes(body));
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
    }

    [Fact]
    public async Task Reference_without_recorded_audio_metadata_denies_as_absent_metadata()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Unprobed Owner");
        // The server never recorded a container or a duration; the boundary must report that, not guess.
        var assetId = await SeedReferenceAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, "audio/mpeg", null, null);

        var body = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        var codes = BlockerCodes(body);
        Assert.Contains("MUSIC_REFERENCE_CONTAINER_UNSUPPORTED", codes);
        Assert.Contains("MUSIC_REFERENCE_DURATION_UNSUPPORTED", codes);
        Assert.True(RecordedDurationIsAbsent(body, assetId));
    }

    [Fact]
    public async Task Mismatched_media_claim_denies_without_leaking_the_recorded_kind()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Kind Owner");
        var assetId = await SeedReferenceAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, "video/mp4", "mp4", 60);

        // The client claims audio for a stored video.
        var body = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        Assert.Contains("MUSIC_REFERENCE_AUTH_MEDIA_KIND_MISMATCH", BlockerCodes(body));
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
    }

    [Fact]
    public async Task Another_account_cannot_resolve_a_private_reference_and_receives_no_detail()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Reference Private Owner");
        var assetId = await SeedReferenceAssetAsync(
            owner.PersonalWorkspace.Id, owner.User.Id, "audio/mpeg", "mp3", 42);

        using var otherClient = factory.CreateClient();
        var other = await Register(otherClient, "Reference Private Intruder");
        var body = await EvaluateAsync(otherClient, other.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        var codes = BlockerCodes(body);
        // One uniform refusal: unknown identifiers and foreign assets are indistinguishable.
        Assert.Contains("MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE", codes);
        Assert.DoesNotContain("MUSIC_REFERENCE_AUTH_ASSET_ARCHIVED", codes);
        Assert.DoesNotContain("MUSIC_REFERENCE_AUTH_WORKSPACE_MISMATCH", codes);
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
        AssertNoSentinelLeak(body, assetId);
    }

    [Fact]
    public async Task Another_workspace_of_the_same_member_cannot_reach_a_workspace_reference()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Two Workspace Owner");
        var assetId = await SeedReferenceAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, "audio/mpeg", "mp3", 42);

        var (teamWorkspaceId, _) = await SeedTeamWorkspaceAsync(auth.User.Id);
        var body = await EvaluateAsync(client, teamWorkspaceId, assetId.ToString(), "audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        // The asset is visible to this member of both workspaces, so the fence that answers is the
        // boundary's own workspace comparison - and it still discloses nothing about the asset.
        var codes = BlockerCodes(body);
        Assert.Contains("MUSIC_REFERENCE_AUTH_WORKSPACE_MISMATCH", codes);
        Assert.DoesNotContain("MUSIC_REFERENCE_AUTH_ASSET_ARCHIVED", codes);
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
        AssertNoSentinelLeak(body, assetId);
    }

    [Fact]
    public async Task Workspace_member_can_resolve_a_shared_project_reference_created_by_someone_else()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Reference Shared Owner");
        var (teamWorkspaceId, teamProjectId) = await SeedTeamWorkspaceAsync(owner.User.Id);
        var assetId = await SeedReferenceAssetAsync(
            teamWorkspaceId, owner.User.Id, "audio/mpeg", "mp3", 42, projectId: teamProjectId);

        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, "Reference Shared Member");
        await AddWorkspaceMemberAsync(teamWorkspaceId, member.User.Id);

        var body = await EvaluateAsync(memberClient, teamWorkspaceId, assetId.ToString(), "audio");

        // Membership grants resolution of the shared asset, so the access layer must stay silent.
        var codes = BlockerCodes(body);
        Assert.DoesNotContain("MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE", codes);
        Assert.Contains(assetId, VerifiedFactIds(body));
        Assert.False(body.GetProperty("isEligible").GetBoolean());
    }

    [Fact]
    public async Task Non_member_workspace_is_fenced_before_any_asset_lookup()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Reference Fence Owner");
        var (teamWorkspaceId, _) = await SeedTeamWorkspaceAsync(owner.User.Id);

        using var strangerClient = factory.CreateClient();
        await Register(strangerClient, "Reference Fence Stranger");
        var response = await strangerClient.GetAsync(
            $"/api/music-references/eligibility?workspaceId={teamWorkspaceId}&assetId={Guid.NewGuid()}&mediaType=audio");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MUSIC_REFERENCE_BOUNDARY_WORKSPACE_DENIED",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Declaring_references_without_asset_identifiers_is_refused_as_malformed()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Malformed Owner");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var noAssets = await client.GetAsync($"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, noAssets.StatusCode);

        var notGuid = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}&assetId=not-a-guid&mediaType=audio");
        Assert.Equal(HttpStatusCode.BadRequest, notGuid.StatusCode);
        var notGuidBody = await notGuid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MUSIC_REFERENCE_BOUNDARY_SELECTION_MALFORMED",
            notGuidBody.GetProperty("error").GetProperty("code").GetString());

        // Two identifiers with one declared kind never resolve: the server refuses instead of
        // guessing which asset a single claim was about.
        var misaligned = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}&assetId={first},{second}&mediaType=audio");
        Assert.Equal(HttpStatusCode.BadRequest, misaligned.StatusCode);

        var unknownIntent = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}&assetId={first}&mediaType=audio&intent=voice-cloning");
        Assert.Equal(HttpStatusCode.BadRequest, unknownIntent.StatusCode);
        var intentBody = await unknownIntent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MUSIC_REFERENCE_BOUNDARY_INTENT_UNSUPPORTED",
            intentBody.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Named_voice_imitation_intent_fails_closed_even_for_an_authorized_reference()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Voice Owner");
        var assetId = await SeedReferenceAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, "audio/mpeg", "mp3", 42);

        // The asset itself authorizes fine, so the refusal must come from the voice right-to-use
        // layer: with no right-to-use record service there is nothing to verify, and an imitation
        // intent is denied loudly instead of being quietly downgraded to style conditioning.
        var body = await EvaluateAsync(
            client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio", intent: "named-voice-imitation");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        var codes = BlockerCodes(body);
        Assert.Contains("MUSIC_REFERENCE_VOICE_RIGHT_MISSING", codes);
        Assert.DoesNotContain("MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE", codes);
    }

    [Fact]
    public async Task Selection_beyond_the_structural_ceiling_is_refused_without_any_lookup()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Ceiling Owner");
        var ids = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList();

        var response = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}"
            + $"&assetId={string.Join(',', ids)}&mediaType={string.Join(',', Enumerable.Repeat("audio", ids.Count))}");

        // An oversized list is rejected at the transport edge, so the request never reaches the
        // asset library and no identifier is individually probed for existence.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MUSIC_REFERENCE_BOUNDARY_SELECTION_TOO_LARGE",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Response_never_names_a_provider_or_exposes_storage_detail()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Honesty Owner");
        var assetId = await SeedReferenceAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, "audio/mpeg", "mp3", 42);

        var response = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}&assetId={assetId}&mediaType=audio");
        var raw = await response.Content.ReadAsStringAsync();

        foreach (var forbidden in new[] { "suno", "mubert", "stable", "providerkey", "storagekey", "originalfilename", "credit", "cost" })
        {
            Assert.False(raw.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task<JsonElement> EvaluateAsync(
        HttpClient client, Guid workspaceId, string assetIds, string mediaTypes, string? intent = null)
    {
        var path = $"/api/music-references/eligibility?workspaceId={workspaceId}&assetId={assetIds}&mediaType={mediaTypes}";
        if (intent is not null)
        {
            path += "&intent=" + intent;
        }

        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IReadOnlyList<string> BlockerCodes(JsonElement body) =>
        body.GetProperty("blockers").EnumerateArray().Select(item => item.GetProperty("code").GetString()!).ToList();

    private static IReadOnlyList<Guid> VerifiedFactIds(JsonElement body) =>
        body.GetProperty("verifiedFacts").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("assetId").GetString()!)).ToList();

    private static bool RecordedDurationIsAbsent(JsonElement body, Guid assetId) =>
        body.GetProperty("verifiedFacts").EnumerateArray().Any(item =>
            Guid.Parse(item.GetProperty("assetId").GetString()!) == assetId
            && item.TryGetProperty("durationSeconds", out var duration)
            && duration.ValueKind == JsonValueKind.Null);

    private static void AssertNoSentinelLeak(JsonElement body, Guid hiddenAssetId)
    {
        var raw = body.ToString();
        // A refusal must not confirm which asset was addressed, let alone how it is stored.
        Assert.DoesNotContain(SentinelStorageKey, raw);
        Assert.DoesNotContain(SentinelFileName, raw);
        Assert.DoesNotContain(SentinelAssetName, raw);
        Assert.DoesNotContain(SentinelWorkspaceName, raw);
        Assert.DoesNotContain("storagekey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hiddenAssetId.ToString(), raw);
    }

    private async Task<Guid> SeedReferenceAssetAsync(
        Guid workspaceId,
        Guid userId,
        string contentType,
        string? containerFormat,
        double? durationSeconds,
        AssetStatus status = AssetStatus.Active,
        Guid? projectId = null)
    {
        var now = DateTime.UtcNow;
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = workspaceId,
                UserId = userId,
                ProjectId = projectId,
                OriginalFileName = SentinelFileName,
                StoredFileName = SentinelFileName,
                ContentType = contentType,
                Extension = contentType.StartsWith("video/", StringComparison.Ordinal) ? ".mp4" : ".mp3",
                SizeBytes = 4_000_000,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = workspaceId.ToString("N") + "/" + storedFileId.ToString("N") + "/" + SentinelStorageKey,
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                ContentHashSha256 = new string('A', 64),
                ContainerFormat = containerFormat,
                DurationSeconds = durationSeconds,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                CreatedByUserId = userId,
                StoredFileId = storedFileId,
                Name = SentinelAssetName,
                AssetType = contentType.StartsWith("video/", StringComparison.Ordinal) ? AssetTypes.Video : AssetTypes.Audio,
                MimeType = contentType,
                Status = status,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        return assetId;
    }

    private async Task<(Guid WorkspaceId, Guid ProjectId)> SeedTeamWorkspaceAsync(Guid ownerId)
    {
        var now = DateTime.UtcNow;
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = SentinelWorkspaceName,
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
                Name = SentinelWorkspaceName,
                Type = ProjectTypes.General,
                Status = ProjectStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        return (workspaceId, projectId);
    }

    private async Task AddWorkspaceMemberAsync(Guid workspaceId, Guid userId)
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

    private async Task<(int Jobs, int Usage, int Credits, int Assets)> SnapshotSideEffectCountsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return (await db.GenerationJobs.CountAsync(), await db.UsageTransactions.CountAsync(),
            await db.CreditLedgerEntries.CountAsync(), await db.Assets.CountAsync());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = "music-reference-" + Guid.NewGuid().ToString("N") + "@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
