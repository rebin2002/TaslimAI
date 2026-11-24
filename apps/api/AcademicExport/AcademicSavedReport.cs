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
    /// <summary>True only when the workspace verifiably holds this source's content: a stored upload whose
    /// text was really extracted, or a web source explicitly marked by a verified fetcher
    /// (<see cref="AcademicSavedReportReader.ContentAccessVerifiedMarker"/>). Provider-synthesized answer
    /// text persisted into Snippet/ExtractedText/Evidence is NOT proof the article itself was read, and a
    /// catalogue record with metadata alone never is.</summary>
    bool ContentAccessed,
    /// <summary>Rendered beside every not-accessed entry so a reader can never infer the work was read.</summary>
    string AccessNote,
    /// <summary>One of <see cref="AccessKindWorkspaceFile"/>, <see cref="AccessKindSearchSummary"/>,
    /// <see cref="AccessKindCatalogueOnly"/>: why the flag above holds, so exports label the distinction
    /// honestly instead of collapsing it into one boolean.</summary>
    string AccessKind)
{
    public const string AccessKindWorkspaceFile = "workspace-file";
    /// <summary>Content present is only the search surface's own generated summary/snippet — the
    /// constant deliberately avoids backend-identity vocabulary (AcademicCatalogPrivacyGuard token family).</summary>
    public const string AccessKindSearchSummary = "search-summary";
    public const string AccessKindCatalogueOnly = "catalogue-only";
}

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
    /// <summary>Honest wording for a web row whose only "content" is the search provider's synthesized
    /// summary/snippet: the workspace holds provider output, not the article's fetched full text.</summary>
    string SummaryOnlyNote,
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
    /// <summary>Every rendered content block in document order (key findings then section blocks),
    /// used by the file validator to re-check block-level contracts such as table citation context.</summary>
    public IEnumerable<AcademicReportBlockModel> AllBlocks => KeyFindings
        .Concat(Sections.SelectMany(section => section.Blocks));

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
            "ملخص نتائج البحث فقط — لم يتم الوصول إلى النص الكامل للمقال عبر مساحة العمل هذه.",
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
            "پوختەی ئەنجامەکانی گەڕان تەنها — دەقی تەواوی بابەتەکە لەم شوێنە کارکردنەدا بەردەست نەبوو.",
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
            "Search-result summary only — the full text of the article itself was not accessed in this workspace.",
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

    /// <summary>
    /// The only marker by which a web (non-uploaded) source may be reported as full-text accessed:
    /// an explicit flag in the row's persisted MetadataJson, written by a verified content fetcher.
    /// The production fetcher today is a pass-through (providers persist their own synthesized
    /// answer text into Snippet/ExtractedText), so no web row qualifies until a real fetcher marks
    /// one — which is exactly the conservative default the honesty rule demands.
    /// </summary>
    public const string ContentAccessVerifiedMarker = "contentAccessVerified";

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

        // The handler persists an anonymous object: outer keys are written camelCase, but the nested
        // ResearchSection/ResearchReportBlock classes serialize with their PascalCase property names.
        // Both spellings are live payloads; every property lookup must therefore be case-tolerant.
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String
            || !TryGetProperty(root, "sections", out var sectionsElement) || sectionsElement.ValueKind != JsonValueKind.Array)
            throw new AcademicSavedReportException("ACADEMIC_REPORT_UNAVAILABLE", "The saved report payload is not in the expected shape.");

        var language = AcademicRtlPolicy.NormalizeLanguage(
            TryGetProperty(root, "language", out var languageElement) && languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString()
                : null);

        var keyFindings = ReadBlocks(TryGetProperty(root, "keyFindings", out var findingsElement) ? findingsElement : default);
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

    /// <summary>Case-tolerant property lookup: exact name first, then an ordinal-ignore-case scan.
    /// Handles both the synthetic camelCase shape and the real PascalCase-nested persisted shape
    /// without changing behaviour for payloads where either spelling is absent.</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? ReadOptionalString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<AcademicReportSectionModel> ReadSections(JsonElement sectionsElement)
    {
        var sections = new List<AcademicReportSectionModel>();
        foreach (var section in sectionsElement.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object) continue;
            var heading = ReadOptionalString(section, "heading") ?? string.Empty;
            var blocks = TryGetProperty(section, "blocks", out var blocksElement)
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
            var items = TryGetProperty(block, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array
                ? itemsElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : null;
            var rows = TryGetProperty(block, "rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array
                ? rowsElement.EnumerateArray()
                    .Where(row => row.ValueKind == JsonValueKind.Object && TryGetProperty(row, "cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
                    .Select(row =>
                    {
                        TryGetProperty(row, "cells", out var cellsElement);
                        return (IReadOnlyList<string>)cellsElement.EnumerateArray()
                            .Where(cell => cell.ValueKind == JsonValueKind.String).Select(cell => cell.GetString()!).ToArray();
                    })
                    .ToArray()
                : null;
            var citations = TryGetProperty(block, "citationIds", out var citationsElement) && citationsElement.ValueKind == JsonValueKind.Array
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
        var labels = AcademicSavedReport.LabelsFor(language);
        return ordered.Select(source =>
        {
            // Honest provenance (review finding #3 on a707938): for web sources the persisted
            // Snippet/ExtractedText come from the provider's synthesized answer (see
            // ResearchProviders parsing: both fields are filled from `answer`), and Evidence rows are
            // derived from that same text by the evidence processor. None of it proves the workspace
            // ever fetched the article. Only a stored upload with extracted text, or a row explicitly
            // marked verified by a real fetcher, counts as accessed.
            var uploaded = string.Equals(source.SourceType, "uploaded", StringComparison.OrdinalIgnoreCase)
                || source.StoredFileId is not null;
            var verified = uploaded || HasVerifiedFetchProvenance(source.MetadataJson);
            var hasProviderContent = !string.IsNullOrWhiteSpace(source.Snippet)
                || !string.IsNullOrWhiteSpace(source.ExtractedText)
                || source.Evidence.Count > 0;
            var accessKind = verified
                ? AcademicReportSourceRow.AccessKindWorkspaceFile
                : hasProviderContent
                    ? AcademicReportSourceRow.AccessKindSearchSummary
                    : AcademicReportSourceRow.AccessKindCatalogueOnly;
            var note = verified
                ? string.Empty
                : hasProviderContent
                    ? labels.SummaryOnlyNote
                    : labels.MetadataOnlyNote;
            return new AcademicReportSourceRow(
                source.CitationId,
                source.Title,
                source.Publisher,
                source.Url ?? source.CanonicalUrl,
                source.PublishedAt?.ToString("yyyy", CultureInfo.InvariantCulture),
                source.SourceType,
                verified,
                note,
                accessKind);
        }).ToArray();
    }

    /// <summary>True only when the row's own persisted MetadataJson carries the explicit verified-fetch
    /// flag. Malformed metadata is treated conservatively as "not verified".</summary>
    private static bool HasVerifiedFetchProvenance(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(ContentAccessVerifiedMarker, out var marker)
                && marker.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
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
                .Append(source.ContentAccessed ? "1" : "0").Append('|')
                .Append(source.AccessKind)
                .Append('\u001f');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
