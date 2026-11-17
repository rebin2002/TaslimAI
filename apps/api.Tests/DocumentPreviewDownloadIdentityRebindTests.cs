using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent, provider-free HTTP regression for the Document Studio reload contract: after the
/// browser is reloaded, the canonical preview read back from the persisted job result must rebind
/// to the very same authorized asset and representations it was generated with, and the download
/// identity (Content-Disposition name) must stay bound to those persisted references — including
/// right-to-left titles that must remain language-safe on the wire.
///
/// This complements, and does not replace, the owned suites:
/// - #746 DocumentPreviewResultBoundTests bounds the serialized preview (contract level, no HTTP).
/// - #755 DocumentFileNameTests encodes download names (unit level, never crosses the HTTP header).
/// - #722 DocumentWorkspaceReadDownloadAuthorizationTests only proves foreign reads are denied.
/// None of them parses the served result JSON envelope the way apps/web/src/lib/
/// documentStudioState.ts does, follows the parsed references back over HTTP, or compares the
/// resulting Content-Disposition identity across a simulated reload. That cross-layer invariant is
/// what this file pins. It is RED until the filename encoding fix (#755) lands with the persisted
/// preview work (#746), which is the point: the two unit suites compose cleanly on paper, but the
/// browser only ever sees the HTTP surface.
/// </summary>
public sealed class DocumentPreviewDownloadIdentityRebindTests : IClassFixture<DocumentGenerationApiFactory>
{
    // Mirrors isSafeDocumentIdentifier() in apps/web/src/lib/documentStudioState.ts.
    private static readonly Regex IdentifierGate = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Mirrors the rejection class of isSafeDocumentRepresentation(): separators and control
    // characters drop the whole representation, hiding the download button of a succeeded job.
    private static readonly Regex DownloadGateForbidden = new(@"[\\/\u0000-\u001f\u007f]", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> RepresentationContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["pdf"] = "application/pdf",
    };

    private readonly DocumentGenerationApiFactory factory;

