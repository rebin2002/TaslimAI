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

    private sealed record RenditionPlan(string RepresentationType, string FileName, string ContentType, string Extension, StoredFileStatus Status);

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
        bool advertise = true)
    {
        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var workspaceId = auth.PersonalWorkspace.Id;
        var userId = auth.User.Id;

        var asset = new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            SourceGenerationJobId = jobId,
            Name = name,
            AssetType = AssetTypes.Document,
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
            var file = new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = workspaceId,
                UserId = userId,
                OriginalFileName = plan.FileName,
                StoredFileName = $"{storedFileId:N}{plan.Extension}",
                ContentType = plan.ContentType,
                Extension = plan.Extension,
                SizeBytes = 4_096,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"issue-1049-http/{name}/{storedFileId:N}{plan.Extension}",
                Status = plan.Status,
                CreatedAt = now,
                ProcessedAt = plan.Status == StoredFileStatus.Ready ? now : null,
                DeletedAt = plan.Status == StoredFileStatus.Deleted ? now : null,
            };
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
                SizeBytes = file.SizeBytes,
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
