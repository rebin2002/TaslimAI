import { normalizeAcademicLanguage } from "./academicTemplateCatalog";

/**
 * Synthetic content for the client-side gallery previews. Everything here is a
 * placeholder that asserts no findings and cites no sources — the reference entries
 * carry the "not a real source" marker and no locator (DOI/ISBN/URL) is faked.
 * The banners themselves are read from the generated catalog snapshot so they stay
 * byte-identical with the server-side DOCX/PDF/HTML renderers.
 */

export interface AcademicPreviewSampleEntry {
  label: string;
  text: string;
}

export interface AcademicPreviewSample {
  bannerIndex: 0 | 1 | 2;
  title: string;
  subtitle: string;
  authorLine: string;
  institutionLine: string;
  dateLine: string;
  contentsHeading: string;
  aiDisclosureHeading: string;
  aiDisclosureText: string;
  abstractHeading: string;
  abstractText: string;
  tocEntries: AcademicPreviewSampleEntry[];
  sections: { heading: string; body: string }[];
  tableCaption: string;
  tableRows: string[][];
  quoteText: string;
  quoteAttribution: string;
  bibliographyHeading: string;
  bibliographyMarker: string;
  bibliography: AcademicPreviewSampleEntry[];
  footnoteText: string;
  pageWord: string;
}

const EN: AcademicPreviewSample = {
  bannerIndex: 0,
  title: "Sample Study Title for Layout Preview",
  subtitle: "A Placeholder Subtitle Describing Nothing Real",
  authorLine: "A. Placeholder Author (not a real person)",
  institutionLine: "Faculty of Placeholder Studies · Representative Institution",
  dateLine: "Month Year",
  contentsHeading: "Contents",
  aiDisclosureHeading: "Statement of Generative-AI Use",
  aiDisclosureText:
    "Template slot: describe, per your institution's policy, where and how generative AI was or was not used. The student completes this disclosure before submission.",
  abstractHeading: "Abstract",
  abstractText:
    "This paragraph exists to show body measure, leading and first-line indent behaviour under the selected design spec. It asserts no findings and cites no sources.",
  tocEntries: [
    { label: "1", text: "Introduction to the Layout" },
    { label: "1.1", text: "Problem Statement (placeholder)" },
    { label: "2", text: "Methodology of Typesetting" },
    { label: "", text: "References" },
  ],
  sections: [
    {
      heading: "1. Introduction to the Layout",
      body: "Heading level one sets the chapter opener from the modular scale, while this body line shows the configured size and leading. A citation renders here as an author-date marker (Sample, Year) purely to show run-in behaviour.",
    },
    {
      heading: "2. Methodology of Typesetting",
      body: "Tables ride the same grid: the exhibit below shows caption position, cell padding and border weight.",
    },
  ],
  tableCaption: "Table 1. Synthetic exhibit values (placeholders)",
  tableRows: [
    ["Exhibit", "Metric", "Value"],
    ["Row A", "Placeholder", "0.00"],
    ["Row B", "Placeholder", "1.00"],
  ],
  quoteText: "“Quoted block placeholder — indentation and leading, not content, are on display.”",
  quoteAttribution: "Sample Placeholder (Year)",
  bibliographyHeading: "References",
  bibliographyMarker: "format sample — not a real source",
  bibliography: [
    {
      label: "Sample, A.",
      text: "Sample, A. (Year). Title of the formatting sample [layout placeholder]. Placeholder Publisher. — format sample — not a real source",
    },
    {
      label: "Sample, B.",
      text: "Sample, B., & Sample, C. (Year). An article that does not exist, shown only to demonstrate the hanging indent. — format sample — not a real source",
    },
  ],
  footnoteText: "[1] Footnote placeholder demonstrating note size and placement at the page foot.",
  pageWord: "Page",
};

const AR: AcademicPreviewSample = {
  bannerIndex: 1,
  title: "عنوان دراسي نموذجي لمعاينة التنسيق",
  subtitle: "عنوان فرعي تمثيلي لا يصف أي بحث حقيقي",
  authorLine: "كاتب نموذجي (ليس شخصًا حقيقيًا)",
  institutionLine: "كلية الدراسات النموذجية · مؤسسة تمثيلية",
  dateLine: "شهر سنة",
  contentsHeading: "المحتويات",
  aiDisclosureHeading: "بيان استخدام الذكاء الاصطناعي التوليدي",
  aiDisclosureText:
    "خانة نموذجية: وِفْقًا لتعليمات مؤسستك، وضّح أين وكيف استُخدم الذكاء الاصطناعي التوليدي أو لم يُستخدم. يُكمل الطالب هذا الإقرار قبل التسليم.",
  abstractHeading: "الملخص",
  abstractText:
    "هذه الفقرة توجد لعرض العرض والتباعد والمسافة البادئة ضمن المواصفات المختارة. لا تقدّم أي نتائج ولا تستشهد بأي مصادر.",
  tocEntries: [
    { label: "١", text: "مقدمة في التنسيق" },
    { label: "٢", text: "منهجية الضبط" },
    { label: "", text: "المراجع" },
  ],
  sections: [
    {
      heading: "١. مقدمة في التنسيق",
      body: "يحدد مستوى العنوان الأول مظهر بداية الفصل، وتعرض هذه السطر الحجم والتباعد المضبوطين. تظهر إحالة هنا بصيغة (Sample, Year) لأغراض العرض فقط.",
    },
    {
      heading: "٢. منهجية الضبط",
      body: "تخضع الجداول لنفس الشبكة: يوضح العرض أدناه موضع التعليق وحجم الحدود.",
    },
  ],
  tableCaption: "الجدول ١. قيم عرض تجريبية (نموذجية)",
  tableRows: [
    ["عرض", "مقياس", "قيمة"],
    ["صف أ", "بديل", "0.00"],
    ["صف ب", "بديل", "1.00"],
  ],
  quoteText: "«اقتباس نموذجي — الغرض عرض المسافة البادئة والتباعد، لا المحتوى.»",
  quoteAttribution: "بديل نموذجي (سنة)",
  bibliographyHeading: "المراجع",
  bibliographyMarker: "نموونة تنسيق — ليست مرجعًا حقيقيًا",
  bibliography: [
    {
      label: "Sample, A.",
      text: "Sample, A. (سنة). عنوان نموذج التنسيق [بديل تخطيط]. دار نشر تمثيلية. — نموونة تنسيق — ليست مرجعًا حقيقيًا",
    },
  ],
  footnoteText: "[١] هامش نموذجي يوضح حجم الحاشية وموضعها أسفل الصفحة.",
  pageWord: "صفحة",
};