    public DocumentPreviewDownloadIdentityRebindTests(DocumentGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Reloaded_job_preview_references_rebind_to_the_same_authorized_job_and_downloads()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner);
        var created = await CreateJob(owner, auth.PersonalWorkspace.Id, "Quarterly brief");
        var terminal = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), terminal.Status);

        // Reload #1: the browser only has the job id; everything else must come from the served
        // result JSON, parsed with the same gates documentStudioState.ts applies.
        var firstRead = await owner.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
        var envelope = ParseAndValidateEnvelope(firstRead!.ResultJson, created.Job.Id);

        // The persisted canonical preview must still render for the reloaded completion screen.
        using (var preview = JsonDocument.Parse(firstRead.ResultJson!))
        {
            Assert.Equal("Generated launch brief", preview.RootElement.GetProperty("title").GetString());
            Assert.Equal("document", preview.RootElement.GetProperty("documentType").GetString());
        }

        // The same envelope identity is still what a second reload serves.
        var secondRead = await owner.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal(firstRead.ResultJson, secondRead!.ResultJson);
        var rebound = ParseAndValidateEnvelope(secondRead.ResultJson, created.Job.Id);
        Assert.Equal(envelope.AssetId, rebound.AssetId);
        Assert.Equal(
            envelope.Representations.Select(item => (item.Id, item.Type, item.FileName)),
            rebound.Representations.Select(item => (item.Id, item.Type, item.FileName)));

        // Bound to the same authorized persisted job: the parsed asset is exactly the asset whose
        // SourceGenerationJobId is this job, with the same representation rows.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets
                .Include(item => item.Representations)
                .AsNoTracking()
                .SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
            Assert.Equal(asset.Id, envelope.AssetId);
            Assert.Equal(AssetTypes.Document, asset.AssetType);
            foreach (var representation in envelope.Representations)
            {
                var row = Assert.Single(asset.Representations, item => item.Id == representation.Id);
                Assert.Equal(representation.Type, row.RepresentationType);
                Assert.Equal(representation.FileName, row.FileName);
                Assert.Equal(representation.ContentType, row.ContentType);
            }
        }

        // Downloads performed strictly through the parsed references must succeed and identify the
        // persisted representation by name and media type.
        foreach (var representation in envelope.Representations)
        {
            var download = await owner.GetAsync(
                $"/api/assets/{envelope.AssetId}/representations/{representation.Id}/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(representation.ContentType, download.Content.Headers.ContentType?.MediaType);
            Assert.True((await download.Content.ReadAsByteArrayAsync()).Length > 100);
            var disposition = Assert.Single(download.Content.Headers.GetValues("Content-Disposition"));
            Assert.Equal(representation.FileName, DecodeDispositionName(disposition));
        }

        // Same job id, different principal: neither the preview references nor the bound downloads
        // are readable, so the binding is meaningful only to the authorized owner.
        using var outsider = factory.CreateClient();
        await Register(outsider);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/generation/jobs/{created.Job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.GetAsync(
                $"/api/assets/{envelope.AssetId}/representations/{envelope.Representations[0].Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Right_to_left_title_keeps_preview_download_identity_language_safe_across_reload()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner);
        // Sorani script plus a backslash: the backslash is legal on the exporter host but is a
        // path separator on the user's machine, and the web download gate drops any
        // representation whose name carries one.
        const string Title = "کورتەی هەفتانە \\ ڕاپۆرتی Q3";
        var created = await CreateJob(owner, auth.PersonalWorkspace.Id, Title);
        var terminal = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), terminal.Status);

        var reload = await owner.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
        var envelope = ParseEnvelopeShape(reload!.ResultJson, created.Job.Id);

        // Language-safe means preserved, not transliterated or stripped: the script itself must
        // survive into the persisted download names for both formats.
        Assert.Contains(envelope.Representations, item => item.Type == "docx" && item.FileName.Contains("کورتەی", StringComparison.Ordinal));
        Assert.Contains(envelope.Representations, item => item.Type == "pdf" && item.FileName.Contains("ڕاپۆرتی", StringComparison.Ordinal));

        // And every served name must still pass the browser download gate — otherwise the reload
        // renders a preview whose download buttons silently disappear.
        foreach (var representation in envelope.Representations)
        {
            Assert.False(DownloadGateForbidden.IsMatch(representation.FileName),
                $"the web download gate would drop '{representation.FileName}' for '{representation.Type}'");
        }

        // Wire safety: the bound download must answer with an ASCII-only header (RFC 5987 encoded
        // name) that decodes back to exactly the persisted representation name — twice, because a
        // reload re-issues the download from freshly parsed references.
        foreach (var representation in envelope.Representations)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var reread = await owner.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
                var rereadEnvelope = ParseEnvelopeShape(reread!.ResultJson, created.Job.Id);
                var bound = Assert.Single(rereadEnvelope.Representations, item => item.Type == representation.Type);
                Assert.Equal(representation.Id, bound.Id);
                Assert.Equal(representation.FileName, bound.FileName);

                var download = await owner.GetAsync(
                    $"/api/assets/{rereadEnvelope.AssetId}/representations/{bound.Id}/download");
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                var disposition = Assert.Single(download.Content.Headers.GetValues("Content-Disposition"));
                Assert.Matches("^[\x20-\x7E]+$", disposition);
                var parsed = ContentDispositionHeaderValue.Parse(disposition);
                Assert.NotNull(parsed.FileNameStar);
                Assert.Equal(bound.FileName, DecodeDispositionName(disposition));
            }
        }
    }

    [Fact]
    public async Task Two_completed_jobs_never_rebind_the_earlier_preview_to_the_later_asset()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner);
        var alpha = await CreateJob(owner, auth.PersonalWorkspace.Id, "Alpha launch brief");
        var alphaTerminal = await WaitForTerminal(owner, alpha.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), alphaTerminal.Status);

        var beta = await CreateJob(owner, auth.PersonalWorkspace.Id, "Beta roadmap brief");
        var betaTerminal = await WaitForTerminal(owner, beta.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), betaTerminal.Status);

        // Both reads happen after the second job completed: the earlier job's persisted envelope
        // must still point at its own asset and its own names.
        var alphaRead = await owner.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{alpha.Job.Id}");
        var betaRead = await owner.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{beta.Job.Id}");
        var alphaEnvelope = ParseEnvelopeShape(alphaRead!.ResultJson, alpha.Job.Id);
        var betaEnvelope = ParseEnvelopeShape(betaRead!.ResultJson, beta.Job.Id);
        Assert.NotEqual(alphaEnvelope.AssetId, betaEnvelope.AssetId);

        var alphaDownload = await owner.GetAsync(
            $"/api/assets/{alphaEnvelope.AssetId}/representations/{alphaEnvelope.Representations[0].Id}/download");
        Assert.Equal(HttpStatusCode.OK, alphaDownload.StatusCode);
        var alphaName = DecodeDispositionName(
            Assert.Single(alphaDownload.Content.Headers.GetValues("Content-Disposition")));
        Assert.Contains("Alpha", alphaName, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta", alphaName, StringComparison.Ordinal);
    }

    private sealed record DocumentEnvelope(Guid AssetId, IReadOnlyList<ServedRepresentation> Representations);
    private sealed record ServedRepresentation(Guid Id, string Type, string FileName, string ContentType);

    /// <summary>
    /// Resolves the wire name the browser would save: RFC 5987 filename* when the name carries
    /// non-ASCII characters, the plain filename fallback otherwise.
    /// </summary>
    private static string DecodeDispositionName(string disposition)
    {
        var parsed = ContentDispositionHeaderValue.Parse(disposition);
        var name = parsed.FileNameStar ?? parsed.FileName;
        Assert.False(string.IsNullOrWhiteSpace(name), $"no download name was served in '{disposition}'");
        return name!;
    }

    /// <summary>
    /// Parses the served result JSON exactly the way apps/web/src/lib/documentStudioState.ts does
    /// after a reload and additionally requires the full bound reference set to be present. Any
    /// representation the browser would drop (gate failure) makes the preview un-downloadable and
    /// is therefore a regression, not a tolerated shape.
    /// </summary>
    private static DocumentEnvelope ParseEnvelopeShape(string? resultJson, Guid jobId)
    {
        Assert.False(string.IsNullOrWhiteSpace(resultJson), $"job {jobId} served no result JSON");
        using var document = JsonDocument.Parse(resultJson!);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("assetId", out var assetIdElement),
            $"job {jobId} persisted no assetId binding in the preview envelope");
        var assetId = assetIdElement.GetGuid();
        Assert.NotEqual(Guid.Empty, assetId);
        Assert.True(root.TryGetProperty("representations", out var representationsElement)
            && representationsElement.ValueKind == JsonValueKind.Array
            && representationsElement.GetArrayLength() == 2,
            $"job {jobId} must serve exactly the two persisted representations");

        var representations = new List<ServedRepresentation>();
        foreach (var item in representationsElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetGuid();
            var type = item.GetProperty("type").GetString()!;
            var fileName = item.GetProperty("fileName").GetString()!;
            var contentType = item.GetProperty("contentType").GetString()!;
            representations.Add(new ServedRepresentation(id, type, fileName, contentType));
        }

        return new DocumentEnvelope(assetId, representations);
    }

    private static DocumentEnvelope ParseAndValidateEnvelope(string? resultJson, Guid jobId)
    {
        var envelope = ParseEnvelopeShape(resultJson, jobId);
        Assert.Equal(
            new[] { "docx", "pdf" },
            envelope.Representations.Select(item => item.Type).OrderBy(item => item, StringComparer.Ordinal));
        foreach (var representation in envelope.Representations)
        {
            Assert.Matches(IdentifierGate, representation.Id.ToString("D"));
            Assert.True(representation.FileName.Length > representation.Type.Length + 1
                && representation.FileName.Length <= 255,
                $"'{representation.FileName}' breaks the 255-character gate limit");
            Assert.False(DownloadGateForbidden.IsMatch(representation.FileName),
                $"the web download gate would drop '{representation.FileName}' for '{representation.Type}'");
            Assert.EndsWith($".{representation.Type}", representation.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RepresentationContentTypes[representation.Type], representation.ContentType);
        }

        return envelope;
    }

    private async Task<CreateDocumentGenerationResponse> CreateJob(HttpClient client, Guid workspaceId, string title)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId,
            title,
            prompt = "Create a concise professional short document.",
            documentType = "report",
            length = "short",
            tone = "professional",
            outputFormat = "both",
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;
    }

    private async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Document job {id} did not reach a terminal state in the provider-free harness.");
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Document Rebind Tester",
            email = $"document-rebind-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
