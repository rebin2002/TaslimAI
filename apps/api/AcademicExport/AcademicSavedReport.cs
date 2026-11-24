using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taslim.Api.AcademicTemplates;
using Taslim.Api.Domain;

namespace Taslim.Api.AcademicExport;

/// <summary>One bibliography row of a real saved report, with the honest access status of its source.</summary>
public sealed record AcademicReportSourceRow(
    string CitationId,
    string Title,
    string? Publisher,
    string? Url,
    string? PublishedYear,
    string SourceType,
    /// <summary>True only when the workspace actually holds content from this source: an uploaded file
    /// whose text was extracted, or fetched web content (snippet/excerpt/evidence). A catalogue record
    /// with metadata alone is never marked as accessed.</summary>
    bool ContentAccessed,
    /// <summary>Rendered beside every metadata-only entry so a reader can never infer the work was read.</summary>
    string AccessNote);

public sealed record AcademicReportBlockModel(
    string Type,
    string? Text,
    IReadOnlyList<string>? Items,
    IReadOnlyList<IReadOnlyList<string>>? Rows,
    IReadOnlyList<string> CitationIds);

public sealed record AcademicReportSectionModel(string Heading, IReadOnlyList<AcademicReportBlockModel> Blocks);

/// <summary>Structural labels an export renders, keyed by the saved report's language.</summary>
public sealed record AcademicExportLabels(
    string ContentsHeading,
    string ReferencesHeading,
    string DisclosureHeading,
    string DisclosureMissing,
    string IntegrityNote,
    string MetadataOnlyNote,
    string ExecutiveSummaryHeading,
    string ConclusionHeading,
    string KeyFindingsHeading);

