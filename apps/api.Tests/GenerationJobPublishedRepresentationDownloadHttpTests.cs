using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Counts the database round-trips issued by the pipeline behind this fixture, so the read path can
/// be pinned to a bounded number of queries instead of one per advertised rendition - the N+1
/// requirement of the issue #1049 release review.
/// </summary>
public sealed class RepresentationReadQueryCounter : DbCommandInterceptor
{
    private long executions;

    public long Executions => Interlocked.Read(ref executions);

    public void Reset() => Interlocked.Exchange(ref executions, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref executions);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref executions);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// The production pipeline over a synthetic SQLite database, with the generation worker switched off
/// so nothing but the measured HTTP request touches the connection counter.
/// </summary>
public sealed class PublishedRepresentationDownloadApiFactory : GenerationJobsApiFactory
{
    public RepresentationReadQueryCounter QueryCounter { get; } = new();

    protected override bool WorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddDbContext<TaslimDbContext>(options => options
                .UseSqlite($"Data Source={databasePath};Cache=Shared;Default Timeout=30")
                .AddInterceptors(QueryCounter));
        });
    }
}

/// <summary>
/// End-to-end acceptance for the issue #1049 release seam: <c>GET /api/generation/jobs/{id}</c> now
/// answers every format its own published snapshot advertises from the live state of that rendition's
/// stored file, through the HTTP route and the production read path rather than by calling the mapper
/// directly.
///
/// A completed Document Studio job publishes a <c>ResultJson</c> snapshot that names its renditions
/// and is never rewritten afterwards, so once retention prunes the PDF the studio payload still
/// promises a download that <c>GET /api/assets/{id}/representations/{representationId}/download</c>
/// refuses with <c>409 ASSET_FILE_UNAVAILABLE</c>. The companion <c>representationDownloads</c> field
/// is where the live truth is served now, per rendition, so a sibling DOCX that is still Ready keeps
/// its button while the pruned PDF loses only its promise.
///
/// Pinned here:
/// 1. per-representation truth over HTTP, agreeing with the download route that serves the bytes;
/// 2. the persisted snapshot column stays byte-for-byte identical across the read;
/// 3. a snapshot advertising no rendition gets the legacy payload (no companion field at all);
/// 4. a denied caller gets the same not-found envelope, with no rendition identity in it;
/// 5. identities are answered from the authorized job's own workspace only, so a snapshot naming
///    another workspace's rendition fails closed instead of leaking that file's state;
    /// 6. one bounded query carries the whole set: a three-rendition snapshot costs no more round-trips
    ///    than a one-rendition snapshot.
    /// 7. the studio answer is the same predicate the route enforces, not file status alone: a Ready
    ///    rendition pinned to somebody else's conversation, a Ready document rendition whose stored
    ///    size or content type has drifted from the rendition row, and a rendition type the document
    ///    route has no serving contract for are each withheld here exactly as that route refuses them
    ///    with <c>409</c>;
    /// 8. controls on both of those new branches - a project-shared file another member may read, and a
    ///    metadata-drifted rendition on a non-document asset - prove the mirror did not become a
    ///    refusal of its own.
    ///
/// Synthetic fixtures only: no paid provider, no secret, no real storage object, no user data and no
/// migration. The snapshot is baked through <c>GenerationJobExecution.AddPublishedAssetReference</c>
/// itself - the same production builder PR #1117 uses in its mapper suite - so its shape cannot drift
/// from what a real completion stores.
/// </summary>
public sealed class GenerationJobPublishedRepresentationDownloadHttpTests : IClassFixture<PublishedRepresentationDownloadApiFactory>
{
    private readonly PublishedRepresentationDownloadApiFactory factory;

