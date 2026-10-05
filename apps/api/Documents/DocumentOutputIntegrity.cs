using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Taslim.Api.Domain;
using UglyToad.PdfPig;
namespace Taslim.Api.Documents;
public sealed class DocumentOutputIntegrityException(string message, Exception? innerException = null) : Exception(message, innerException);
public static class DocumentOutputIntegrityValidator
{
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public static void Validate(RenderedDocument rendered)
    {
        var isDocx = string.Equals(rendered.RepresentationType, AssetRepresentationTypes.Docx, StringComparison.OrdinalIgnoreCase);
        var isPdf = string.Equals(rendered.RepresentationType, AssetRepresentationTypes.Pdf, StringComparison.OrdinalIgnoreCase);
        var expectedType = isDocx ? DocxContentType : isPdf ? "application/pdf" : null;
        var expectedExtension = isDocx ? ".docx" : isPdf ? ".pdf" : null;
        if (expectedType is null || expectedExtension is null
            || !string.Equals(rendered.ContentType, expectedType, StringComparison.OrdinalIgnoreCase)
            || !rendered.FileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new DocumentOutputIntegrityException("The document output metadata is invalid.");
        if (rendered.Content.Length == 0)
            throw new DocumentOutputIntegrityException("The document output is empty.");
        try
        {
            if (isDocx) ValidateDocx(rendered.Content);
            else ValidatePdf(rendered.Content);
        }
        catch (DocumentOutputIntegrityException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentOutputIntegrityException("The document output could not be read safely.", exception);
        }
    }
    private static void ValidateDocx(byte[] content)
    {
        using var archive = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count())
            throw new DocumentOutputIntegrityException("The DOCX package contains duplicate parts.");
        if (names.Any(name => string.IsNullOrWhiteSpace(name) || name.StartsWith("/", StringComparison.Ordinal) || name.Contains('\0') || name.Split('/', '\\').Any(part => part is "." or "..")))
            throw new DocumentOutputIntegrityException("The DOCX package contains an unsafe part path.");
        RequireEntry(archive, "[Content_Types].xml");
        RequireEntry(archive, "_rels/.rels");
        RequireEntry(archive, "word/document.xml");
        var contentTypes = ParseXml(archive, "[Content_Types].xml");
        if (contentTypes.Root?.Name != XName.Get("Types", ContentTypesNamespace))
            throw new DocumentOutputIntegrityException("The DOCX package has an invalid content-types part.");
        var documentXml = ParseXml(archive, "word/document.xml");
        var body = documentXml.Root?.Element(XName.Get("body", WordNamespace));
        if (documentXml.Root?.Name != XName.Get("document", WordNamespace) || body is null || !body.Elements().Any())
            throw new DocumentOutputIntegrityException("The DOCX package has no valid document body content.");
        using var document = WordprocessingDocument.Open(new MemoryStream(content, writable: false), isEditable: false);
        if (document.MainDocumentPart?.Document is null)
            throw new DocumentOutputIntegrityException("The DOCX package has no valid main document part.");
    }
    private static void ValidatePdf(byte[] content)
    {
        using var document = PdfDocument.Open(new MemoryStream(content, writable: false));
        if (document.NumberOfPages < 1)
            throw new DocumentOutputIntegrityException("The PDF output has no pages.");
    }
    private static void RequireEntry(ZipArchive archive, string name)
    {
        if (archive.GetEntry(name) is null)
            throw new DocumentOutputIntegrityException($"The DOCX package is missing required part '{name}'.");
    }
    private static XDocument ParseXml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new DocumentOutputIntegrityException($"The DOCX package is missing required part '{name}'.");
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = 8 * 1024 * 1024,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }
}
