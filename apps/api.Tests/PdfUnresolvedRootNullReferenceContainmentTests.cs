using System.Text;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Containment for the one unresolved-catalog shape that escapes the extraction boundary: a
/// trailer whose <c>/Root</c> names an object the cross-reference table never registers. PdfPig
/// dereferences the absent object while resolving the catalog and reports it as a
/// <see cref="NullReferenceException"/> from <c>PdfDocumentFactory.ParseTrailer</c>, a type that
/// neither the boundary's format-exception filter nor the parser-specific conversion recognises,
/// so the exception propagates out of <see cref="FileContentExtractor.ExtractAsync"/> instead of
/// ending as the bounded <c>EXTRACTION_FAILED</c> result. The generic failure path in
/// FileProcessingService still marks the file Failed, so the observable defect is that the upload
/// is classified by the unexpected-failure handler rather than at the boundary, and the classified
/// extraction-failure log line carrying <c>FailureCode</c> is never reached.
/// Kept separate from PdfParserExceptionExtractionBoundaryTests (the types converted there) and
/// from PdfUnresolvedCatalogExtractionBoundaryTests (the red probe for this case): this file is the
/// green side, and it also pins that the widening did not turn the boundary into a blanket
/// null-reference catch. Every input is synthetic bytes assembled in memory; no real document,
/// upload or stored object is used.
/// </summary>
public sealed class PdfUnresolvedRootNullReferenceContainmentTests
{
    private const string ContentStream = "BT /F1 12 Tf 20 250 Td (Probe text) Tj ET";

    // Both documents are otherwise well formed: the objects, the cross-reference rows and the
    // startxref offset all point where they should, so the only fault is the reference the
    // trailer cannot resolve.
    private static readonly (string Name, string Trailer)[] UnresolvedRootVariants =
    [
        // /Root names an object number that the document never defines.
        ("absent-object-number", "<</Size 6/Root 42 0 R>>"),
        // /Root names object 0, the free-object slot, which can never be a catalog dictionary.
        ("free-object-number", "<</Size 6/Root 0 0 R>>"),
    ];

    private static FileContentExtractor Extractor() => new(Options.Create(new Taslim.Api.Files.FileOptions()));

    private static byte[] BuildDocument(string trailer)
    {
        var objects = new (int Number, string Body)[]
        {
            (1, "<</Type/Catalog/Pages 2 0 R>>"),
            (2, "<</Type/Pages/Kids[3 0 R]/Count 1>>"),
            (3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>"),
            (4, $"<</Length {ContentStream.Length}>>stream\n{ContentStream}\nendstream\n"),
            (5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>"),
        };

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index] = builder.Length;
            builder.Append($"{objects[index].Number} 0 obj{objects[index].Body}endobj\n");
        }

        var crossReferenceAt = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer{trailer}\nstartxref\n{crossReferenceAt}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    [Fact]
    public async Task SameDocumentWithResolvableRootStillExtracts()
    {
        // Guards the fixture itself: identical bytes apart from the trailer reference, so a
        // bounded failure below is caused by the unresolved /Root and not by a malformed probe.
        await using var stream = new MemoryStream(BuildDocument("<</Size 6/Root 1 0 R>>"));
        var result = await Extractor().ExtractAsync(".pdf", stream);

        Assert.Equal(FileExtractionStatus.Ready, result.Status);
        Assert.Null(result.FailureCode);
        Assert.Contains("Probe text", result.ExtractedText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnresolvedRootPdfEndsAsBoundedExtractionFailure(int variantIndex)
    {
        var (name, trailer) = UnresolvedRootVariants[variantIndex];
        await using var stream = new MemoryStream(BuildDocument(trailer));

        // Fails on main and on the #911 head with an unhandled NullReferenceException; passes
        // once the parser-specific conversion recognises the catalog-resolution failure.
        var result = await Extractor().ExtractAsync(".pdf", stream);

        Assert.True(result.Status == FileExtractionStatus.Failed,
            $"{name}: expected a bounded Failed result, got {result.Status} with code {result.FailureCode ?? "(none)"}.");
        Assert.Equal("EXTRACTION_FAILED", result.FailureCode);
        Assert.Null(result.ExtractedText);
    }

    [Fact]
    public async Task NullReferenceOutsidePdfParsingStillEscapesTheBoundary()
    {
        // The conversion was widened inside ExtractPdf only. The boundary must keep letting an
        // unrelated null reference propagate so FileProcessingService's generic handler still sees
        // genuine defects instead of reporting every one of them as an unreadable upload.
        // The null extension dereferences before any parser is chosen, which keeps this outside
        // the PDF path without needing a contrived document.
        await using var stream = new MemoryStream(BuildDocument("<</Size 6/Root 1 0 R>>"));

        await Assert.ThrowsAsync<NullReferenceException>(() => Extractor().ExtractAsync(null!, stream));
    }
}
