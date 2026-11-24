namespace Taslim.Api.AcademicTemplates;

/// <summary>One synthetic bibliography row for previews. It is intentionally un-citable: no DOI, no
/// plausible journal, and a marker string saying so. The "no fictional references" rule means the
/// product must never show invented real-looking citations; the gallery therefore shows format samples
/// that are labelled as format samples everywhere they appear.</summary>
public sealed record AcademicSampleBibliographyEntry(string Label, string Text, string Marker);

/// <summary>A heading in the synthetic table of contents.</summary>
public sealed record AcademicSampleTocEntry(string Number, string Title, string Page);

/// <summary>
/// The fixed, private-data-free document every template preview renders. This content is the preview:
/// no user research, no asset ids, no workspace data can reach it, and it never reaches an export of
/// real research. All text is placeholder prose with an explicit synthetic banner; bibliography rows
/// are format samples, not sources.
/// </summary>
public sealed class AcademicSampleContent
{
    public const string SyntheticBannerEn = "SYNTHETIC LAYOUT PREVIEW — placeholder text only. Contains no research content, no sources and no real citations.";
    public const string SyntheticBannerAr = "معاينة تنسيق تجريبية — نص نموذجي فقط، ولا يحتوي على محتوى بحثي أو مراجع حقيقية.";
    public const string SyntheticBannerKu = "پێشبینینی ڕووکاری نمونەیی — تەنها دەقی شوێنگر. هیچ ناوەڕۆکی توێژینەوە و هیچ سەرچاوەیەکی ڕاستەقینەی تێدا نییە.";
    public const string BibliographyMarkerEn = "format sample — not a real source";
    public const string BibliographyMarkerAr = "نموونەی ستایل — سەرچاوەیەکی ڕاستەقینە نییە";
    public const string BibliographyMarkerKu = "نموونەی شێواز — سەرچاوەی ڕاستەقینە نییە";

