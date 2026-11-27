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
    /// <summary>True only when this workspace holds content drawn from the work itself: an uploaded
    /// file, or passages extracted out of it. A snippet the search surface printed about the work, or
    /// an evidence line saved beside it, is NOT this claim — those rows are "search_result_only" and
    /// are counted with the metadata rows as not accessed. <see cref="AccessTier"/> states how much is
    /// held, <see cref="FullTextAccessed"/> whether the document itself is, and neither is derivable
    /// from the other two.</summary>
    bool ContentAccessed,
    /// <summary>"full_text" | "extracted_text" | "search_result_only" | "metadata_only": how much of
    /// this source this workspace actually holds, stated separately from <see cref="ContentAccessed"/>
    /// so no consumer can read "some content present" as "the work was read".</summary>
    string AccessTier,
    /// <summary>True only when the source document itself is held here (an uploaded file). Extracted
    /// passages and search snippets are content ABOUT a work, not the work.</summary>
    bool FullTextAccessed,
    /// <summary>Rendered beside every entry whose holdings fall short of the document itself, so a
    /// reader can never infer the work was read from the bibliography alone.</summary>
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
    /// <summary>Beside a row where only a search-result line or saved excerpt is held: not a claim
    /// that the document was accessed.</summary>
    string SearchResultOnlyNote,
    /// <summary>Beside a row where extracted passages are held but the document itself is not.</summary>
    string ExtractedTextNote,
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
    /// <summary>Rows where the document itself is in the workspace — the only rows a "full text
    /// reviewed" claim may count.</summary>
    public int FullTextSourceCount => Sources.Count(source => source.FullTextAccessed);
    /// <summary>Rows backed by nothing stronger than a search result or saved excerpt.</summary>
    public int SearchResultOnlySourceCount => Sources.Count(source => source.AccessTier == "search_result_only");
    public int ExtractedTextSourceCount => Sources.Count(source => source.AccessTier == "extracted_text");

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
            "ملاحظة النزاهة الأكاديمية: المراجع المعلَّمة بـ«سجل فهرسة فقط» حُددت من بيانات الفهرسة، والمعلَّمة بـ«نتيجة بحث فقط» لا تتجاوز سطراً من نتائج البحث، والمعلَّمة بـ«نص مستخرَج» تحتفظ بمقاطع دون المستند كاملًا؛ وفي جميع هذه الحالات لم يُطَّلع على النص الكامل داخل مساحة العمل هذه. تُعيد هذه الوثيقة عرض تقرير بحثي محفوظ، وهي لا تحتوي على أي مخرجات توليدية جديدة.",
            "سجل فهرسة فقط — لم يتم الوصول إلى النص الكامل عبر مساحة العمل هذه.",
            "ملخص نتيجة البحث فقط — لم يُطَّلع على النص الكامل للمقال داخل مساحة العمل هذه.",
            "نص مستخرَج — مقاطع محفوظة هنا، وليس المستند كاملًا.",
            "الملخص التنفيذي",
            "الخاتمة",
            "أبرز النتائج"),
        "ku" => new AcademicExportLabels(
            "پێڕست",
            "سەرچاوەکان",
            "ڕاگەیاندنی بەکارهێنانی زیرەکی دەستکردی دروستکەر",
            "قوتابی ئەم ڕاگەیاندنەی تەواو نەکردووە. سیاسەتی ئەکادیمی بە شێوەیەکی ئاسایی داوا دەکات پێش پێشکەشکردن باس لە شوێن و شێوازی بەکارهێنانی ئامرازەکانی زیرەکی دەستکرد بکرێت.",
            "تێبینی ڕێزپارێزی ئەکادیمی: سەرچاوە پێناسکراوەکان بە «تۆماری کەتەلۆگ تەنها» لە زانیاری کەتەلۆگەوە دیاری کراون، ئەوانەی بە «دەرەنجامی گەڕان تەنها» تەنها دێڕێک لە ئەنجامەکانی گەڕانن، و ئەوانەی بە «دەقی وەرگیراو» بەشێک لەم شوێنە کارکردنەدا هەڵگیراوە نەک بەڵگەکە بەتەواوی؛ لە هیچ یەکێک لەم کاتانەدا دەقی تەواو لەم شوێنە کارکردنەدا نەخوێندراوەتەوە. ئەم بەڵگەیە دووبارە ڕووکارکردنەوەی توێژینەوەی پاشەکەوتکراوە و هیچ ناوەڕۆکی نوێی لێ دروست نەکراوە.",
            "تۆماری کەتەلۆگ تەنها — دەقی تەواوی لەم شوێنە کارکردنەدا بەردەست نەبوو.",
            "پوختەی دەرەنجامی گەڕان تەنها — دەقی تەواوی وتارەکە لەم شوێنە کارکردنەدا نەخوێندراوەتەوە.",
            "دەقی وەرگیراو — بەشێک لەم شوێنە کارکردنەدا هەڵگیراوە، نەک بەڵگەکە بەتەواوی.",
            "پوختەی جێبەجێکردن",
            "ئەنجام",
            "دەرەنجامە سەرەکییەکان"),
        _ => new AcademicExportLabels(
            "Contents",
            "References",
            "Statement of Generative-AI Use",
            "The student has not completed this disclosure. Academic policy normally requires describing where and how generative AI tools were used before submission.",
            "Research integrity note: entries marked \"catalogue record only\" were identified from catalogue metadata, entries marked \"search-result summary only\" are backed by no more than a line the search surface printed, and entries marked \"extracted text\" hold saved passages rather than the document itself; in none of those cases was the full text read in this workspace. This document re-renders a saved research report and contains no newly generated content.",
            "Catalogue record only — the full text was not accessed in this workspace.",
            "Search-result summary only — the full text of the article itself was not accessed in this workspace.",
            "Extracted text — saved passages held here; the full document was not.",
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
            || !TryProperty(root, "title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String
            || !TryProperty(root, "sections", out var sectionsElement) || sectionsElement.ValueKind != JsonValueKind.Array)
            throw new AcademicSavedReportException("ACADEMIC_REPORT_UNAVAILABLE", "The saved report payload is not in the expected shape.");

        var language = AcademicRtlPolicy.NormalizeLanguage(
            TryProperty(root, "language", out var languageElement) && languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString()
                : null);

        var keyFindings = ReadBlocks(TryProperty(root, "keyFindings", out var findingsElement) ? findingsElement : default);
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
        element.ValueKind == JsonValueKind.Object && TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Property lookup that accepts both spellings of a name, exact match first.
    ///
    /// This is not cosmetic. The writer of `ResultJson` serializes an anonymous object with no
    /// <see cref="JsonSerializerOptions"/>, so the naming policy is the default one: the OUTER keys are
    /// camelCase because they are hand-written that way (`sections`, `keyFindings`), while every NESTED
    /// object keeps its C# property names — `Heading`, `Blocks`, `Type`, `Text`, `Items`, `Rows`,
    /// `Cells`, `CitationIds`. A case-sensitive camelCase-only reader therefore found the sections array
    /// and decoded each of them into an empty heading with no blocks, which rendered as a document with
    /// a title page, a summary, and no body — while still passing validation, because the citation check
    /// keys off cited identifiers the reader had just lost. Real saved reports exist in that shape now.
    ///
    /// camelCase is kept as an accepted spelling, not dropped: it is what earlier fixtures and any
    /// future camel-naming producer emit, and an export must render whichever shape is stored.
    /// </summary>
    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;
            if (name.Length > 0)
            {
                var pascal = char.ToUpperInvariant(name[0]) + name[1..];
                if (!string.Equals(pascal, name, StringComparison.Ordinal)
                    && element.TryGetProperty(pascal, out value)) return true;
            }
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

    private static IReadOnlyList<AcademicReportSectionModel> ReadSections(JsonElement sectionsElement)
    {
        var sections = new List<AcademicReportSectionModel>();
        foreach (var section in sectionsElement.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object) continue;
            var heading = ReadOptionalString(section, "heading") ?? string.Empty;
            var blocks = TryProperty(section, "blocks", out var blocksElement)
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
            // Both spellings of every nested name (see TryProperty): the real payload says "Items",
            // "Rows" and "CitationIds"; a camelCase payload says the lower-cased version.
            var items = ReadStrings(block, "items");
            var rows = ReadArray(block, "rows") is { } rowsElement
                ? rowsElement.EnumerateArray()
                    .Select(row => ReadStrings(row, "cells") ?? Array.Empty<string>())
                    .ToArray()
                : null;
            var citations = ReadStrings(block, "citationIds") ?? Array.Empty<string>();
            blocks.Add(new AcademicReportBlockModel(type, ReadOptionalString(block, "text"), items, rows, citations));
        }
        return blocks;
    }

    private static JsonElement? ReadArray(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

    /// <summary>The array of strings under <paramref name="name"/>, or null when the name is absent or
    /// not an array. Non-string entries inside it are skipped rather than guessed at.</summary>
    private static string[]? ReadStrings(JsonElement element, string name) =>
        ReadArray(element, name) is { } array
            ? array.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!).ToArray()
            : null;

    private static IReadOnlyList<AcademicReportSourceRow> ReadSources(IReadOnlyList<ResearchSource> sources, string language)
    {
        var ordered = sources
            .OrderBy(source => source.Rank)
            .ThenBy(source => source.CitationId, StringComparer.Ordinal)
            .ToList();
        var labels = AcademicSavedReport.LabelsFor(language);
        return ordered.Select(source =>
        {
            var uploaded = string.Equals(source.SourceType, "uploaded", StringComparison.OrdinalIgnoreCase)
                || source.StoredFileId is not null;
            // Three different claims, kept apart on purpose. A search-result snippet, or an excerpt the
            // pipeline saved beside it, says that SOMETHING was retrieved about the work; it does not
            // say anybody in this workspace opened the work. Marking either as "content accessed" made
            // the bibliography imply a reading that the persisted state never proved — and the panel's
            // own count said "full text reviewed here" about exactly those rows.
            var extracted = !string.IsNullOrWhiteSpace(source.ExtractedText);
            var excerptOnly = !extracted
                && (!string.IsNullOrWhiteSpace(source.Snippet) || source.Evidence.Count > 0);
            var (tier, note) = uploaded
                ? ("full_text", string.Empty)
                : extracted
                    ? ("extracted_text", labels.ExtractedTextNote)
                    : excerptOnly
                        ? ("search_result_only", labels.SearchResultOnlyNote)
                        : ("metadata_only", labels.MetadataOnlyNote);
            return new AcademicReportSourceRow(
                source.CitationId,
                source.Title,
                source.Publisher,
                source.Url ?? source.CanonicalUrl,
                source.PublishedAt?.ToString("yyyy", CultureInfo.InvariantCulture),
                source.SourceType,
                // Honest claim, per the review: a line of text the search surface printed about a work
                // is not evidence that anyone in this workspace accessed the work. Only content drawn
                // from the document itself — the uploaded file, or passages extracted from it — counts.
                // The content identity behind the no-charge re-export rule deliberately does not read
                // this field (see HoldsAnyContent), so tightening the claim here cannot retroactively
                // move the identity of already-exported reports and take away a free re-render.
                uploaded || extracted,
                tier,
                uploaded,
                note);
        }).ToArray();
    }

    /// <summary>
    /// Canonical content identity for the free-re-export rule: the stored result payload verbatim
    /// (it is immutable once a job succeeds) plus the persisted per-source facts the bibliography and
    /// the honesty notes depend on. Same job + same sources ⇒ same hash, in any process.
    /// </summary>
    internal static string ComputeHash(GenerationJob job, IReadOnlyList<AcademicReportSourceRow> sources)
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
                .Append(HoldsAnyContent(source) ? "1" : "0")
                .Append('\u001f');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Hash input only: the wide, wording-independent predicate — does this workspace hold anything at
    /// all from this source. "metadata_only" is the one tier that means nothing is held, which is
    /// exactly what the stored boolean asserted before the access claim was tightened. Reading the
    /// tightened field here instead would move the content identity of every report exported before it
    /// that has a snippet row, re-classifying a re-render as changed content and taking back an export
    /// the no-charge policy had already promised.
    /// </summary>
    internal static bool HoldsAnyContent(AcademicReportSourceRow source)
        => string.IsNullOrEmpty(source.AccessTier)
            ? source.ContentAccessed || source.FullTextAccessed
            : !string.Equals(source.AccessTier, "metadata_only", StringComparison.Ordinal);
}
