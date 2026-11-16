import { locales, type Locale } from "./i18n";
import { readStoredLocale } from "./localePersistence";

const languageToLocale: Record<string, Locale> = {
  en: "en",
  ar: "ar",
  ckb: "ku",
  ku: "ku",
};

function localeFromDocumentLanguage(language: string | null | undefined) {
  const normalized = language?.trim().toLowerCase();
  if (!normalized) return null;

  const exact = languageToLocale[normalized];
  if (exact) return exact;

  const baseLanguage = normalized.split("-")[0];
  // The app's `ku` alias is broad; only exact `ku` or specific `ckb` tags
  // identify the supported Sorani catalog without risking another variety.
  if (baseLanguage === "ku") return null;
  return languageToLocale[baseLanguage] ?? null;
}

/**
 * Resolve the locale that was already visible before the root layout failed.
 * The document wins for an authenticated session; anonymous storage is the
 * fallback for a first-load failure before LocaleProvider applies its choice.
 */
export function readRootErrorLocale(
  documentLanguage: string | null | undefined,
  storage: Pick<Storage, "getItem"> | null | undefined,
): Locale {
  const documentLocale = localeFromDocumentLanguage(documentLanguage);
  if (documentLocale && documentLocale !== "en") return documentLocale;
  return readStoredLocale(storage) ?? documentLocale ?? locales[0];
}

export function readBrowserRootErrorLocale(): Locale {
  const documentLanguage = typeof document === "undefined" ? null : document.documentElement.lang;
  let storage: Pick<Storage, "getItem"> | null = null;
  try {
    storage = typeof window === "undefined" ? null : window.localStorage;
  } catch {
    // Storage can be unavailable in privacy-restricted recovery contexts.
  }
  return readRootErrorLocale(documentLanguage, storage);
}
