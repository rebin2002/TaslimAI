using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Assets;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Deterministic unit contracts for the Music Studio reference asset authorization path
// (issue #708 criterion 3). Provider-free by construction: no web factory, no database, no
// network, no file bytes, no provider — the authorizer only ever sees the shared asset service
// and the server-recorded rows behind it, so selecting a reference cannot start a paid call.
// The fake asset service stands in for the shared visibility + membership rules exactly as
// AssetService applies them, and asserts the authorizer routes ownership through it.
public sealed class MusicReferenceAssetAuthorizationTests
{
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AudioAssetId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid VideoAssetId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static Asset MakeAsset(
        Guid assetId,
        Guid workspaceId,
        string assetMimeType,
        StoredFile? file,
        AssetStatus status = AssetStatus.Active,
        string assetType = AssetTypes.Music) => new()
    {
        Id = assetId,
        WorkspaceId = workspaceId,
        CreatedByUserId = OwnerId,
        AssetType = assetType,
        MimeType = assetMimeType,
        Status = status,
        Name = "Reference",
        StoredFile = file,
    };

    private static StoredFile MakeFile(
        Guid workspaceId,
        string contentType,
        long sizeBytes = 4_000_000,
        StoredFileStatus status = StoredFileStatus.Ready,
        string? containerFormat = null,
        double? durationSeconds = null,
        DateTime? retainUntil = null,
        DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = OwnerId,
        ContentType = contentType,
        SizeBytes = sizeBytes,
        Status = status,
        ContainerFormat = containerFormat,
        DurationSeconds = durationSeconds,
        // Server-recorded retention posture. Both default to null, which means "not on record"
        // (the upload-time shape), never "expired".
        RetainUntil = retainUntil,
        DeletedAt = deletedAt,
        // Sentinel storage details: if any of these ever reaches an outcome, the leakage test fails.
        StorageKey = "SENTINEL-STORAGE-KEY",
        StoredFileName = "SENTINEL-STORED-NAME",
        OriginalFileName = "SENTINEL-ORIGINAL-NAME.mp3",
    };

    private sealed class FakeAssetService : IAssetService
    {
        public Dictionary<Guid, Asset> Assets { get; } = [];
        public List<(Guid UserId, Guid AssetId)> Lookups { get; } = [];

        public Task<Asset?> GetAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
        {
            Lookups.Add((userId, assetId));
            return Task.FromResult(Assets.TryGetValue(assetId, out var asset) ? asset : null);
        }

