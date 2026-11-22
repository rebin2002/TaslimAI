import { localeDirection, locales, type Locale } from "./i18n";
import { readStoredLocale } from "./localePersistence";

/** The locale union the bootstrap announcement can resolve to. */
export type ProtectedPageBootstrapLocale = Locale;

/**
 * Copy for the protected-page bootstrap live region.
 *
 * The boundary is a `role="status" aria-live="polite"` element whose only
 * descendant is a decorative `aria-hidden` spinner, so with no authored name the
 * accessible-name computation resolves to the empty string and a screen-reader
 * user on a stalled or expiring session hears nothing before an unannounced
 * route change. The region therefore needs a name of its own, in the language
 * the visitor is actually reading the page in.
 *
 * The catalog lives here rather than in `i18n.ts` for the same reason as the
 * sibling boundary modules (`globalErrorI18n.ts`, `healthCareI18n.ts`): it is
 * boundary copy that must resolve without the React locale provider, which the
 * bootstrap state can outlive. It stays keyed by the canonical `Locale` union so
 * the shared parity test still owns the locale list.
 */
export const protectedPageBootstrapStatusLabels: Record<Locale, string> = {
  en: "Checking your sign-in status",
  ar: "جارٍ التحقق من حالة تسجيل الدخول",
  ku: "پشکنینی دۆخی چوونەژوورەوە",
};

/**
 * Language tags the document can carry. `LocaleProvider` writes the language-pack
 * tag, so the built-in Kurdish locale appears as the canonical `ckb` rather than
 * the `ku` short code; both are accepted, and an unrecognised tag falls back to
 * English rather than announcing a region in the wrong language.
 */
const languageTagToLocale: Record<string, Locale> = {
  en: "en",
  ar: "ar",
  ckb: "ku",
  ku: "ku",
};

function localeFromLanguageTag(tag: string | null | undefined): Locale | null {
  const normalized = tag?.trim().toLowerCase();
  if (!normalized) return null;
  const exact = languageTagToLocale[normalized];
  if (exact) return exact;
  // A region-tagged value ("ar-EG", "en-GB") still announces in its language.
  const primary = languageTagToLocale[normalized.split("-")[0] ?? ""];
  return primary ?? null;
}

/**
 * Resolve the locale the bootstrap boundary must announce in.
 *
 * A non-English document language is what the visitor is already reading, so it
 * wins; the English tag the server-rendered shell carries is not evidence of an
 * English reader, so the persisted anonymous choice decides for that case, and
 * the browser language covers a first visit with nothing stored. English closes
 * the chain. Every read is guarded so the resolution is safe during server
 * rendering and in the repo's dependency-free DOM shim, where only some globals
 * exist.
 */
export function protectedPageBootstrapStatusLocale(
  documentLanguage: string | null | undefined,
  storage: Pick<Storage, "getItem"> | null | undefined,
  navigatorLanguage: string | null | undefined,
): Locale {
  const documentLocale = localeFromLanguageTag(documentLanguage);
  // An English `lang` is what the server-rendered shell always carries, because
  // LocaleProvider only writes the real choice after hydration, so it is not
  // evidence of an English reader. Any other tag is already visible and wins.
  // This is the same precedence the root-error boundary resolves with.
  if (documentLocale && documentLocale !== "en") return documentLocale;
  const storedLocale = readStoredLocale(storage);
  if (storedLocale) return storedLocale;
  return documentLocale ?? localeFromLanguageTag(navigatorLanguage) ?? locales[0];
}

export function readBrowserProtectedPageBootstrapLocale(): Locale {
  const documentLanguage = typeof document === "undefined" ? null : document.documentElement?.lang;
  const navigatorLanguage = typeof navigator === "undefined" ? null : navigator.language;
  let storage: Pick<Storage, "getItem"> | null = null;
  try {
    storage = typeof window === "undefined" ? null : window.localStorage;
  } catch {
    // Storage can be denied outright (privacy-restricted contexts); the
    // announcement must still resolve rather than throw on the boundary.
    storage = null;
  }
  return protectedPageBootstrapStatusLocale(documentLanguage, storage, navigatorLanguage);
}

/**
 * The announcement itself: the requested locale, falling back to English. An
 * unrepresentable locale must not silence the region, so the lookup can never
 * return undefined.
 */
export function protectedPageBootstrapStatusLabelFor(locale: Locale | null | undefined): string {
  return (locale && protectedPageBootstrapStatusLabels[locale]) || protectedPageBootstrapStatusLabels.en;
}

/** Convenience alias kept for the component call site. */
export const protectedPageBootstrapStatusText = protectedPageBootstrapStatusLabelFor;

/** The writing direction the announcement belongs to; RTL for Arabic and Kurdish. */
export function protectedPageBootstrapStatusDirection(locale: Locale) {
  return localeDirection(locale);
}
