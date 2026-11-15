/**
 * Provider-free Movie Makeover surface copy. Kept feature-local to avoid
 * overwriting the shared Movie route/i18n owner's files. A route must still
 * pass its resolved locale and separately authorize data before mounting.
 *
 * These strings do NOT claim that provider generation is available.
 */
export type MovieMakeoverLocale = "en" | "ar" | "ku";

const copy = {
  en: {
    inputRegion: "AI Video Makeover inputs",
    title: "AI Video Makeover",
    draftNotice: "Draft input only. No provider is wired: estimate and start controls stay disabled and no credit is reserved.",
    mode: "Mode", modeRegion: "Makeover mode", supportStatus: "Support status",
    modeStudio: "Studio swap", modeRelight: "AI relight", modeRestyle: "Video restyle",
    modeWardrobe: "Outfit change", future: "Future feature — not yet available",
    source: "Source video", chooseClip: "Choose an uploaded clip", selectedClip: "Selected clip",
    remove: "Remove", attachSource: "Attach the source video to makeover.",
    preset: "Preset", prompt: "Optional prompt", capability: "Verified capabilities",
    requiredForMode: "Required for this mode:", none: "none until a supported mode is selected",
    capabilityRule: "A feature counts only when documented by the provider and confirmed by a tested capability.",
    evidence: "Evidence", preservation: "Preservation and consent",
    preserveIdentity: "Preserve subject identity (never swap faces)",
    preserveAudio: "Preserve original audio", creatorApproval: "Creator explicitly approves this makeover draft",
    notWired: "Not wired yet", start: "Start makeover",
    sourceRegion: "Makeover source video", sourceEmpty: "Nothing to show yet. A source has to be a finished video asset in this workspace.",
    sourceNoClips: "No clips to list. Finish a short-form Movie render, or upload a video asset, then reopen this picker.",
    sourceNoneEligible: "None of these clips can be a makeover source yet. Each row says what is missing.",
    sourceCount: (eligible: number, total: number) => `${eligible} of ${total} clip${total === 1 ? "" : "s"} can be attached as a source.`,
    sourceScope: "Picking a source only edits the draft. Upload from this panel is not wired, and attaching a clip starts no job and reserves no credit.",
    attach: "Attach as source", attached: "Attached", detach: "Detach",
    waitingForScope: "No source attached. Movie Makeover draft unavailable until account and workspace data are refreshed.",
    region: "AI Video Makeover before and after comparison", view: "Comparison view",
    split: "Split", before: "Before", after: "After", original: "Original video",
    result: "Makeover result", splitter: "Reveal position between original and makeover result",
    beforeUnavailable: "Original video preview is not available.",
    afterUnavailable: "Makeover result preview is not available yet. This screen did not request it again.",
    blocked: "This preview source was not accepted. Only authorized local or blob preview sources are shown here.",
  },
  ar: {
    inputRegion: "مدخلات تحسين الفيديو بالذكاء الاصطناعي",
    title: "تحسين الفيديو بالذكاء الاصطناعي",
    draftNotice: "مسودة فقط. لم يتم ربط مزود الخدمة؛ تقدير التكلفة والبدء معطّلان ولا يتم حجز أي رصيد.",
    mode: "الوضع", modeRegion: "نوع التحسين", supportStatus: "حالة الدعم",
    modeStudio: "تغيير الاستوديو", modeRelight: "إعادة الإضاءة", modeRestyle: "تغيير أسلوب الفيديو",
    modeWardrobe: "تغيير الملابس", future: "ميزة مستقبلية — غير متاحة حالياً",
    source: "الفيديو المصدر", chooseClip: "اختر مقطعاً مرفوعاً", selectedClip: "المقطع المحدد",
    remove: "إزالة", attachSource: "أرفق الفيديو المصدر لعملية التحسين.",
    preset: "الإعداد المسبق", prompt: "وصف اختياري", capability: "القدرات المتحقق منها",
    requiredForMode: "المطلوب لهذا الوضع:", none: "لا شيء حتى اختيار وضع مدعوم",
    capabilityRule: "لا تُعد القدرة متاحة إلا إذا وثّقها المزود وأكدها اختبار.",
    evidence: "الدليل", preservation: "الحفاظ والموافقة",
    preserveIdentity: "الحفاظ على هوية الشخص (دون تبديل الوجوه)",
    preserveAudio: "الحفاظ على الصوت الأصلي", creatorApproval: "أوافق صراحةً على مسودة هذا التحسين",
    notWired: "غير متصل بعد", start: "بدء التحسين",
    sourceRegion: "الفيديو المصدر للتحسين", sourceEmpty: "لا توجد مقاطع للعرض. يلزم وجود فيديو مكتمل ضمن مساحة العمل هذه.",
    sourceNoClips: "لا توجد مقاطع. أكمل إنتاج فيديو قصير أو ارفع ملف فيديو ثم افتح أداة الاختيار مجدداً.",
    sourceNoneEligible: "لا يوجد مقطع مؤهل كمصدر حتى الآن. توضح كل خانة سبب المنع.",
    sourceCount: (eligible: number, total: number) => `يمكن إرفاق ${eligible} من أصل ${total} مقطع كمصدر.`,
    sourceScope: "اختيار المصدر يعدّل المسودة فقط. الرفع غير متصل هنا، ولا يبدأ الإرفاق أي مهمة أو يحجز رصيداً.",
    attach: "إرفاق كمصدر", attached: "مرفق", detach: "فصل",
    waitingForScope: "لا يوجد مصدر مرفق. المسودة غير متاحة حتى تحديث بيانات الحساب ومساحة العمل.",
    region: "مقارنة الفيديو قبل التحسين وبعده", view: "طريقة المقارنة",
    split: "تقسيم", before: "قبل", after: "بعد", original: "الفيديو الأصلي",
    result: "نتيجة التحسين", splitter: "موضع الكشف بين الفيديو الأصلي والنتيجة",
    beforeUnavailable: "معاينة الفيديو الأصلي غير متاحة.",
    afterUnavailable: "معاينة النتيجة غير متاحة بعد. لم تُرسل أي عملية جديدة.",
    blocked: "تم رفض مصدر المعاينة. لا تُعرض إلا مصادر المعاينة المحلية أو المصرح بها.",
  },
  ku: {
    inputRegion: "زانیارییەکانی جوانکردنی ڤیدیۆ بە زیرەکی دەستکرد",
    title: "جوانکردنی ڤیدیۆ بە زیرەکی دەستکرد",
    draftNotice: "تەنها ڕەشنووسە. هیچ دابینکەرێک پەیوەست نەکراوە؛ خەملاندن و دەستپێکردن ناچالاکن و هیچ کرێدتێک گیر ناگیرێت.",
    mode: "جۆر", modeRegion: "جۆری جوانکردن", supportStatus: "دۆخی پشتگیری",
    modeStudio: "گۆڕینی ستۆدیۆ", modeRelight: "ڕووناکیکردنەوە", modeRestyle: "گۆڕینی شێوازی ڤیدیۆ",
    modeWardrobe: "گۆڕینی جل", future: "تایبەتمەندی داهاتوو — ئێستا بەردەست نییە",
    source: "ڤیدیۆی سەرچاوە", chooseClip: "کلیپێکی بارکراو هەڵبژێرە", selectedClip: "کلیپی هەڵبژێردراو",
    remove: "لابردن", attachSource: "ڤیدیۆی سەرچاوە بۆ جوانکردن زیاد بکە.",
    preset: "ڕێکخستنی پێشوەخت", prompt: "وەسفی ئارەزوومەندانە", capability: "تواناکانی پشتڕاستکراوە",
    requiredForMode: "پێویستییەکانی ئەم جۆرە:", none: "هیچ، تا جۆرێکی پشتگیریکراو هەڵدەبژێردرێت",
    capabilityRule: "توانا تەنها کاتێک پەسەندە کە دابینکەر بەڵگەی بۆ هەبێت و تاقیکردنەوە پشتڕاستی بکاتەوە.",
    evidence: "بەڵگە", preservation: "پاراستن و ڕەزامەندی",
    preserveIdentity: "پاراستنی ناسنامەی کەسەکە (بێ گۆڕینی ڕووخسار)",
    preserveAudio: "پاراستنی دەنگی ڕەسەن", creatorApproval: "بە ڕوونی ڕەزامەندم بە ئەم ڕەشنووسە",
    notWired: "هێشتا پەیوەست نەکراوە", start: "دەستپێکردنی جوانکردن",
    sourceRegion: "ڤیدیۆی سەرچاوە بۆ جوانکردن", sourceEmpty: "هیچ کلیپێک نییە. پێویستە ڤیدیۆیەکی تەواوکراو لەم بۆشایی کارەدا هەبێت.",
    sourceNoClips: "هیچ کلیپێک نییە. ڤیدیۆیەکی کورت تەواو بکە یان ڤیدیۆیەک بار بکە و دووبارە بگەڕێوە.",
    sourceNoneEligible: "هێشتا هیچ کلیپێک بۆ سەرچاوە گونجاو نییە. هۆکارەکان لە هەر ڕیزێکدا دیارن.",
    sourceCount: (eligible: number, total: number) => `${eligible} لە ${total} کلیپ دەتوانرێت وەک سەرچاوە زیاد بکرێت.`,
    sourceScope: "هەڵبژاردنی سەرچاوە تەنها ڕەشنووسەکە دەگۆڕێت. بارکردن لێرە کار ناکات و هیچ کارێک دەست پێ ناکات یان کرێدت ناگیرێت.",
    attach: "زیادکردن وەک سەرچاوە", attached: "زیادکراوە", detach: "لابردنی پەیوەندی",
    waitingForScope: "هیچ سەرچاوەیەک زیاد نەکراوە. ڕەشنووس بەردەست نییە تا زانیاری هەژمار و بۆشایی کار نوێ دەبێتەوە.",
    region: "بەراوردی پێش و دوای جوانکردنی ڤیدیۆ", view: "شێوازی بەراورد",
    split: "دابەشکردن", before: "پێش", after: "دوای", original: "ڤیدیۆی ڕەسەن",
    result: "ئەنجامی جوانکردن", splitter: "شوێنی دەرخستنی جیاوازی نێوان ڕەسەن و ئەنجام",
    beforeUnavailable: "پێشبینینی ڤیدیۆی ڕەسەن بەردەست نییە.",
    afterUnavailable: "پێشبینینی ئەنجام هێشتا بەردەست نییە. داواکارییەکی نوێ نەکراوە.",
    blocked: "سەرچاوەی پێشبینین پەسەند نەکرا. تەنها سەرچاوە ناوخۆیی یان ڕێپێدراوەکان نیشان دەدرێن.",
  },
} as const;

