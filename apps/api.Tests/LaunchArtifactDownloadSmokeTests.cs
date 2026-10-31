using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Exercises the real authenticated asset download boundary for deterministic,
/// test-host-generated artifacts. Existing modality tests cover job creation and
/// persistence; this smoke suite joins those results to artifact bytes and access scope.
/// </summary>
public sealed class LaunchArtifactDownloadSmokeTests :
    IClassFixture<ImageGenerationApiFactory>,
    IClassFixture<DocumentGenerationApiFactory>,
    IClassFixture<PresentationGenerationApiFactory>
{
    private readonly ImageGenerationApiFactory imageFactory;
    private readonly DocumentGenerationApiFactory documentFactory;
    private readonly PresentationGenerationApiFactory presentationFactory;

    public LaunchArtifactDownloadSmokeTests(
        ImageGenerationApiFactory imageFactory,
        DocumentGenerationApiFactory documentFactory,
        PresentationGenerationApiFactory presentationFactory)
    {
        this.imageFactory = imageFactory;
        this.documentFactory = documentFactory;
        this.presentationFactory = presentationFactory;
        EnsureDatabase(imageFactory);
        EnsureDatabase(documentFactory);
        EnsureDatabase(presentationFactory);
    }

    [Fact]
    public async Task Image_generation_downloads_png_and_denies_a_different_user()
    {
        using var owner = imageFactory.CreateClient();
        var auth = await Register(owner);
        using var create = await SendWithCsrf(owner, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "QA image",
            description = "A deterministic local QA image.",
            style = "illustration",
            aspectRatio = "square",
            quality = "standard",
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CreateImageGenerationResponse>())!;
        var job = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), job.Status);

        var asset = await LoadAsset(imageFactory, job.Id);
        Assert.Equal(AssetTypes.Image, asset.AssetType);
        Assert.Equal("image/png", asset.MimeType);
        Assert.NotNull(asset.StoredFile);

        using var download = await owner.GetAsync($"/api/assets/{asset.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        AssertAttachmentName(download, ".png");
        var png = await download.Content.ReadAsByteArrayAsync();
        Assert.True(png.Length > 8);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());

        using var anonymous = imageFactory.CreateClient();
        using (var deniedAnonymous = await anonymous.GetAsync($"/api/assets/{asset.Id}/download"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, deniedAnonymous.StatusCode);
            Assert.Null(deniedAnonymous.Content.Headers.ContentDisposition);
        }
        using (var deniedInline = await anonymous.GetAsync($"/api/assets/{asset.Id}/download?inline=true"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, deniedInline.StatusCode);
            Assert.Null(deniedInline.Content.Headers.ContentDisposition);
        }

        using var other = imageFactory.CreateClient();
        await Register(other);
        await AssertAssetMetadataPrivacy(owner, anonymous, other, asset.Id);
        // Authorization must precede range handling; a partial request cannot leak PNG bytes.
        await AssertDeniedPartialDownload(anonymous, $"/api/assets/{asset.Id}/download", HttpStatusCode.Unauthorized);
        await AssertDeniedPartialDownload(other, $"/api/assets/{asset.Id}/download", HttpStatusCode.NotFound);
        using var denied = await other.GetAsync($"/api/assets/{asset.Id}/download");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task Document_docx_and_pdf_representations_download_and_deny_a_different_user()
    {
        using var owner = documentFactory.CreateClient();
        var auth = await Register(owner);
        using var create = await SendWithCsrf(owner, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "QA document",
            prompt = "Create a short deterministic document for a launch smoke test.",
            language = "en",
            outputFormat = "both",
            tone = "professional",
            includeTableOfContents = false,
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;
        var job = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), job.Status);

        var asset = await LoadAsset(documentFactory, job.Id);
        Assert.Equal(AssetTypes.Document, asset.AssetType);
        Assert.Equal(2, asset.Representations.Count);
        var docx = Assert.Single(asset.Representations, item => item.RepresentationType == AssetRepresentationTypes.Docx);
        var pdf = Assert.Single(asset.Representations, item => item.RepresentationType == AssetRepresentationTypes.Pdf);

        using var anonymous = documentFactory.CreateClient();
        foreach (var representation in asset.Representations)
        {
            using var deniedAnonymous = await anonymous.GetAsync($"/api/assets/{asset.Id}/representations/{representation.Id}/download");
            Assert.Equal(HttpStatusCode.Unauthorized, deniedAnonymous.StatusCode);
            Assert.Null(deniedAnonymous.Content.Headers.ContentDisposition);
        }

        using var other = documentFactory.CreateClient();
        var otherAuth = await Register(other);
        await AssertAssetMetadataPrivacy(owner, anonymous, other, asset.Id);
        foreach (var representation in asset.Representations)
        {
            var path = $"/api/assets/{asset.Id}/representations/{representation.Id}/download";
            await AssertDeniedPartialDownload(anonymous, path, HttpStatusCode.Unauthorized);
            await AssertDeniedPartialDownload(other, path, HttpStatusCode.NotFound);
        }
        await AssertRepresentationDownload(owner, other, asset.Id, docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx", "PK");
        await AssertRepresentationDownload(owner, other, asset.Id, pdf, "application/pdf", ".pdf", "%PDF-");
        
        // A representation identifier is scoped to its owning asset. An authorized
        // parent asset must not become a route to another user's PDF (IDOR).
        using var otherCreate = await SendWithCsrf(other, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = otherAuth.PersonalWorkspace.Id,
            title = "Other user's QA document",
            prompt = "Create a different deterministic document for cross-asset access testing.",
            language = "en",
            outputFormat = "both",
            tone = "professional",
            includeTableOfContents = false,
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, otherCreate.StatusCode);
        var otherCreated = (await otherCreate.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;
        var otherJob = await WaitForTerminal(other, otherCreated.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), otherJob.Status);
        var otherAsset = await LoadAsset(documentFactory, otherJob.Id);
        var otherPdf = Assert.Single(otherAsset.Representations,
            item => item.RepresentationType == AssetRepresentationTypes.Pdf);
        
        using (var otherOwnsPdf = await other.GetAsync($"/api/assets/{otherAsset.Id}/representations/{otherPdf.Id}/download"))
            Assert.Equal(HttpStatusCode.OK, otherOwnsPdf.StatusCode);

        // Both parents exist, both representation IDs exist, and each valid pair works.
        // Neither caller may substitute the PDF ID from the other parent's record.
        using (var mixedOwner = await owner.GetAsync($"/api/assets/{asset.Id}/representations/{otherPdf.Id}/download"))
        {
            Assert.Equal(HttpStatusCode.NotFound, mixedOwner.StatusCode);
            Assert.Null(mixedOwner.Content.Headers.ContentDisposition);
        }
        using (var mixedOther = await other.GetAsync($"/api/assets/{otherAsset.Id}/representations/{pdf.Id}/download"))
        {
            Assert.Equal(HttpStatusCode.NotFound, mixedOther.StatusCode);
            Assert.Null(mixedOther.Content.Headers.ContentDisposition);
        }
    }

    [Fact]
    public async Task Presentation_pptx_representation_downloads_and_denies_a_different_user()
    {
        using var owner = presentationFactory.CreateClient();
        var auth = await Register(owner);
        using var create = await SendWithCsrf(owner, HttpMethod.Post, "/api/presentation-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "QA presentation",
            description = "A deterministic presentation for a launch smoke test.",
            presentationType = "business",
            length = "standard",
            tone = "professional",
            language = "en",
            includeAgenda = true,
            includeClosingNextSteps = true,
            attachmentIds = Array.Empty<Guid>(),
        }, $"launch-smoke-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;
        var job = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), job.Status);

        var asset = await LoadAsset(presentationFactory, job.Id);
        Assert.Equal(AssetTypes.Presentation, asset.AssetType);
        var pptx = Assert.Single(asset.Representations, item => item.RepresentationType == AssetRepresentationTypes.Pptx);

        using var anonymous = presentationFactory.CreateClient();
        using (var deniedAnonymous = await anonymous.GetAsync($"/api/assets/{asset.Id}/representations/{pptx.Id}/download"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, deniedAnonymous.StatusCode);
            Assert.Null(deniedAnonymous.Content.Headers.ContentDisposition);
        }

        using var other = presentationFactory.CreateClient();
        await Register(other);
        await AssertAssetMetadataPrivacy(owner, anonymous, other, asset.Id);
        var downloadPath = $"/api/assets/{asset.Id}/representations/{pptx.Id}/download";
        await AssertDeniedPartialDownload(anonymous, downloadPath, HttpStatusCode.Unauthorized);
        await AssertDeniedPartialDownload(other, downloadPath, HttpStatusCode.NotFound);
        await AssertRepresentationDownload(owner, other, asset.Id, pptx,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx", "PK");
    }

    private static async Task AssertAssetMetadataPrivacy(HttpClient owner, HttpClient anonymous, HttpClient other, Guid assetId)
    {
        // Positive control: the owner can read the metadata while other callers cannot.
        using var ownerResponse = await owner.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);

        // The same private artifact must hide its metadata as well as its file bytes.
        using var deniedAnonymous = await anonymous.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedAnonymous.StatusCode);

        // Do not disclose whether another workspace's asset exists.
        using var deniedOther = await other.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.NotFound, deniedOther.StatusCode);
    }

    private static async Task AssertDeniedPartialDownload(HttpClient client, string path, HttpStatusCode expectedStatus)
    {
        // The endpoint enables byte ranges for legitimate clients. Unauthenticated or
        // unrelated users must never receive a partial (206) response or file headers.
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(0, 7);
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    private static void EnsureDatabase(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Launch QA User",
            email = $"launch-qa-{Guid.NewGuid():N}@example.test",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload, string? idempotencyKey = null)
    {
        using var csrfResponse = await client.GetAsync("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        using var csrfDocument = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
        var token = csrfDocument.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }

    private static async Task<Asset> LoadAsset(WebApplicationFactory<Program> factory, Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.Assets
            .AsNoTracking()
            .Include(item => item.StoredFile)
            .Include(item => item.Representations)
            .ThenInclude(item => item.StoredFile)
            .SingleAsync(item => item.SourceGenerationJobId == jobId);
    }

    private static async Task AssertRepresentationDownload(
        HttpClient owner,
        HttpClient other,
        Guid assetId,
        AssetRepresentation representation,
        string expectedContentType,
        string expectedExtension,
        string expectedSignature)
    {
        using var download = await owner.GetAsync($"/api/assets/{assetId}/representations/{representation.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(expectedContentType, download.Content.Headers.ContentType?.MediaType);
        AssertAttachmentName(download, expectedExtension);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 8);
        Assert.StartsWith(expectedSignature, System.Text.Encoding.ASCII.GetString(bytes, 0, expectedSignature.Length));

        using var denied = await other.GetAsync($"/api/assets/{assetId}/representations/{representation.Id}/download");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private static void AssertAttachmentName(HttpResponseMessage response, string expectedExtension)
    {
        ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        var filename = (disposition.FileNameStar ?? disposition.FileName)?.Trim('"');
        Assert.False(string.IsNullOrWhiteSpace(filename));
        Assert.EndsWith(expectedExtension, filename, StringComparison.OrdinalIgnoreCase);
    }
}