/// <summary>
/// A completed research job's stored payload, re-materialised for templated export. Everything here
/// comes from immutable, already-paid-for state: <see cref="GenerationJob.ResultJson"/> and the job's
/// persisted <see cref="ResearchSource"/> rows. Building it touches no AI layer, so no provider call
/// is possible on this path by construction.
/// </summary>
public sealed class AcademicSavedReport
{
    public required Guid JobId { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    /// <summary>"en" | "ar" | "ku" after normalisation.</summary>
    public required string Language { get; init; }
    public required string ExecutiveSummary { get; init; }
    public IReadOnlyList<AcademicReportBlockModel> KeyFindings { get; init; } = Array.Empty<AcademicReportBlockModel>();
    public IReadOnlyList<AcademicReportSectionModel> Sections { get; init; } = Array.Empty<AcademicReportSectionModel>();
    public required string Conclusion { get; init; }
    public IReadOnlyList<AcademicReportSourceRow> Sources { get; init; } = Array.Empty<AcademicReportSourceRow>();
    /// <summary>Canonical SHA-256 over the stored payload + persisted source facts. The no-charge
    /// policy compares this against the snapshot taken at the previous export.</summary>
    public required string SourceContentHash { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public IReadOnlyList<string> CitedCitationIds { get; init; } = Array.Empty<string>();
    public AcademicExportLabels Labels { get; init; } = DefaultLabels;
    public int AccessedSourceCount => Sources.Count(source => source.ContentAccessed);
    public int MetadataOnlySourceCount => Sources.Count(source => !source.ContentAccessed);

    private static readonly AcademicExportLabels DefaultLabels = LabelsFor("en");

    /// <summary>Stable labels for structural headings. Never content-bearing: no provider, model,
    /// routing or commercial wording is allowed to enter these strings (guarded in tests).</summary>
    public static AcademicExportLabels LabelsFor(string language) => language switch
    {
        "ar" => new AcademicExportLabels(
            "جدول المحتويات",
            "المراجع",
            "بيان استخدام الذكاء الاصطناعي التوليدي",
            "لم يُكمل الطالب هذا البيان. تقتضي السياسة الأكاديمية عادةً وصف مواضع استخدام أدوات الذكاء الاصطناعي وكيفية استخدامها قبل التقديم.",
            "ملاحظة النزاهة الأكاديمية: المراجع المعلَّمة بـ«سجل فهرسة فقط» حُددت من بيانات الفهرسة ولم يُطَّلع على نصها الكامل داخل مساحة العمل هذه. تُعيد هذه الوثيقة عرض تقرير بحثي محفوظ، وهي لا تحتوي على أي مخرجات توليدية جديدة.",
            "سجل فهرسة فقط — لم يتم الوصول إلى النص الكامل عبر مساحة العمل هذه.",
            "الملخص التنفيذي",
            "الخاتمة",
            "أبرز النتائج"),
        "ku" => new AcademicExportLabels(
            "پێڕست",
            "سەرچاوەکان",
            "ڕاگەیاندنی بەکارهێنانی زیرەکی دەستکردی دروستکەر",
            "قوتابی ئەم ڕاگەیاندنەی تەواو نەکردووە. سیاسەتی ئەکادیمی بە شێوەیەکی ئاسایی داوا دەکات پێش پێشکەشکردن باس لە شوێن و شێوازی بەکارهێنانی ئامرازەکانی زیرەکی دەستکرد بکرێت.",
            "تێبینی ڕێزپارێزی ئەکادیمی: سەرچاوە پێناسکراوەکان بە «تۆماری کەتەلۆگ تەنها» لە زانیاری کەتەلۆگەوە دیاری کراون و دەقی تەواویان لەم شوێنە کارکردنەدا نەخوێندراوەتەوە. ئەم بەڵگەیە دووبارە ڕووکارکردنەوەی توێژینەوەی پاشەکەوتکراوە و هیچ ناوەڕۆکی نوێی لێ دروست نەکراوە.",
            "تۆماری کەتەلۆگ تەنها — دەقی تەواوی لەم شوێنە کارکردنەدا بەردەست نەبوو.",
            "پوختەی جێبەجێکردن",
            "ئەنجام",
            "دەرەنجامە سەرەکییەکان"),
        _ => new AcademicExportLabels(
            "Contents",
            "References",
            "Statement of Generative-AI Use",
            "The student has not completed this disclosure. Academic policy normally requires describing where and how generative AI tools were used before submission.",
            "Research integrity note: entries marked \"catalogue record only\" were identified from catalogue metadata and their full text was not read in this workspace. This document re-renders a saved research report and contains no newly generated content.",
            "Catalogue record only — the full text was not accessed in this workspace.",
            "Executive summary",
            "Conclusion",
            "Key findings"),
    };
}

/// <summary>Failure reasons surfaced as stable codes; the messages never include backend identity.</summary>
public sealed class AcademicSavedReportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class AcademicSavedReportReader
{
    /// <summary>Stored payloads are bounded by the generation options upstream; this is the export-side
    /// ceiling so a pathological row cannot stall a synchronous render.</summary>
    public const int MaximumResultJsonCharacters = 8 * 1024 * 1024;

    public static AcademicSavedReport Read(GenerationJob job, IReadOnlyList<ResearchSource> sources)
    {
        if (string.IsNullOrWhiteSpace(job.ResultJson))
            throw new AcademicSavedReportException("ACADEMIC_REPORT_UNAVAILABLE", "The saved report payload is missing.");
        if (job.ResultJson.Length > MaximumResultJsonCharacters)
            throw new AcademicSavedReportException("ACADEMIC_REPORT_TOO_LARGE", "The saved report is too large to render in one pass. Try a shorter report.");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(job.ResultJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AcademicSavedReportException("ACADEMIC_REPORT_UNAVAILABLE", "The saved report payload could not be read.");
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("sections", out var sectionsElement) || sectionsElement.ValueKind != JsonValueKind.Array)
            throw new AcademicSavedReportException("ACADEMIC_REPORT_UNAVAILABLE", "The saved report payload is not in the expected shape.");

        var language = AcademicRtlPolicy.NormalizeLanguage(
            root.TryGetProperty("language", out var languageElement) && languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString()
                : null);

        var keyFindings = ReadBlocks(root.TryGetProperty("keyFindings", out var findingsElement) ? findingsElement : default);
        var sections = ReadSections(sectionsElement);
        var orderedSources = ReadSources(sources, language);

        var cited = new List<string>();
        foreach (var block in keyFindings.Concat(sections.SelectMany(section => section.Blocks)))
            foreach (var citation in block.CitationIds)
                if (!cited.Contains(citation, StringComparer.OrdinalIgnoreCase))
                    cited.Add(citation);

        var report = new AcademicSavedReport
        {
            JobId = job.Id,
            Title = titleElement.GetString() ?? "Untitled research report",
            Subtitle = ReadOptionalString(root, "subtitle"),
            Language = language,
            ExecutiveSummary = ReadOptionalString(root, "executiveSummary") ?? string.Empty,
            KeyFindings = keyFindings,
            Sections = sections,
            Conclusion = ReadOptionalString(root, "conclusion") ?? string.Empty,
            Sources = orderedSources,
            GeneratedAtUtc = job.CompletedAt ?? job.CreatedAt,
            CitedCitationIds = cited,
            Labels = AcademicSavedReport.LabelsFor(language),
            SourceContentHash = ComputeHash(job, orderedSources),
        };
        return report;
    }

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<AcademicReportSectionModel> ReadSections(JsonElement sectionsElement)
    {
        var sections = new List<AcademicReportSectionModel>();
        foreach (var section in sectionsElement.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object) continue;
            var heading = ReadOptionalString(section, "heading") ?? string.Empty;
            var blocks = section.TryGetProperty("blocks", out var blocksElement)
                ? ReadBlocks(blocksElement)
                : Array.Empty<AcademicReportBlockModel>();
            sections.Add(new AcademicReportSectionModel(heading, blocks));
        }
        return sections;
    }

    private static IReadOnlyList<AcademicReportBlockModel> ReadBlocks(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<AcademicReportBlockModel>();
        var blocks = new List<AcademicReportBlockModel>();
        foreach (var block in element.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = ReadOptionalString(block, "type") ?? "paragraph";
            var items = block.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array
                ? itemsElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : null;
            var rows = block.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array
                ? rowsElement.EnumerateArray()
                    .Where(row => row.ValueKind == JsonValueKind.Object && row.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
                    .Select(row => (IReadOnlyList<string>)row.GetProperty("cells").EnumerateArray()
                        .Where(cell => cell.ValueKind == JsonValueKind.String).Select(cell => cell.GetString()!).ToArray())
                    .ToArray()
                : null;
            var citations = block.TryGetProperty("citationIds", out var citationsElement) && citationsElement.ValueKind == JsonValueKind.Array
                ? citationsElement.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).ToArray()
                : Array.Empty<string>();
            blocks.Add(new AcademicReportBlockModel(type, ReadOptionalString(block, "text"), items, rows, citations));
        }
        return blocks;
    }

    private static IReadOnlyList<AcademicReportSourceRow> ReadSources(IReadOnlyList<ResearchSource> sources, string language)
    {
        var ordered = sources
            .OrderBy(source => source.Rank)
            .ThenBy(source => source.CitationId, StringComparer.Ordinal)
            .ToList();
        var note = AcademicSavedReport.LabelsFor(language).MetadataOnlyNote;
        return ordered.Select(source =>
        {
            var uploaded = string.Equals(source.SourceType, "uploaded", StringComparison.OrdinalIgnoreCase);
            var hasContent = uploaded
                || !string.IsNullOrWhiteSpace(source.Snippet)
                || !string.IsNullOrWhiteSpace(source.ExtractedText)
                || source.Evidence.Count > 0;
            return new AcademicReportSourceRow(
                source.CitationId,
                source.Title,
                source.Publisher,
                source.Url ?? source.CanonicalUrl,
                source.PublishedAt?.ToString("yyyy", CultureInfo.InvariantCulture),
                source.SourceType,
                hasContent,
                hasContent ? string.Empty : note);
        }).ToArray();
    }

    /// <summary>
    /// Canonical content identity for the free-re-export rule: the stored result payload verbatim
    /// (it is immutable once a job succeeds) plus the persisted per-source facts the bibliography and
    /// the honesty notes depend on. Same job + same sources ⇒ same hash, in any process.
    /// </summary>
    private static string ComputeHash(GenerationJob job, IReadOnlyList<AcademicReportSourceRow> sources)
    {
        var canonical = new StringBuilder();
        canonical.Append(job.Id.ToString("N")).Append('\u001f');
        canonical.Append(job.ResultJson).Append('\u001f');
        foreach (var source in sources)
        {
            canonical
                .Append(source.CitationId).Append('|')
                .Append(source.Title).Append('|')
                .Append(source.SourceType).Append('|')
                .Append(source.Url ?? string.Empty).Append('|')
                .Append(source.PublishedYear ?? string.Empty).Append('|')
                .Append(source.ContentAccessed ? "1" : "0")
                .Append('\u001f');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
