using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

public sealed record FileExtractionResult(
    FileExtractionStatus Status,
    string? ExtractedText,
    int? ExtractedTextLength,
    string? MetadataJson,
    string? FailureCode = null);

public interface IFileContentExtractor
{
    bool CanHandle(string extension);
    Task<FileExtractionResult> ExtractAsync(string extension, Stream content, CancellationToken cancellationToken = default);
}

public sealed class FileContentExtractor(IOptions<FileOptions> options) : IFileContentExtractor
{
    private readonly int maxCharacters = Math.Clamp(options.Value.MaxExtractedTextCharacters, 1_000, 1_000_000);
    private readonly long maxStagingBytes = Math.Min(options.Value.MaxFileSizeBytes, 25 * 1_048_576);
    private readonly int maxArchiveEntries = Math.Clamp(options.Value.MaxArchiveEntries, 1, 10_000);
    private readonly long maxArchiveUncompressedBytes = Math.Clamp(options.Value.MaxArchiveUncompressedBytes, 1_048_576, 1_073_741_824);
    private readonly long maxArchiveEntryBytes = Math.Clamp(options.Value.MaxArchiveEntryBytes, 64 * 1024, Math.Clamp(options.Value.MaxArchiveUncompressedBytes, 1_048_576, 1_073_741_824));
    private readonly double maxArchiveCompressionRatio = Math.Clamp(options.Value.MaxArchiveCompressionRatio, 1, 10_000);
    private readonly long maxArchiveXmlCharacters = Math.Clamp(options.Value.MaxArchiveXmlCharacters, 1_024, 100_000_000);

    public bool CanHandle(string extension) => FileContentTypes.IsTextExtractable(extension);

    public async Task<FileExtractionResult> ExtractAsync(string extension, Stream content, CancellationToken cancellationToken = default)
    {
        MemoryStream? staged = null;
        try
        {
            if (!content.CanSeek)
            {
                staged = new MemoryStream();
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > maxStagingBytes) throw new InvalidDataException("File extraction input exceeds the configured limit.");
                    await staged.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                staged.Position = 0;
                content = staged;
            }

            var normalized = extension.ToLowerInvariant();
            if (normalized is ".txt" or ".md" or ".csv")
            {
                var text = normalized == ".csv" ? await ExtractCsvAsync(content, cancellationToken) : await ReadTextAsync(content, cancellationToken);
                return Ready(text, normalized == ".csv" ? "csv" : "text");
            }
            if (normalized == ".pdf") return Ready(ExtractPdf(content), "pdf");
            if (normalized == ".docx") return Ready(ExtractDocx(content), "docx");
            if (normalized == ".xlsx") return Ready(ExtractXlsx(content), "xlsx");
            return new FileExtractionResult(FileExtractionStatus.NotApplicable, null, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Some document parsers report malformed input as InvalidOperationException
        // rather than FormatException. Treat it as an unreadable upload at this
        // boundary; unexpected failures outside extraction still fail closed in
        // FileProcessingService.
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or InvalidOperationException or XmlException)
        {
            return new FileExtractionResult(FileExtractionStatus.Failed, null, null, null, "EXTRACTION_FAILED");
        }
        finally
        {
            if (staged is not null) await staged.DisposeAsync();
        }
    }

    private FileExtractionResult Ready(string text, string kind)
    {
        var normalized = NormalizeAndBound(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return new FileExtractionResult(FileExtractionStatus.Failed, null, null, $"{{\"kind\":\"{kind}\"}}", "EXTRACTION_EMPTY");

        return new FileExtractionResult(
            FileExtractionStatus.Ready,
            normalized,
            normalized.Length,
            $"{{\"kind\":\"{kind}\",\"bounded\":{(text.Length > normalized.Length ? "true" : "false")} }}");
    }

    private string NormalizeAndBound(string text)
    {
        var normalized = string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(line => line.TrimEnd()));
        return normalized.Length <= maxCharacters ? normalized : normalized[..Math.Max(0, maxCharacters - 80)] + "\n\n[Document content truncated for context safety.]";
    }

