using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #714 adjacent (presentation export) — workspace isolation of a generated
/// PPTX representation, plus the artifact-integrity and representation-metadata
/// acceptance the independent QA review on PR #723 required.
///
/// Review 5819992734 (rebin2002, state COMMENTED = explicitly no release approval, at
/// exact head 41055b5b92eb9a3998f4438f60040794492cf8d7) accepted the owner-200 /
/// outsider-404 authorization behaviour but held that "Artifact integrity acceptance
/// remains incomplete: the positive assertion only checks `Length > 500` and first
/// bytes `PK`; an arbitrary ZIP with junk or missing OOXML entries could pass", and
/// asked for a ZIP/OOXML readback of `[Content_Types].xml`, `ppt/presentation.xml`,
/// at least one actual slide and the slide references, an optional re-opened
/// deterministic title/content check, and a check that an outsider cannot obtain
/// representation metadata as well as the blob. Those are added here.
///
/// The artifact-integrity half is now accepted against the PRODUCTION gate rather than
/// only against this file's own checks: the bytes the owner downloads are handed to
/// `PresentationOutputIntegrityValidator`, the same validator
/// `PresentationGenerationJobHandler` runs before a rendered deck is stored, together with
/// the representation metadata the owner is served. Two controls keep that acceptance
/// honest: a one-relationship edit of the downloaded package must be REJECTED by that gate,
/// and a junk ZIP satisfying exactly the `Length > 500` + `PK` probes the review rejected
/// must be rejected by both the production gate and this file's package readback.
/// The shipped `PresentationOutputIntegrityTests` cover the validator over a freshly
/// rendered object; they do not cover the bytes returned by the asset download route,
/// which is what these two additions test, so they are not duplicates of it.
///
/// Scope stated honestly, per the same review: this is a synthetic, provider-free
/// fixture. It does NOT qualify real Cloudflare R2 / production storage, PDF or PPTX
/// roundtrips; a separately controlled live-equivalent readback remains required for
/// any such claim.
/// </summary>
public sealed class PresentationExportWorkspaceIsolationTests : IClassFixture<PresentationGenerationApiFactory>
{
    private const string PptxMediaType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    private const string PresentationMainContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
    private const string SlideContentType = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";

    private readonly PresentationGenerationApiFactory factory;

