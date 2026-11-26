import type { Locale } from "@/lib/i18n";
import type { HealthCareConsentSelectionPanelStrings } from "@/components/HealthCareConsentSelectionPanel";
import type { PrivateDocument } from "@/lib/healthCareConsentSelection";

/**
 * Issue #714 — feature-local copy for the Health Care consent journey
 * wiring (#715 route x #804 consent panel).
 *
 * Ownership boundaries honoured by this file:
 * - the shared `apps/web/src/lib/i18n.ts` is READ ONLY for the `Locale` type;
 *   no shared keys are added (the five i18n.ts claims remain unresolved per
 *   the 2026-11-02 owner hold on issue #714);
 * - `healthCareI18n.ts` (#715) and `HealthCareConsentSelection*.ts/tsx`
 *   (#804) are consumed as-is and never modified here.
 *
 * Privacy: everything below is invented demonstration content. The document
 * fixtures carry opaque synthetic identifiers and localized sample labels;
 * they are not real health records, are never uploaded, stored, read or sent
 * to any provider, and nothing here persists across sessions.
 */

export type HealthCareConsentJourneyCopy = HealthCareConsentSelectionPanelStrings &
  Readonly<{
    /** Heading of the consent section on the Health Care screen. */
    heading: string;
    /** Explains that the journey is a consent-state preview with samples only. */
    intro: string;
    /** Status announced once consent is active. */
    grantedNotice: string;
    /** Inert note shown when a synthetic sample preview is opened. */
    previewNotice: string;
    /** Synthetic sample document labels offered after an explicit grant. */
    documents: readonly PrivateDocument[];
  }>;

/**
 * Opaque synthetic identifiers, shared across locales so selection state is
 * stable while the user switches language. Never derived from real data and
 * never rendered: the panel and journey show the localized label only.
 */
export const HEALTH_CARE_SYNTHETIC_DOCUMENT_IDS = [
  "synth-journey-01",
  "synth-journey-02",
] as const;

const syntheticDocuments: Record<Locale, readonly PrivateDocument[]> = {
  en: [
    { id: "synth-journey-01", label: "Sample lab report (synthetic)", kind: "lab-report" },
    { id: "synth-journey-02", label: "Sample clinic note (synthetic)", kind: "note" },
  ],
  ar: [
    { id: "synth-journey-01", label: "تقرير مختبر تجريبي (تخليقي)", kind: "lab-report" },
    { id: "synth-journey-02", label: "ملاحظة عيادة تجريبية (تخليقية)", kind: "note" },
  ],
  ku: [
    { id: "synth-journey-01", label: "نموونەی ڕاپۆرتی تاقیگە (دروستکراو)", kind: "lab-report" },
    { id: "synth-journey-02", label: "نووسرای کلینیکی نموونەیی (دروستکراو)", kind: "note" },
  ],
};

const copy: Record<Locale, HealthCareConsentJourneyCopy> = {
  en: {
    heading: "Consent preview (synthetic)",
    intro:
      "Try the consent flow with invented sample documents. No real health information is uploaded, read, stored, analyzed, or sent anywhere.",
    optInTitle: "Use your own health documents",
    optInBody: "Nothing is read until you choose a document.",
    grantLabel: "Enable private documents",
    revokeLabel: "Withdraw consent",
    activeTitle: "Private documents",
    withdrawnNotice: "Consent withdrawn. Selections were cleared.",
    previewLabel: "Open preview",
    grantedNotice: "Consent active. Only synthetic samples are listed.",
    previewNotice: "No preview is available yet. Nothing was uploaded or analyzed.",
    documents: syntheticDocuments.en,
  },
  ar: {
    heading: "معاينة الموافقة (بيانات تجريبية)",
    intro:
      "جرّب تدفق الموافقة باستخدام مستندات تجريبية مخترعة. لا يتم رفع أي معلومات صحية حقيقية أو قراءتها أو تخزينها أو تحليلها أو إرسالها إلى أي جهة.",
    optInTitle: "استخدام مستنداتك الصحية الخاصة",
    optInBody: "لا تُقرأ أي بيانات حتى تختار مستندًا.",
    grantLabel: "تفعيل المستندات الخاصة",
    revokeLabel: "سحب الموافقة",
    activeTitle: "المستندات الخاصة",
    withdrawnNotice: "تم سحب الموافقة. تمت إزالة جميع الاختيارات.",
    previewLabel: "فتح المعاينة",
    grantedNotice: "الموافقة مفعّلة. تُعرض بيانات تجريبية فقط.",
    previewNotice: "لا توجد معاينة حتى الآن. لم يُرفع أو يُحلَّل أي شيء.",
    documents: syntheticDocuments.ar,
  },
  ku: {
    heading: "پێشاندانی ڕازی بوون (دراوستکراو)",
    intro:
      "هەنگاوی ڕازی بوونەکە بە بەڵگەنامە نموونەییە دروستکراوەکان تاقی بکەرەوە. هیچ زانیارییەکی ڕاستەقینەی تەندروستی بار ناکرێت، خوێنراوە نییە، هەڵنراوە، شیکاری نەکراوە، و نانێردرێت بۆ هیچ شوێنێک.",
    optInTitle: "بەکارهێنانی بەڵگەنامە تەندروستییە تایبەتەکانت",
    optInBody: "هیچ شتێک ناخوێندرێتەوە هەتا بەڵگەنامەیەک هەڵنابژێریت.",
    grantLabel: "چالاککردنی بەڵگەنامە تایبەتەکان",
    revokeLabel: "پاشگەزبوونەوە لە ڕازی بوون",
    activeTitle: "بەڵگەنامە تایبەتەکان",
    withdrawnNotice: "ڕازی بوون کشێرایەوە. هەڵبژاردنەکان سڕایەوە.",
    previewLabel: "کردنەوەی پێشاندان",
    grantedNotice: "ڕازی بوون چالاکە. تەنیا نموونە دروستکراوەکان دەردەکەون.",
    previewNotice: "هێشتا پێشاندان بەردەست نییە. هیچ شتێک بار نەکراوە یان شیکاری نەکراوە.",
    documents: syntheticDocuments.ku,
  },
};

export function healthCareConsentJourneyCopy(locale: Locale): HealthCareConsentJourneyCopy {
  return copy[locale];
}

/** Panel props stay referentially stable per locale for host memoization. */
export function healthCareConsentJourneyStrings(locale: Locale): HealthCareConsentSelectionPanelStrings {
  const { optInTitle, optInBody, grantLabel, revokeLabel, activeTitle, withdrawnNotice, previewLabel } = copy[locale];
  return { optInTitle, optInBody, grantLabel, revokeLabel, activeTitle, withdrawnNotice, previewLabel };
}
