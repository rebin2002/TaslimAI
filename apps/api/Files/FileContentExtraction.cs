using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or XmlException)
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
        using var document = PdfDocument.Open(content);
        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            builder.AppendLine($"[Page {page.Number}]");
            builder.AppendLine(page.Text);
        }
        return builder.ToString();
    }

    private static string ExtractDocx(Stream content)
    {
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException();
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants(word + "p"))
        {
            var text = string.Concat(paragraph.Descendants(word + "t").Select(item => item.Value));
            if (text.Length > 0) builder.AppendLine(text);
        }
        return builder.ToString();
    }

    private static string ExtractXlsx(Stream content)
    {
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var shared = ReadSharedStrings(archive);
        var builder = new StringBuilder();
        var sheets = archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName);
        foreach (var sheet in sheets)
        {
            builder.AppendLine($"[Sheet {Path.GetFileNameWithoutExtension(sheet.Name)}]");
            using var stream = sheet.Open();
            var document = XDocument.Load(stream);
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            foreach (var row in document.Descendants(spreadsheet + "row"))
            {
                var cells = row.Elements(spreadsheet + "c").Select(cell => ReadCell(cell, spreadsheet, shared));
                builder.AppendLine(string.Join(" | ", cells));
            }
        }
        return builder.ToString();
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(spreadsheet + "si").Select(item => string.Concat(item.Descendants(spreadsheet + "t").Select(text => text.Value))).ToList();
    }

    private static string ReadCell(XElement cell, XNamespace spreadsheet, IReadOnlyList<string> shared)
    {
        var value = cell.Element(spreadsheet + "v")?.Value ?? cell.Element(spreadsheet + "is")?.Value ?? string.Empty;
        return cell.Attribute("t")?.Value == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : value;
    }
}
