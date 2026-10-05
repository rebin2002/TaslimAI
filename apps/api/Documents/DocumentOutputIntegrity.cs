using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Taslim.Api.Domain;
namespace Taslim.Api.Documents;
public sealed class DocumentOutputIntegrityException(string message, Exception? innerException = null) : Exception(message, innerException);
public static class DocumentOutputIntegrityValidator
{
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string MainDocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    public static void Validate(RenderedDocument rendered)
    {
        if (rendered.Content.Length == 0)
            throw new DocumentOutputIntegrityException("The document output is empty.");
        if (string.Equals(rendered.RepresentationType, AssetRepresentationTypes.Docx, StringComparison.OrdinalIgnoreCase))
        {
            ValidateMetadata(rendered, ".docx", DocxContentType);
            ValidateDocx(rendered.Content);
            return;
        }
        if (string.Equals(rendered.RepresentationType, AssetRepresentationTypes.Pdf, StringComparison.OrdinalIgnoreCase))
        {
            ValidateMetadata(rendered, ".pdf", "application/pdf");
            ValidatePdf(rendered.Content);
            return;
        }
        throw new DocumentOutputIntegrityException("The document output representation is not supported.");
    }
    private static void ValidateMetadata(RenderedDocument rendered, string extension, string contentType)
    {
        if (!string.Equals(rendered.ContentType, contentType, StringComparison.OrdinalIgnoreCase)
            || !rendered.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new DocumentOutputIntegrityException("The document output metadata is invalid.");
    }
    private static void ValidatePdf(ReadOnlyMemory<byte> content)
    {
        var bytes = content.Span;
        if (bytes.Length < 5 || !bytes[..5].SequenceEqual("%PDF-"u8))
            throw new DocumentOutputIntegrityException("The PDF output has an invalid header.");
        var eof = bytes.LastIndexOf("%%EOF"u8);
        if (eof < 0)
            throw new DocumentOutputIntegrityException("The PDF output is missing its end-of-file marker.");
        var trailing = bytes[(eof + "%%EOF"u8.Length)..];
        for (var index = 0; index < trailing.Length; index++)
        {
            if (trailing[index] is not (0 or 9 or 10 or 12 or 13 or 32))
                throw new DocumentOutputIntegrityException("The PDF output contains data after its end-of-file marker.");
        }
        if (!ContainsPdfPageObject(bytes))
            throw new DocumentOutputIntegrityException("The PDF output does not contain a page object.");
    }
    private static void ValidateDocx(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entries = archive.Entries;
            var names = entries.Select(entry => entry.FullName).ToArray();
            if (names.Length != names.Distinct(StringComparer.Ordinal).Count())
                throw new DocumentOutputIntegrityException("The DOCX package contains duplicate parts.");
            if (names.Any(name => string.IsNullOrEmpty(name) || name.Contains('\0') || Path.IsPathRooted(name)
                || name.StartsWith("/", StringComparison.Ordinal)
                || name.Split('/', '\\').Any(part => part == "..")))
                throw new DocumentOutputIntegrityException("The DOCX package contains an unsafe part path.");
            foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml" })
                _ = archive.GetEntry(part) ?? throw new DocumentOutputIntegrityException($"The DOCX package is missing required part '{part}'.");
            var contentTypes = RequireRoot(ParseRequiredXml(archive, "[Content_Types].xml"), "Types", ContentTypesNamespace, "[Content_Types].xml");
            _ = RequireRoot(ParseRequiredXml(archive, "_rels/.rels"), "Relationships", RelationshipsNamespace, "_rels/.rels");
            var hasMainDocumentContentType = contentTypes.Elements(XName.Get("Override", ContentTypesNamespace)).Any(item =>
                    string.Equals(item.Attribute("PartName")?.Value, "/word/document.xml", StringComparison.Ordinal)
                    && string.Equals(item.Attribute("ContentType")?.Value, MainDocumentContentType, StringComparison.Ordinal))
                || contentTypes.Elements(XName.Get("Default", ContentTypesNamespace)).Any(item =>
                    string.Equals(item.Attribute("Extension")?.Value, "xml", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Attribute("ContentType")?.Value, MainDocumentContentType, StringComparison.Ordinal));
            if (!hasMainDocumentContentType)
                throw new DocumentOutputIntegrityException("The DOCX package has no valid main document content type.");
            var document = RequireRoot(ParseRequiredXml(archive, "word/document.xml"), "document", WordNamespace, "word/document.xml");
            var body = document.Element(XName.Get("body", WordNamespace));
            if (body is null || !body.Elements().Any(element => element.Name == XName.Get("p", WordNamespace) || element.Name == XName.Get("tbl", WordNamespace)))
                throw new DocumentOutputIntegrityException("The DOCX package does not contain document body content.");
        }
        catch (DocumentOutputIntegrityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException or ArgumentException)
        {
            throw new DocumentOutputIntegrityException("The DOCX package could not be read safely.", exception);
        }
    }
    private static XDocument ParseRequiredXml(ZipArchive archive, string part)
    {
        var entry = archive.GetEntry(part) ?? throw new DocumentOutputIntegrityException($"The DOCX package is missing required part '{part}'.");
        try
        {
            using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
            });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or IOException)
        {
            throw new DocumentOutputIntegrityException($"The DOCX package contains invalid XML in '{part}'.", exception);
        }
    }
    private static XElement RequireRoot(XDocument document, string localName, string namespaceName, string part)
    {
        var root = document.Root;
        if (root is null || root.Name != XName.Get(localName, namespaceName))
            throw new DocumentOutputIntegrityException($"The DOCX package has an invalid root in '{part}'.");
        return root;
    }
    private static bool ContainsPdfPageObject(ReadOnlySpan<byte> content) =>
        ContainsPdfToken(content, "/Type /Page"u8) || ContainsPdfToken(content, "/Type/Page"u8);
    private static bool ContainsPdfToken(ReadOnlySpan<byte> content, ReadOnlySpan<byte> token)
    {
        var offset = 0;
        while (offset < content.Length)
        {
            var relativeIndex = content[offset..].IndexOf(token);
            if (relativeIndex < 0) return false;
            var index = offset + relativeIndex;
            var end = index + token.Length;
            if (end == content.Length || IsPdfDelimiter(content[end])) return true;
            offset = end;
        }
        return false;
    }
    private static bool IsPdfDelimiter(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32 or 40 or 41 or 60 or 62 or 91 or 93 or 123 or 125 or 47 or 37;
}
