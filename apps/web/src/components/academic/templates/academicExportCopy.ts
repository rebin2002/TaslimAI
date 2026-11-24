/**
 * Issue #1267 — copy for the saved-report export surface (English, Kurdish ckb,
 * Arabic). Interface copy only; it never rewrites research content.
 *
 * House rules (mirroring #1259 and #1265 and pinned by academicExportCopy.test.ts):
 * - The panel must never promise to write, complete or improve the report itself:
 *   it only reformats an already-saved report.
 * - No vendor, provider, model or billing wording anywhere.
 * - Metadata-only sources are described honestly: catalogue record, not read.
 * - The academic-integrity disclosure is always present in the exported file, and
 *   the copy says so rather than suggesting it can be skipped.
 */
export type AcademicExportCopyLocale = "en" | "ckb" | "ar";

export type AcademicExportCopyKey =
  | "panelTitle"
  | "panelIntro"
  | "reportLabel"
  | "reportPlaceholder"
  | "reportsLoading"
  | "reportsEmpty"
  | "reportsFailed"
  | "summaryLoading"
  | "summaryTitle"
  | "summaryFailed"
  | "savedAt"
  | "sectionsCount"
  | "sourcesCount"
  | "accessedCount"
  | "metadataOnlyCount"
  | "metadataWarning"
  | "disclosureHeading"
  | "disclosureBody"
  | "disclosureLabel"
  | "disclosurePlaceholder"
  | "disclosureMissing"
  | "templateHeading"
  | "templateSelected"
  | "templateNone"
  | "formatHeading"
  | "exportDocx"
  | "exportPdf"
  | "exporting"
  | "statusFirst"
  | "statusSame"
  | "statusReformat"
  | "downloadCheck"
  | "errorGeneric"
  | "errorIntegrity"
  | "errorDirection"
  | "errorNotFound"
  | "errorRateLimited";

