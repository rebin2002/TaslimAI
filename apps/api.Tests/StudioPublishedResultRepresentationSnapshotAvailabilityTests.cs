using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA follow-up lane of issue #1030 / PR #1041. PR #1041 mirrors the download route's
/// availability fact into the asset LIBRARY read model (<c>AssetContracts.cs</c>, consumed by
/// <c>AssetDetail.tsx</c>). The sibling surface the QA note on #1041 flagged stays unfixed:
/// <c>DocumentStudioView.tsx</c> and <c>PresentationStudioView.tsx> render their representation
/// download buttons from the PUBLICATION-TIME <c>ResultJson</c> snapshot attached by
/// <c>GenerationJobExecution.AddPublishedAssetReference</c> and served unmodified through
/// <c>GET /api/generation/jobs/{id}</c> (<c>GenerationJobContractMapper.ToDto</c> passes
/// <c>job.ResultJson</c> verbatim). After a retention prune flips the representation's own
/// <c>StoredFile.Status</c> to <c>Deleted</c>, the snapshot never learns about it, the studio
/// buttons remain live, and every click dead-ends in <c>409 ASSET_FILE_UNAVAILABLE</c> from
/// <c>AssetService.GetDownloadAsync</c> — the same 409 #1041 made the library stop advertising.
///
/// This lane touches no file claimed by PR #1041 (AssetService.cs, AssetContracts.cs,
/// AssetDetail.tsx, its two test files, api.ts, globals.css) and modifies no production code:
/// the snapshot builder is exercised through its own reflection tripwire, not edited.
///
/// Deterministic and provider-free: synthetic registered users, in-memory stored-file rows with
/// local provider markers, no bytes ever requested, no paid provider, no real assets, no storage
/// mutation — the "prune" is exactly the row-status flip the retention worker performs.
///
/// Facts 1-3 are GREEN pins of the mechanism and of the route that remains the source of truth.
/// Facts 4-5 are the RED handoff: they assert the shape-agnostic desired invariant (a studio result
/// read model must not advertise a representation download that its own enforcement route rejects
/// with 409) and fail on current main AND on PR #1041 head faf0d9d0, because the studio seam files
/// are byte-identical at both revisions (zero drift between the #1041 base 92a77495 and main
/// 17d9434e for this lane's files).
/// </summary>
public sealed class StudioPublishedResultRepresentationSnapshotAvailabilityTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public StudioPublishedResultRepresentationSnapshotAvailabilityTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // ----- Mechanism pins (green on current main and at PR #1041 head) -----

    [Fact]
    public void Publisher_snapshot_bakes_representations_with_identity_only_and_no_availability_fact()
    {
        // The publication-time snapshot is produced by the production builder itself. Each baked
        // entry carries id/type/fileName/contentType and NO availability property, so a consumer
        // reading only this payload cannot know a download is 409-bound even at publication time.
        var fixture = BuildPublishedFixture(AssetTypes.Document, GenerationJobTypes.DocumentGenerate, readyFirst: "docx", prunedSecond: "pdf", pruneAtPublication: false);

        var representations = fixture.PublishedRepresentations;
        Assert.Equal(2, representations.GetArrayLength());
        foreach (var representation in representations.EnumerateArray())
        {
            var properties = representation.EnumerateObject().Select(property => property.Name.ToLowerInvariant()).OrderBy(name => name).ToArray();
            Assert.Equal(["contenttype", "filename", "id", "type"], properties);
        }
    }

    [Fact]
    public void Retention_prune_never_rewrites_the_published_snapshot()
    {
        // Pin the persistence fact behind the dead end: the ResultJson column is written once at
        // publication. Pruning the representation's stored file afterwards leaves the advertised
        // array byte-identical, so the studio read model cannot learn the availability fact from
        // the snapshot itself.
        using var client = factory.CreateClient();
        var auth = Register(client, "snapshot-immutable").GetAwaiter().GetResult();
        var fixture = BuildPublishedFixture(AssetTypes.Document, GenerationJobTypes.DocumentGenerate, readyFirst: "docx", prunedSecond: "pdf", pruneAtPublication: false, auth: auth);

        var before = GetJobResultJson(client, fixture.JobId).GetAwaiter().GetResult();
        SetStoredFileStatus(fixture.PrunedStoredFileId, StoredFileStatus.Deleted).GetAwaiter().GetResult();
        var after = GetJobResultJson(client, fixture.JobId).GetAwaiter().GetResult();

        Assert.Equal(before, after);
        Assert.Contains(fixture.PrunedRepresentationId.ToString(), after, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Representation_download_route_is_the_409_source_of_truth_for_a_pruned_representation()
    {
        // Same enforcement PR #1041 mirrors in the library read model — unchanged by that PR's
        // lane and independent of it: once the representation's StoredFile is Deleted, the route
        // answers 409 while the job snapshot (facts 2 and 5) still advertises the entry.
        using var client = factory.CreateClient();
        var auth = Register(client, "route-409-truth").GetAwaiter().GetResult();
        var fixture = BuildPublishedFixture(AssetTypes.Document, GenerationJobTypes.DocumentGenerate, readyFirst: "docx", prunedSecond: "pdf", pruneAtPublication: false, auth: auth);
        SetStoredFileStatus(fixture.PrunedStoredFileId, StoredFileStatus.Deleted).GetAwaiter().GetResult();

        var download = client.GetAsync($"/api/assets/{fixture.AssetId}/representations/{fixture.PrunedRepresentationId}/download").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
    }

    // ----- RED handoff: the studio result read model must not advertise a 409-bound download -----

    [Fact]
    public async Task DocumentStudio_result_read_model_does_not_advertise_pruned_representation_downloads()
    {
        // Shape-agnostic desired contract for the seam DocumentStudioView.tsx consumes: for every
        // representation the GET job payload keeps advertising as downloadable — here, by being
        // present with no false availability boolean, which is what the view renders as a live
        // download button — the enforcement route must not answer 409. On current code the pruned
        // pdf entry is advertised with no availability fact at all and the route 409s: RED.
        using var client = factory.CreateClient();
        var auth = await Register(client, "document-studio-red");
        var fixture = BuildPublishedFixture(AssetTypes.Document, GenerationJobTypes.DocumentGenerate, readyFirst: "docx", prunedSecond: "pdf", pruneAtPublication: true, auth: auth);
        await AssertStudioResultModelDoesNotAdvertiseUnavailableDownloads(client, fixture);
    }

    [Fact]
    public async Task PresentationStudio_result_read_model_does_not_advertise_pruned_representation_downloads()
    {
        // The PresentationStudioView.tsx half of the same seam (buttons rendered via
        // selectPresentationExports, which filters the snapshot by TYPE only). The ready pptx
        // sibling must stay advertised; only the pruned pdf entry has to be withheld by the
        // read model — mirroring the per-row rule PR #1041 established for the library.
        using var client = factory.CreateClient();
        var auth = await Register(client, "presentation-studio-red");
        var fixture = BuildPublishedFixture(AssetTypes.Presentation, GenerationJobTypes.PresentationGenerate, readyFirst: "pptx", prunedSecond: "pdf", pruneAtPublication: true, auth: auth);
        await AssertStudioResultModelDoesNotAdvertiseUnavailableDownloads(client, fixture);
    }

    // ----- Invariant helper -----

    private async Task AssertStudioResultModelDoesNotAdvertiseUnavailableDownloads(HttpClient client, PublishedFixture fixture)
    {
        var resultJson = await GetJobResultJson(client, fixture.JobId);
        using var document = JsonDocument.Parse(resultJson);
        var representations = document.RootElement.GetProperty("representations");

        var advertisedViolations = new List<string>();
        foreach (var representation in representations.EnumerateArray())
        {
            var representationId = representation.GetProperty("id").GetGuid();
            if (representationId != fixture.PrunedRepresentationId)
            {
                // Ready-sibling axis: the entry whose stored file is Ready must remain fully
                // advertised (present, with no false availability boolean). An over-tightened
                // fix that hides working downloads is caught on THIS assertion instead.
                Assert.Equal(fixture.ReadyRepresentationId, representationId);
                Assert.False(representation.EnumerateObject().Any(property =>
                    property.Value.ValueKind == JsonValueKind.False));
                continue;
            }

            var withheld = representation.EnumerateObject().Any(property =>
                property.Value.ValueKind == JsonValueKind.False);
            if (withheld) continue;

            var download = await client.GetAsync($"/api/assets/{fixture.AssetId}/representations/{representationId}/download");
            if (download.StatusCode == HttpStatusCode.Conflict)
            {
                advertisedViolations.Add(
                    $"representation '{representation.GetProperty("type").GetString()}' ({representationId}) is still advertised " +
                    "by the published ResultJson snapshot served at GET /api/generation/jobs/{id} with no availability fact, " +
                    "while its stored file is Deleted and the download route answers 409 ASSET_FILE_UNAVAILABLE.");
            }
        }

        Assert.True(advertisedViolations.Count == 0,
            "Studio dead-end reproduced (RED handoff for issue #1030 follow-up): the studio result read model advertises " +
            "a representation download the enforcement route rejects with 409. A correct fix must either mirror the " +
            "availability fact into this read model (the PR #1041 canDownload pattern) or omit the 409-bound entry, per " +
            "representation, while ready siblings stay advertised.\n" + string.Join("\n", advertisedViolations));
    }

    // ----- Synthetic publisher-path fixture -----

    private sealed record PublishedFixture(
        Guid JobId,
        Guid AssetId,
        Guid ReadyRepresentationId,
        Guid PrunedRepresentationId,
        Guid PrunedStoredFileId,
        JsonElement PublishedRepresentations);

    private PublishedFixture BuildPublishedFixture(
        string assetType,
        string jobType,
        string readyFirst,
        string prunedSecond,
        bool pruneAtPublication,
        AuthResponse? auth = null)
    {
        auth ??= Register(factory.CreateClient(), $"inline-{Guid.NewGuid():N}").GetAwaiter().GetResult();

        var now = DateTime.UtcNow;
        var assetId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var originalFileId = Guid.NewGuid();
        var readyRepresentationId = Guid.NewGuid();
        var readyStoredFileId = Guid.NewGuid();
        var prunedRepresentationId = Guid.NewGuid();
        var prunedStoredFileId = Guid.NewGuid();

        StoredFile File(Guid id, string extension, string contentType, StoredFileStatus status, string name) => new()
        {
            Id = id,
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = auth.User.Id,
            OriginalFileName = $"{name}{extension}",
            StoredFileName = $"{id:N}{extension}",
            ContentType = contentType,
            Extension = extension,
            SizeBytes = 6,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"studio-snapshot-availability/{id:N}{extension}",
            Status = status,
            CreatedAt = now,
            ProcessedAt = status == StoredFileStatus.Ready ? now : null,
        };

        var originalFile = File(originalFileId, $".{(assetType == AssetTypes.Presentation ? "pptx" : "docx")}",
            "application/octet-stream", StoredFileStatus.Ready, "report");
        var readyStoredFile = File(readyStoredFileId, $".{readyFirst}",
            ReadyContentType(readyFirst), StoredFileStatus.Ready, "report");
        var prunedStoredFile = File(prunedStoredFileId, $".{prunedSecond}",
            ReadyContentType(prunedSecond), pruneAtPublication ? StoredFileStatus.Deleted : StoredFileStatus.Ready, "report");

        var asset = new Asset
        {
            Id = assetId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            StoredFileId = originalFileId,
            Name = $"studio-snapshot-{assetType}-{Guid.NewGuid():N}",
            AssetType = assetType,
            MimeType = originalFile.ContentType,
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        asset.Representations.Add(new AssetRepresentation
        {
            Id = readyRepresentationId,
            AssetId = assetId,
            StoredFileId = readyStoredFileId,
            RepresentationType = readyFirst,
            FileName = $"report.{readyFirst}",
            ContentType = ReadyContentType(readyFirst),
            SizeBytes = 6,
            CreatedAt = now,
        });
        asset.Representations.Add(new AssetRepresentation
        {
            Id = prunedRepresentationId,
            AssetId = assetId,
            StoredFileId = prunedStoredFileId,
            RepresentationType = prunedSecond,
            FileName = $"report.{prunedSecond}",
            ContentType = ReadyContentType(prunedSecond),
            SizeBytes = 6,
            CreatedAt = now,
        });

        // Bake the snapshot through the PRODUCTION builder so the fixture shape cannot drift from
        // what real document/presentation publications store. The method is private static on the
        // worker; the reflection tripwire fails loudly if the publication seam is ever renamed.
        var addPublishedAssetReference = typeof(GenerationJobWorker)
            .GetMethod("AddPublishedAssetReference", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(addPublishedAssetReference);
        var publications = new[]
        {
            new PreparedGenerationOutput(new GenerationJobOutput
            {
                Id = Guid.NewGuid(),
                GenerationJobId = jobId,
                OutputType = GenerationJobOutputTypes.StoredFile,
                StoredFileId = originalFileId,
                CreatedAt = now,
            }, asset, originalFile),
        };
        var baseResult = JsonSerializer.Serialize(new { title = "Studio snapshot fixture", jobType = jobType });
        var resultJson = (string)addPublishedAssetReference!.Invoke(null, new object[] { baseResult, publications })!;

        var job = new GenerationJob
        {
            Id = jobId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            JobType = jobType,
            Status = GenerationJobStatus.Succeeded,
            Title = "studio-snapshot-fixture",
            InputJson = "{}",
            ResultJson = resultJson,
            ProgressPercent = 100,
            CreatedAt = now,
            StartedAt = now,
            CompletedAt = now,
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.AddRange(originalFile, readyStoredFile, prunedStoredFile);
            db.Assets.Add(asset);
            db.GenerationJobs.Add(job);
            db.SaveChanges();
        }

        using var published = JsonDocument.Parse(resultJson);
        return new PublishedFixture(jobId, assetId, readyRepresentationId, prunedRepresentationId, prunedStoredFileId,
            published.RootElement.GetProperty("representations").Clone());
    }

    private static string ReadyContentType(string representationType) => representationType switch
    {
        AssetRepresentationTypes.Pdf => "application/pdf",
        AssetRepresentationTypes.Pptx => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    private async Task<string> GetJobResultJson(HttpClient client, Guid jobId)
    {
        var job = await client.GetFromJsonAsync<JsonElement>($"/api/generation/jobs/{jobId}");
        return job.GetProperty("resultJson").GetString()!;
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
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"studio-snapshot-availability-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