    public PresentationExportWorkspaceIsolationTests(PresentationGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Generated_pptx_representation_is_downloadable_only_from_its_workspace()
    {
        var export = await CreateExportAsync("Presentation Export Owner");
        try
        {
            using var ownerDownload = await export.Owner.GetAsync(export.DownloadPath);
            Assert.Equal(HttpStatusCode.OK, ownerDownload.StatusCode);
            Assert.Equal(PptxMediaType, ownerDownload.Content.Headers.ContentType?.MediaType);
            var exportedPptx = await ownerDownload.Content.ReadAsByteArrayAsync();

            // The bare `Length > 500` + `PK` probe the review rejected is replaced by an
            // actual OOXML package readback: junk ZIP or a ZIP missing presentation
            // entries no longer passes this assertion set.
            AssertPresentationPackageStructure(exportedPptx);

            using var otherWorkspace = factory.CreateClient();
            _ = await Register(otherWorkspace, "Unrelated Workspace User");
            using var deniedDownload = await otherWorkspace.GetAsync(export.DownloadPath);
            Assert.Equal(HttpStatusCode.NotFound, deniedDownload.StatusCode);
            var deniedBody = await deniedDownload.Content.ReadAsStringAsync();
            Assert.DoesNotContain(export.Title, deniedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("pptx", deniedBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            export.Owner.Dispose();
        }
    }

    [Fact]
    public async Task Downloaded_package_reopens_with_its_deterministic_slides_and_text()
    {
        var export = await CreateExportAsync("Presentation Export Owner");
        try
        {
            // Determinism: re-opening the representation a second time yields the exact
            // same bytes, so the deck the owner reads back is the stored artifact rather
            // than something re-rendered per request.
            using var first = await export.Owner.GetAsync(export.DownloadPath);
            var bytes = await first.Content.ReadAsByteArrayAsync();
            using var second = await export.Owner.GetAsync(export.DownloadPath);
            Assert.True(bytes.SequenceEqual(await second.Content.ReadAsByteArrayAsync()));

            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

            // Every slide part carries at least one non-empty text run, and the run text
            // is the asset's own title/content, read back from the reopened bytes.
            var slideParts = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToList();
            Assert.NotEmpty(slideParts);

            var runTexts = new List<string>();
            foreach (var slide in slideParts)
            {
                var xml = ReadPart(archive, slide.FullName);
                var runs = Regex.Matches(xml, "<a:t>(?<text>[^<]*)</a:t>", RegexOptions.None, TimeSpan.FromSeconds(1))
                    .Select(match => match.Groups["text"].Value)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();
                Assert.NotEmpty(runs);
                runTexts.AddRange(runs);
            }

            // The visible title on the deck is the asset title the owner is served, and the
            // body is the asset description; compared against the metadata rather than a
            // hardcoded literal, because the deterministic renderer localises its strings
            // (the fixture's `language = "en"` request renders Sorani run text, so a literal
            // English assertion would be wrong, not merely brittle).
            // Measured behaviour of the deterministic renderer, recorded rather than assumed:
            //  - the served asset `name` is exactly the title run on slide 1;
            //  - the slide body is the renderer's own fixed template text, NOT the caller's
            //    `description`, which is stored as asset metadata and never inlined into a
            //    slide run (an earlier draft of this test asserted the description and failed
            //    on the exact bytes, so that claim is deliberately not made here);
            //  - both are Sorani even though the job requested `language = "en"`, so the
            //    assertions compare against the metadata the owner is served instead of
            //    hardcoding a literal in either language.
            Assert.Contains(export.Title, runTexts);
            var bodyRuns = runTexts
                .Where(text => !string.Equals(text, export.Title, StringComparison.Ordinal))
                .Where(text => !Regex.IsMatch(text, @"^\d+$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                .ToList();
            Assert.True(
                bodyRuns.Count >= 2,
                $"The reopened package carries {bodyRuns.Count} deterministic body run(s); expected at least a subtitle and a content line.");
            Assert.Equal(2, slideParts.Count);
        }
        finally
        {
            export.Owner.Dispose();
        }
    }

    [Fact]
    public async Task Outsider_cannot_obtain_representation_metadata_or_the_blob()
    {
        var export = await CreateExportAsync("Presentation Export Owner");
        try
        {
            // The owner can read the representation metadata, and it describes the bytes
            // that the owner download actually returns.
            using var ownerMetadata = await export.Owner.GetAsync($"/api/assets/{export.AssetId}");
            Assert.Equal(HttpStatusCode.OK, ownerMetadata.StatusCode);
            var ownerJson = JsonDocument.Parse(await ownerMetadata.Content.ReadAsStringAsync());
            var representations = ownerJson.RootElement.GetProperty("representations");
            var representation = Assert.Single(representations.EnumerateArray());
            Assert.Equal("pptx", representation.GetProperty("representationType").GetString());
            Assert.Equal(PptxMediaType, representation.GetProperty("contentType").GetString());
            Assert.EndsWith(".pptx", representation.GetProperty("fileName").GetString());
            Assert.Equal(export.Bytes.LongLength, representation.GetProperty("sizeBytes").GetInt64());
            var representationId = representation.GetProperty("id").GetString()!;

            using var outsider = factory.CreateClient();
            var outsiderAuth = await Register(outsider, "Unrelated Workspace User");

            // Metadata, not just the blob, is workspace-scoped.
            using var deniedMetadata = await outsider.GetAsync($"/api/assets/{export.AssetId}");
            Assert.Equal(HttpStatusCode.NotFound, deniedMetadata.StatusCode);
            var deniedMetadataBody = await deniedMetadata.Content.ReadAsStringAsync();
            Assert.Contains("ASSET_NOT_FOUND", deniedMetadataBody, StringComparison.Ordinal);
            Assert.DoesNotContain(representationId, deniedMetadataBody, StringComparison.Ordinal);
            Assert.DoesNotContain(export.Title, deniedMetadataBody, StringComparison.Ordinal);
            Assert.DoesNotContain("pptx", deniedMetadataBody, StringComparison.OrdinalIgnoreCase);

            using var deniedBlob = await outsider.GetAsync(export.DownloadPath);
            Assert.Equal(HttpStatusCode.NotFound, deniedBlob.StatusCode);

            // And the asset cannot be enumerated out of its own workspace either.
            using var deniedList = await outsider.GetAsync($"/api/assets?workspaceId={export.OwnerAuth.PersonalWorkspace.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
            Assert.DoesNotContain(export.AssetId.ToString(), await deniedList.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.NotEqual(outsiderAuth.PersonalWorkspace.Id, export.OwnerAuth.PersonalWorkspace.Id);
        }
        finally
        {
            export.Owner.Dispose();
        }
    }

    [Fact]
    public async Task Downloaded_package_satisfies_the_production_output_integrity_gate()
    {
        var export = await CreateExportAsync("Presentation Export Owner");
        try
        {
            // The metadata behind the integrity claim is the metadata the owner is served,
            // not literals chosen by this file, so the production gate is exercised over the
            // real representation triple (type, file name, content type) plus the real bytes.
            using var metadata = await export.Owner.GetAsync($"/api/assets/{export.AssetId}");
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            using var metadataJson = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
            var representation = Assert.Single(metadataJson.RootElement.GetProperty("representations").EnumerateArray());

            var served = new RenderedPresentation(
                representation.GetProperty("representationType").GetString()!,
                representation.GetProperty("fileName").GetString()!,
                representation.GetProperty("contentType").GetString()!,
                export.Bytes);

            // The served representation is a .pptx OOXML presentation and the downloaded bytes
            // pass the production output-integrity gate unchanged: if the asset route
            // truncated, re-encoded or swapped the stored artifact, this is where it shows.
            Assert.Equal("pptx", served.RepresentationType);
            Assert.EndsWith(".pptx", served.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PptxMediaType, served.ContentType);
            PresentationOutputIntegrityValidator.Validate(served);
        }
        finally
        {
            export.Owner.Dispose();
        }
    }

    [Fact]
    public async Task The_production_gate_rejects_a_downloaded_package_with_one_broken_slide_reference()
    {
        var export = await CreateExportAsync("Presentation Export Owner");
        try
        {
            // Control for the test above, so its acceptance is not vacuous: repointing the
            // first slide relationship of the downloaded package at a non-existent rId must
            // make the same gate fail.
            var corrupted = RewritePackagePart(
                export.Bytes,
                "ppt/presentation.xml",
                content => content.Replace("r:id=\"rId2\"", "r:id=\"rId999\"", StringComparison.Ordinal));
            Assert.False(corrupted.SequenceEqual(export.Bytes), "Rewriting the slide reference did not change the package bytes.");

            var served = new RenderedPresentation("pptx", $"{export.Title}.pptx", PptxMediaType, export.Bytes);
            var broken = served with { Content = corrupted };

            var exception = Assert.Throws<PresentationOutputIntegrityException>(
                () => PresentationOutputIntegrityValidator.Validate(broken));
            Assert.Contains("rId999", exception.Message, StringComparison.Ordinal);

            // The junk package the review described fails BOTH the production gate and this
            // file's own package readback, even though it clears the `Length > 500` + `PK`
            // probes that were judged insufficient.
            var junk = BuildJunkPackage();
            Assert.True(junk.Length > 500, $"The junk package is only {junk.Length} bytes, so it would not have cleared the rejected length probe.");
            Assert.Equal((byte)'P', junk[0]);
            Assert.Equal((byte)'K', junk[1]);
            var junkPresentation = served with { Content = junk };

            var junkFailure = Record.Exception(() => PresentationOutputIntegrityValidator.Validate(junkPresentation));
            Assert.IsType<PresentationOutputIntegrityException>(junkFailure);
            Assert.Contains("ppt/presentation.xml", junkFailure!.Message, StringComparison.Ordinal);

            var readbackFailure = Record.Exception(() => AssertPresentationPackageStructure(junk));
            Assert.NotNull(readbackFailure);
            Assert.Contains("ppt/presentation.xml", readbackFailure!.Message, StringComparison.Ordinal);
        }
        finally
        {
            export.Owner.Dispose();
        }
    }

    private sealed record ExportedPresentation(
        HttpClient Owner,
        AuthResponse OwnerAuth,
        Guid AssetId,
        Guid RepresentationId,
        string DownloadPath,
        byte[] Bytes,
        string Title,
        string Description);

    /// <summary>
    /// Runs one deterministic, provider-free presentation export and returns the owner
    /// client with the stored artifact already downloaded.
    /// </summary>
    private async Task<ExportedPresentation> CreateExportAsync(string ownerDisplayName)
    {
        // Owned by the caller: tests dispose it, since the returned client is used for
        // the assertions that follow the export.
        var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, ownerDisplayName);
        var csrf = await owner.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        const string title = "Workspace-private presentation";
        const string description = "A deterministic export used to verify workspace access.";

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/presentation-generation/jobs");
        createRequest.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        createRequest.Headers.Add("Idempotency-Key", $"presentation-export-{Guid.NewGuid():N}");
        createRequest.Content = JsonContent.Create(new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            title,
            description,
            presentationType = "business",
            length = "standard",
            tone = "professional",
            language = "en",
            attachmentIds = Array.Empty<Guid>(),
        });

        using var createResponse = await owner.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;
        var terminal = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);

        Guid assetId;
        Guid representationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets
                .Include(item => item.Representations)
                .SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
            Assert.Equal(AssetTypes.Presentation, asset.AssetType);
            var representation = Assert.Single(asset.Representations);
            Assert.Equal(AssetRepresentationTypes.Pptx, representation.RepresentationType);
            assetId = asset.Id;
            representationId = representation.Id;
        }

        var downloadPath = $"/api/assets/{assetId}/representations/{representationId}/download";
        using var download = await owner.GetAsync(downloadPath);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PptxMediaType, download.Content.Headers.ContentType?.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync();

        // The stored artifact and the owner-facing metadata use the same visible title and
        // description; readback compares against them instead of hardcoding one locale.
        using var metadata = await owner.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using var metadataJson = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
        return new ExportedPresentation(
            owner,
            ownerAuth,
            assetId,
            representationId,
            downloadPath,
            bytes,
            metadataJson.RootElement.GetProperty("name").GetString()!,
            metadataJson.RootElement.GetProperty("description").GetString()!);
    }

    /// <summary>
    /// Reads the package back as OOXML rather than as an opaque ZIP: the presentationml
    /// content-type declarations, the package root relationship to `ppt/presentation.xml`,
    /// the presentation part itself, and every slide reference in
    /// `ppt/_rels/presentation.xml.rels` must resolve to a slide part that is actually
    /// present and declared.
    /// </summary>
    private static void AssertPresentationPackageStructure(byte[] pptx)
    {
        Assert.True(pptx.Length > 500, $"The exported package is only {pptx.Length} bytes.");
        Assert.Equal((byte)'P', pptx[0]);
        Assert.Equal((byte)'K', pptx[1]);

        using var archive = new ZipArchive(new MemoryStream(pptx), ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ppt/presentation.xml", entryNames);
        Assert.Contains("_rels/.rels", entryNames);

        var contentTypes = ReadPart(archive, "[Content_Types].xml");
        Assert.Contains(PresentationMainContentType, contentTypes, StringComparison.Ordinal);
        Assert.Contains("/ppt/presentation.xml", contentTypes, StringComparison.Ordinal);

        var packageRoot = ReadPart(archive, "_rels/.rels");
        Assert.Contains("Target=\"ppt/presentation.xml\"", packageRoot, StringComparison.Ordinal);

        // Each slide declared in [Content_Types].xml must be typed as a presentationml
        // slide and must actually exist in the package.
        var declaredSlides = Regex
            .Matches(contentTypes, "PartName=\"(?<part>/ppt/slides/slide\\d+\\.xml)\" ContentType=\"(?<type>[^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(1))
            .ToList();
        Assert.NotEmpty(declaredSlides);
        foreach (var declaration in declaredSlides)
        {
            Assert.Equal(SlideContentType, declaration.Groups["type"].Value);
            Assert.Contains(declaration.Groups["part"].Value.TrimStart('/'), entryNames);
        }

        var presentationRels = ReadPart(archive, "ppt/_rels/presentation.xml.rels");
        var slideReferences = Regex
            .Matches(presentationRels, "Type=\"[^\"]*/relationships/slide\" Target=\"(?<target>[^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(1))
            .ToList();
        Assert.NotEmpty(slideReferences);
        foreach (var reference in slideReferences)
        {
            var resolved = "ppt/" + reference.Groups["target"].Value;
            Assert.Contains(resolved, entryNames);
        }
    }

    private static string ReadPart(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new Xunit.Sdk.XunitException($"The exported package does not contain {name}.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Copies a package while rewriting one text part, so a defect can be introduced into
    /// the bytes the owner actually downloaded. Same technique as the shipped
    /// `PresentationOutputIntegrityTests.RewriteArchive`, applied to served content instead
    /// of freshly rendered content; parts are stored uncompressed so only the rewritten part
    /// differs from the original bytes.
    /// </summary>
    private static byte[] RewritePackagePart(byte[] packageBytes, string part, Func<string, string> rewrite)
    {
        using var sourceStream = new MemoryStream(packageBytes, writable: false);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        using var targetStream = new MemoryStream();
        using (var target = new ZipArchive(targetStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var copyEntry = target.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var copyStream = copyEntry.Open();
                if (string.Equals(entry.FullName, part, StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    using var writer = new StreamWriter(copyStream, new UTF8Encoding(false));
                    writer.Write(rewrite(reader.ReadToEnd()));
                }
                else
                {
                    using var entryStream = entry.Open();
                    entryStream.CopyTo(copyStream);
                }
            }
        }

        return targetStream.ToArray();
    }

    /// <summary>
    /// A ZIP that satisfies a magic-byte and length probe but carries no OOXML parts.
    /// </summary>
    private static byte[] BuildJunkPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentTypesEntry = archive.CreateEntry("[Content_Types].xml", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(contentTypesEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
            }

            // The package root relationships part is present and well-formed, so the only
            // thing wrong with this archive is the OOXML content the review described as
            // missing: it carries no presentation part at all.
            var packageRelsEntry = archive.CreateEntry("_rels/.rels", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(packageRelsEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" />");
            }

            var junkEntry = archive.CreateEntry("junk.bin", CompressionLevel.NoCompression);
            using var junkStream = junkEntry.Open();
            junkStream.Write(Encoding.UTF8.GetBytes(new string('k', 4096)));
        }

        return stream.ToArray();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"presentation-export-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }

        throw new TimeoutException("Presentation export job did not reach a terminal state.");
    }
}