        public Task<AssetListDto?> ListAsync(Guid userId, AssetFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Asset?> UpdateAsync(Guid userId, Guid assetId, UpdateAssetRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Asset?> ArchiveAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Asset?> RestoreAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssetDownload?> GetDownloadAsync(Guid userId, Guid assetId, Guid? representationId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static (MusicReferenceAssetAuthorizer Authorizer, FakeAssetService Library) Build(params (Guid Id, Asset Asset)[] assets)
    {
        var library = new FakeAssetService();
        foreach (var (id, asset) in assets) library.Assets[id] = asset;
        return (new MusicReferenceAssetAuthorizer(library), library);
    }

    private static MusicReferenceAssetSelection Audio(Guid assetId) => new("audio", assetId);
    private static MusicReferenceAssetSelection Video(Guid assetId) => new("video", assetId);

    private static Asset OwnedAudioFile() => MakeAsset(AudioAssetId, WorkspaceA, "audio/mpeg", MakeFile(WorkspaceA, "audio/mpeg"));
    private static Asset OwnedVideoFile() => MakeAsset(VideoAssetId, WorkspaceA, "video/mp4", MakeFile(WorkspaceA, "video/mp4", containerFormat: "MP4", durationSeconds: 42.5));

    [Fact]
    public async Task Authorized_audio_asset_returns_server_recorded_facts_and_names_the_verifying_service()
    {
        var (authorizer, library) = Build((AudioAssetId, OwnedAudioFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.True(outcome.AllAuthorized);
        Assert.Empty(outcome.Denials);
        var record = Assert.Single(outcome.Assets);
        Assert.Equal(AudioAssetId, record.AssetId);
        Assert.Equal(WorkspaceA, record.WorkspaceId);
        Assert.Equal("audio", record.RequestedMediaType);
        Assert.Equal("audio/mpeg", record.ContentType);
        Assert.Equal(4_000_000, record.SizeBytes);
        Assert.Equal("music_reference_asset_authorization", record.VerifiedByService);
        // Ownership was decided by the shared asset service, not by a private rule.
        Assert.Equal(new[] { (OwnerId, AudioAssetId) }, library.Lookups);
    }

    [Fact]
    public async Task Authorizer_never_invents_facts_the_server_never_recorded()
    {
        var (authorizer, _) = Build((AudioAssetId, OwnedAudioFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        var record = Assert.Single(outcome.Assets);
        Assert.Null(record.ContainerFormat);
        Assert.Null(record.DurationSeconds);
        Assert.True(record.AudioTrackDetected);
    }

    [Fact]
    public async Task Video_reference_keeps_audio_track_unknown_until_a_real_probe_records_it()
    {
        var (authorizer, _) = Build((VideoAssetId, OwnedVideoFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Video(VideoAssetId)]);

        Assert.True(outcome.AllAuthorized);
        var record = Assert.Single(outcome.Assets);
        Assert.Equal("mp4", record.ContainerFormat);
        Assert.Equal(42.5, record.DurationSeconds);
        Assert.Null(record.AudioTrackDetected);
    }

    [Fact]
    public async Task Pasted_asset_id_without_access_denies_instead_of_fabricating_evidence()
    {
        var (authorizer, library) = Build();
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.False(outcome.AllAuthorized);
        Assert.Empty(outcome.Assets);
        Assert.Single(outcome.Denials);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.AssetNotAvailable, outcome.Denials[0].Code);
        Assert.Single(library.Lookups);
    }

    [Fact]
    public async Task Asset_visible_to_the_user_but_belonging_to_another_workspace_denies()
    {
        // The shared asset service may return assets from any workspace the user belongs to;
        // reference use must still be fenced to the workspace making the request.
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceB, "audio/mpeg", MakeFile(WorkspaceB, "audio/mpeg"))));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.False(outcome.AllAuthorized);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.WorkspaceMismatch, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Archived_asset_denies()
    {
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceA, "audio/mpeg", MakeFile(WorkspaceA, "audio/mpeg"), status: AssetStatus.Archived)));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.AssetArchived, Assert.Single(outcome.Denials).Code);
    }

