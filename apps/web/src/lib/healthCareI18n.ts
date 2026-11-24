import type { Locale } from "@/lib/i18n";

type HealthCareFeature = Readonly<{ id: string; label: string }>;

export type HealthCareCopy = Readonly<{
  category: string;
  eyebrow: string;
  title: string;
  description: string;
  status: string;
  plannedHeading: string;
  features: readonly HealthCareFeature[];
  actionUnavailable: string;
  privacyLabel: string;
  privacyNote: string;
  safetyLabel: string;
  safetyNote: string;
  backToCreate: string;
  errorEyebrow: string;
  errorTitle: string;
  errorDescription: string;
  retry: string;
}>;

const copy: Record<Locale, HealthCareCopy> = {
  en: {
    category: "Health & Wellness",
    eyebrow: "A private place for health information",
    title: "Health Care",
    description: "A private place to organize health information is being prepared. Health reports cannot be uploaded or analyzed here yet.",
    status: "Coming soon",
    plannedHeading: "Planned, not available yet",
    features: [
      { id: "reports", label: "Health documents and lab reports" },
      { id: "medications", label: "Medication and allergy organization" },
      { id: "timeline", label: "Personal health timeline" },
      { id: "questions", label: "Questions to discuss with a clinician" },
    ],
    actionUnavailable: "Unavailable — coming soon",
    privacyLabel: "Privacy first:",
    privacyNote: "This preview does not collect, upload, store, or analyze health information and sends no health information to external services.",
    safetyLabel: "Safety:",
    safetyNote: "This is not medical advice, diagnosis, treatment, or emergency care. For urgent symptoms, contact local emergency services or a qualified clinician.",
    backToCreate: "Back to Create",
    errorEyebrow: "Health Care",
    errorTitle: "Health Care is temporarily unavailable",
    errorDescription: "No health information was uploaded or analyzed. Please try again.",
    retry: "Try again",
  },
  ar: {
    category: "الصحة والعافية",
    eyebrow: "مساحة خاصة للمعلومات الصحية",
    title: "الرعاية الصحية",
    description: "يجري إعداد مساحة خاصة لتنظيم المعلومات الصحية. لا يمكن رفع التقارير الصحية أو تحليلها هنا حتى الآن.",
    status: "قريبًا",
    plannedHeading: "مخطط لها، وغير متاحة بعد",
    features: [
      { id: "reports", label: "المستندات الصحية وتقارير المختبر" },
      { id: "medications", label: "تنظيم الأدوية والحساسيات" },
      { id: "timeline", label: "الخط الزمني الصحي الشخصي" },
      { id: "questions", label: "أسئلة لمناقشتها مع مختص صحي" },
    ],
    actionUnavailable: "غير متاح — قريبًا",
    privacyLabel: "الخصوصية أولًا:",
    privacyNote: "هذا العرض لا يجمع المعلومات الصحية أو يرفعها أو يخزنها أو يحللها، ولا يرسلها إلى خدمات خارجية.",
    safetyLabel: "السلامة:",
    safetyNote: "لا يُعدّ هذا نصيحةً طبيةً أو تشخيصًا أو علاجًا أو رعايةً طارئةً. عند ظهور أعراض تستدعي تدخّلًا عاجلًا، اتصل بخدمات الطوارئ المحلية أو بمختص صحي مؤهل.",
    backToCreate: "العودة إلى إنشاء",
    errorEyebrow: "الرعاية الصحية",
    errorTitle: "الرعاية الصحية غير متاحة مؤقتًا",
    errorDescription: "لم يتم رفع أي معلومات صحية أو تحليلها. يرجى المحاولة مرة أخرى.",
    retry: "حاول مرة أخرى",
  },
  ku: {
    category: "تەندروستی و خۆشگوزەرانی",
    eyebrow: "شوێنێکی تایبەت بۆ زانیارییە تەندروستییەکان",
    title: "چاودێریی تەندروستی",
    description: "شوێنێکی تایبەت بۆ ڕێکخستنی زانیارییە تەندروستییەکان ئامادە دەکرێت. هێشتا ناتوانرێت ڕاپۆرتە تەندروستییەکان لێرە بار بکرێن یان شیکاری بکرێن.",
    status: "بەم زووانە",
    plannedHeading: "لە پلاندا هاتووە، بەڵام هێشتا بەردەست نییە",
    features: [
      { id: "reports", label: "بەڵگەنامە تەندروستییەکان و ڕاپۆرتەکانی تاقیگە" },
      { id: "medications", label: "ڕێکخستنی دەرمان و حەساسیەتەکان" },
      { id: "timeline", label: "هێڵی کاتی تەندروستیی کەسی" },
      { id: "questions", label: "پرسیارەکان بۆ گفتوگۆ لەگەڵ پسپۆڕێکی تەندروستی" },
    ],
    actionUnavailable: "بەردەست نییە — بەم زووانە",
    privacyLabel: "تایبەتمەندی لە پێشەوە:",
    privacyNote: "ئەم پێشاندانە هیچ زانیارییەکی تەندروستی کۆ ناکاتەوە، بار ناکات، هەڵی ناگرێت یان شیکاری ناکات، و هیچ زانیارییەکی تەندروستی نانێرێت بۆ خزمەتگوزارییە دەرەکییەکان.",
    safetyLabel: "سەلامەتی:",
    safetyNote: "ئەمە ڕاوێژی پزیشکی، دەستنیشانکردنی نەخۆشی، چارەسەر یان چاودێریی پزیشکیی فۆری نییە. لە کاتی هەبوونی نیشانە فۆرییەکاندا، پەیوەندی بە خزمەتگوزارییە فۆرییەکانی ناوچەکەت یان پسپۆڕێکی تەندروستیی شیاو بکە.",
    backToCreate: "گەڕانەوە بۆ دروستکردن",
    errorEyebrow: "چاودێریی تەندروستی",
    errorTitle: "چاودێریی تەندروستی بە شێوەی کاتی بەردەست نییە",
    errorDescription: "هیچ زانیارییەکی تەندروستی بار نەکراوە یان شیکاری نەکراوە.",
    retry: "دووبارە هەوڵ بدەوە",
  },
};

export function healthCareCopy(locale: Locale): HealthCareCopy {
  return copy[locale];
}

export function healthCareDirection(locale: Locale): "ltr" | "rtl" {
  return locale === "en" ? "ltr" : "rtl";
}

export function healthCareLanguageTag(locale: Locale): string {
  return locale === "ku" ? "ckb" : locale;
}