export const movieMakeoverCopy = (locale: MovieMakeoverLocale) => copy[locale];
export const movieMakeoverDirection = (locale: MovieMakeoverLocale): "ltr" | "rtl" =>
  locale === "en" ? "ltr" : "rtl";


import { isMovieVideoMakeoverUnsupportedFutureMode } from "@/lib/movieVideoMakeoverDraftState";
import {
  movieVideoMakeoverSourcePendingCopy,
  type MovieVideoMakeoverSourceBlockCode,
  type MovieVideoMakeoverSourceGapCode,
  type MovieVideoMakeoverSourceEntry,
} from "@/lib/movieVideoMakeoverSourceCatalog";

/** Source eligibility codes are authoritative; never translate arbitrary server text. */
type IssueLocale = Exclude<MovieMakeoverLocale, "en">;
const sourceIssueCopy: Record<IssueLocale, {
  blockers: Record<MovieVideoMakeoverSourceBlockCode, string>;
  gaps: Record<MovieVideoMakeoverSourceGapCode, string>;
  futureHeadline: string; futureDetail: string;
  noneHeadline: string; noneDetail: string;
  blockedHeadline: string;
  completeHeadline: string; completeDetail: string;
  pendingHeadline: (count: number) => string;
}> = {
  ar: {
    blockers: {
    source_asset_missing: "لا يوجد معرّف ملف لإرفاق هذا المقطع.",
    source_asset_not_video: "يتطلب التحسين ملف فيديو مكتملًا؛ هذا العنصر ليس فيديو مؤهلًا.",
    source_asset_unavailable: "الملف غير متاح أو لم يسمح الخادم بإرفاقه.",
    source_clip_not_ready: "لم يكتمل إنتاج هذا المقطع بعد.",
    source_file_missing: "لا يوجد ملف محفوظ مرتبط بهذا العنصر.",
    source_cross_workspace: "ينتمي المقطع إلى مساحة عمل أخرى ولا يمكن إرفاقه هنا.",
    source_cross_principal: "لم يتم إثبات ملكية منشئ المحتوى لهذا المقطع.",
    source_preview_unavailable: "معاينة الملف غير متاحة؛ لا يمكن التحقق من مقارنة قبل وبعد.",
    },
    gaps: {
    ownership_unverified: "خاوەنی هذا المقطع غير متحقق منه.",
    visibility_unverified: "خصوصية هذا المقطع غير متحقق منها.",
    retention_unverified: "سياسة الاحتفاظ بالملف غير متحقق منها.",
    duration_unknown: "مدة الفيديو غير معروفة.",
    dimensions_unknown: "أبعاد الفيديو غير معروفة.",
    frame_rate_unknown: "معدل الإطارات غير معروف.",
    audio_state_unknown: "وجود الصوت غير معروف.",
    size_unknown: "حجم الملف غير معروف.",
    },
    futureHeadline: "تغيير الملابس ميزة مستقبلية؛ إرفاق مقطع لا يجعلها متاحة.",
    futureDetail: "لم يُثبت دعم تعديل الملابس داخل الفيديو؛ يبقى الطلب غير متاح.",
    noneHeadline: "لم يُرفق فيديو مصدر. اختر مقطعًا مكتملًا من مساحة العمل هذه.",
    noneDetail: "رفع فيديو جديد غير متاح في هذه اللوحة؛ يمكن فقط إرفاق المقاطع الموجودة.",
    blockedHeadline: "لا يمكن إرفاق هذا المقطع.",
    completeHeadline: "معلومات المصدر مكتملة، لكن تقدير التكلفة والبدء غير متاحين بعد.",
    completeDetail: "إرفاق المصدر لا يبدأ مهمة ولا يحجز رصيدًا ولا يتيح التنفيذ.",
    pendingHeadline: (count: number) => "تم إرفاق المصدر، لكن معلومات تحتاج إلى تحقق: " + String(count),
  },
  ku: {
    blockers: {
    source_asset_missing: "ناسێنەری فایل بۆ زیادکردنی ئەم کلیپە نییە.",
    source_asset_not_video: "جوانکردن پێویستی بە ڤیدیۆیەکی تەواوکراو هەیە؛ ئەم دانەیە ڤیدیۆی گونجاو نییە.",
    source_asset_unavailable: "فایلەکە بەردەست نییە یان ڕاژە ڕێگەی زیادکردنی نەداوە.",
    source_clip_not_ready: "بەرهەمهێنانی ئەم کلیپە هێشتا تەواو نەبووە.",
    source_file_missing: "هیچ فایلێکی پاشەکەوتکراو بۆ ئەم دانەیە نییە.",
    source_cross_workspace: "ئەم کلیپە هی بۆشایی کارێکی ترە و ناتوانرێت لێرە زیاد بکرێت.",
    source_cross_principal: "خاوەندارێتی دروستکەری ئەم کلیپە پشتڕاست نەکراوەتەوە.",
    source_preview_unavailable: "پێشبینینی فایلەکە بەردەست نییە؛ بەراوردی پێش و دوا ناتوانرێت پشتڕاست بکرێتەوە.",
    },
    gaps: {
    ownership_unverified: "خاوەنی ئەم کلیپە پشتڕاست نەکراوەتەوە.",
    visibility_unverified: "تایبەتمەندی ئەم کلیپە پشتڕاست نەکراوەتەوە.",
    retention_unverified: "سیاسەتی پاراستنی فایلەکە پشتڕاست نەکراوەتەوە.",
    duration_unknown: "ماوەی ڤیدیۆکە نادیارە.",
    dimensions_unknown: "قەبارەی وێنەی ڤیدیۆکە نادیارە.",
    frame_rate_unknown: "ژمارەی چوارچێوەکان لە چرکەیەکدا نادیارە.",
    audio_state_unknown: "بوونی دەنگ نادیارە.",
    size_unknown: "قەبارەی فایلەکە نادیارە.",
    },
    futureHeadline: "گۆڕینی جل تایبەتمەندی داهاتووە؛ زیادکردنی کلیپ چالاکی ناکات.",
    futureDetail: "پشتگیری گۆڕینی جل لە ڤیدیۆدا پشتڕاست نەکراوەتەوە؛ داواکارییەکە ناچالاکە.",
    noneHeadline: "هیچ ڤیدیۆیەکی سەرچاوە زیاد نەکراوە. کلیپێکی تەواوکراو هەڵبژێرە.",
    noneDetail: "بارکردنی ڤیدیۆی نوێ لەم پەنێڵەدا بەردەست نییە؛ تەنها کلیپە هەبووەکان زیاد دەکرێن.",
    blockedHeadline: "ئەم کلیپە ناتوانرێت زیاد بکرێت.",
    completeHeadline: "زانیارییەکانی سەرچاوە تەواون، بەڵام خەملاندن و دەستپێکردن بەردەست نین.",
    completeDetail: "زیادکردنی سەرچاوە هیچ کارێک دەست پێ ناکات و کرێدت ناگیرێت.",
    pendingHeadline: (count: number) => "سەرچاوە زیادکراوە، بەڵام ئەم ژمارە زانیارییانە پێویستیان بە پشتڕاستکردنەوە هەیە: " + String(count),
  },
};
export function localizedMovieMakeoverBlocker(code: MovieVideoMakeoverSourceBlockCode, original: string, locale: MovieMakeoverLocale): string {
  return locale === "en" ? original : sourceIssueCopy[locale].blockers[code];
}
export function localizedMovieMakeoverGap(code: MovieVideoMakeoverSourceGapCode, original: string, locale: MovieMakeoverLocale): string {
  return locale === "en" ? original : sourceIssueCopy[locale].gaps[code];
}
export function localizedMovieMakeoverSourcePendingCopy(
  entry: MovieVideoMakeoverSourceEntry | null, mode: string | null, locale: MovieMakeoverLocale,
): { readonly headline: string; readonly details: readonly string[] } {
  if (locale === "en") return movieVideoMakeoverSourcePendingCopy(entry, mode);
  const c = sourceIssueCopy[locale];
  if (isMovieVideoMakeoverUnsupportedFutureMode(mode)) return { headline: c.futureHeadline, details: [c.futureDetail] };
  if (entry === null) return { headline: c.noneHeadline, details: [c.noneDetail] };
  if (!entry.selectable) return {
    headline: c.blockedHeadline,
    details: entry.blockers.map((item) => localizedMovieMakeoverBlocker(item.code, item.message, locale)),
  };
  if (entry.gaps.length === 0) return { headline: c.completeHeadline, details: [c.completeDetail] };
  return {
    headline: c.pendingHeadline(entry.gaps.length),
    details: entry.gaps.map((item) => localizedMovieMakeoverGap(item.code, item.message, locale)),
  };
}