const COPY: Record<AcademicExportCopyLocale, Record<AcademicExportCopyKey, string>> = {
  en: {
    panelTitle: "Export a saved report with a template design",
    panelIntro:
      "Pick one of the saved research reports below, choose any of the {count} layout designs, and download the same report text as DOCX or PDF. The content is not rewritten and no new research is started.",
    reportLabel: "Saved report",
    reportPlaceholder: "Select a saved research report",
    reportsLoading: "Loading your saved reports…",
    reportsEmpty:
      "No completed research reports are saved in this workspace yet. Finish a research run in Academic Research Studio first, then come back to export it.",
    reportsFailed: "Your saved reports could not be loaded. Reload the page to try again.",
    summaryLoading: "Loading the report summary…",
    summaryFailed: "This report summary could not be loaded. Choose another report or try again.",
    summaryTitle: "What will be exported",
    savedAt: "Saved {date}",
    sectionsCount: "{count} sections",
    sourcesCount: "{count} sources",
    accessedCount: "{count} sources with full text reviewed here",
    metadataOnlyCount: "{count} catalogue records only",
    metadataWarning:
      "Sources listed as catalogue records were not read in full in this workspace. The export labels every one of them honestly, and a submitted paper must not claim they were read.",
    disclosureHeading: "Academic-integrity disclosure",
    disclosureBody:
      "Describe how AI assistance was used and which parts are your own writing. The disclosure is included in every export and cannot be removed; leaving it blank prints a statement that it was not completed.",
    disclosureLabel: "AI-use disclosure",
    disclosurePlaceholder:
      "For example: AI helped search the literature and outline chapters. All analysis, argument and final writing are my own.",
    disclosureMissing: "Not completed — the exported file will state that the disclosure was left incomplete.",
    templateHeading: "Choose a layout design, then preview it",
    templateSelected: "Selected design",
    templateNone: "No design selected yet. Preview one in the gallery above to enable the downloads.",
    formatHeading: "Download format",
    exportDocx: "Export as DOCX",
    exportPdf: "Export as PDF",
    exporting: "Rendering your saved report with the selected design…",
    statusFirst: "Exported: your saved report was reformatted with the selected design. No new research was started.",
    statusSame: "Re-exported with your saved settings. Your report text is unchanged.",
    statusReformat: "Reformatted with a different design. Your report text is unchanged.",
    downloadCheck:
      "The file was checked before download: title, citations, bibliography and the integrity disclosure are present.",
    errorGeneric: "The export could not be completed. Your saved report is untouched — review the message and try again.",
    errorIntegrity:
      "Some sources cannot be cited verifiably in a submitted document yet. Review the sources in your report, then try again.",
    errorDirection: "The selected design reads in a different direction than this report. Choose a matching design.",
    errorNotFound: "This report is no longer available to your account, or its saved payload cannot be exported.",
    errorRateLimited:
      "Too many exports were asked for in a short time, so this one was not produced. Your saved report is untouched and no new research was started — wait a minute, then try again.",
  },
  ckb: {
    panelTitle: "هەناردەکردنی ڕاپۆرتی پاشەکەوتکراو بە دیزاینی داڕشتەوە",
    panelIntro:
      "یەکێک لە ڕاپۆرتە توێژینەوە پاشەکەوتکراوەکان هەڵبژێرە، دیزاینی ڕازاندنەوەت لە نێو {count} داڕشتەدا هەڵبژێرە، و ڕاپۆرتەکە بە هەمان دەق وەکوو DOCX یان PDF دابەزێنە. دەق نوێنابێتەوە و توێژینەوەی نوێ دەست پێناکات.",
    reportLabel: "ڕاپۆرتی پاشەکەوتکراو",
    reportPlaceholder: "ڕاپۆرتی توێژینەوەی پاشەکەوتکراو هەڵبژێرە",
    reportsLoading: "بارکردنی ڕاپۆرتە پاشەکەوتکراوەکان…",
    reportsEmpty:
      "هێشتا هیچ ڕاپۆرتی توێژینەوەی تەواوکراو پاشەکەوت نەکراوە. سەرەتا توێژینەوەیەک تەواو بکە، پاشان بگەڕێوە بۆ هەناردەکردنی.",
    reportsFailed: "ڕاپۆرتە پاشەکەوتکراوەکانت نەهاتن. پەڕەکە دووبارە بار بکەوە.",
    summaryLoading: "بارکردنی پوختەی ڕاپۆرت…",
    summaryFailed: "پوختەی ئەم ڕاپۆرتە نەهات. ڕاپۆرتێکی تر هەڵبژێرە یان دووبارە هەوڵ بدە.",
    summaryTitle: "چی هەناردە دەکرێت",
    savedAt: "پاشەکەوتکراو لە {date}",
    sectionsCount: "{count} بەش",
    sourcesCount: "{count} سەرچاوە",
    accessedCount: "{count} سەرچاوە کە تەواوی دەقەکەی لێرەوە خوێندراوەتەوە",
    metadataOnlyCount: "{count} تۆماری کەتەلۆگ تەنها",
    metadataWarning:
      "سەرچاوەکانی وەکوو تۆماری کەتەلۆگ نیشاندران تەواوی دەقەکەیان لەم شوێنەدا خوێندراونەتەوە نییە. هەناردەکە هەموویان بە ڕاستگۆیی نیشان دەکات، و پێویستە لە توێژینەوەی پێشکەشکراودا وەکوو خوێندراو نەنرێن.",
    disclosureHeading: "ڕاگەیاندنی ڕەوشتی ئەکادیمی",
    disclosureBody:
      "ڕابگەیەنە یارمەتی زیرەکی دەستکرد چۆن بەکارهاتووە و کام بەشەکان دەستی خۆتن. ئەم ڕاگەیاندنە لە هەموو هەناردەیەکدا هەیە و ناڕەشێت بدرێت؛ ئەگەر بەتاڵ بمێنێتەوە، فایلەکە نووسینی نەتەواوکراوی تێدا دەردەکەوێت.",
    disclosureLabel: "ڕاگەیاندنی بەکارهێنانی زیرەکی دەستکرد",
    disclosurePlaceholder:
      "بۆ نموونە: زیرەکی دەستکرد لە گەڕان بۆ سەرچاوە و ڕیزی بەشەکان یارمەتیدەر بوو. هەموو شیکردنەوە و نووسینی کۆتایی مالی خۆمە.",
    disclosureMissing: "نەتەواوکراو — فایلە هەناردەکراوەکە دەنووسێت کە ڕاگەیاندنەکە نەتەواو بەرجەماوەتەوە.",
    templateHeading: "دیزاینی ڕازاندنەوە هەڵبژێرە، پێشبینینەکەی ببینە",
    templateSelected: "دیزاینی هەڵبژێردراو",
    templateNone: "هێشتا دیزاین هەڵنەبژێردراوە. لە گەلەری سەرەوە پێشبینینی یەکێک ببینە تا دوگمەکانی دابەزاندن چالاک بن.",
    formatHeading: "فۆڕمەتی دابەزاندن",
    exportDocx: "هەناردەکردن وەکوو DOCX",
    exportPdf: "هەناردەکردن وەکوو PDF",
    exporting: "ڕاپۆرتی پاشەکەوتکراوەکەت بە دیزاینی هەڵبژێردراو ئامادە دەکرێت…",
    statusFirst: "هەناردەکرا: ڕاپۆرتی پاشەکەوتکراوەکەت بەم دیزاینە ڕازێندرایەوە. توێژینەوەی نوێ دەست پێنەکرد.",
    statusSame: "دووبارە هەناردەکرا بە ڕێکخستنە پاشەکەوتکراوەکانت. دەقی ڕاپۆرت نەگۆڕاوە.",
    statusReformat: "بە دیزاینێکی جیاواز ڕازێندرایەوە. دەقی ڕاپۆرت نەگۆڕاوە.",
    downloadCheck:
      "پێش دابەزاندن فایلەکە پشکنرا: سەرناو، سەرچاوەپێکردنەکان، کتێپسۆر و ڕاگەیاندنی ڕەوشتی لەخۆدەگرێت.",
    errorGeneric: "هەناردەکدن تەواو نابوو. ڕاپۆرتی پاشەکەوتکراوەکەت دەست‌نەکراوەیە — نامەکە بخوێنەرەوە و دووبارە هەوڵ بدە.",
    errorIntegrity:
      "هەندێک سەرچاوە هێشتا بە شێوەیەکی دڵنیاکراو ناتوانرێت لە بەڵگەنامەی پێشکەشکراودا بهێنرێت. سەرچاوەکان پشکنە و دووبارە هەوڵ بدە.",
    errorDirection: "دیزاینی هەڵبژێردراو لەگەڵ ئاراستەی خوێندنەوەی ئەم ڕاپۆرتەدا ناکۆکە. دیزاینێکی گونجاو هەڵبژێرە.",
    errorNotFound: "ئەم ڕاپۆرتە بۆ هەژمارەکەت بەردەست نییە، یان دەقی پاشەکەوتکراوەکەی ناگەنجرێتە هەناردەکردن.",
    errorRateLimited:
      "لە ماوەیەکی کورتدا هەناردەکردنی زۆرت داوا کردووە، بۆیە ئەمە دروست نەکرا. ڕاپۆرتی پاشەکەوتکراوەکەت دەست‌نەکراوەیە و هیچ لێکۆڵینەوەیەکی نوێ دەست پێنەکات — چەند خولەکێک چاوەڕێ بکە و دووبارە هەوڵ بدە.",
  },
  ar: {
    panelTitle: "تصدير تقرير محفوظ بتصميم قالب",
    panelIntro:
      "اختر أحد تقارير البحث المحفوظة أدناه، ثم اختر واحدًا من {count} تصميمًا للصفحة، وحمّل نص التقرير نفسه بصيغة DOCX أو PDF. لا يُعاد كتابة المحتوى ولا يبدأ بحثٌ جديد.",
    reportLabel: "التقرير المحفوظ",
    reportPlaceholder: "اختر تقرير بحث محفوظًا",
    reportsLoading: "جارٍ تحميل تقاريرك المحفوظة…",
    reportsEmpty:
      "لا توجد تقارير بحث مكتملة محفوظة في مساحة العمل هذه بعد. أنهِ تشغيل بحث في «استوديو البحث الأكاديمي» أولًا، ثم عد للتصدير.",
    reportsFailed: "تعذّر تحميل التقارير المحفوظة. أعد تحميل الصفحة للمحاولة مرة أخرى.",
    summaryLoading: "جارٍ تحميل ملخص التقرير…",
    summaryFailed: "تعذّر تحميل ملخص هذا التقرير. اختر تقريرًا آخر أو أعد المحاولة.",
    summaryTitle: "ما الذي سيُصدَّر",
    savedAt: "حُفظ في {date}",
    sectionsCount: "{count} أقسام",
    sourcesCount: "{count} مصادر",
    accessedCount: "{count} مصادر تمت مراجعة نصها الكامل هنا",
    metadataOnlyCount: "{count} سجلّ فهرس فقط",
    metadataWarning:
      "المصادر المعروضة كسجلّات فهرس لم يُقرأ نصها الكامل في مساحة العمل هذه. يوسم التصدير كل مصدر بصدق، ولا يجوز أن يدّعي بحثٌ مقدَّم أنها قُرئت.",
    disclosureHeading: "إفصاح الأمانة الأكاديمية",
    disclosureBody:
      "وضّح كيف استُخدم الذكاء الاصطناعي وأي الأجزاء من كتابتك. يُدرَج هذا الإفصاح في كل تصدير ولا يمكن حذفه؛ وإذا تُرك فارغًا طُبِع تنبيه بأنه غير مكتمل.",
    disclosureLabel: "إفصاح استخدام الذكاء الاصطناعي",
    disclosurePlaceholder:
      "مثال: ساعد الذكاء الاصطناعي في البحث عن المصادر وترتيب العناوين. التحليل والحجج والكتابة النهائية كلها منّي.",
    disclosureMissing: "غير مكتمل — سيذكر الملف المصدَّر أن الإفصاح تُرك غير مكتمل.",
    templateHeading: "اختر تصميم الصفحة وعاينه",
    templateSelected: "التصميم المختار",
    templateNone: "لم يُختر تصميم بعد. عاين أحد التصاميم في المعرض أعلاه لتفعيل أزرار التنزيل.",
    formatHeading: "صيغة التنزيل",
    exportDocx: "تصدير بصيغة DOCX",
    exportPdf: "تصدير بصيغة PDF",
    exporting: "جارٍ تجهيز تقريرك المحفوظ بالتصميم المختار…",
    statusFirst: "تم التصدير: أُعيد تنسيق تقريرك المحفوظ بالتصميم المختار. لم يبدأ أي بحث جديد.",
    statusSame: "أُعيد التصدير بإعداداتك المحفوظة. نص تقريرك دون تغيير.",
    statusReformat: "أُعيد التنسيق بتصميم مختلف. نص تقريرك دون تغيير.",
    downloadCheck:
      "فُحص الملف قبل التنزيل: العنوان والاقتباسات وقائمة المراجع وإفصاح الأمانة كلها موجودة.",
    errorGeneric: "لم يكتمل التصدير. تقريرك المحفوظ لم يُمسّ — اقرأ الرسالة وأعد المحاولة.",
    errorIntegrity:
      "بعض المصادر لا يمكن الاستشهاد بها بشكل قابل للتحقق بعد. راجع مصادر التقرير ثم أعد المحاولة.",
    errorDirection: "التصميم المختار يختلف في اتجاه القراءة عن هذا التقرير. اختر تصميمًا مطابقًا.",
    errorNotFound: "هذا التقرير لم يعد متاحًا لحسابك، أو إن نسخة المحفوظات منه لا يمكن تصديرها.",
    errorRateLimited:
      "طلبت تصديرات كثيرة في وقت قصير، فلم يُنشأ هذا الملف. تقريرك المحفوظ لم يُمسّ ولا يبدأ أي بحث جديد — انتظر دقيقة ثم أعد المحاولة.",
  },
};

export const academicExportCopyLocales: AcademicExportCopyLocale[] = ["en", "ckb", "ar"];

function interpolate(value: string, vars?: Record<string, string | number>): string {
  if (!vars) return value;
  return value.replace(/\{(\w+)\}/g, (whole, name: string) =>
    Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : whole,
  );
}

export function academicExportCopy(
  locale: AcademicExportCopyLocale,
  key: AcademicExportCopyKey,
  vars?: Record<string, string | number>,
): string {
  return interpolate(COPY[locale]?.[key] ?? COPY.en[key], vars);
}

/** Keys a locale has not translated yet — the completeness test asserts this stays empty. */
export function missingExportCopyKeys(locale: AcademicExportCopyLocale): AcademicExportCopyKey[] {
  const table = COPY[locale] ?? COPY.en;
  return (Object.keys(COPY.en) as AcademicExportCopyKey[]).filter((key) => !table[key]);
}
