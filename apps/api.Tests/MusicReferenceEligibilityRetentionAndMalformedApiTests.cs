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
/// Issue #708 QA lane (test-only): the <em>expired</em> and <em>malformed</em> reference axes of the
/// read-only reference authorization/eligibility route joined on #815 (built on the #805 authorizer
/// and the #776 policy), asserted through the real HTTP pipeline. The acceptance sentence this lane
/// verifies is: an audio or video reference becomes usable only after server-side authorization, and
/// a cross-workspace, deleted, expired or malformed reference must fail closed.
///
/// Coverage audit against the exact heads of the owner slices (#805 `c97e5890`, #776 `7cfd443f`,
/// #815 `163f7a6c`, #1116 `a02a29cd`) - what was already proven elsewhere and is therefore NOT
/// repeated here:
/// <list type="bullet">
/// <item>cross-workspace audio and video, mixed owned/foreign selections, and the retention-purged
/// (<c>StoredFileStatus.Deleted</c>) or missing file row: #815 and #1116;</item>
/// <item>archived asset, media-kind claim mismatch, absent audio metadata, rights/provider refusal,
/// non-member fencing, oversized lists, a missing identifier list, an unknown intent token and
/// non-identifier text in <c>assetId</c>: #815;</item>
/// <item>malformed selectors (blank, <c>midi</c>, <c>null</c>, <see cref="Guid"/>.Empty, duplicated)
/// and their aggregation: at the #805 unit-fake level only;</item>
/// <item>the eligibility decision over hand-built evidence, including defaulted and misconfigured
/// evidence: the #776 unit suite.</item>
/// </list>
/// What this lane adds, at the boundary where a user's selection actually lands:
/// <list type="bullet">
/// <item><b>Retention expiry.</b> <see cref="StoredFile"/> carries two server-recorded facts that no
/// part of this path currently consults: <c>RetainUntil</c> (the retention window) and
/// <c>DeletedAt</c> (the recorded deletion). #1116 proves the <em>post-purge</em> row
/// (<c>Status = Deleted</c>) is refused; the <em>pre-purge</em> row - retention elapsed, or a
/// deletion already recorded, while the status field still says <c>Ready</c> - has never been
/// exercised over HTTP. Issue #708 requires those references to fail closed, so they are asserted
/// here as refusals that name no asset, leak no storage detail and change nothing. The positive
/// controls in the same class (an open window, and a row with no retention deadline at all) pin that
/// the answer must stay precise: expiry is a recorded fact, so "not on record" is not "expired".</item>
/// <item><b>Disclosure discipline around the expiry axis.</b> A stranger's answer for an
/// expired-retention asset must stay the uniform not-available refusal, so closing the expiry gap
/// cannot quietly turn the route into a probe that reveals another workspace's retention state.</item>
/// <item><b>Malformed selectors over HTTP.</b> The duplicated, empty-identifier and unknown-kind
/// refusals exist only as unit-fake results today; these are the first requests that prove the route
/// itself aggregates them, resolves the healthy half of a mixed selection, and never lets one
/// malformed entry blanket or silently drop the others - and that none of it reaches a provider,
/// a job, a usage row or a credit entry.</item>
/// </list>
/// Everything is synthetic: no real audio or video, no network, no storage, no provider adapter, no
/// Suno/Mubert/Stable Audio call, no generation dispatch, no billing movement, no schema change, no
/// secret, no activation of sung generation. The two expiry expectations are written as acceptance
/// outcomes (refused at the access layer) rather than as a demand for one specific new reason code:
/// the <c>MUSIC_REFERENCE_AUTH_*</c> vocabulary belongs to the #805 resolver, and naming a code here
/// would put this test lane in charge of a decision it does not own.
/// </summary>
public sealed class MusicReferenceEligibilityRetentionAndMalformedApiTests : IClassFixture<GenerationJobsApiFactory>
{
    private const string SentinelStorageKey = "SENTINELRETENTIONSTORAGEKEY";
    private const string SentinelAudioFileName = "sentinel-retention-audio.mp3";
    private const string SentinelAudioAssetName = "SENTINEL RETENTION AUDIO ASSET";
    private const string SentinelTeamWorkspaceName = "SENTINEL RETENTION TEAM WORKSPACE";