    [Theory]
    [InlineData(StoredFileStatus.Uploading)]
    [InlineData(StoredFileStatus.Processing)]
    [InlineData(StoredFileStatus.Failed)]
    [InlineData(StoredFileStatus.Deleted)]
    public async Task Stored_file_that_is_not_ready_denies(StoredFileStatus status)
    {
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceA, "audio/mpeg", MakeFile(WorkspaceA, "audio/mpeg", status: status))));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileUnavailable, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Asset_without_backing_file_row_denies()
    {
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceA, "audio/mpeg", file: null)));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileUnavailable, Assert.Single(outcome.Denials).Code);
    }

    // -----------------------------------------------------------------------------------------
    // Retention posture (#708 criterion 3, as exercised by the #1152 QA lane): the stored-file
    // row carries RetainUntil and DeletedAt, and both outrank a status field that still says
    // Ready. A deadline that is simply not on record outranks nothing.
    // -----------------------------------------------------------------------------------------

    private static Asset AudioFileWith(DateTime? retainUntil, DateTime? deletedAt) => MakeAsset(
        AudioAssetId, WorkspaceA, "audio/mpeg",
        MakeFile(WorkspaceA, "audio/mpeg", retainUntil: retainUntil, deletedAt: deletedAt));

    [Fact]
    public async Task Expired_retention_window_denies_at_the_access_layer_even_while_status_still_says_ready()
    {
        // The pre-purge row #1152 found unexercised: only the server's own deadline has moved.
        var (authorizer, _) = Build((AudioAssetId, AudioFileWith(retainUntil: DateTime.UtcNow.AddDays(-2), deletedAt: null)));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.False(outcome.AllAuthorized);
        Assert.Empty(outcome.Assets);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileRetentionExpired, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Retention_deadline_that_has_just_arrived_denies_rather_than_rounding_to_usable()
    {
        // Fail-closed at the boundary: the deadline is inclusive, so a row that has reached its
        // recorded instant is refused instead of being admitted for one more clock tick.
        var (authorizer, _) = Build((AudioAssetId, AudioFileWith(retainUntil: DateTime.UtcNow, deletedAt: null)));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileRetentionExpired, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Recorded_deletion_denies_even_while_status_still_says_ready()
    {
        // A contradiction between the server's own fields denies rather than picks a side: the
        // deletion is recorded, so the Ready status cannot rehabilitate the row (#1152 axis 1b).
        var (authorizer, _) = Build((AudioAssetId, AudioFileWith(retainUntil: DateTime.UtcNow.AddDays(30), deletedAt: DateTime.UtcNow.AddDays(-1))));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.False(outcome.AllAuthorized);
        Assert.Empty(outcome.Assets);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileDeletionRecorded, Assert.Single(outcome.Denials).Code);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(1)]
    public async Task Retention_window_still_open_keeps_resolving(int daysAhead)
    {
        // Positive control: closing the expiry gap must not become "refuse every row that carries
        // a retention field".
        var (authorizer, _) = Build((AudioAssetId, AudioFileWith(retainUntil: DateTime.UtcNow.AddDays(daysAhead), deletedAt: null)));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.True(outcome.AllAuthorized);
        Assert.Empty(outcome.Denials);
        Assert.Equal(AudioAssetId, Assert.Single(outcome.Assets).AssetId);
    }

    [Fact]
    public async Task Absence_of_a_recorded_retention_deadline_is_not_read_as_expiry()
    {
        // Upload-time rows carry neither field. Treating "not on record" as expired would deny the
        // whole reference library instead of the expired rows inside it.
        var (authorizer, _) = Build((AudioAssetId, AudioFileWith(retainUntil: null, deletedAt: null)));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.True(outcome.AllAuthorized);
        Assert.DoesNotContain(outcome.Denials, d => d.Code.StartsWith("MUSIC_REFERENCE_AUTH_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Purged_row_still_denies_as_file_unavailable_so_the_deletion_axes_have_one_owner()
    {
        // #1116's contract depends on this ordering: Status = Deleted answers with
        // MUSIC_REFERENCE_AUTH_FILE_UNAVAILABLE even when the same row also carries RetainUntil and
        // DeletedAt, so the post-purge and pre-purge rows cannot drift into different codes.
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceA, "audio/mpeg", MakeFile(
            WorkspaceA, "audio/mpeg",
            status: StoredFileStatus.Deleted,
            retainUntil: DateTime.UtcNow.AddDays(-2),
            deletedAt: DateTime.UtcNow.AddDays(-1)))));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileUnavailable, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Mixed_selection_resolves_the_healthy_half_and_refuses_only_the_expired_reference()
    {
        // One expired reference must not blanket-refuse a selection, and must not be silently
        // dropped: the aggregation stays per-asset, matching this service's partial-success contract.
        var (authorizer, _) = Build(
            (AudioAssetId, AudioFileWith(retainUntil: DateTime.UtcNow.AddDays(-5), deletedAt: null)),
            (VideoAssetId, OwnedVideoFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId), Video(VideoAssetId)]);

        Assert.False(outcome.AllAuthorized);
        Assert.Equal(VideoAssetId, Assert.Single(outcome.Assets).AssetId);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileRetentionExpired, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Expired_reference_in_another_workspace_still_answers_with_the_ownership_fence_first()
    {
        // The refusal stays non-disclosing: a stranger learns "another workspace", never that the
        // row is expired, so closing the expiry gap cannot turn this path into a retention probe.
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceB, "audio/mpeg", MakeFile(
            WorkspaceB, "audio/mpeg", retainUntil: DateTime.UtcNow.AddDays(-2), deletedAt: DateTime.UtcNow.AddDays(-1)))));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.WorkspaceMismatch, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Retention_denials_disclose_no_asset_identifier_storage_detail_or_deadline()
    {
        var (authorizer, _) = Build((AudioAssetId, AudioFileWith(retainUntil: DateTime.UtcNow.AddDays(-2), deletedAt: null)));
        var expired = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);
        var (authorizer2, _) = Build((AudioAssetId, AudioFileWith(retainUntil: DateTime.UtcNow.AddDays(30), deletedAt: DateTime.UtcNow.AddDays(-1))));
        var deleted = await authorizer2.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        foreach (var outcome in new[] { expired, deleted })
        {
            var serialized = JsonSerializer.Serialize(outcome);
            Assert.DoesNotContain("SENTINEL-STORAGE-KEY", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL-STORED-NAME", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL-ORIGINAL-NAME", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(AudioAssetId.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
            Assert.All(outcome.Denials, denial => Assert.DoesNotContain("RetainUntil", denial.Message, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Cross_workspace_storage_lineage_denies_even_when_the_asset_row_sits_in_the_right_workspace()
    {
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceA, "audio/mpeg", MakeFile(WorkspaceB, "audio/mpeg"))));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileLineageMismatch, Assert.Single(outcome.Denials).Code);
    }

    [Theory]
    [InlineData("audio", "video/mp4")]
    [InlineData("video", "audio/mpeg")]
    [InlineData("audio", "application/pdf")]
    [InlineData("audio", "")]
    public async Task Requested_kind_must_match_the_media_kind_the_server_recorded(string requested, string storedContentType)
    {
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceA, storedContentType, MakeFile(WorkspaceA, storedContentType))));
        var selection = new MusicReferenceAssetSelection(requested, AudioAssetId);
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [selection]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.MediaKindMismatch, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Asset_mime_and_stored_file_content_type_disagreement_denies()
    {
        var (authorizer, _) = Build((AudioAssetId, MakeAsset(AudioAssetId, WorkspaceA, "video/mp4", MakeFile(WorkspaceA, "audio/mpeg"))));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.MediaKindMismatch, Assert.Single(outcome.Denials).Code);
    }

    [Theory]
    [InlineData("midi")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Unqualified_media_type_denies_without_a_lookup(string? mediaType)
    {
        var (authorizer, library) = Build((AudioAssetId, OwnedAudioFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [new MusicReferenceAssetSelection(mediaType, AudioAssetId)]);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.SelectionInvalid, Assert.Single(outcome.Denials).Code);
        Assert.Empty(library.Lookups);
    }

    [Theory]
    [InlineData(" Audio ")]
    [InlineData("VIDEO")]
    public async Task Kind_claim_normalization_is_the_only_courtesy_extended_to_the_client(string mediaType)
    {
        var expected = mediaType.Trim().ToLowerInvariant();
        var asset = expected == "audio" ? OwnedAudioFile() : OwnedVideoFile();
        var assetId = expected == "audio" ? AudioAssetId : VideoAssetId;
        var (authorizer, _) = Build((assetId, asset));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [new MusicReferenceAssetSelection(mediaType, assetId)]);

        Assert.True(outcome.AllAuthorized);
        Assert.Equal(expected, Assert.Single(outcome.Assets).RequestedMediaType);
    }

    [Fact]
    public async Task Empty_asset_id_denies_and_duplicates_denied_without_a_second_lookup()
    {
        var (authorizer, library) = Build((AudioAssetId, OwnedAudioFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(Guid.Empty), Audio(AudioAssetId), Audio(AudioAssetId)]);

        Assert.False(outcome.AllAuthorized);
        Assert.Single(outcome.Assets);
        var codes = outcome.Denials.Select(denial => denial.Code).ToArray();
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.SelectionInvalid, codes[0]);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.SelectionDuplicated, codes[1]);
        Assert.Single(library.Lookups);
    }

    [Fact]
    public async Task No_selections_and_an_invalid_principal_denial_is_never_reported_as_success()
    {
        var (authorizer, library) = Build((AudioAssetId, OwnedAudioFile()));

        var empty = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, []);
        Assert.False(empty.AllAuthorized);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.SelectionsMissing, Assert.Single(empty.Denials).Code);

        var nullList = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, null!);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.SelectionsMissing, Assert.Single(nullList.Denials).Code);

        var noUser = await authorizer.AuthorizeAsync(Guid.Empty, WorkspaceA, [Audio(AudioAssetId)]);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.PrincipalInvalid, Assert.Single(noUser.Denials).Code);

        var noWorkspace = await authorizer.AuthorizeAsync(OwnerId, Guid.Empty, [Audio(AudioAssetId)]);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.PrincipalInvalid, Assert.Single(noWorkspace.Denials).Code);

        Assert.Empty(library.Lookups);
    }

    [Fact]
    public async Task Oversized_selection_list_denies_before_any_lookup_runs()
    {
        var (authorizer, library) = Build();
        var selections = Enumerable.Range(0, MusicReferenceAssetAuthorizationCodes.MaxLookupsPerRequest + 1)
            .Select(_ => Audio(Guid.NewGuid()))
            .ToArray();
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, selections);

        Assert.Equal(MusicReferenceAssetAuthorizationCodes.SelectionLimitExceeded, Assert.Single(outcome.Denials).Code);
        Assert.Empty(library.Lookups);
    }

    [Fact]
    public async Task Denials_aggregate_every_failing_selection_while_valid_ones_stay_on_the_record()
    {
        var missing = Guid.NewGuid();
        var (authorizer, _) = Build((AudioAssetId, OwnedAudioFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA, [Audio(AudioAssetId), Audio(missing), Audio(Guid.Empty)]);

        Assert.False(outcome.AllAuthorized);
        Assert.Single(outcome.Assets);
        Assert.Equal(2, outcome.Denials.Count);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.AssetNotAvailable, outcome.Denials[0].Code);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.SelectionInvalid, outcome.Denials[1].Code);
    }

    [Fact]
    public async Task Outcome_never_carries_storage_keys_file_names_paths_or_asset_text()
    {
        var (authorizer, _) = Build((AudioAssetId, OwnedAudioFile()), (VideoAssetId, OwnedVideoFile()));
        var outcome = await authorizer.AuthorizeAsync(OwnerId, WorkspaceA,
            [Audio(AudioAssetId), Video(VideoAssetId), Audio(Guid.NewGuid()), new MusicReferenceAssetSelection("midi", Guid.NewGuid())]);

        var json = JsonSerializer.Serialize(outcome);
        Assert.DoesNotContain("SENTINEL", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL-ORIGINAL-NAME.mp3", json, StringComparison.Ordinal);
        foreach (var denial in outcome.Denials)
        {
            Assert.DoesNotContain(AudioAssetId.ToString(), denial.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(WorkspaceA.ToString(), denial.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("/", denial.Message, StringComparison.Ordinal);
        }

        // Every field of every record is server state: reflect over the record itself, not just
        // one sample, so a future field that stores an address fails this contract by name.
        var forbiddenMembers = new[] { "storagekey", "storedfilename", "originalfilename", "path", "url", "provider" };
        var memberNames = typeof(MusicReferenceAuthorizedAsset).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray();
        Assert.All(memberNames, name => Assert.All(forbiddenMembers, forbidden => Assert.DoesNotContain(forbidden, name, StringComparison.Ordinal)));
    }
}

// ---------------------------------------------------------------------------------------------
// Real-DbContext integration contracts (independent review of head 6aaf29c1, 2026-11-24).
// The unit suite above routes ownership through a fake asset service; these tests run the same
// authorizer against the real AssetService (real EF-Core queries, real AssetVisibility
// expression, real workspace-membership check) on an in-memory SQLite database. They pin the
// premise the review raised — AssetVisibility resolves a personal asset for its creator even
// when the backing StoredFile belongs to another member (and even when that file is bound to a
// conversation) — and the fix's two sides: the authorizer now denies with
// MUSIC_REFERENCE_AUTH_FILE_NOT_READABLE, and its answer agrees with the server's own download
// rule (AssetService.GetDownloadAsync -> ASSET_FILE_UNAVAILABLE) on the very same row. The
// project-shared shape passes both, proving the parity gate refuses contradictions rather than
// the legitimate sharing AssetService itself honors. No deployed creation path is known to
// produce the contradicting rows (GeneratedAssetPublisher binds a job's asset to a file the job
// stored); these states are constructed at the database level to lock the fail-closed invariant
// for any future writer or lifecycle mutation. This remains an unwired slice: nothing here is a
// deployed-exposure claim.
// ---------------------------------------------------------------------------------------------
public sealed class MusicReferenceAssetAuthorizationPersistenceTests
{
    private static readonly Guid MemberId = Guid.NewGuid(); // B: a second member of the same workspace

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, TaslimDbContext db, Guid workspaceId, Guid ownerId, Guid projectId, Guid conversationId)
        {
            Connection = connection;
            Db = db;
            WorkspaceId = workspaceId;
            OwnerId = ownerId;
            ProjectId = projectId;
            ConversationId = conversationId;
            Assets = new AssetService(db, new WorkspaceAccessService(db));
            Authorizer = new MusicReferenceAssetAuthorizer(Assets);
        }

        private SqliteConnection Connection { get; }
        public TaslimDbContext Db { get; }
        public Guid WorkspaceId { get; }
        public Guid OwnerId { get; }
        public Guid ProjectId { get; }
        public Guid ConversationId { get; }
        public AssetService Assets { get; }
        public MusicReferenceAssetAuthorizer Authorizer { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
            var db = new TaslimDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            var workspaceId = Guid.NewGuid();
            var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"owner-{Guid.NewGuid():N}", NormalizedUserName = "OWNER", DisplayName = "Owner", CreatedAt = now, UpdatedAt = now };
            var member = new ApplicationUser { Id = MemberId, UserName = $"member-{Guid.NewGuid():N}", NormalizedUserName = "MEMBER", DisplayName = "Member", CreatedAt = now, UpdatedAt = now };
            db.Users.AddRange(owner, member);
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Reference Studio", Slug = $"reference-studio-{Guid.NewGuid():N}", Type = WorkspaceType.Business, CreatedAt = now, UpdatedAt = now });
            db.WorkspaceMembers.AddRange(
                new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = owner.Id, Role = WorkspaceRole.Owner, JoinedAt = now },
                new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = member.Id, Role = WorkspaceRole.Member, JoinedAt = now });
            var project = new Project { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Shared Project", CreatedAt = now, UpdatedAt = now };
            db.Projects.Add(project);
            var conversation = new Conversation { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = member.Id, Title = "Member conversation", CreatedAt = now, UpdatedAt = now };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, workspaceId, owner.Id, project.Id, conversation.Id);
        }

        // A personal music asset created by the owner and backed by the given stored file.
        public async Task<Asset> AddOwnerAssetAsync(StoredFile file)
        {
            var now = DateTime.UtcNow;
            Db.StoredFiles.Add(file);
            var asset = new Asset
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                CreatedByUserId = OwnerId,
                StoredFile = file,
                Name = "Reference",
                AssetType = AssetTypes.Music,
                MimeType = "audio/mpeg",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            Db.Assets.Add(asset);
            await Db.SaveChangesAsync();
            return asset;
        }

        public StoredFile MakeFile(Guid ownerId, Guid? projectId = null, Guid? conversationId = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = ownerId,
            ProjectId = projectId,
            ConversationId = conversationId,
            ContentType = "audio/mpeg",
            OriginalFileName = "member-upload.mp3",
            StoredFileName = "SENTINEL-STORED-NAME",
            Extension = ".mp3",
            SizeBytes = 4_000_000,
            Status = StoredFileStatus.Ready,
            StorageKey = "SENTINEL-STORAGE-KEY",
            CreatedAt = DateTime.UtcNow,
            ProcessedAt = DateTime.UtcNow,
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private static MusicReferenceAssetSelection Audio(Guid assetId) => new("audio", assetId);

    [Fact]
    public async Task Creator_asset_backed_by_another_members_personal_file_denies_instead_of_marking_access_verified()
    {
        // The reviewed contradiction, on real queries: AssetVisibility lets the asset creator
        // resolve the row regardless of the backing file's user linkage, but the download rule
        // (CanReadStoredFile, via GetDownloadAsync) refuses that file to the same requester.
        // The authorizer must agree with the download rule, not with the visibility shortcut.
        await using var fixture = await Fixture.CreateAsync();
        var asset = await fixture.AddOwnerAssetAsync(fixture.MakeFile(MemberId));

        // Premise the review reported, verified end-to-end on the real service: the creator
        // still resolves the asset (so a bare denial of GetAsync would have been the wrong fix
        // — the file-specific rule is the one that has to gate authorization).
        Assert.NotNull(await fixture.Assets.GetAsync(fixture.OwnerId, asset.Id));

        var outcome = await fixture.Authorizer.AuthorizeAsync(fixture.OwnerId, fixture.WorkspaceId, [Audio(asset.Id)]);
        Assert.False(outcome.AllAuthorized);
        Assert.Empty(outcome.Assets);
        var denial = Assert.Single(outcome.Denials);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileNotReadable, denial.Code);
        // Denial messages stay identifier-free, like every other code in this path.
        Assert.DoesNotContain(asset.Id.ToString(), denial.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", JsonSerializer.Serialize(outcome), StringComparison.Ordinal);

        // Parity with the server's own download rule on the very same row.
        var download = await Assert.ThrowsAsync<AssetValidationException>(
            () => fixture.Assets.GetDownloadAsync(fixture.OwnerId, asset.Id));
        Assert.Equal("ASSET_FILE_UNAVAILABLE", download.Code);
    }

    [Fact]
    public async Task Creator_asset_backed_by_another_members_conversation_file_denies_too()
    {
        // Conversation-scoped files are the second half of the reported state: never readable
        // through another account's asset, and already excluded from generated-asset rebinding
        // by the publisher gate. Fail-closed here as well.
        await using var fixture = await Fixture.CreateAsync();
        var asset = await fixture.AddOwnerAssetAsync(
            fixture.MakeFile(MemberId, projectId: fixture.ProjectId, conversationId: fixture.ConversationId));

        var outcome = await fixture.Authorizer.AuthorizeAsync(fixture.OwnerId, fixture.WorkspaceId, [Audio(asset.Id)]);
        Assert.False(outcome.AllAuthorized);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.FileNotReadable, Assert.Single(outcome.Denials).Code);
    }

    [Fact]
    public async Task Creator_asset_backed_by_its_own_file_still_verifies_on_the_real_service()
    {
        // Positive control: the parity gate must not become "refuse the owner's own library".
        await using var fixture = await Fixture.CreateAsync();
        var asset = await fixture.AddOwnerAssetAsync(fixture.MakeFile(fixture.OwnerId));

        var outcome = await fixture.Authorizer.AuthorizeAsync(fixture.OwnerId, fixture.WorkspaceId, [Audio(asset.Id)]);
        Assert.True(outcome.AllAuthorized);
        Assert.Empty(outcome.Denials);
        var record = Assert.Single(outcome.Assets);
        Assert.Equal(asset.Id, record.AssetId);
        Assert.Equal("audio/mpeg", record.ContentType);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.VerifiedByService, record.VerifiedByService);
    }

    [Fact]
    public async Task Creator_asset_backed_by_a_project_shared_file_verifies_as_the_download_rule_does()
    {
        // The other positive half: a member's file inside a workspace project is exactly what
        // CanReadStoredFile (and GetDownloadAsync) allow, so the authorizer must keep allowing
        // it too. Parity means both directions.
        await using var fixture = await Fixture.CreateAsync();
        var asset = await fixture.AddOwnerAssetAsync(fixture.MakeFile(MemberId, projectId: fixture.ProjectId));

        var outcome = await fixture.Authorizer.AuthorizeAsync(fixture.OwnerId, fixture.WorkspaceId, [Audio(asset.Id)]);
        Assert.True(outcome.AllAuthorized);
        Assert.Empty(outcome.Denials);
        var download = await fixture.Assets.GetDownloadAsync(fixture.OwnerId, asset.Id);
        Assert.NotNull(download);
    }

    [Fact]
    public async Task Membership_revocation_resolves_the_asset_to_nothing_at_the_access_layer()
    {
        // Workspace isolation/revocation on real queries: removing the membership makes the
        // shared service itself return null, so the authorizer reports the uniform
        // "not available" code and never reaches a file-specific one.
        await using var fixture = await Fixture.CreateAsync();
        var asset = await fixture.AddOwnerAssetAsync(fixture.MakeFile(fixture.OwnerId));
        Assert.True((await fixture.Authorizer.AuthorizeAsync(fixture.OwnerId, fixture.WorkspaceId, [Audio(asset.Id)])).AllAuthorized);

        var membership = await fixture.Db.WorkspaceMembers.SingleAsync(item => item.UserId == fixture.OwnerId);
        fixture.Db.WorkspaceMembers.Remove(membership);
        await fixture.Db.SaveChangesAsync();

        Assert.Null(await fixture.Assets.GetAsync(fixture.OwnerId, asset.Id));
        var outcome = await fixture.Authorizer.AuthorizeAsync(fixture.OwnerId, fixture.WorkspaceId, [Audio(asset.Id)]);
        Assert.False(outcome.AllAuthorized);
        Assert.Equal(MusicReferenceAssetAuthorizationCodes.AssetNotAvailable, Assert.Single(outcome.Denials).Code);
    }
}
