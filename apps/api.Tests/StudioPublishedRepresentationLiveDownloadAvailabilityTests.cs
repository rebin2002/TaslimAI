using System.Reflection;
using System.Text.Json;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Synthetic, provider-free tests for the issue #1049 read-model seam: the publication-time
/// <c>ResultJson</c> snapshot attached by <c>GenerationJobExecution.AddPublishedAssetReference</c>
/// stores rendition identity only, so Document/Presentation Studio - which render their download
/// buttons straight from that snapshot through <c>GET /api/generation/jobs/{id}</c> - keep
/// advertising a rendition whose route answers <c>409 ASSET_FILE_UNAVAILABLE</c> after a retention
/// prune. The library lane (#1030 / PR #1041) mirrors the route's fact into the asset payloads;
/// this lane mirrors the same fact beside the studio snapshot.
///
/// Contract pinned here:
/// 1. availability is computed per representation from its own stored-file status, the exact
///    predicate <c>AssetService.GetDownloadAsync</c> enforces;
/// 2. a ready sibling of a pruned rendition stays advertised (never asset-level);
/// 3. the historical <c>ResultJson</c> snapshot - persisted column and served string - stays
///    byte-for-byte immutable; the availability fact rides beside it;
/// 4. the seam is backward compatible: a read model that supplies no rendition graph gets the
///    identical payload it gets today, and the companion field is absent from the JSON;
/// 5. unknown state fails closed, because the route would not serve it either.
///
/// No HTTP client, database, storage provider, paid provider, secret, migration or billing object
/// is involved: the fixtures are in-memory entities and the snapshot is baked through the
/// production builder itself (reflection tripwire, not an edit), so its shape cannot drift from
/// what real jobs store.
/// </summary>
public sealed class StudioPublishedRepresentationLiveDownloadAvailabilityTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Now = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    private sealed record SnapshotFixture(
        GenerationJob Job,
        Asset Asset,
        Guid ReadyRepresentationId,
        Guid PrunedRepresentationId,
        IReadOnlyList<AssetRepresentation> Representations,
        string PublishedResultJson);

    /// <summary>
    /// A completed document job holding two renditions of the same published asset - a ready DOCX
    /// and a PDF whose stored file retention flipped to <see cref="StoredFileStatus.Deleted"/> -
    /// with the snapshot baked by the production publisher, exactly as a real completion stores it.
    /// </summary>
    private static SnapshotFixture CreateSnapshotFixture(string jobType = GenerationJobTypes.DocumentGenerate)
    {
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        var readyFile = CreateFile("report.docx", ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", StoredFileStatus.Ready);
        var prunedFile = CreateFile("report.pdf", ".pdf", "application/pdf", StoredFileStatus.Deleted);

        var readyRepresentation = new AssetRepresentation
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            StoredFileId = readyFile.Id,
            RepresentationType = AssetRepresentationTypes.Docx,
            FileName = "report.docx",
            ContentType = readyFile.ContentType,
            SizeBytes = readyFile.SizeBytes,
            CreatedAt = Now,
            StoredFile = readyFile,
        };
        var prunedRepresentation = new AssetRepresentation
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            StoredFileId = prunedFile.Id,
            RepresentationType = AssetRepresentationTypes.Pdf,
            FileName = "report.pdf",
            ContentType = prunedFile.ContentType,
            SizeBytes = prunedFile.SizeBytes,
            CreatedAt = Now,
            StoredFile = prunedFile,
        };

        var asset = new Asset
        {
            Id = assetId,
            WorkspaceId = WorkspaceId,
            CreatedByUserId = UserId,
            Name = "report",
            AssetType = AssetTypes.Document,
            Status = AssetStatus.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
            StoredFileId = readyFile.Id,
            StoredFile = readyFile,
            SourceGenerationJobId = jobId,
        };
        // The publisher orders by representation type, so docx is the ready sibling and pdf the pruned one.
        asset.Representations.Add(readyRepresentation);
        asset.Representations.Add(prunedRepresentation);

        var outputs = new GenerationJobOutput
        {
            Id = Guid.NewGuid(),
            GenerationJobId = jobId,
            OutputType = GenerationJobOutputTypes.StoredFile,
            StoredFileId = readyFile.Id,
            CreatedAt = Now,
        };
        var originalFile = new StoredFile
        {
            Id = readyFile.Id,
            WorkspaceId = WorkspaceId,
            UserId = UserId,
            OriginalFileName = "report.docx",
            StoredFileName = readyFile.StoredFileName,
            ContentType = readyFile.ContentType,
            Extension = ".docx",
            SizeBytes = 4_096,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = "local/report.docx",
            Status = StoredFileStatus.Ready,
        };

        // Bake through the production builder rather than hand-writing the shape: the method is
        // private static on the worker and this tripwire fails loudly if the publication seam moves.
        var addPublishedAssetReference = typeof(GenerationJobWorker)
            .GetMethod("AddPublishedAssetReference", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(addPublishedAssetReference);
        var publications = new[]
        {
            new PreparedGenerationOutput(outputs, asset, originalFile),
        };
        var baseResult = JsonSerializer.Serialize(new { title = "Studio snapshot fixture", jobType });
        var publishedResultJson = (string)addPublishedAssetReference!.Invoke(null, new object[] { baseResult, publications })!;

        var job = new GenerationJob
        {
            Id = jobId,
            WorkspaceId = WorkspaceId,
            CreatedByUserId = UserId,
            JobType = jobType,
            Status = GenerationJobStatus.Succeeded,
            Title = "Studio snapshot fixture",
            ProgressPercent = 100,
            InputJson = "{}",
            ResultJson = publishedResultJson,
            CreatedAt = Now,
            StartedAt = Now,
            CompletedAt = Now,
        };
        job.Assets.Add(asset);

        return new SnapshotFixture(job, asset, readyRepresentation.Id, prunedRepresentation.Id,
            new[] { readyRepresentation, prunedRepresentation }, publishedResultJson);
    }

    private static StoredFile CreateFile(string name, string extension, string contentType, StoredFileStatus status)
    {
        var id = Guid.NewGuid();
        return new StoredFile
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            UserId = UserId,
            OriginalFileName = name,
            StoredFileName = $"{id:N}{extension}",
            ContentType = contentType,
            Extension = extension,
            SizeBytes = 4_096,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"local/{name}",
            Status = status,
        };
    }

    // ----- Backward compatibility: the seam adds nothing to a read model that does not use it -----

    [Fact]
    public void Read_model_without_rendition_graph_serves_the_snapshot_byte_for_byte()
    {
        var fixture = CreateSnapshotFixture();

        var untouched = GenerationJobContractMapper.ToDto(fixture.Job);
        var unwired = GenerationJobContractMapper.ToDtoWithRepresentationDownloads(fixture.Job, null);

        Assert.Equal(fixture.PublishedResultJson, untouched.ResultJson);
        Assert.Equal(fixture.PublishedResultJson, unwired.ResultJson);
        Assert.Null(untouched.RepresentationDownloads);
        Assert.Null(unwired.RepresentationDownloads);
        Assert.Equal(untouched, unwired);
    }

    [Fact]
    public void Companion_field_is_absent_from_the_payload_so_existing_consumers_are_unaffected()
    {
        var fixture = CreateSnapshotFixture();

        var payload = JsonSerializer.Serialize(GenerationJobContractMapper.ToDto(fixture.Job), WebJson);

        Assert.DoesNotContain("\"representationDownloads\"", payload);
        Assert.Contains("\"resultJson\"", payload);
    }

    [Fact]
    public void Annotation_changes_no_other_field_of_the_job_dto()
    {
        var fixture = CreateSnapshotFixture();

        var plain = GenerationJobContractMapper.ToDto(fixture.Job);
        var annotated = GenerationJobContractMapper.ToDtoWithRepresentationDownloads(
            fixture.Job, fixture.Representations);

        Assert.Equal(plain with { RepresentationDownloads = annotated.RepresentationDownloads }, annotated);
        Assert.Equal(plain.Id, annotated.Id);
        Assert.Equal(plain.WorkspaceId, annotated.WorkspaceId);
        Assert.Equal(plain.Status, annotated.Status);
        Assert.Equal(plain.ResultJson, annotated.ResultJson);
        Assert.Equal(plain.Outputs, annotated.Outputs);
    }

    // ----- The desired contract: per-representation live readiness, ready sibling preserved -----

    [Fact]
    public void Pruned_representation_is_withheld_while_its_ready_sibling_stays_advertised()
    {
        var fixture = CreateSnapshotFixture();

        var downloads = GenerationJobContractMapper
            .ToDtoWithRepresentationDownloads(fixture.Job, fixture.Representations)
            .RepresentationDownloads!;

        var ready = Assert.Single(downloads, item => item.Id == fixture.ReadyRepresentationId);
        var pruned = Assert.Single(downloads, item => item.Id == fixture.PrunedRepresentationId);

        Assert.True(ready.CanDownload);
        Assert.False(pruned.CanDownload);
        Assert.Equal(fixture.PublishedResultJson, GenerationJobContractMapper
            .ToDtoWithRepresentationDownloads(fixture.Job, fixture.Representations).ResultJson);
    }

    [Fact]
    public void Availability_is_decided_per_representation_never_per_asset()
    {
        var fixture = CreateSnapshotFixture();

        // One asset, one live and one pruned rendition, shared asset-level StoredFile that is Ready.
        var availability = GenerationJobContractMapper.BuildRepresentationDownloadability(fixture.Representations);

        Assert.True(availability[fixture.ReadyRepresentationId]);
        Assert.False(availability[fixture.PrunedRepresentationId]);
        Assert.True(fixture.Asset.StoredFile is { Status: StoredFileStatus.Ready });
    }

    [Fact]
    public void Withheld_entries_keep_their_identity_and_metadata_only_the_promise_is_withdrawn()
    {
        var fixture = CreateSnapshotFixture();

        var downloads = GenerationJobContractMapper
            .ToDtoWithRepresentationDownloads(fixture.Job, fixture.Representations)
            .RepresentationDownloads!;

        var pruned = downloads.Single(item => item.Id == fixture.PrunedRepresentationId);

        Assert.False(pruned.CanDownload);
        Assert.Equal("pdf", pruned.Type);
        Assert.Equal("report.pdf", pruned.FileName);
        Assert.Equal("application/pdf", pruned.ContentType);
    }

    [Fact]
    public void Snapshot_advertises_exactly_the_renditions_the_publisher_baked()
    {
        var fixture = CreateSnapshotFixture();

        var advertised = GenerationJobContractMapper
            .ToDtoWithRepresentationDownloads(fixture.Job, fixture.Representations)
            .RepresentationDownloads!;

        Assert.Equal(2, advertised.Count);
        Assert.Equal(fixture.Representations.Select(item => item.Id).OrderBy(id => id),
            advertised.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public void Advertised_identities_can_be_resolved_from_the_snapshot_before_annotating()
    {
        var fixture = CreateSnapshotFixture();

        var ids = GenerationJobContractMapper.AdvertisedRepresentationIds(fixture.Job.ResultJson);

        Assert.Equal(2, ids.Count);
        Assert.Contains(fixture.ReadyRepresentationId, ids);
        Assert.Contains(fixture.PrunedRepresentationId, ids);
        Assert.Empty(GenerationJobContractMapper.AdvertisedRepresentationIds(null));
        Assert.Empty(GenerationJobContractMapper.AdvertisedRepresentationIds("not json at all"));
        Assert.Empty(GenerationJobContractMapper.AdvertisedRepresentationIds(
            JsonSerializer.Serialize(new { summary = "no published asset" })));
    }

    [Fact]
    public void Lookup_entry_point_answers_the_advertised_set_and_leaves_the_snapshot_untouched()
    {
        var fixture = CreateSnapshotFixture();
        var downloadable = new Dictionary<Guid, bool>
        {
            [fixture.ReadyRepresentationId] = true,
            [fixture.PrunedRepresentationId] = false,
        };

        var annotated = GenerationJobContractMapper.ToDtoWithRepresentationAvailability(
            fixture.Job, downloadable);
        var unwired = GenerationJobContractMapper.ToDtoWithRepresentationAvailability(fixture.Job, null);

        Assert.Equal(fixture.PublishedResultJson, annotated.ResultJson);
        Assert.Equal(2, annotated.RepresentationDownloads!.Count);
        Assert.True(annotated.RepresentationDownloads.Single(item => item.Id == fixture.ReadyRepresentationId).CanDownload);
        Assert.False(annotated.RepresentationDownloads.Single(item => item.Id == fixture.PrunedRepresentationId).CanDownload);
        Assert.Null(unwired.RepresentationDownloads);
        Assert.Equal(GenerationJobContractMapper.ToDto(fixture.Job), unwired);
    }

    // ----- Fail-closed semantics, matching what the enforcement route would answer -----

    [Fact]
    public void Rendition_missing_from_the_loaded_graph_fails_closed()
    {
        var fixture = CreateSnapshotFixture();

        var downloads = GenerationJobContractMapper
            .ToDtoWithRepresentationDownloads(fixture.Job, new[] { fixture.Representations.First() })
            .RepresentationDownloads!;

        Assert.True(downloads.Single(item => item.Id == fixture.ReadyRepresentationId).CanDownload);
        Assert.False(downloads.Single(item => item.Id == fixture.PrunedRepresentationId).CanDownload);
    }

    [Fact]
    public void Empty_loaded_graph_fails_closed_instead_of_inventing_availability()
    {
        var fixture = CreateSnapshotFixture();

        var downloads = GenerationJobContractMapper
            .ToDtoWithRepresentationDownloads(fixture.Job, Array.Empty<AssetRepresentation>())
            .RepresentationDownloads!;

        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, item => Assert.False(item.CanDownload));
    }

    [Theory]
    [InlineData(StoredFileStatus.Uploading)]
    [InlineData(StoredFileStatus.Processing)]
    [InlineData(StoredFileStatus.Failed)]
    [InlineData(StoredFileStatus.Deleted)]
    public void Only_a_ready_stored_file_counts_as_downloadable(StoredFileStatus status)
    {
        var file = CreateFile("report.pptx", ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", status);
        var representation = new AssetRepresentation
        {
            Id = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            StoredFileId = file.Id,
            RepresentationType = AssetRepresentationTypes.Pptx,
            FileName = "report.pptx",
            ContentType = file.ContentType,
            CreatedAt = Now,
            StoredFile = file,
        };

        var availability = GenerationJobContractMapper.BuildRepresentationDownloadability(new[] { representation });

        Assert.False(availability[representation.Id]);
    }

    [Fact]
    public void Ready_stored_file_is_downloadable()
    {
        var file = CreateFile("report.pptx", ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", StoredFileStatus.Ready);
        var representation = new AssetRepresentation
        {
            Id = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            StoredFileId = file.Id,
            RepresentationType = AssetRepresentationTypes.Pptx,
            FileName = "report.pptx",
            ContentType = file.ContentType,
            CreatedAt = Now,
            StoredFile = file,
        };

        var availability = GenerationJobContractMapper.BuildRepresentationDownloadability(new[] { representation });

        Assert.True(availability[representation.Id]);
    }

    [Fact]
    public void Navigation_hard_missing_on_a_rendition_fails_closed()
    {
        var representation = new AssetRepresentation
        {
            Id = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            StoredFileId = Guid.NewGuid(),
            RepresentationType = AssetRepresentationTypes.Pdf,
            FileName = "report.pdf",
            ContentType = "application/pdf",
            CreatedAt = Now,
        };

        var availability = GenerationJobContractMapper.BuildRepresentationDownloadability(new[] { representation });

        Assert.False(availability[representation.Id]);
    }

    // ----- Immutability of the historical snapshot, and tolerance of what it is not -----

    [Fact]
    public void Retention_prune_never_touches_the_persisted_snapshot_column()
    {
        var fixture = CreateSnapshotFixture();
        var persisted = fixture.Job.ResultJson;
        var pruned = fixture.Representations.Single(item => item.Id == fixture.PrunedRepresentationId);

        // Exactly the write retention performs: flip the rendition's own row status.
        pruned.StoredFile!.Status = StoredFileStatus.Deleted;
        pruned.StoredFile.DeletedAt = Now;

        GenerationJobContractMapper.ToDtoWithRepresentationDownloads(fixture.Job, fixture.Representations);

        Assert.Equal(persisted, fixture.Job.ResultJson);
        Assert.Contains(fixture.PrunedRepresentationId.ToString(), fixture.Job.ResultJson);
    }

    [Fact]
    public void Presentation_publications_are_annotated_the_same_way()
    {
        var fixture = CreateSnapshotFixture(GenerationJobTypes.PresentationGenerate);

        var downloads = GenerationJobContractMapper
            .ToDtoWithRepresentationDownloads(fixture.Job, fixture.Representations)
            .RepresentationDownloads!;

        Assert.Equal(2, downloads.Count);
        Assert.True(downloads.Single(item => item.Id == fixture.ReadyRepresentationId).CanDownload);
        Assert.False(downloads.Single(item => item.Id == fixture.PrunedRepresentationId).CanDownload);
    }

    [Fact]
    public void Results_without_an_advertised_set_yield_no_companion_list()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            CreatedByUserId = UserId,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Succeeded,
            InputJson = "{}",
            ResultJson = JsonSerializer.Serialize(new { summary = "text only result" }),
            CreatedAt = Now,
        };

        var annotated = GenerationJobContractMapper.ToDtoWithRepresentationDownloads(
            job, Array.Empty<AssetRepresentation>());

        Assert.Null(annotated.RepresentationDownloads);
    }

    [Fact]
    public void Results_that_are_not_json_objects_are_left_alone()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            CreatedByUserId = UserId,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Succeeded,
            InputJson = "{}",
            ResultJson = "not json at all",
            CreatedAt = Now,
        };

        var annotated = GenerationJobContractMapper.ToDtoWithRepresentationDownloads(
            job, Array.Empty<AssetRepresentation>());

        Assert.Null(annotated.RepresentationDownloads);
        Assert.Equal("not json at all", annotated.ResultJson);
    }

    [Fact]
    public void Pascal_case_snapshot_entries_are_recognised()
    {
        var representationId = Guid.NewGuid();
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            CreatedByUserId = UserId,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Succeeded,
            InputJson = "{}",
            ResultJson = JsonSerializer.Serialize(new
            {
                AssetId = Guid.NewGuid(),
                Representations = new[]
                {
                    new
                    {
                        Id = representationId,
                        Type = "docx",
                        FileName = "report.docx",
                        ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    },
                },
            }),
            CreatedAt = Now,
        };

        var annotated = GenerationJobContractMapper.ToDtoWithRepresentationDownloads(
            job, Array.Empty<AssetRepresentation>());

        var entry = Assert.Single(annotated.RepresentationDownloads!);
        Assert.Equal(representationId, entry.Id);
        Assert.False(entry.CanDownload);
    }
}
