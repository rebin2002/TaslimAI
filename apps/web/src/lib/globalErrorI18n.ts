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
  return normalized ? languageToLocale[normalized] ?? null : null;
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