const KU: AcademicPreviewSample = {
  bannerIndex: 2,
  title: "ناونیشانی نموونەیی بۆ پێشبینینی ڕووکار",
  subtitle: "ژێرنیوونشانێکی شوێنگر کە هیچ توێژینەوەیەکی ڕاستەقینە بازنەکات",
  authorLine: "نووسەری نمونەیی (کەسێکی ڕاستەقینە نییە)",
  institutionLine: "کۆلێژی خوێندنی نمونەیی · دامەزراوەی نوێنەرایەتی",
  dateLine: "مانگ ساڵ",
  contentsHeading: "ناوەڕۆک",
  aiDisclosureHeading: "ڕاگەیاندنی بەکارهێنانی زیرەکی دەستکردی دروستکەر",
  aiDisclosureText:
    "خانەی شابلۆن: بەپێی سیاسەتی زانکۆکەت ڕوون بکەرەوە لە کوێ و چۆن زیرەکی دەستکرد بەکارهاتووە یان نییە. خوێندکار پێش پێشکەشکردن ئەم ڕاگەیاندنە تەواو دەکات.",
  abstractHeading: "پوختە",
  abstractText:
    "ئەم دەقە تەنها بۆ پیشاندانی پانی، فەزای نیوان دێڕ و کردنەوەی دێڕی یەکەمە لەژێ ڕێکخستنی هەڵبژێردراودا. هیچ ئەنجامێکی تێدا نییە و هیچ سەرچاوەیەکی لێناکرێت.",
  tocEntries: [
    { label: "١", text: "پێشەکی ڕووکار" },
    { label: "٢", text: "ڕێبازی ڕیزکردن" },
    { label: "", text: "سەرچاوەکان" },
  ],
  sections: [
    {
      heading: "١. پێشەکی ڕووکار",
      body: "ئاستی یەکەمی سەردێڕ دەستپێکی بەش دیاری دەکات و ئەم دێڕە قەبارە و فەزای هەڵبژێردراو پیشان دەدات. سەرچاوەناساندن بە شێوەی (Sample, Year) تەنها بۆ نمایشە.",
    },
    {
      heading: "٢. ڕێبازی ڕیزکردن",
      body: "خشتەکان لەسەر هەمان تۆڕ دەکشێن: نموونەی خوارەوە شوێنی پێوانە و قەڵەوی هێڵ پیشان دەدات.",
    },
  ],
  tableCaption: "خشتەی ١. بەهای نموونەیی (شوێنگر)",
  tableRows: [
    ["نموونە", "پێوەر", "بەها"],
    ["ڕیز ب", "شوێنگر", "1.00"],
    ["ڕیز پ", "شوێنگر", "0.00"],
  ],
  quoteText: "«وتەی وەرگیراوی شوێنگر — مەبەست پیشاندانی کردنەوە و فەزایە، ناوەڕۆک نییە.»",
  quoteAttribution: "شوێنگری نمونەیی (ساڵ)",
  bibliographyHeading: "سەرچاوەکان",
  bibliographyMarker: "نموونەی شێواز — سەرچاوەی ڕاستەقینە نییە",
  bibliography: [
    {
      label: "Sample, A.",
      text: "Sample, A. (ساڵ). ناونیشانی نموونەی ڕووکار [شوێنگری پلان]. بڵاوکەرەوەی نوێنەرایەتی. — نموونەی شێواز — سەرچاوەی ڕاستەقینە نییە",
    },
  ],
  footnoteText: "[١] پێینۆسی شوێنگر بۆ پیشاندانی قەبارەی تێبینی لە پەڕیدا.",
  pageWord: "پەڕە",
};

const SAMPLES: Record<string, AcademicPreviewSample> = { en: EN, ar: AR, ku: KU };

export function previewSampleFor(languageCode: string): AcademicPreviewSample {
  return SAMPLES[normalizeAcademicLanguage(languageCode)] ?? EN;
}
