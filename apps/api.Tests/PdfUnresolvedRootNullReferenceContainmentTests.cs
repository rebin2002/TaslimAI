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
/// The containment is scoped to the operation that produces it. <c>PdfDocument.Open</c> reads the
/// cross-reference table and resolves the catalog, and that is where an unresolvable <c>/Root</c>
/// surfaces; the page traversal and text assembly that follow are a separate concern, and a null
/// reference there is a fault rather than a malformed upload. Both halves are pinned from both
/// sides below, using two null references that PdfPig 0.1.9 really does raise at each site, so
/// neither assertion depends on a contrived fixture.
/// Kept separate from PdfParserExceptionExtractionBoundaryTests (the types converted there) and
/// from PdfUnresolvedCatalogExtractionBoundaryTests (the red probe for this case): this file is the
/// green side. Every input is synthetic bytes assembled in memory; no real document, upload or
/// stored object is used.
/// </summary>
public sealed class PdfUnresolvedRootNullReferenceContainmentTests
{
    private const string ContentStream = "BT /F1 12 Tf 20 250 Td (Probe text) Tj ET";

    private const string SoundFont = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>";

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

    // A font whose /Widths array names an object the cross-reference table never registers. The
    // catalog resolves, so the document opens, and the null reference arrives later, when the page
    // lays out the text that needs those widths. This is the shape that must not be mistaken for an
    // unreadable upload: it stands for every null reference raised after the parse point, whether
    // from the parser's own later stages or from application code walking the pages.
    private const string DanglingWidthsFont =
        "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/FirstChar 65/LastChar 66/Widths 9 0 R>>";

    private static FileContentExtractor Extractor() => new(Options.Create(new Taslim.Api.Files.FileOptions()));

    private static byte[] BuildDocument(string trailer, string font = SoundFont)
    {
        var objects = new (int Number, string Body)[]
        {
            (1, "<</Type/Catalog/Pages 2 0 R>>"),
            (2, "<</Type/Pages/Kids[3 0 R]/Count 1>>"),
            (3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>"),
            (4, $"<</Length {ContentStream.Length}>>stream\n{ContentStream}\nendstream\n"),
            (5, font),
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NullReferenceFromTheCatalogIsConvertedAtTheParsePoint(int variantIndex)
    {
        // Pins where the containment lives, not merely that it happens. Both unresolved /Root
        // variants are converted by the open operation itself, so a caller that only walks pages
        // never has to know about the catalog failure at all.
        var variant = UnresolvedRootVariants[variantIndex];
        using var stream = new MemoryStream(BuildDocument(variant.Trailer));

        var converted = Assert.Throws<InvalidDataException>(() => FileContentExtractor.OpenPdfDocument(stream));

        // Wrapped as the parser-type failure the boundary already understands, with the parser's
        // own null reference kept as the inner exception for diagnosis.
        Assert.IsType<NullReferenceException>(converted.InnerException);
    }

    [Fact]
    public async Task UnrelatedNullReferenceAfterTheParsePointEscapesTheContainment()
    {
        // The negative control, and the reason the conversion is scoped to a single call rather
        // than to the whole of ExtractPdf. This document's catalog resolves, so the open succeeds
        // and the fault belongs to the traversal half: PdfPig dereferences the unregistered /Widths
        // object while laying out the text, a NullReferenceException that says nothing about the
        // upload being unreadable. Containing it would answer a genuine defect with a normal
        // EXTRACTION_FAILED, which is exactly what the broad filter this narrows used to do.
        await using var stream = new MemoryStream(BuildDocument("<</Size 6/Root 1 0 R>>", DanglingWidthsFont));
        using (var probe = new MemoryStream(BuildDocument("<</Size 6/Root 1 0 R>>", DanglingWidthsFont)))
        {
            // Self-guard for the follow-up: the whole point is that this shape gets past the parse
            // point. If the open ever fails, the fault moved and this control proves nothing.
            using var opened = FileContentExtractor.OpenPdfDocument(probe);
            Assert.NotNull(opened);
        }

        var escaped = await Assert.ThrowsAsync<NullReferenceException>(() => Extractor().ExtractAsync(".pdf", stream));

        // Reached as itself rather than wrapped, so the outer filter in ExtractAsync does not
        // classify it either and FileProcessingService still sees an unexpected failure.
        Assert.IsType<NullReferenceException>(escaped);
        Assert.DoesNotContain("PDF could not be parsed", escaped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PageTraversalDoesNotConvertNullReferenceException()
    {
        // The stable side of the same guarantee, independent of any particular PdfPig build:
        // nothing in the traversal is wrapped in a null-reference conversion, so the shape above
        // cannot be re-contained by widening this method's filter back out.
        var escaped = Assert.Throws<NullReferenceException>(() => FileContentExtractor.RenderPdfPageText(null!));

        Assert.IsType<NullReferenceException>(escaped);
        Assert.DoesNotContain("PDF could not be parsed", escaped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullReferenceOutsidePdfParsingStillEscapesTheBoundary()
    {
        // The conversion was scoped inside ExtractPdf only. The boundary must keep letting an
        // unrelated null reference propagate so FileProcessingService's generic handler still sees
        // genuine defects instead of reporting every one of them as an unreadable upload.
        // The null extension dereferences before any parser is chosen, which keeps this outside
        // the PDF path without needing a contrived document.
        await using var stream = new MemoryStream(BuildDocument("<</Size 6/Root 1 0 R>>"));

        await Assert.ThrowsAsync<NullReferenceException>(() => Extractor().ExtractAsync(null!, stream));
    }
}