    public required string Banner { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string AuthorLine { get; init; }
    public required string InstitutionLine { get; init; }
    public required string DateLine { get; init; }
    public required string ContentsHeading { get; init; }
    public required string AiDisclosureHeading { get; init; }
    public required string AiDisclosureText { get; init; }
    public required string AbstractHeading { get; init; }
    public required string AbstractText { get; init; }
    public required IReadOnlyList<AcademicSampleTocEntry> Contents { get; init; }
    public required IReadOnlyList<(string Heading, string Body, string? SubHeading)> Sections { get; init; }
    public required string TableCaption { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> TableRows { get; init; }
    public required string FigureCaption { get; init; }
    public required string QuoteText { get; init; }
    public required string QuoteAttribution { get; init; }
    public required string BibliographyHeading { get; init; }
    public required IReadOnlyList<AcademicSampleBibliographyEntry> Bibliography { get; init; }
    public required string AppendixHeading { get; init; }
    public required string AppendixText { get; init; }
    public required string FootnoteText { get; init; }
    public required string PageLabelWord { get; init; }

    private static readonly Lazy<Dictionary<string, AcademicSampleContent>> Cache = new(() => new Dictionary<string, AcademicSampleContent>(StringComparer.Ordinal)
    {
        ["en"] = BuildEnglish(),
        ["ar"] = BuildArabic(),
        ["ku"] = BuildKurdish(),
    });

    public static AcademicSampleContent For(AcademicDesignSpec spec) => ForLanguage(spec.LanguageCode);

    public static AcademicSampleContent ForLanguage(string language) =>
        Cache.Value.TryGetValue(AcademicRtlPolicy.NormalizeLanguage(language), out var content) ? content : Cache.Value["en"];

    private static AcademicSampleContent BuildEnglish() => new()
    {
        Banner = SyntheticBannerEn,
        Title = "Sample Study Title for Layout Preview",
        Subtitle = "A Placeholder Subtitle Describing Nothing Real",
        AuthorLine = "Sample Author (not a person)",
        InstitutionLine = "Faculty of Placeholder Studies · Sample Institution",
        DateLine = "Month Year",
        ContentsHeading = "Contents",
        AiDisclosureHeading = "Statement of Generative-AI Use",
        AiDisclosureText = "Template slot: describe, per your institution's policy, where and how generative AI was or was not used. The student completes this disclosure before submission.",
        AbstractHeading = "Abstract",
        AbstractText = "This paragraph exists to show body measure, leading and first-line indent behaviour under the selected design spec. It asserts no findings and cites no sources. Placeholder prose continues so the reviewer can judge widow control, line length and the rhythm of paragraph spacing against the page grid.",
        Contents = new[]
        {
            new AcademicSampleTocEntry("1", "Introduction to the Layout", "1"),
            new AcademicSampleTocEntry("1.1", "Problem Statement (placeholder)", "3"),
            new AcademicSampleTocEntry("2", "Methodology of Typesetting", "5"),
            new AcademicSampleTocEntry("2.1", "Instrument: the Page Grid", "6"),
            new AcademicSampleTocEntry("3", "Synthetic Results", "9"),
            new AcademicSampleTocEntry("", "References", "12"),
            new AcademicSampleTocEntry("", "Appendix A — Sample Instrument", "13"),
        },
        Sections = new (string, string, string?)[]
        {
            ("1. Introduction to the Layout",
             "Heading level one sets the chapter opener: its size comes from the modular scale, its space-before from the heading gap, and its numbering from the selected scheme. This body paragraph demonstrates the type pairing at the configured size and leading. A citation renders here as an author-date marker (Sample, Year) purely to show run-in behaviour.",
             "1.1 Problem Statement"),
            ("2. Methodology of Typesetting",
             "A second section gives the renderer enough prose to cross at least one page boundary, which is what makes the approximate pagination badge meaningful. Tables ride the same grid: the exhibit below shows caption position, cell padding and border weight.",
             "2.1 Instrument: the Page Grid"),
            ("3. Synthetic Results",
             "The results section carries the figure placeholder, the block quote and the footnote marker so every structural element of a real export appears in the preview. Footnote reference markers appear beside sentences that need one.[1]",
             null),
        },
        TableCaption = "Table 1. Synthetic exhibit values (placeholders)",
        TableRows = new[]
        {
            new[] { "Exhibit", "Metric", "Value" },
            new[] { "Row A", "Placeholder", "0.00" },
            new[] { "Row B", "Placeholder", "1.00" },
        },
        FigureCaption = "Figure 1. Placeholder figure caption below the exhibit.",
        QuoteText = "“Quoted block placeholder — indentation and leading, not content, are on display.”",
        QuoteAttribution = "Sample Placeholder (Year)",
        BibliographyHeading = "References",
        Bibliography = new[]
        {
            new AcademicSampleBibliographyEntry("Sample, A.", "Sample, A. (Year). Title of the formatting sample [layout placeholder]. Placeholder Publisher. — " + BibliographyMarkerEn, BibliographyMarkerEn),
            new AcademicSampleBibliographyEntry("Sample, B.", "Sample, B., & Sample, C. (Year). An article that does not exist, shown only to demonstrate the hanging indent. Placeholder Journal of Layouts, 00(00), 00–00. — " + BibliographyMarkerEn, BibliographyMarkerEn),
            new AcademicSampleBibliographyEntry("Sample, D.", "Sample, D. (Year). Chapter placeholder inside an edited book that was never published. In P. Editor (Ed.), Placeholder Book Title (pp. 00–00). Placeholder Press. — " + BibliographyMarkerEn, BibliographyMarkerEn),
        },
        AppendixHeading = "Appendix A — Sample Instrument",
        AppendixText = "Appendix typography: the same body spec continues after the references, proving section restarts and running heads behave.",
        FootnoteText = "[1] Footnote placeholder demonstrating note size, separator and placement at the page foot.",
        PageLabelWord = "Page",
    };

    private static AcademicSampleContent BuildArabic() => new()
    {
        Banner = SyntheticBannerAr,
        Title = "عنوان دراسي نموذجي لمعاينة التنسيق",
        Subtitle = "عنوان فرعي تمثيلي لا يصف أي بحث حقيقي",
        AuthorLine = "كاتب نموذجي (ليس شخصًا حقيقيًا)",
        InstitutionLine = "كلية الدراسات النموذجية · مؤسسة تمثيلية",
        DateLine = "شهر سنة",
        ContentsHeading = "المحتويات",
        AiDisclosureHeading = "بيان استخدام الذكاء الاصطناعي التوليدي",
        AiDisclosureText = "خانة نموذجية: وِفْقًا لتعليمات مؤسستك، وضّح أين وكيف استُخدم الذكاء الاصطناعي التوليدي أو لم يُستخدم. يُكمل الطالب هذا الإقرار قبل التسليم.",
        AbstractHeading = "الملخص",
        AbstractText = "توجد هذه الفقرة لعرض سلوك عمود النص وتباعد الأسطر في التنسيق المختار. لا تقدم أي نتائج ولا تستشهد بأي مصادر، لأن الغرض هو معاينة التصميم فقط.",
        Contents = new[]
        {
            new AcademicSampleTocEntry("١", "مقدمة في نموذج التنسيق", "١"),
            new AcademicSampleTocEntry("١.١", "صياغة المشكلة (تمثيلية)", "٣"),
            new AcademicSampleTocEntry("٢", "منهجية الضبط الطباعي", "٥"),
            new AcademicSampleTocEntry("٢.١", "أداة العمل: شبكة الصفحة", "٦"),
            new AcademicSampleTocEntry("٣", "نتائج نموذجية", "٩"),
            new AcademicSampleTocEntry("", "المراجع", "١٢"),
            new AcademicSampleTocEntry("", "الملحق أ — أداة نموذجية", "١٣"),
        },
        Sections = new (string, string, string?)[]
        {
            ("١. مقدمة في نموذج التنسيق",
             "يحدد العنوان الرئيسي مفتتح الفصل: حجمه من سلم القياس، والمسافة قبله من تباعد العناوين، وترقيمه من النظام المختار. تعرض هذه الفقرة ازدواج الخطوط بالحجم والتباعد المضبطين، كما يظهر هنا marker الاستشهاد (كاتب، سنة) لغرض المعاينة فقط.",
             "١.١ صياغة المشكلة"),
            ("٢. منهجية الضبط الطباعي",
             "يوفر قسم ثانٍ نصًا كافيًا لتجاوز حد صفحة واحد على الأقل، وهو ما يجعل شارة الترقيم التقريبية ذات معنى. تعرض الجدولوالأشكال أدناه موضع التعليقات وحشو الخلايا وسمك الحدود.",
             "٢.١ أداة العمل: شبكة الصفحة"),
            ("٣. نتائج نموذجية",
             "تحمل فقرة النتائج الشكل التمثيلي والاقتباس وعلامة الحاشية كي تظهر كل العناصر الهيكلية في معاينة التصدير.[١]",
             null),
        },
        TableCaption = "الجدول ١. قيم تمثيلية (لا نتائج حقيقية)",
        TableRows = new[]
        {
            new[] { "العنصر", "المقياس", "القيمة" },
            new[] { "صف أ", "تمثيلي", "٠٫٠٠" },
            new[] { "صف ب", "تمثيلي", "١٫٠٠" },
        },
        FigureCaption = "الشكل ١. تعليق تمثيلي يوضع أسفل المعروض.",
        QuoteText = "«اقتباس نموذجي — الغرض عرض المسافة البادئة وتباعد الأسطر لا المحتوى.»",
        QuoteAttribution = "كاتب نموذجي (سنة)",
        BibliographyHeading = "المراجع",
        Bibliography = new[]
        {
            new AcademicSampleBibliographyEntry("نموذج، أ.", "نموذج، أ. (سنة). عنوان نموذج التنسيق [للمعاينة فقط]. دار نشر تمثيلية. — " + BibliographyMarkerAr, BibliographyMarkerAr),
            new AcademicSampleBibliographyEntry("نموذج، ب.", "نموذج، ب.، ونموذج، ج. (سنة). مقال غير موجود غرضه عرض المسافة المعلقة فقط. مجلة التنسيق التمثيلية، ٠٠(٠٠)، ٠٠–٠٠. — " + BibliographyMarkerAr, BibliographyMarkerAr),
        },
        AppendixHeading = "الملحق أ — أداة نموذجية",
        AppendixText = "تنسيق الملاحق: يستمر نمط المتن بعد المراجع لإثبات سلوك استئناف الأقسام ورؤوس الصفحات.",
        FootnoteText = "[١] حاشية نموذجية لعرض حجم الهامش وفاصله وموضعه أسفل الصفحة.",
        PageLabelWord = "صفحة",
    };

    private static AcademicSampleContent BuildKurdish() => new()
    {
        Banner = SyntheticBannerKu,
        Title = "نموونەی ناونیشانی توێژینەوە بۆ پێشبینینی ڕووکار",
        Subtitle = "پێنووسی شوێنگر کە هیچ بەسەڕێکی ڕاستەقینە ڕانەگەیەنێت",
        AuthorLine = "نووسەری نموونەیی (کەسێکی ڕاستەقینە نییە)",
        InstitutionLine = "کۆلێژی خوێندنەکانی نموونە · زانکۆی شوێنگر",
        DateLine = "مانگ ساڵ",
        ContentsHeading = "پێڕست",
        AiDisclosureHeading = "ڕاگەیاندنی بەکارهێنانی زیرەکی دەستکردی بەرهەمهێنەر",
        AiDisclosureText = "شوێنی نموونە: بەپێی ڕێسای زانکۆکەت ڕابگەیەنە لە کوێ و چۆن زیرەکی دەستکردی بەرهەمهێنەر بەکارهاتووە یان بەکارنەهاتووە. پێش تۆمارکردن ئەم ڕاگەیاندنە تەواو بکە.",
        AbstractHeading = "پوختە",
        AbstractText = "ئەم پەڕەگرافە تەنها بۆ پیشاندانی ڕێژەی دێڕ و فەصلەخط و بۆشانی نێوان پەڕەگرافەکانە لە ڕووکاری هەڵبژێردراودا. هیچ ئەنجامێک و هیچ سەرچاوەیەکی تێدا نییە.",
        Contents = new[]
        {
            new AcademicSampleTocEntry("١", "پێشەکی ڕووکاری نموونە", "١"),
            new AcademicSampleTocEntry("١.١", "ڕوونکردنەوەی کێشە (شوێنگر)", "٣"),
            new AcademicSampleTocEntry("٢", "ڕێبازی ڕێکخستنی چاپکاری", "٥"),
            new AcademicSampleTocEntry("٢.١", "ئامراز: تۆڕی پەڕە", "٦"),
            new AcademicSampleTocEntry("٣", "ئەنجامە نموونەییەکان", "٩"),
            new AcademicSampleTocEntry("", "سەرچاوەکان", "١٢"),
            new AcademicSampleTocEntry("", "پاشگر A — ئامرازی نموونە", "١٣"),
        },
        Sections = new (string, string, string?)[]
        {
            ("١. پێشەکی ڕووکاری نموونە",
             "سەرەکی یەکەم دەستپێکی بەش دیاری دەکات: قەبارەی لە پلەی قەبارەکانەوەیە و ژمارەکردنی لە سیستەمی هەڵبژێردراوەوەیە. ئەم پەڕەگرافە جووتە هێلیکەکان بە قەبارە و فەصلەخطی ڕێکخراو پیشان دەدات.",
             "١.١ ڕوونکردنەوەی کێشە"),
            ("٢. ڕێبازی ڕێکخستنی چاپکاری",
             "بەشی دووەم دەقی پێویست دابین دەکات بۆ ئەوەی لانیکەم یەک پەڕە بشکێت، ئەمەش واتای پێوەری ژمارەکردنی خێرا دەردەخات. خشتە و وێنەکان لەسەر هەمان تۆڕ ڕادەگیراون.",
             "٢.١ ئامراز: تۆڕی پەڕە"),
            ("٣. ئەنجامە نموونەییەکان",
             "بەشی ئەنجامەکان وێنەی شوێنگر و وەرگێڕانی ناوەکی و ئاماژەی پێنووسی تێدایە، بۆیە هەموو توخمە ڕێکخراوەییەکان لە پێشبینیندا دەردەکەون.[١]",
             null),
        },
        TableCaption = "خشتەی ١. بەهای نموونەیی (هیچ ئەنجامێکی ڕاستەقینە نییە)",
        TableRows = new[]
        {
            new[] { "بەش", "پێوەر", "بەها" },
            new[] { "ڕیزی ئە", "نموونە", "٠٫٠٠" },
            new[] { "ڕیزی ب", "نموونە", "١٫٠٠" },
        },
        FigureCaption = "وێنەی ١. پێنووسی وێنەی شوێنگر لەژێر بەشەکە.",
        QuoteText = "«وەرگێڕانی شوێنگر — مەبەست پیشاندانی بۆشانی ناوەوە و فەصلەخطە، نەک ناوەڕۆک.»",
        QuoteAttribution = "نووسەی نموونە (ساڵ)",
        BibliographyHeading = "سەرچاوەکان",
        Bibliography = new[]
        {
            new AcademicSampleBibliographyEntry("نموونە، ئ.", "نموونە، ئ. (ساڵ). ناونیشانی شێوازی پێشبینین [تەنها بۆ ڕووکار]. بڵاوکەرەوەی شوێنگر. — " + BibliographyMarkerKu, BibliographyMarkerKu),
            new AcademicSampleBibliographyEntry("نموونە، ب.", "نموونە، ب.، و نموونە، ج. (ساڵ). توێژینەوەیەک کە بوونی نییە، تەنها بۆ پیشاندانی بۆشانی ناوەوەی سەرچاوەکان. گۆڤاری ڕووکاری نموونە، ٠٠(٠٠)، ٠٠–٠٠. — " + BibliographyMarkerKu, BibliographyMarkerKu),
        },
        AppendixHeading = "پاشگر A — ئامرازی نموونە",
        AppendixText = "ڕووکاری پاشگر: هەمان شێوازی دەونووست دوای سەرچاوەکان بەردەوامێت، بۆ ئەوەی دەرخات سەرەتای دووبارە و سەرشێوەکان بەڕێوە دەبن.",
        FootnoteText = "[١] پێنووسی شوێنگر بۆ پیشاندانی قەبارە و جیاکەرەوە و شوێنی پێنووس لە بەشی خوارەوەی پەڕە.",
        PageLabelWord = "پەڕە",
    };
}