    // Existing, owner-declared codes. Used only to describe what the boundary already says.
    private const string AuthAssetNotAvailable = "MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE";
    private const string AuthSelectionInvalid = "MUSIC_REFERENCE_AUTH_SELECTION_INVALID";
    private const string AuthSelectionDuplicated = "MUSIC_REFERENCE_AUTH_SELECTION_DUPLICATED";
    private const string BoundarySelectionMalformed = "MUSIC_REFERENCE_BOUNDARY_SELECTION_MALFORMED";
    private const string RightsMissing = "MUSIC_REFERENCE_RIGHTS_MISSING";
    private const string ProviderCapabilityMissing = "MUSIC_REFERENCE_PROVIDER_CAPABILITY_MISSING";

    private readonly GenerationJobsApiFactory factory;

    public MusicReferenceEligibilityRetentionAndMalformedApiTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // ---------------------------------------------------------------------------------------------
    // Axis 1 - retention expiry: a reference whose retention window has elapsed is not a usable
    // reference, and a reference whose deletion the server already recorded is neither.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retention_window_elapsed_reference_is_refused_at_the_access_layer()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Elapsed Owner");
        // The status field still says Ready: what changed is the server's own retention deadline.
        // #1116 proves the post-purge row is refused; this is the pre-purge row, which issue #708
        // requires to fail closed as well.
        var assetId = await SeedAudioAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id,
            retainUntil: DateTime.UtcNow.AddDays(-2), deletedAt: null);
        var before = await SnapshotSideEffectCountsAsync();

        var (raw, body) = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.False(body.GetProperty("isEligible").GetBoolean());
        Assert.True(
            BlockerCodes(body).Any(code => code.StartsWith("MUSIC_REFERENCE_AUTH_", StringComparison.Ordinal)),
            $"An expired reference must be refused by the server-side authorization path, but the boundary "
            + $"answered only with: {string.Join(", ", BlockerCodes(body))}. Nothing in #805's authorizer, "
            + "the #815 boundary or #776's policy consults StoredFile.RetainUntil, so a reference whose "
            + "retention window has elapsed is still reported as resolved.");
        Assert.True(
            body.GetProperty("verifiedFacts").GetArrayLength() == 0,
            $"A reference past its recorded retention deadline must not be returned as verified server "
            + $"facts; the response carried {body.GetProperty("verifiedFacts").GetArrayLength()} fact(s).");
        Assert.DoesNotContain(assetId, VerifiedFactIds(body));

        // The refusal may not become a disclosure channel: no storage key, no recorded file name,
        // no retention timestamp of its own.
        AssertNoSentinelLeak(body.ToString(), forbiddenAssetId: null);

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    [Fact]
    public async Task Retention_window_still_open_reference_keeps_resolving()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Open Owner");
        // The positive control: an in-window reference must keep resolving, so closing the expiry gap
        // above cannot be done by refusing every row that carries a retention field.
        var assetId = await SeedAudioAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id,
            retainUntil: DateTime.UtcNow.AddDays(30), deletedAt: null);

        var (_, body) = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.Contains(assetId, VerifiedFactIds(body));
        var codes = BlockerCodes(body);
        Assert.DoesNotContain(codes, code => code.StartsWith("MUSIC_REFERENCE_AUTH_", StringComparison.Ordinal));
        // It is still not eligible: rights attestation and a verified provider contract are absent.
        Assert.False(body.GetProperty("isEligible").GetBoolean());
        Assert.Contains(RightsMissing, codes);
        Assert.Contains(ProviderCapabilityMissing, codes);
    }

    [Fact]
    public async Task Reference_with_no_recorded_retention_deadline_is_not_treated_as_expired()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Unrecorded Owner");
        // Upload-time rows carry no retention deadline at all. Per the owner convention "null means
        // not on record", the boundary must not read that absence as an expiry - that would refuse
        // the whole reference library instead of the expired rows in it.
        var assetId = await SeedAudioAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id, retainUntil: null, deletedAt: null);

        var (_, body) = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.Contains(assetId, VerifiedFactIds(body));
        Assert.DoesNotContain(
            BlockerCodes(body), code => code.StartsWith("MUSIC_REFERENCE_AUTH_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reference_whose_deletion_the_server_already_recorded_is_refused_even_while_status_says_ready()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention DeletedFlag Owner");
        // A deletion recorded on the server's own row while the status field still claims Ready is a
        // contradiction, and #776's stated convention is that contradictions deny instead of picking a
        // side. #1116 covers Status = Deleted; this is the other half of the same row.
        var assetId = await SeedAudioAssetAsync(
            auth.PersonalWorkspace.Id, auth.User.Id,
            retainUntil: DateTime.UtcNow.AddDays(30), deletedAt: DateTime.UtcNow.AddDays(-1));
        var before = await SnapshotSideEffectCountsAsync();

        var (_, body) = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        Assert.True(
            body.GetProperty("verifiedFacts").GetArrayLength() == 0
            && BlockerCodes(body).Any(code => code.StartsWith("MUSIC_REFERENCE_AUTH_", StringComparison.Ordinal)),
            $"A reference whose deletion is already recorded on the stored-file row must be refused at the "
            + $"access layer; the boundary instead resolved it and answered: {string.Join(", ", BlockerCodes(body))}.");

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    [Fact]
    public async Task Stranger_cannot_probe_a_references_retention_state()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, "Retention Probe Owner");
        var expiredAssetId = await SeedAudioAssetAsync(
            owner.PersonalWorkspace.Id, owner.User.Id,
            retainUntil: DateTime.UtcNow.AddDays(-2), deletedAt: null);

        using var strangerClient = factory.CreateClient();
        var stranger = await Register(strangerClient, "Retention Probe Intruder");
        var unknownAssetId = Guid.NewGuid();

        var (_, expiredAnswer) = await EvaluateAsync(
            strangerClient, stranger.PersonalWorkspace.Id, expiredAssetId.ToString(), "audio");
        var (_, unknownAnswer) = await EvaluateAsync(
            strangerClient, stranger.PersonalWorkspace.Id, unknownAssetId.ToString(), "audio");

        // The same uniform refusal either way: closing the expiry gap must not let a stranger learn
        // that an addressed asset exists and simply ran out of retention.
        Assert.Equal(new[] { AuthAssetNotAvailable }, BlockerCodes(expiredAnswer));
        Assert.Equal(new[] { AuthAssetNotAvailable }, BlockerCodes(unknownAnswer));
        Assert.Equal(0, expiredAnswer.GetProperty("verifiedFacts").GetArrayLength());
        AssertNoSentinelLeak(expiredAnswer.ToString(), forbiddenAssetId: expiredAssetId);
    }

    // ---------------------------------------------------------------------------------------------
    // Axis 2 - malformed selectors: the shapes #805 proves only against its unit fake, now proven on
    // the route a user's selection actually reaches, including aggregation across a mixed selection.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_identifier_resolves_once_and_is_refused_once()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Duplicate Owner");
        var assetId = await SeedAudioAssetAsync(auth.PersonalWorkspace.Id, auth.User.Id, null, null);
        var before = await SnapshotSideEffectCountsAsync();

        var (_, body) = await EvaluateAsync(
            client, auth.PersonalWorkspace.Id, $"{assetId},{assetId}", "audio,audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        var codes = BlockerCodes(body);
        Assert.Contains(AuthSelectionDuplicated, codes);
        // The first declaration is a real selection and is still answered on its own evidence, so the
        // duplicate refusal cannot be used to hide it.
        Assert.Equal(new[] { assetId }, VerifiedFactIds(body));
        Assert.Contains(RightsMissing, codes);
        Assert.DoesNotContain(AuthAssetNotAvailable, codes);

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    [Fact]
    public async Task Empty_identifier_is_refused_as_an_invalid_selector_without_a_lookup()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Empty Id Owner");
        // A well-formed all-zero identifier parses at the transport edge, so the refusal must come
        // from the authorizer's own selector rule rather than from a lookup miss.
        var (_, body) = await EvaluateAsync(
            client, auth.PersonalWorkspace.Id, Guid.Empty.ToString(), "audio");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        // Exactly the selector refusal: nothing was judged, so nothing was invented on top of it.
        Assert.Equal(new[] { AuthSelectionInvalid }, BlockerCodes(body));
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
        var blocker = body.GetProperty("blockers")[0];
        AssertRefusalNamesNoAsset(blocker);
        var message = blocker.GetProperty("message").GetString()!;
        Assert.DoesNotContain("/", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_kind_token_on_an_owned_asset_is_refused_without_disclosing_the_recorded_kind()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Bad Kind Owner");
        var assetId = await SeedAudioAssetAsync(auth.PersonalWorkspace.Id, auth.User.Id, null, null);

        // The asset is plainly readable by this member; the malformed half is the declared kind. The
        // boundary must refuse the selector without confirming or denying what the asset really is.
        var (raw, body) = await EvaluateAsync(client, auth.PersonalWorkspace.Id, assetId.ToString(), "midi");

        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.False(body.GetProperty("isEligible").GetBoolean());
        Assert.Equal(new[] { AuthSelectionInvalid }, BlockerCodes(body));
        Assert.Equal(0, body.GetProperty("verifiedFacts").GetArrayLength());
        var json = body.ToString();
        Assert.DoesNotContain("audio/mpeg", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".mp3", json, StringComparison.OrdinalIgnoreCase);
        AssertNoSentinelLeak(json, forbiddenAssetId: null);
    }

    [Fact]
    public async Task Kind_list_that_does_not_line_up_with_the_identifiers_is_refused_before_any_lookup()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Misaligned Owner");
        var assetId = await SeedAudioAssetAsync(auth.PersonalWorkspace.Id, auth.User.Id, null, null);
        var before = await SnapshotSideEffectCountsAsync();

        // More kind claims than identifiers - the mirror of the case #815 already proves.
        var tooManyKinds = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}"
            + $"&assetId={assetId}&mediaType=audio,video");
        Assert.Equal(HttpStatusCode.BadRequest, tooManyKinds.StatusCode);

        // A kind list whose entries are all empty must not be read as "no kind declared for a
        // selection that is otherwise fine"; the request is refused instead of guessed.
        var blankKinds = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={auth.PersonalWorkspace.Id}"
            + $"&assetId={assetId}&mediaType=,");
        Assert.Equal(HttpStatusCode.BadRequest, blankKinds.StatusCode);
        var body = await blankKinds.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(BoundarySelectionMalformed, body.GetProperty("error").GetProperty("code").GetString());

        // A transport-edge refusal must not echo the identifier it refused to look up.
        Assert.DoesNotContain(assetId.ToString(), body.ToString(), StringComparison.Ordinal);

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    [Fact]
    public async Task One_malformed_selection_neither_blankets_nor_silently_drops_the_healthy_reference()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Retention Aggregation Owner");
        var healthyId = await SeedAudioAssetAsync(auth.PersonalWorkspace.Id, auth.User.Id, null, null);
        var unknownId = Guid.NewGuid();
        var badKindId = await SeedAudioAssetAsync(auth.PersonalWorkspace.Id, auth.User.Id, null, null);
        var before = await SnapshotSideEffectCountsAsync();

        var (_, body) = await EvaluateAsync(
            client, auth.PersonalWorkspace.Id,
            $"{healthyId},{unknownId},{badKindId}", "audio,audio,midi");

        Assert.False(body.GetProperty("isEligible").GetBoolean());
        var codes = BlockerCodes(body);
        // All three answers arrive in one pass: the unknown identifier, the malformed selector, and
        // the eligibility layers that judged the healthy reference.
        Assert.Contains(AuthAssetNotAvailable, codes);
        Assert.Contains(AuthSelectionInvalid, codes);
        Assert.Contains(RightsMissing, codes);
        Assert.Contains(ProviderCapabilityMissing, codes);
        // Only the healthy reference is confirmed, and it is confirmed exactly once.
        Assert.Equal(new[] { healthyId }, VerifiedFactIds(body));

        var raw = body.ToString();
        Assert.DoesNotContain(unknownId.ToString(), raw, StringComparison.Ordinal);
        Assert.DoesNotContain(badKindId.ToString(), raw, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "suno", "mubert", "stable", "providerkey", "storagekey", "originalfilename" })
        {
            Assert.False(raw.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        var after = await SnapshotSideEffectCountsAsync();
        AssertNoBoundaryMutation(before, after);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers: same shapes as the #815 and #1116 suites, with the retention fields this lane needs.
    // ---------------------------------------------------------------------------------------------

    private async Task<(HttpResponseMessage Raw, JsonElement Body)> EvaluateAsync(
        HttpClient client, Guid workspaceId, string assetIds, string mediaTypes)
    {
        var response = await client.GetAsync(
            $"/api/music-references/eligibility?workspaceId={workspaceId}&assetId={assetIds}&mediaType={mediaTypes}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static IReadOnlyList<string> BlockerCodes(JsonElement body) =>
        body.GetProperty("blockers").EnumerateArray().Select(item => item.GetProperty("code").GetString()!).ToList();

    private static IReadOnlyList<Guid> VerifiedFactIds(JsonElement body) =>
        body.GetProperty("verifiedFacts").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("assetId").GetString()!)).ToList();

    /// <summary>An access-layer refusal that names no asset and carries no path detail.</summary>
    private static void AssertRefusalNamesNoAsset(JsonElement blocker)
    {
        if (blocker.TryGetProperty("assetId", out var assetId))
        {
            Assert.Equal(JsonValueKind.Null, assetId.ValueKind);
        }
    }

    private static void AssertNoSentinelLeak(string json, Guid? forbiddenAssetId)
    {
        foreach (var sentinel in new[]
                 {
                     SentinelStorageKey, SentinelAudioFileName, SentinelAudioAssetName, SentinelTeamWorkspaceName,
                 })
        {
            Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("storagekey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("originalfilename", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retainuntil", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deletedat", json, StringComparison.OrdinalIgnoreCase);
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
        DateTime? retainUntil,
        DateTime? deletedAt)
    {
        var now = DateTime.UtcNow;
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = workspaceId,
            UserId = userId,
            OriginalFileName = SentinelAudioFileName,
            StoredFileName = SentinelAudioFileName,
            ContentType = "audio/mpeg",
            Extension = ".mp3",
            SizeBytes = 4_000_000,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = workspaceId.ToString("N") + "/" + storedFileId.ToString("N") + "/" + SentinelStorageKey,
            Status = StoredFileStatus.Ready,
            CreatedAt = now,
            ProcessedAt = now,
            TextExtractionStatus = FileExtractionStatus.NotApplicable,
            // StoredFiles carries a unique (WorkspaceId, ContentHashSha256) index, so the synthetic
            // content fingerprint is derived from the row it describes instead of being one constant
            // shared by every seeded asset in a workspace.
            ContentHashSha256 = storedFileId.ToString("N") + new string('C', 32),
            ContainerFormat = "mp3",
            DurationSeconds = 42,
            RetainUntil = retainUntil,
            DeletedAt = deletedAt,
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            StoredFileId = storedFileId,
            Name = SentinelAudioAssetName,
            AssetType = AssetTypes.Audio,
            MimeType = "audio/mpeg",
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return assetId;
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
            email = "retention-malformed-" + Guid.NewGuid().ToString("N") + "@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
