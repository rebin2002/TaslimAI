using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using UglyToad.PdfPig;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Follow-up boundary probe for #911, covering the one fail-closed case that PR does not reach:
/// a PDF whose trailer resolves <b>no catalog at all</b>. #911 pins the shapes where PdfPig
/// raises <c>PdfDocumentFormatException</c> (unreachable startxref, wrong object type for the root,
/// bad font resource) and the zero-length <c>ArgumentOutOfRangeException</c>. Those conversions do
/// not cover the catalog-lookup path: when <c>/Root</c> names an object number the cross-reference
/// table never registers (a number the document does not define, or the free-object number 0),
/// PdfPig dereferences the absent object and throws <c>NullReferenceException</c> from
/// <c>PdfDocumentFactory.ParseTrailer</c> (through <c>OpenDocument</c>). That type is neither
/// converted by
/// <c>FileContentExtractor.ExtractPdf</c> nor listed in the <c>ExtractAsync</c> filter, so it
/// escapes the extraction boundary and is routed through FileProcessingService's generic
/// unexpected-failure path, which deletes the stored object instead of keeping it like every
/// other extraction failure.
///
/// Status: the characterization test passes on main and on #911 head. The contract test is red by
/// design on both - it is the reproduction for the follow-up, not a claim about merged behaviour.
/// Inputs are synthetic bytes built in memory; no real document, report or stored object is used.
/// </summary>
public sealed class PdfUnresolvedCatalogExtractionBoundaryTests
{
    private const string ContentStream = "BT /F1 12 Tf 20 250 Td (Probe text) Tj ET";

    // Trailer variants that resolve to no catalog object. Both documents are otherwise well
    // formed: the objects, the cross-reference rows and the startxref offset all point where they
    // should, so the only fault is the reference the trailer cannot resolve.
    private static readonly (string Name, string Trailer)[] UnresolvedRootVariants =
    [
        // /Root names an object number that the document never defines.
        ("absent-object-number", "<</Size 6/Root 42 0 R>>"),
        // /Root names object 0, the free-object slot, which can never be a catalog dictionary.
        ("free-object-number", "<</Size 6/Root 0 0 R>>"),
    ];

    // Exception types contained by the boundary today: the outer filter of
    // FileContentExtractor.ExtractAsync plus the two types converted by #911 in ExtractPdf.
    private static readonly string[] ContainedExceptionTypes =
    [
        nameof(IOException), nameof(InvalidDataException), nameof(FormatException),
        nameof(InvalidOperationException), nameof(XmlException),
        nameof(UglyToad.PdfPig.Core.PdfDocumentFormatException), nameof(ArgumentOutOfRangeException),
    ];

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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PdfParserSignalsUnresolvedCatalogWithNullReferenceException(int variantIndex)
    {
        // Characterization of the behaviour that makes #911's narrow conversion incomplete. It is
        // deliberately checked against the runtime type rather than a compile-time pattern, so it
        // keeps describing what the parser does instead of what the boundary happens to contain.
        var (name, trailer) = UnresolvedRootVariants[variantIndex];
        using var stream = new MemoryStream(BuildDocument(trailer));
        var exception = Assert.Throws<NullReferenceException>(() =>
        {
            using var document = PdfDocument.Open(stream);
            foreach (var page in document.GetPages())
            {
                _ = page.Text;
            }
        });

        // A self-guard for the follow-up: if PdfPig ever starts reporting this shape with a type
        // the boundary already contains, the escape is gone and these tests should be retired.
        if (ContainedExceptionTypes.Contains(exception.GetType().Name, StringComparer.Ordinal))
            Assert.Fail($"{name}: the parser now reports an unresolved catalog as {exception.GetType().Name}, which the extraction boundary already contains.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnresolvedCatalogPdfEndsAsBoundedExtractionFailure(int variantIndex)
    {
        // The contract from #179, restated by #911: a structurally malformed upload becomes a
        // bounded Failed result with the generic EXTRACTION_FAILED code, classified at the
        // extraction boundary. Red by design on main and on #911 head: the NullReferenceException
        // from PdfDocument.Open propagates out of ExtractAsync instead.
        var extractor = new FileContentExtractor(Options.Create(new Taslim.Api.Files.FileOptions()));
        await using var stream = new MemoryStream(BuildDocument(UnresolvedRootVariants[variantIndex].Trailer));
        var result = await extractor.ExtractAsync(".pdf", stream);
        Assert.Equal(FileExtractionStatus.Failed, result.Status);
        Assert.Equal("EXTRACTION_FAILED", result.FailureCode);
        Assert.Null(result.ExtractedText);
    }
}