    private static async Task<string> ReadTextAsync(Stream content, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<string> ExtractCsvAsync(Stream content, CancellationToken cancellationToken)
    {
        var raw = await ReadTextAsync(content, cancellationToken);
        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new StringBuilder();
        foreach (var line in lines.Where(line => line.Length > 0).Take(2_000))
        {
            var cells = ParseCsvLine(line);
            output.AppendLine(string.Join(" | ", cells));
        }
        return output.ToString();
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { cell.Append('"'); index++; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (character == ',' && !quoted) { result.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(character);
        }
        result.Add(cell.ToString().Trim());
        return result;
    }

    private static string ExtractPdf(Stream content)
    {
        // Upload admission only verifies the "%PDF-" magic, so every structurally broken document
        // reaches this call. PdfPig signals those documents with PdfDocumentFormatException, and
        // with ArgumentOutOfRangeException when the content is shorter than the cross-reference
        // window it reads from the end; neither derives from InvalidDataException, so they would
        // otherwise escape the extraction boundary's format-exception filter and be logged by the
        // generic processing-failure path, which includes parser text quoting tokens from the
        // uploaded document. Convert them here, where the meaning is unambiguously "unreadable
        // upload", leaving the outer filter (and its handling of genuinely unexpected failures)
        // unchanged.
        try
        {
            using var document = OpenPdfDocument(content);
            return RenderPdfPageText(document);
        }
        // Those two parser types can also surface after the structure has been read, while the
        // pages are resolved and their text assembled, so this filter still covers the whole
        // method. NullReferenceException is deliberately absent from it: OpenPdfDocument is the
        // only place that type is treated as a malformed upload, because it is the only place the
        // fault is known to mean an unresolvable catalog rather than a programming defect.
        catch (Exception exception) when (exception is PdfDocumentFormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The PDF could not be parsed.", exception);
        }
    }

    // The parse point: PdfDocument.Open reads the cross-reference table and resolves the trailer
    // /Root dictionary, which is where a document's structure either resolves or does not.
    // One shape is missing from the types ExtractPdf converts above. When /Root names an object
    // that the cross-reference table never registers, an undefined object number or the
    // free-object number 0, PdfPig dereferences the absent object while it resolves the catalog
    // and reports that as NullReferenceException from PdfDocumentFactory.ParseTrailer rather than
    // as PdfDocumentFormatException, so it reaches the boundary unconverted and escapes it. The
    // other unresolved-reference shapes probed against synthetic documents, an absent /Root key, a
    // root naming a page, font, stream or literal, an unreachable startxref and a missing trailer,
    // already raise one of the two types above, and a dangling /Pages, /Kids, /Contents or /Font
    // reference either recovers or raises InvalidOperationException, which the outer filter
    // contains. NullReferenceException is therefore specific to this call rather than a general
    // symptom of bad input, which is why the conversion is scoped to this call alone instead of
    // being widened over the traversal that follows a successful open. A null reference raised
    // downstream is a fault in page traversal, in the parser's later stages or in this class, and
    // has to stay visible as one rather than being answered as an unreadable upload.
    // The widening stays inside PDF parsing on purpose: the outer filter in ExtractAsync still
    // omits NullReferenceException, so a null reference from document, spreadsheet or plain-text
    // extraction, or from this class itself, keeps propagating to the generic unexpected-failure
    // handling in FileProcessingService unchanged.
    internal static PdfDocument OpenPdfDocument(Stream content)
    {
        try
        {
            return PdfDocument.Open(content);
        }
        catch (Exception exception) when (exception is PdfDocumentFormatException or ArgumentOutOfRangeException or NullReferenceException)
        {
            throw new InvalidDataException("The PDF could not be parsed.", exception);
        }
    }

    // Downstream of the parse point: page traversal and text assembly over a document whose
    // structure has already resolved. This half must not treat a null reference as a malformed
    // upload, so it carries no NullReferenceException filter of its own and ExtractPdf's filter
    // does not add one for it. Split out so the containment is a property of the single call that
    // needs it instead of a try block spanning both halves;
    // PdfUnresolvedRootNullReferenceContainmentTests pins that a null reference raised here
    // propagates rather than becoming a bounded EXTRACTION_FAILED.
    internal static string RenderPdfPageText(PdfDocument document)
    {
        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            builder.AppendLine($"[Page {page.Number}]");
            builder.AppendLine(page.Text);
        }
        return builder.ToString();
    }

    private string ExtractDocx(Stream content)
    {
        using var archive = OpenSafeArchive(content);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException();
        using var stream = entry.Open();
        var document = LoadBoundedXml(stream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants(word + "p"))
        {
            var text = string.Concat(paragraph.Descendants(word + "t").Select(item => item.Value));
            if (text.Length > 0) builder.AppendLine(text);
        }
        return builder.ToString();
    }

    private string ExtractXlsx(Stream content)
    {
        using var archive = OpenSafeArchive(content);
        var shared = ReadSharedStrings(archive);
        var builder = new StringBuilder();
        var sheets = archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName);
        foreach (var sheet in sheets)
        {
            builder.AppendLine($"[Sheet {Path.GetFileNameWithoutExtension(sheet.Name)}]");
            using var stream = sheet.Open();
            var document = LoadBoundedXml(stream);
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            foreach (var row in document.Descendants(spreadsheet + "row"))
            {
                var cells = row.Elements(spreadsheet + "c").Select(cell => ReadCell(cell, spreadsheet, shared));
                builder.AppendLine(string.Join(" | ", cells));
            }
        }
        return builder.ToString();
    }

    private IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var document = LoadBoundedXml(stream);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(spreadsheet + "si").Select(item => string.Concat(item.Descendants(spreadsheet + "t").Select(text => text.Value))).ToList();
    }

    private static string ReadCell(XElement cell, XNamespace spreadsheet, IReadOnlyList<string> shared)
    {
        var value = cell.Element(spreadsheet + "v")?.Value ?? cell.Element(spreadsheet + "is")?.Value ?? string.Empty;
        return cell.Attribute("t")?.Value == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : value;
    }

    private ZipArchive OpenSafeArchive(Stream content)
    {
        var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            if (archive.Entries.Count > maxArchiveEntries) throw new InvalidDataException("Archive contains too many entries.");

            long uncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateArchivePath(entry.FullName);
                if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > maxArchiveEntryBytes)
                    throw new InvalidDataException("Archive entry exceeds the configured limit.");
                if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > maxArchiveCompressionRatio))
                    throw new InvalidDataException("Archive entry compression ratio exceeds the configured limit.");
                uncompressedBytes = checked(uncompressedBytes + entry.Length);
                if (uncompressedBytes > maxArchiveUncompressedBytes)
                    throw new InvalidDataException("Archive exceeds the configured uncompressed size limit.");
            }

            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static void ValidateArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized)
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..")
            || normalized.Contains('\0'))
            throw new InvalidDataException("Archive contains an unsafe path.");
    }

    private XDocument LoadBoundedXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxArchiveXmlCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreWhitespace = false,
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }
}
