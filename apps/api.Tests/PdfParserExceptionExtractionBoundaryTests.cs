using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// Boundary contract for uploaded PDFs: admission accepts any file whose first bytes are the
/// "%PDF-" magic, so every structurally malformed PDF reaches the extraction layer. The
/// documented contract (merged #179: "parser/extraction failures remain a created Failed file
/// without exposing parser details") is that such a file ends as FileExtractionStatus.Failed with
/// the generic EXTRACTION_FAILED code. These regressions pin that contract for the PDF parser's
/// own exception types, using only synthetic malformed documents; no real-world file is needed to
/// reproduce the escape.
/// </summary>
public sealed class PdfParserExceptionExtractionBoundaryTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public PdfParserExceptionExtractionBoundaryTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // Every variant below starts with the real "%PDF-" magic (so it passes upload admission) but is
    // structurally broken. All of them are produced inline; none is user data.
    private static readonly (string Name, string Pdf)[] MalformedVariants =
    [
        // No startxref at all: the parser fails while locating the cross-reference section.
        ("no-startxref", "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n"),
        // Header followed by garbage: the trailer scan finds no startxref within its window.
        ("header-garbage", "%PDF-1.7\nGARBAGEGARBAGEGARBAGE\n"),
        // startxref points past the end of the file, so the catalog lookup fails.
        ("bad-xref-offset", "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\nxref\n0 2\n0000000000 65535 f \ntrailer<</Size 2/Root 1 0 R>>\nstartxref\n99999\n%%EOF\n"),
        // Root resolves to an array instead of the expected catalog dictionary.
        ("array-root", "%PDF-1.4\n1 0 obj[1 2 3]endobj\nxref\n0 2\n0000000000 65535 f \n0000000009 00000 n \ntrailer<</Size 2/Root 1 0 R>>\nstartxref\n20\n%%EOF\n"),
        // A font resource that is a stream object instead of a font dictionary.
        ("font-is-stream", "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>endobj\n4 0 obj<</Length 4>>stream\njunk\nendstream\nendobj\n5 0 obj<</Length 21>>stream\nBT /F1 12 Tf (x) Tj ET\nendstream\nendobj\nxref\n0 6\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n0000000268 00000 n \n0000000347 00000 n \ntrailer<</Size 6/Root 1 0 R>>\nstartxref\n436\n%%EOF\n"),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PdfParserFailsMalformedInputWithItsOwnDocumentFormatException(int variantIndex)
    {
        // Records the parser behaviour that makes the boundary contract load-bearing: these inputs
        // surface as IOException/InvalidDataException/FormatException, and some only fail once the
        // pages are enumerated, so a filter that omits the parser's own type lets it escape the
        // extraction layer.
        var (name, pdf) = MalformedVariants[variantIndex];
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(pdf));
        var exception = Assert.Throws<UglyToad.PdfPig.Core.PdfDocumentFormatException>(() =>
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(stream);
            foreach (var page in document.GetPages())
            {
                _ = page.Text;
            }
        });
        Assert.False(exception is InvalidDataException, $"{name}: parser exception unexpectedly derives from InvalidDataException; re-check the boundary filter.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task MalformedPdfEndsAsBoundedExtractionFailureOnSeekableStreams(int variantIndex)
    {
        var pdf = MalformedVariants[variantIndex].Pdf;
        var extractor = factory.Services.GetRequiredService<IFileContentExtractor>();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(pdf));
        var result = await extractor.ExtractAsync(".pdf", stream);
        Assert.Equal(FileExtractionStatus.Failed, result.Status);
        Assert.Equal("EXTRACTION_FAILED", result.FailureCode);
        Assert.Null(result.ExtractedText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task MalformedPdfEndsAsBoundedExtractionFailureOnNonSeekableStreams(int variantIndex)
    {
        // The read-back path may hand the extractor a forward-only stream; the staging copy must
        // bound it and the failure contract must be identical.
        var pdf = MalformedVariants[variantIndex].Pdf;
        var extractor = factory.Services.GetRequiredService<IFileContentExtractor>();
        await using var stream = new ForwardOnlyStream(Encoding.ASCII.GetBytes(pdf));
        var result = await extractor.ExtractAsync(".pdf", stream);
        Assert.Equal(FileExtractionStatus.Failed, result.Status);
        Assert.Equal("EXTRACTION_FAILED", result.FailureCode);
    }

    [Fact]
    public async Task ZeroBytePdfStreamEndsAsBoundedExtractionFailure()
    {
        // A zero-length stream cannot come from the upload endpoint (empty files are rejected at
        // admission), but the extractor boundary must not depend on that guarantee: the parser's
        // arithmetic on a negative window must be contained like every other malformed input.
        var extractor = factory.Services.GetRequiredService<IFileContentExtractor>();
        await using var empty = new MemoryStream();
        var result = await extractor.ExtractAsync(".pdf", empty);
        Assert.Equal(FileExtractionStatus.Failed, result.Status);
        Assert.Equal("EXTRACTION_FAILED", result.FailureCode);
    }

    [Fact]
    public async Task WellFormedPdfStillExtractsReadyThroughTheSameBoundary()
    {
        // Control: the containment must not reject a syntactically valid PDF.
        const string pdf =
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n4 0 obj<</Length 44>>stream\nBT /F1 12 Tf 20 250 Td (Hello probe) Tj ET\nendstream\nendobj\n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\nxref\n0 6\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n0000000241 00000 n \n0000000335 00000 n \ntrailer<</Size 6/Root 1 0 R>>\nstartxref\n409\n%%EOF\n";
        var extractor = factory.Services.GetRequiredService<IFileContentExtractor>();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(pdf));
        var result = await extractor.ExtractAsync(".pdf", stream);
        Assert.Equal(FileExtractionStatus.Ready, result.Status);
        Assert.Contains("Hello probe", result.ExtractedText);
    }

    [Fact]
    public async Task UploadOfAdmittedMalformedPdfBecomesCreatedFailedFileWithoutParserDetails()
    {
        // End-to-end: the file passes the "%PDF-" admission signature, fails structurally inside
        // the parser, and the documented lifecycle must apply: 201 Created with Failed/Failed and
        // no parser diagnostic (which embeds raw tokens from the untrusted document) in the body.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pdf Boundary Owner");
        var response = await UploadPdf(client, auth.PersonalWorkspace.Id, "report.pdf", MalformedVariants[1].Pdf);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("Failed", root.GetProperty("status").GetString());
        Assert.Equal("Failed", root.GetProperty("textExtractionStatus").GetString());
        Assert.False(root.TryGetProperty("extractedTextLength", out var length) && length.ValueKind != JsonValueKind.Null);
        Assert.DoesNotContain("startxref", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GARBAGE", body, StringComparison.Ordinal);

        // Discriminates the two failure routes. A contained extraction failure keeps the stored
        // object, exactly like every other extraction failure on main (empty text, unreadable
        // archive). The unexpected-failure fallback instead deletes the stored object and logs the
        // parser exception, whose message quotes tokens from the uploaded document. Retention here
        // therefore proves the malformed PDF was classified at the extraction boundary.
        var fileId = root.GetProperty("id").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var stored = await db.StoredFiles.AsNoTracking().SingleAsync(item => item.Id == fileId);
        Assert.Equal(FileExtractionStatus.Failed, stored.TextExtractionStatus);
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        Assert.True(await storage.ExistsAsync(stored.StorageKey),
            "the malformed PDF was routed through the unexpected-failure fallback instead of the extraction boundary");
    }

    private async Task<AuthResponse> Register(HttpClient client, string name)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = name,
            email = $"pdf-boundary-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> UploadPdf(HttpClient client, Guid workspaceId, string fileName, string content)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int index;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => index; set => throw new NotSupportedException(); }
        public override int Read(Span<byte> buffer)
        {
            if (index >= data.Length) return 0;
            var count = Math.Min(buffer.Length, data.Length - index);
            data.AsSpan(index, count).CopyTo(buffer);
            index += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