    public GenerationJobPublishedRepresentationDownloadHttpTests(PublishedRepresentationDownloadApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// One rendition the published snapshot advertises. The optional members carry the two shapes the
    /// retention lifecycle cannot produce but the download route still refuses: a <see cref="StoredFileStatus.Ready"/>
    /// file this viewer is not entitled to read (<see cref="OwnerUserId"/> plus <see cref="ConversationScoped"/>),
    /// and a Ready file whose stored metadata no longer agrees with the rendition row
    /// (<see cref="FileSizeBytes"/> / <see cref="FileContentType"/>). Every member defaults to the
    /// healthy shape, so the pre-existing plans describe exactly what they described before.
    /// </summary>
    private sealed record RenditionPlan(
        string RepresentationType,
        string FileName,
        string ContentType,
        string Extension,
        StoredFileStatus Status,
        bool ConversationScoped = false,
        bool ProjectShared = false,
        Guid? OwnerUserId = null,
        long? FileSizeBytes = null,
        string? FileContentType = null,
        long? RepresentationSizeBytes = null);

    private sealed record SeededPublication(
        Guid JobId,
        Guid AssetId,
        Guid WorkspaceId,
        Guid UserId,
        string StoredResultJson,
        IReadOnlyList<Guid> RepresentationIds);

    private static readonly RenditionPlan ReadyDocx = new(
        AssetRepresentationTypes.Docx, "report.docx",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx", StoredFileStatus.Ready);

    private static readonly RenditionPlan PrunedPdf = new(
        AssetRepresentationTypes.Pdf, "report.pdf", "application/pdf", ".pdf", StoredFileStatus.Deleted);

    private static readonly RenditionPlan ReadyPptx = new(
        AssetRepresentationTypes.Pptx, "deck.pptx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx", StoredFileStatus.Ready);

    /// <summary>
    /// A second healthy document type. <c>(AssetId, RepresentationType)</c> is unique, so a fixture
    /// that contrasts one withheld rendition against one served sibling has to use two types.
    /// </summary>
    private static readonly RenditionPlan ReadyPdf = new(
        AssetRepresentationTypes.Pdf, "report.pdf", "application/pdf", ".pdf", StoredFileStatus.Ready);

    private static readonly RenditionPlan[] DocxAndPdf = [ReadyDocx, PrunedPdf];
    private static readonly RenditionPlan[] DocxOnly = [ReadyDocx];
    private static readonly RenditionPlan[] DocxPdfAndPptx = [ReadyDocx, PrunedPdf, ReadyPptx];

    [Fact]
    public async Task Route_Answers_Every_Advertised_Rendition_From_Its_Live_File_State()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Live Readiness Owner");
        var seeded = await SeedPublication(client, auth, "live-readiness", DocxAndPdf);
        var docxId = seeded.RepresentationIds[0];
        var pdfId = seeded.RepresentationIds[1];

        var (response, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(payload.TryGetProperty("representationDownloads", out var downloads),
            "the read path never reached the live availability seam");
        Assert.Equal(2, downloads.GetArrayLength());

        var docx = downloads.EnumerateArray().Single(item => item.GetProperty("type").GetString() == AssetRepresentationTypes.Docx);
        var pdf = downloads.EnumerateArray().Single(item => item.GetProperty("type").GetString() == AssetRepresentationTypes.Pdf);
        Assert.Equal(docxId, docx.GetProperty("id").GetGuid());
        Assert.Equal(pdfId, pdf.GetProperty("id").GetGuid());
        Assert.True(docx.GetProperty("canDownload").GetBoolean());
        Assert.False(pdf.GetProperty("canDownload").GetBoolean());

        // The withheld entry keeps its identity and metadata; only the promise is withdrawn.
        Assert.Equal("report.pdf", pdf.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", pdf.GetProperty("contentType").GetString());
        Assert.Equal("report.docx", docx.GetProperty("fileName").GetString());

        // The served snapshot still names both formats: availability rides beside it, never inside it.
        var snapshot = payload.GetProperty("resultJson").GetString()!;
        Assert.Contains("report.docx", snapshot);
        Assert.Contains("report.pdf", snapshot);

        // The two signals now agree with the route that actually serves the bytes - that disagreement
        // was the defect.
        var prunedRoute = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{pdfId}/download");
        Assert.Equal(HttpStatusCode.Conflict, prunedRoute.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await prunedRoute.Content.ReadAsStringAsync());
        var readyRoute = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{docxId}/download");
        Assert.NotEqual(HttpStatusCode.Conflict, readyRoute.StatusCode);
    }

    [Fact]
    public async Task Http_Read_Leaves_The_Persisted_Snapshot_Byte_For_Byte_Identical()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Immutable Snapshot Owner");
        var seeded = await SeedPublication(client, auth, "immutable", DocxAndPdf);

        var before = await ReadStoredSnapshot(seeded.JobId);
        Assert.Equal(seeded.StoredResultJson, before);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.Equal(before, payload.GetProperty("resultJson").GetString());

        var after = await ReadStoredSnapshot(seeded.JobId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Snapshot_Without_Advertised_Renditions_Serves_The_Legacy_Payload()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Legacy Payload Owner");
        var seeded = await SeedPublication(client, auth, "legacy", DocxOnly, advertise: false);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.DoesNotContain("\"representationDownloads\"", payload.GetRawText());
        // Nothing else in the contract moved.
        Assert.Equal(seeded.JobId, payload.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Denied_Caller_Gets_The_Not_Found_Envelope_Without_Any_Rendition_Identity()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Private Readiness Owner");
        var seeded = await SeedPublication(owner, auth, "denied", DocxAndPdf);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Denied Caller");
        var response = await outsider.GetAsync($"/api/generation/jobs/{seeded.JobId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("JOB_NOT_FOUND", body);
        Assert.DoesNotContain("representationDownloads", body);
        foreach (var representationId in seeded.RepresentationIds)
        {
            Assert.DoesNotContain(representationId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(representationId.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Advertised_Identities_Are_Answered_From_The_Authorized_Jobs_Workspace_Only()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Source Workspace Owner");
        var source = await SeedPublication(owner, ownerAuth, "cross-workspace-source", DocxOnly);

        using var other = factory.CreateClient();
        var otherAuth = await Register(other, "Foreign Snapshot Reader");
        // A snapshot in the reader's own workspace naming another workspace's rendition: the file
        // behind that identity is Ready, so a lookup by identity alone would answer a true promise
        // from data that caller may never see. The read path pins every identity to the authorized
        // job's workspace and fails closed instead.
        var forged = await SeedPublication(other, otherAuth, "cross-workspace-forged", DocxOnly, advertise: false);
        var forgedSnapshot = JsonSerializer.Serialize(new
        {
            title = "Foreign rendition identities",
            jobType = GenerationJobTypes.DocumentGenerate,
            assetId = forged.AssetId,
            representations = source.RepresentationIds.Select(id => new
            {
                id,
                type = AssetRepresentationTypes.Docx,
                fileName = "borrowed.docx",
                contentType = ReadyDocx.ContentType,
            }).ToArray(),
        });
        await WriteSnapshot(forged.JobId, forgedSnapshot);

        var (_, payload, _) = await ReadJob(other, forged.JobId);
        Assert.True(payload.TryGetProperty("representationDownloads", out var downloads));
        var entry = Assert.Single(downloads.EnumerateArray());
        Assert.Equal(source.RepresentationIds[0], entry.GetProperty("id").GetGuid());
        Assert.False(entry.GetProperty("canDownload").GetBoolean());
    }

    [Fact]
    public async Task Availability_Costs_One_Bounded_Query_Not_One_Per_Rendition()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Query Shape Owner");
        var none = await SeedPublication(client, auth, "shape-none", DocxOnly, advertise: false);
        var single = await SeedPublication(client, auth, "shape-one", DocxOnly);
        var triple = await SeedPublication(client, auth, "shape-three", DocxPdfAndPptx);

        // Warm the route once so a first-request cost cannot be mistaken for the seam's cost.
        await ReadJob(client, none.JobId);

        var (_, nonePayload, noneQueries) = await ReadJob(client, none.JobId);
        Assert.DoesNotContain("\"representationDownloads\"", nonePayload.GetRawText());
        var (_, singlePayload, singleQueries) = await ReadJob(client, single.JobId);
        Assert.Equal(1, singlePayload.GetProperty("representationDownloads").GetArrayLength());
        var (_, triplePayload, tripleQueries) = await ReadJob(client, triple.JobId);
        Assert.Equal(3, triplePayload.GetProperty("representationDownloads").GetArrayLength());

        // Three advertised renditions are answered by the same number of round-trips as one, and the
        // whole availability fact costs exactly one query on top of the unchanged read.
        Assert.Equal(singleQueries, tripleQueries);
        Assert.Equal(noneQueries + 1, singleQueries);
    }

    // ----- The two route branches the studio half used to ignore (#1202 availability parity) -----

    [Fact]
    public async Task Conversation_Scoped_Ready_Rendition_Is_Withheld_By_The_Studio_Because_The_Route_Refuses_It()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Conversation Scope Owner");
        Guid foreignId;
        using (var otherClient = factory.CreateClient())
        {
            foreignId = (await Register(otherClient, "Conversation File Owner")).User.Id;
        }

        // Two Ready renditions on one published asset. The DOCX is the viewer's own file and stays
        // served; the PDF is Ready in the same workspace but belongs to another member and is pinned
        // to a conversation, so AssetService.CanReadStoredFile refuses it and the download route
        // answers 409. Status alone cannot tell these two apart - which is exactly the
        // over-advertisement the studio half of the contract used to keep.
        var scoped = ReadyPdf with { ConversationScoped = true, OwnerUserId = foreignId };
        var seeded = await SeedPublication(client, auth, "conversation-scoped", [ReadyDocx, scoped]);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.True(
            CanDownload(payload, seeded.RepresentationIds[0]),
            "a healthy rendition lost its promise, so the fix over-tightened the studio path");
        Assert.False(CanDownload(payload, seeded.RepresentationIds[1]));

        // The withheld entry keeps its identity and metadata: only the promise is withdrawn.
        Assert.Equal("report.pdf", Entry(payload, seeded.RepresentationIds[1]).GetProperty("fileName").GetString());

        // The flag and the route agree, which is the whole point of resolving one predicate.
        var refused = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{seeded.RepresentationIds[1]}/download");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await refused.Content.ReadAsStringAsync());
        var served = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{seeded.RepresentationIds[0]}/download");
        Assert.NotEqual(HttpStatusCode.Conflict, served.StatusCode);
    }

    [Fact]
    public async Task Project_Shared_Ready_Rendition_Another_Member_Can_Read_Stay_Advertised()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Project Sharing Owner");
        Guid memberId;
        using (var otherClient = factory.CreateClient())
        {
            memberId = (await Register(otherClient, "Project File Owner")).User.Id;
        }

        // Control on the same branch: the file is owned by somebody else, but it is shared through a
        // project in this workspace and carries no conversation scope, so the route serves it. The
        // studio must keep advertising it - the mirror must not become a refusal of its own.
        var shared = ReadyDocx with { ProjectShared = true, OwnerUserId = memberId };
        var seeded = await SeedPublication(client, auth, "project-shared", [shared]);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.True(CanDownload(payload, seeded.RepresentationIds[0]));

        var served = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{seeded.RepresentationIds[0]}/download");
        Assert.NotEqual(HttpStatusCode.Conflict, served.StatusCode);
        Assert.DoesNotContain("ASSET_FILE_UNAVAILABLE", await served.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_Rendition_Whose_File_Size_Drifted_Is_Withheld_Because_The_Route_Refuses_It()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Drifted Size Owner");

        // The Ready file behind the rendition shrank to 99 bytes while the rendition row still
        // promises its published 4,096. That is the document metadata half of
        // AssetService.IsDocumentRepresentationConsistent: two rows that each look fine alone.
        var drifted = ReadyPdf with { FileSizeBytes = 99, RepresentationSizeBytes = 4_096 };
        var seeded = await SeedPublication(client, auth, "drifted-size", [ReadyDocx, drifted]);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.True(CanDownload(payload, seeded.RepresentationIds[0]));
        Assert.False(CanDownload(payload, seeded.RepresentationIds[1]));

        var refused = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{seeded.RepresentationIds[1]}/download");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_Rendition_Whose_File_Content_Type_Drifted_Is_Withheld_Because_The_Route_Refuses_It()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Drifted Content Type Owner");

        // The other half of the same consistency contract: a Ready file whose content type is no
        // longer the representation type's canonical one.
        var drifted = ReadyDocx with { FileContentType = "application/octet-stream" };
        var seeded = await SeedPublication(client, auth, "drifted-content-type", [drifted]);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.False(CanDownload(payload, seeded.RepresentationIds[0]));

        var refused = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{seeded.RepresentationIds[0]}/download");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_Rendition_Type_The_Document_Route_Cannot_Serve_Is_Withheld()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Unmapped Rendition Owner");

        // A PPTX rendition hanging off a document asset is Ready and readable, but the document
        // route has no serving contract for that type, so it answers 409 too. Status alone said
        // "downloadable" here; the single predicate says what the route says.
        var seeded = await SeedPublication(client, auth, "unmapped-type", [ReadyDocx, ReadyPptx]);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.True(CanDownload(payload, seeded.RepresentationIds[0]));
        Assert.False(CanDownload(payload, seeded.RepresentationIds[1]));

        var refused = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{seeded.RepresentationIds[1]}/download");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Non_Document_Asset_Keeps_A_Metadata_Drifted_Rendition_Advertised()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Image Drift Owner");

        // Control on the type axis: the route only applies document strictness to document assets, so
        // the studio must not refuse a drifted rendition it would actually serve. Without this case a
        // "fix" that always checked consistency would look green while hiding live downloads.
        var drifted = ReadyPptx with { FileSizeBytes = 99, RepresentationSizeBytes = 4_096 };
        var seeded = await SeedPublication(client, auth, "image-drift", [drifted], assetType: AssetTypes.Image);

        var (_, payload, _) = await ReadJob(client, seeded.JobId);
        Assert.True(CanDownload(payload, seeded.RepresentationIds[0]));

        var served = await client.GetAsync($"/api/assets/{seeded.AssetId}/representations/{seeded.RepresentationIds[0]}/download");
        Assert.NotEqual(HttpStatusCode.Conflict, served.StatusCode);
        Assert.DoesNotContain("ASSET_FILE_UNAVAILABLE", await served.Content.ReadAsStringAsync());
    }

    private static JsonElement Entry(JsonElement payload, Guid representationId) =>
        payload.GetProperty("representationDownloads").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == representationId);

    private static bool CanDownload(JsonElement payload, Guid representationId) =>
        Entry(payload, representationId).GetProperty("canDownload").GetBoolean();

    private async Task<(HttpResponseMessage Response, JsonElement Payload, long Queries)> ReadJob(HttpClient client, Guid jobId)
    {
        factory.QueryCounter.Reset();
        var response = await client.GetAsync($"/api/generation/jobs/{jobId}");
        var queries = factory.QueryCounter.Executions;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET /api/generation/jobs/{jobId} -> {(int)response.StatusCode}: {text}");
        return (response, JsonDocument.Parse(text).RootElement.Clone(), queries);
    }

    private async Task<string?> ReadStoredSnapshot(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.GenerationJobs.AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => job.ResultJson)
            .FirstOrDefaultAsync();
    }

    private async Task WriteSnapshot(Guid jobId, string resultJson)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.SingleAsync(item => item.Id == jobId);
        job.ResultJson = resultJson;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Persists a completed Document Studio job whose publication created one asset holding one
    /// rendition per plan entry, with the <c>ResultJson</c> the production publisher bakes. Passing
    /// <paramref name="advertise"/> as <c>false</c> leaves the published asset reference out of the
    /// snapshot, which is the shape older and non-rendition results have.
    /// </summary>
    private async Task<SeededPublication> SeedPublication(
        HttpClient client,
        AuthResponse auth,
        string name,
        RenditionPlan[] plans,
        bool advertise = true,
        string assetType = AssetTypes.Document)
    {
        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var workspaceId = auth.PersonalWorkspace.Id;
        var userId = auth.User.Id;
        // Lazily created, and shared by every conversation-scoped plan in one fixture: the readability
        // guard only reaches the conversation branch through a project that exists in this workspace.
        Project? sharedProject = null;

        var asset = new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            SourceGenerationJobId = jobId,
            Name = name,
            AssetType = assetType,
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var representationIds = new List<Guid>();
        StoredFile? primaryFile = null;
        foreach (var plan in plans)
        {
            var storedFileId = Guid.NewGuid();
            var representationId = Guid.NewGuid();
            var ownerId = plan.OwnerUserId ?? userId;
            var file = new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = workspaceId,
                UserId = ownerId,
                OriginalFileName = plan.FileName,
                StoredFileName = $"{storedFileId:N}{plan.Extension}",
                ContentType = plan.FileContentType ?? plan.ContentType,
                Extension = plan.Extension,
                SizeBytes = plan.FileSizeBytes ?? 4_096,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"issue-1049-http/{name}/{storedFileId:N}{plan.Extension}",
                Status = plan.Status,
                CreatedAt = now,
                ProcessedAt = plan.Status == StoredFileStatus.Ready ? now : null,
                DeletedAt = plan.Status == StoredFileStatus.Deleted ? now : null,
            };
            if (plan.ConversationScoped || plan.ProjectShared)
            {
                sharedProject ??= new Project
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    Name = $"Issue 1049 HTTP fixture {name} project",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                file.ProjectId = sharedProject.Id;
                file.Project = sharedProject;
                if (plan.ConversationScoped)
                {
                    // A real conversation row owned by the file's own owner: the guard's project branch
                    // is reached and then refused because the file is pinned to somebody's conversation.
                    file.ConversationId = Guid.NewGuid();
                    file.Conversation = new Conversation
                    {
                        Id = file.ConversationId.Value,
                        WorkspaceId = workspaceId,
                        ProjectId = sharedProject.Id,
                        UserId = ownerId,
                        Title = $"Issue 1049 HTTP fixture {name} conversation",
                        Status = ConversationStatus.Active,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                }
            }
            if (primaryFile is null)
            {
                primaryFile = file;
                asset.StoredFileId = storedFileId;
                asset.StoredFile = file;
            }

            asset.Representations.Add(new AssetRepresentation
            {
                Id = representationId,
                AssetId = assetId,
                StoredFileId = storedFileId,
                StoredFile = file,
                RepresentationType = plan.RepresentationType,
                FileName = plan.FileName,
                ContentType = plan.ContentType,
                SizeBytes = plan.RepresentationSizeBytes ?? file.SizeBytes,
                CreatedAt = now,
            });
            representationIds.Add(representationId);
        }

        var output = new GenerationJobOutput
        {
            Id = Guid.NewGuid(),
            GenerationJobId = jobId,
            OutputType = GenerationJobOutputTypes.StoredFile,
            StoredFileId = primaryFile!.Id,
            CreatedAt = now,
        };

        var baseResultJson = JsonSerializer.Serialize(new
        {
            title = $"Issue 1049 HTTP fixture {name}",
            jobType = GenerationJobTypes.DocumentGenerate,
        });

        // Bake through the production builder rather than hand-writing the shape, so the snapshot
        // under test is exactly what a real completion stores (the publisher orders by type).
        var snapshotBuilder = typeof(GenerationJobWorker)
            .GetMethod("AddPublishedAssetReference", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(snapshotBuilder);
        var publications = new[] { new PreparedGenerationOutput(output, asset, primaryFile) };
        var publishedResultJson = advertise
            ? (string)snapshotBuilder!.Invoke(null, new object?[] { baseResultJson, publications })!
            : baseResultJson;

        var job = new GenerationJob
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Succeeded,
            Title = $"Issue 1049 HTTP fixture {name}",
            ProgressPercent = 100,
            InputJson = "{}",
            ResultJson = publishedResultJson,
            CreatedAt = now,
            StartedAt = now,
            CompletedAt = now,
        };
        job.Assets.Add(asset);
        job.Outputs.Add(output);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync();
        }

        return new SeededPublication(jobId, assetId, workspaceId, userId, publishedResultJson, representationIds);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"issue-1049-http-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
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
