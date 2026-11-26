/**
 * UI strings for the academic template gallery in English, Kurdish (Kurmanji/ckb)
 * and Arabic. This is interface copy only — it never touches research content, and
 * the synthetic-preview banners themselves come from the generated catalog snapshot
 * so they stay byte-identical with the server renderers.
 */

export type AcademicCopyLocale = "en" | "ckb" | "ar";

export type AcademicCopyKey =
  | "galleryTitle"
  | "gallerySubtitle"
  | "searchLabel"
  | "searchPlaceholder"
  | "filterKind"
  | "filterCitation"
  | "filterLanguage"
  | "filterPaper"
  | "filterDirection"
  | "optionAny"
  | "directionLtr"
  | "directionRtl"
  | "resultsCount"
  | "loadMore"
  | "allLoaded"
  | "previewAria"
  | "designHashLabel"
  | "variantsLabel"
  | "useDesignLabel"
  | "selectedDesignLabel"
  | "integrityNote"
  | "snapshotNote"
  | "privacyNote";

const COPY: Record<AcademicCopyLocale, Record<AcademicCopyKey, string>> = {
  en: {
    galleryTitle: "Academic layout templates",
    gallerySubtitle: "Preview thesis, dissertation, proposal and report designs before export. Layout only — previews contain placeholder text, never research content.",
    searchLabel: "Search designs",
    searchPlaceholder: "e.g. kurdish, two-column, roman…",
    filterKind: "Document kind",
    filterCitation: "Citation style",
    filterLanguage: "Language",
    filterPaper: "Paper",
    filterDirection: "Direction",
    optionAny: "Any",
    directionLtr: "Left-to-right",
    directionRtl: "Right-to-left",
    resultsCount: "{count} distinct designs",
    loadMore: "Show more designs",
    allLoaded: "All matching designs shown",
    previewAria: "Synthetic layout preview for {name}",
    designHashLabel: "Design fingerprint",
    variantsLabel: "Selected variants",
    useDesignLabel: "Use this design",
    selectedDesignLabel: "Selected",
    integrityNote: "Template slots support honest disclosure of generative-AI use in line with your institution's policy. They never hide authorship.",
    snapshotNote: "Reformatting or re-exporting saved research with these templates is a layout operation — it does not trigger a new content generation charge.",
    privacyNote: "Previews are computed from layout settings only. No provider, model or cost information is shown.",
  },
  ckb: {
    galleryTitle: "ڤاڵێڕێوەی ڕووکاری ئەکادیمی",
    gallerySubtitle: "پێشبینینی دیزاینی تێزی، ڕیسەرچ پرۆپۆزاڵ و ڕاپۆرت بکە پێش هەناردەکردن. تەنها ڕووکارە — پێشبینینەکان دەقی شوێنگر لەخۆدەگرن، ناوەڕۆکی توێژینەوە نەخێر.",
    searchLabel: "گەڕان بۆ دیزاین",
    searchPlaceholder: "بۆ نموونە کوردی، دوو ستوونی، ڕۆمانی…",
    filterKind: "جۆری بەڵگەنامە",
    filterCitation: "شێوازی سەرچاوەناساندن",
    filterLanguage: "زمان",
    filterPaper: "قەبارەی کاغەز",
    filterDirection: "ئاراستە",
    optionAny: "هەرکدام",
    directionLtr: "چەپ بۆ ڕاست",
    directionRtl: "ڕاست بۆ چەپ",
    resultsCount: "{count} دیزایینی جیاواز",
    loadMore: "دیزاینی زیاتر پیشان بدە",
    allLoaded: "هەموو دیزاینە بەردەستەکان پیشان دران",
    previewAria: "پێشبینینی ڕووکاری نمونەیی بۆ {name}",
    designHashLabel: "پەنجەمەڵی دیزاین",
    variantsLabel: "گۆڕاوە هەڵبژێردراوەکان",
    useDesignLabel: "ئەم دیزاینە بەکاربهێنە",
    selectedDesignLabel: "هەڵبژێردراو",
    integrityNote: "خانەی شابلۆن یارمەتی ڕوونکردنەوەی ڕاستگۆی بەکارهێنانی زیرەکی دەستکرد دەدەن بەپێی سیاسەتی زانکۆکەت. هەرگیز نووسەرێناتیان ناشارنەوە.",
    snapshotNote: "دووبارە ڕێکخستن یان هەناردەکردنەوەی توێژینەوەی پاشەکەوتکراو بەم ڤاڵێڕێوانە کارێکی ڕووکارە — هیچ کرێی بەرەوپێشبردنی نوێی ناخوێنێتەوە.",
    privacyNote: "پێشبینینەکان تەنها لە ڕێکخستنی ڕووکار دروستدەبن. هیچ زانیاری پێشکەشکار، مۆدێل یان تێچوو پیشان نادرێت.",
  },
  ar: {
    galleryTitle: "قوالب التنسيق الأكاديمي",
    gallerySubtitle: "عاين تصاميم الرسائل والأطروحات ومقترحات البحث والتقارير قبل التصدير. التنسيق فقط — المعاينات تحتوي نصًا نموذجيًا ولا تعرض أي محتوى بحثي.",
    searchLabel: "بحث في التصاميم",
    searchPlaceholder: "مثال: كردي، عمودان، أرقام رومانية…",
    filterKind: "نوع الوثيقة",
    filterCitation: "نمط التوثيق",
    filterLanguage: "اللغة",
    filterPaper: "مقاس الورق",
    filterDirection: "الاتجاه",
    optionAny: "الكل",
    directionLtr: "من اليسار إلى اليمين",
    directionRtl: "من اليمين إلى اليسار",
    resultsCount: "{count} تصميمًا مميزًا",
    loadMore: "إظهار المزيد",
    allLoaded: "تم عرض جميع التصاميم المطابقة",
    previewAria: "معاينة تنسيق تجريبية لـ {name}",
    designHashLabel: "بصمة التصميم",
    variantsLabel: "الخيارات المحددة",
    useDesignLabel: "استخدم هذا التصميم",
    selectedDesignLabel: "محدد",
    integrityNote: "تدعم خانات القالب الإفصاح الصادق عن استخدام الذكاء الاصطناعي التوليدي وفق سياسة مؤسستك، ولا تخفي نسبة التأليف أبدًا.",
    snapshotNote: "إعادة التنسيق أو إعادة تصدير بحث محفوظ باستخدام هذه القوالب عملية تنسيقية — ولا تُفعّل رسوم توليد محتوى جديد.",
    privacyNote: "تُحسب المعاينات من إعدادات التنسيق فقط، ولا يُعرض أي معلومات عن المزوّد أو النموذج أو التكلفة.",
  },
};

export const academicCopyLocales: AcademicCopyLocale[] = ["en", "ckb", "ar"];

export function academicCopy(locale: AcademicCopyLocale, key: AcademicCopyKey, vars?: Record<string, string | number>): string {
  const template = COPY[locale]?.[key] ?? COPY.en[key];
  if (!vars) return template;
  return template.replace(/\{(\w+)\}/g, (match, name: string) => (name in vars ? String(vars[name]) : match));
}

/** Keys missing from a locale relative to English — empty means the locale is complete. */
export function missingCopyKeys(locale: AcademicCopyLocale): AcademicCopyKey[] {
  const table = COPY[locale];
  return (Object.keys(COPY.en) as AcademicCopyKey[]).filter((key) => !(key in table));
}
